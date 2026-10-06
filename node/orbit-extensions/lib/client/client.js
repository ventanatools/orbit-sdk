// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * A companion's connection to one host registration (contract §7, §9.2, §10): it connects over
 * the pairing's named pipe, authenticates, runs the host's sessions and invocations on the
 * handler, and reconnects with backoff. It never starts the host or loads anything into it.
 *
 * Events are delivered asynchronously, one at a time and in order, never on the pipe reader's
 * stack: "status" { state, reason?, retryInMs?, attempt, serverVerified, host?, negotiatedVersion? }
 * and "handlerFaulted" { kind, contributionId, sessionId?, requestId?, error? }. A listener that
 * throws is caught and never stops the client.
 */

const { EventEmitter } = require("node:events");
const { validateManifest } = require("../manifest.js");
const { computeManifestHash } = require("../hash.js");
const { systemClock } = require("../wire/limits.js");
const { RetryPolicy, resolveRetry } = require("./retry.js");
const { pipeTransport } = require("./transport.js");
const { Connection } = require("./connection.js");

/** The key of the client's internal seams (transport, clock, observer); used by the test kit. */
const kInternals = Symbol("internals");
/** A status event's peer error.message, for runCompanion's verbose output only. */
const kPeerMessage = Symbol("peerMessage");
/** Cuts a Waiting delay short. */
const kRetryNow = Symbol("retryNow");
/** Reports a listener that threw. */
const kSubscriberFaulted = Symbol("subscriberFaulted");
/** Completes when every event raised so far has been delivered. */
const kFlushEvents = Symbol("flushEvents");

const MAX_RUNNING_SESSIONS = 64;
const MAX_RUNNING_INVOCATIONS = 32;

/** Running author handlers, counted across reconnects (contract §9.2). */
class HandlerTracker {
    constructor() {
        this.sessions = 0;
        this.invocations = 0;
    }

    tryEnterSession() {
        if (this.sessions >= MAX_RUNNING_SESSIONS) return false;
        this.sessions++;
        return true;
    }

    exitSession() {
        this.sessions--;
    }

    tryEnterInvocation() {
        if (this.invocations >= MAX_RUNNING_INVOCATIONS) return false;
        this.invocations++;
        return true;
    }

    exitInvocation() {
        this.invocations--;
    }
}

function checkPairing(pairing) {
    if (!pairing || typeof pairing !== "object" || typeof pairing.computeProof !== "function" || typeof pairing.verifyProof !== "function"
        || typeof pairing.hostId !== "string" || typeof pairing.pipeName !== "string" || typeof pairing.registrationId !== "string"
        || typeof pairing.extensionId !== "string") {
        throw new TypeError("pairing must be a pairing read with readPairingFile.");
    }
}

function snapshot(manifest) {
    const validated = validateManifest(manifest);
    if (!validated.manifest) {
        const codes = [...new Set(validated.diagnostics.filter((d) => d.severity === "Error").map((d) => d.code))].join(", ");
        throw new TypeError("The manifest is invalid: " + codes + ".");
    }
    return validated.manifest;
}

class CompanionClient extends EventEmitter {
    #pairing;
    #retry;
    #state = "NotStarted";
    #running = false;
    #queue = [];
    #draining = false;
    #drained = [];
    #wake = null;

    /**
     * @param {{ pairing, manifest, handler, retry? }} options the pairing (the caller keeps
     * ownership and disposes it after run completes), the companion's manifest (the one the host
     * admitted; the client keeps a validated copy), the handler, and the backoff options.
     */
    constructor({ pairing, manifest, handler, retry } = {}) {
        super();
        checkPairing(pairing);
        if (!handler || typeof handler !== "object") throw new TypeError("handler must be an object with runSession and/or invoke.");
        this.#pairing = pairing;
        this.#retry = resolveRetry(retry);
        this.handler = handler;
        this.manifest = snapshot(manifest);
        this.manifestHash = computeManifestHash(this.manifest);
        this.tracker = new HandlerTracker();
        this[kInternals] = {
            transport: null,
            clock: systemClock,
            observer: null,
            offeredCapabilities: ["x.test-echo"],
            reloadManifest: null,
            random: Math.random,
            handlerStopMs: 5000,
        };
        this[kSubscriberFaulted] = null;
    }

    /** "NotStarted", "Connecting", "Connected", "Waiting" or "Stopped". */
    get state() {
        return this.#state;
    }

    get clock() {
        return this[kInternals].clock;
    }

    get observer() {
        return this[kInternals].observer;
    }

    get offeredCapabilities() {
        return this[kInternals].offeredCapabilities;
    }

    get handlerStopMs() {
        return this[kInternals].handlerStopMs;
    }

    /**
     * Connects and serves the host until `signal` is aborted or the client reaches Stopped.
     * Rejects when the client is already running, or when it has no transport (the companion
     * transport is a Windows named pipe).
     */
    async run(signal) {
        if (this.#running) throw new Error("This companion client is already running.");
        const internals = this[kInternals];
        if (!internals.transport && process.platform !== "win32") {
            throw new Error("The companion transport is a Windows named pipe.");
        }
        this.#running = true;
        try {
            await this.#loop(signal, internals.transport || pipeTransport(this.#pairing.pipeName));
        } finally {
            await this[kFlushEvents]();
            this.#running = false;
        }
    }

    async #loop(signal, transport) {
        const pairing = this.#pairing;
        if (pairing.extensionId !== this.manifest.id) {
            this.#setState("Stopped", "pairing.extension-mismatch", undefined, 1, false);
            return;
        }
        if (!this.manifest.hosts.includes(pairing.hostId)) {
            this.#setState("Stopped", "pairing.host-not-listed", undefined, 1, false);
            return;
        }
        const policy = new RetryPolicy(this.#retry, this[kInternals].random);
        let attempt = 1;
        while (!(signal && signal.aborted)) {
            this.#setState("Connecting", undefined, undefined, attempt, false);
            const outcome = await this.#connectOnce(transport, attempt, signal);
            if (signal && signal.aborted) break;
            if (outcome.connectedForMs >= this.#retry.stableMs) {
                policy.reset();
                attempt = 1;
            }
            const decision = policy.next(outcome.reason, outcome.retryAfterMs, outcome.serverVerified);
            if (decision.kind === "Stop") {
                this.#setState("Stopped", outcome.reason, undefined, attempt, outcome.serverVerified, outcome.host, outcome.version, outcome.peerMessage);
                return;
            }
            if (decision.kind === "Wait") {
                // The delay starts before Waiting is raised, so retryInMs counts from a timer that already runs.
                const wait = this.#delay(decision.delayMs, signal);
                this.#setState("Waiting", outcome.reason, decision.delayMs, attempt, outcome.serverVerified, outcome.host, outcome.version, outcome.peerMessage);
                await wait;
                if (signal && signal.aborted) break;
                if ((outcome.reason === "manifest.mismatch" || outcome.reason === "auth.identity-changed") && this[kInternals].reloadManifest) {
                    let changed;
                    try {
                        changed = await this[kInternals].reloadManifest();
                    } catch {
                        changed = undefined;
                    }
                    if (changed) this.#replaceManifest(changed);
                }
            }
            attempt++;
        }
        this.#setState("Stopped", undefined, undefined, attempt, false);
    }

    async #connectOnce(transport, attempt, signal) {
        let connected;
        try {
            connected = await transport.connect(signal);
        } catch {
            connected = { failure: "host.not-running" };
        }
        if (!connected || !connected.socket) {
            return { reason: connected ? connected.failure : undefined, serverVerified: false, connectedForMs: 0 };
        }
        const connection = new Connection(this, connected.socket, this.#pairing, this.manifest, this.manifestHash, attempt);
        return connection.run(signal);
    }

    #delay(ms, signal) {
        return new Promise((resolve) => {
            const clock = this[kInternals].clock;
            let timer = null;
            const finish = () => {
                if (timer !== null) clock.clearTimeout(timer);
                timer = null;
                if (signal) signal.removeEventListener("abort", finish);
                this.#wake = null;
                resolve();
            };
            timer = clock.setTimeout(finish, ms);
            this.#wake = finish;
            if (signal) signal.addEventListener("abort", finish, { once: true });
        });
    }

    [kRetryNow]() {
        if (this.#wake) this.#wake();
    }

    #replaceManifest(manifest) {
        try {
            const next = snapshot(manifest);
            if (next.id === this.manifest.id) {
                this.manifest = next;
                this.manifestHash = computeManifestHash(next);
            }
        } catch {
            // An invalid replacement leaves the current manifest in place.
        }
    }

    reportConnected(attempt, serverVerified, host, version) {
        this.#setState("Connected", undefined, undefined, attempt, serverVerified, host, version);
    }

    raiseHandlerFaulted(kind, contributionId, sessionId, requestId, error) {
        const args = { kind, contributionId };
        if (sessionId !== undefined) args.sessionId = sessionId;
        if (requestId !== undefined) args.requestId = requestId;
        if (error !== undefined) args.error = error;
        this.#post("handlerFaulted", Object.freeze(args));
    }

    #setState(state, reason, retryInMs, attempt, serverVerified, host, negotiatedVersion, peerMessage) {
        this.#state = state;
        const args = { state };
        if (reason !== undefined) args.reason = reason;
        if (retryInMs !== undefined) args.retryInMs = retryInMs;
        args.attempt = attempt;
        args.serverVerified = serverVerified === true;
        if (host !== undefined) args.host = host;
        if (negotiatedVersion !== undefined) args.negotiatedVersion = negotiatedVersion;
        if (peerMessage !== undefined) Object.defineProperty(args, kPeerMessage, { value: peerMessage });
        this.#post("status", Object.freeze(args));
    }

    #post(name, args) {
        this.#queue.push([name, args]);
        if (!this.#draining) {
            this.#draining = true;
            setImmediate(() => this.#drain());
        }
    }

    #drain() {
        while (this.#queue.length > 0) {
            const [name, args] = this.#queue.shift();
            for (const listener of this.listeners(name)) {
                try {
                    listener.call(this, args);
                } catch (error) {
                    try {
                        if (this[kSubscriberFaulted]) this[kSubscriberFaulted](error);
                    } catch {
                        // A reporter that throws must not stop the queue either.
                    }
                }
            }
        }
        this.#draining = false;
        const waiting = this.#drained;
        this.#drained = [];
        for (const resolve of waiting) resolve();
    }

    [kFlushEvents]() {
        if (!this.#draining && this.#queue.length === 0) return Promise.resolve();
        return new Promise((resolve) => this.#drained.push(resolve));
    }

    /** Never emits synchronously: listeners run from the event queue. */
    emit(name, ...args) {
        if (name === "status" || name === "handlerFaulted") return false;
        return super.emit(name, ...args);
    }
}

module.exports = { CompanionClient, kInternals, kPeerMessage, kRetryNow, kSubscriberFaulted, kFlushEvents };
