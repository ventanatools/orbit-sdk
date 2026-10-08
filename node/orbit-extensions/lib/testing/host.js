// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * startTestHost (contract §9.3, §10): a real CompanionClient over an in-memory duplex stream that
 * speaks real frames to a scripted protocol-3 host. The host fills missing settings with defaults
 * (and sends extra keys as given), answers pings, cancels an invocation at its invokeTimeoutMs
 * (TimedOut), claims host version 1.0.0, and builds the pipe name from edition "test" and a zero
 * user hash, which the in-memory transport never uses.
 */

const { randomBytes } = require("node:crypto");
const { FrameReader, MAX_HANDSHAKE_FRAME_BYTES } = require("../wire/framing.js");
const { readMessage, writeMessage } = require("../wire/messages.js");
const { newNonce, negotiate, computeProof, verifyProof, createPipeName, PROTOCOL_MIN, PROTOCOL_MAX } = require("../wire/handshake.js");
const { protocol3Limits, limitsWithinRanges, systemClock } = require("../wire/limits.js");
const { validateManifest, findContribution } = require("../manifest.js");
const { computeManifestHash } = require("../hash.js");
const { firstActiveHost } = require("../hosts.js");
const { readPairing } = require("../pairing.js");
const { isHostId, isLanguageTag } = require("../text.js");
const { CompanionClient, kInternals } = require("../client/client.js");
const { duplexPair } = require("../client/transport.js");
const { completeSettings } = require("../client/settings.js");
const { toAuthorFace } = require("../client/session.js");

function newId() {
    let id;
    do {
        id = randomBytes(16).toString("hex");
    } while (/^0+$/.test(id));
    return id;
}

function deferred() {
    let resolve;
    let reject;
    const promise = new Promise((res, rej) => {
        resolve = res;
        reject = rej;
    });
    return { promise, resolve, reject };
}

/** One host-side connection: the handshake, then sessions, invocations and faces. */
class HostConnection {
    constructor(owner, socket) {
        this.owner = owner;
        this.socket = socket;
        this.phase = "Handshake";
        this.open = false;
        this.closed = false;
        this.hello = null;
        this.transcript = null;
        this.reader = new FrameReader({
            maxFrameBytes: MAX_HANDSHAKE_FRAME_BYTES,
            clock: owner.clock,
            onFrame: (body) => this.onFrame(body),
            onError: (code) => this.refuse(code),
        });
        socket.on("data", (chunk) => this.reader.push(chunk));
        socket.on("error", () => {});
        socket.on("end", () => this.shutdown());
        socket.on("close", () => this.shutdown());
    }

    send(message) {
        if (this.closed || this.socket.destroyed || this.socket.writableEnded) throw new Error("The companion is not connected.");
        const body = writeMessage(message);
        const frame = Buffer.alloc(4 + body.length);
        frame.writeUInt32LE(body.length, 0);
        body.copy(frame, 4);
        this.owner.record("Host", message);
        this.socket.write(frame);
    }

    refuse(code, supported) {
        if (this.closed) return;
        try {
            const error = { type: "error", code };
            if (supported) error.supported = supported;
            this.send(error);
        } catch {
            // Already closed.
        }
        this.close();
    }

    close() {
        if (this.closed) return;
        this.socket.end();
        this.shutdown();
    }

    shutdown() {
        if (this.closed) return;
        this.closed = true;
        this.open = false;
        this.reader.close();
        this.owner.connectionClosed(this);
    }

    onFrame(body) {
        if (this.closed) return;
        const read = readMessage(body, "Companion", this.phase);
        if (read.violation) {
            this.refuse(read.violation);
            return;
        }
        if (!read.message) return;
        const message = read.message;
        this.owner.record("Companion", message);
        if (this.phase !== "Authenticated") {
            this.onHandshake(message);
            return;
        }
        this.owner.onCompanionMessage(this, message);
    }

    onHandshake(message) {
        const owner = this.owner;
        if (message.type === "hello") {
            if (message.registrationId !== owner.registrationId) {
                this.refuse("auth.registration-mismatch");
                return;
            }
            const version = negotiate(message.minVersion, message.maxVersion, PROTOCOL_MIN, PROTOCOL_MAX);
            if (version === null) {
                this.refuse("protocol.version-unsupported", { minVersion: PROTOCOL_MIN, maxVersion: PROTOCOL_MAX });
                return;
            }
            this.hello = message;
            this.transcript = {
                hostId: owner.hostId,
                hostVersion: "1.0.0",
                registrationId: message.registrationId,
                clientNonce: message.clientNonce,
                serverNonce: newNonce(),
                version,
                minVersion: message.minVersion,
                maxVersion: message.maxVersion,
                clientCapabilities: message.capabilities,
                hostCapabilities: owner.capabilities,
                manifestHash: message.manifestHash,
            };
            this.send({
                type: "challenge",
                serverNonce: this.transcript.serverNonce,
                version,
                capabilities: owner.capabilities,
                host: { id: owner.hostId, version: "1.0.0" },
                proof: computeProof(owner.secret, this.transcript, "server"),
            });
            return;
        }
        if (message.type === "authenticate" && this.transcript) {
            if (!verifyProof(owner.secret, message.proof, this.transcript, "client")) {
                this.refuse("auth.proof-invalid");
                return;
            }
            if (this.hello.manifestHash !== owner.manifestHash) {
                this.refuse("manifest.mismatch");
                return;
            }
            this.send({
                type: "ready",
                version: this.transcript.version,
                capabilities: owner.capabilities,
                host: { id: owner.hostId, version: "1.0.0" },
                uiLanguage: owner.uiLanguage,
                limits: owner.limits,
            });
            this.phase = "Authenticated";
            this.open = true;
            this.reader.setMaxFrameBytes(owner.limits.maxFrameBytes);
            return;
        }
        if (message.type === "error") {
            this.close();
            return;
        }
        this.refuse("protocol.unexpected");
    }
}

class TestHost {
    constructor(options) {
        this.manifest = options.manifest;
        this.manifestHash = computeManifestHash(options.manifest);
        this.hostId = options.hostId;
        this.limits = options.limits;
        this.capabilities = options.capabilities;
        this.uiLanguage = options.uiLanguage;
        this.clock = options.clock;
        this.secret = randomBytes(32);
        this.registrationId = newId();
        this.statuses = [];
        this.faults = [];
        this.entries = [];
        this.sessions = new Map();
        this.invocations = new Map();
        this.connection = null;
        this.connectionWaiters = [];
        this.startedAt = this.clock.now();
        this.started = new Map();
    }

    get transcript() {
        return Object.freeze([...this.entries]);
    }

    /** One JSON object per transcript entry, one per line. */
    transcriptJsonLines() {
        return this.entries.map((entry) => JSON.stringify(entry)).join("\n") + (this.entries.length ? "\n" : "");
    }

    record(from, message) {
        const entry = { atMs: this.clock.now() - this.startedAt, from, type: message.type };
        if (typeof message.sessionId === "string") entry.sessionId = message.sessionId;
        if (typeof message.requestId === "string") entry.requestId = message.requestId;
        this.entries.push(Object.freeze(entry));
    }

    accept(socket) {
        this.connection = new HostConnection(this, socket);
    }

    connectionClosed(connection) {
        if (this.connection === connection) this.connection = null;
        for (const invocation of this.invocations.values()) {
            invocation.fail(new Error("The companion disconnected before answering."));
        }
        this.invocations.clear();
    }

    requireOpen() {
        const connection = this.connection;
        if (!connection || !connection.open) throw new Error("The companion is not connected.");
        return connection;
    }

    onCompanionMessage(connection, message) {
        switch (message.type) {
            case "setFace": {
                const session = this.sessions.get(message.sessionId);
                if (session) session.addFace(Object.freeze({ face: toAuthorFace(message.face), arrivedAtMs: this.clock.now() - this.startedAt }));
                break;
            }
            case "sessionRefused": {
                const session = this.sessions.get(message.sessionId);
                if (session) {
                    session.refusedCode = message.code;
                    session.refusal.resolve();
                }
                break;
            }
            case "result": {
                const invocation = this.invocations.get(message.requestId);
                if (invocation) {
                    this.invocations.delete(message.requestId);
                    invocation.complete(message.failure ? { kind: message.outcome, failure: message.failure } : { kind: message.outcome });
                }
                break;
            }
            case "ping":
                try {
                    connection.send({ type: "pong", id: message.id });
                } catch {
                    // Closed.
                }
                break;
            case "error":
                connection.close();
                break;
            default:
                break;
        }
    }

    sessionStarted(sessionId, refusal) {
        const waiter = this.started.get(sessionId);
        if (waiter) waiter.resolve(refusal);
    }

    /**
     * Starts a session of `contributionId` and resolves once the companion accepted or refused it;
     * refusedCode then says which. Missing settings get their defaults.
     */
    async startSession(contributionId, settings) {
        const connection = this.requireOpen();
        const contribution = findContribution(this.manifest, contributionId);
        const complete = contribution ? completeSettings(contribution, settings) : { ...(settings || {}) };
        const sessionId = newId();
        const session = new TestHostSession(sessionId, contributionId);
        this.sessions.set(sessionId, session);
        const started = deferred();
        this.started.set(sessionId, started);
        connection.send({ type: "startSession", sessionId, contributionId, settings: complete });
        const refusal = await started.promise;
        this.started.delete(sessionId);
        if (refusal) await session.refusal.promise;
        return session;
    }

    /** Sends invoke for `session` and returns at once with { requestId, result }. */
    startInvoke(session) {
        const connection = this.requireOpen();
        const requestId = newId();
        const done = deferred();
        let settled = false;
        const invocation = {
            requestId,
            result: done.promise,
            complete: (outcome) => {
                if (settled) return false;
                settled = true;
                this.clock.clearTimeout(timer);
                done.resolve(Object.freeze(outcome));
                return true;
            },
            fail: (error) => {
                if (settled) return;
                settled = true;
                this.clock.clearTimeout(timer);
                done.reject(error);
            },
        };
        const timer = this.clock.setTimeout(() => {
            this.invocations.delete(requestId);
            if (invocation.complete({ kind: "TimedOut" })) {
                try {
                    this.requireOpen().send({ type: "cancel", requestId });
                } catch {
                    // Nothing to cancel.
                }
            }
        }, this.limits.invokeTimeoutMs);
        this.invocations.set(requestId, invocation);
        connection.send({ type: "invoke", requestId, sessionId: session.sessionId });
        return Object.freeze({ requestId, result: done.promise, complete: invocation.complete });
    }

    /** Sends cancel for an invocation; its result is then Cancelled. */
    cancel(invocation) {
        this.invocations.delete(invocation.requestId);
        if (invocation.complete({ kind: "Cancelled" })) {
            this.requireOpen().send({ type: "cancel", requestId: invocation.requestId });
        }
    }

    /** startInvoke, then its result; aborting `signal` sends cancel. */
    async invoke(session, { signal } = {}) {
        const invocation = this.startInvoke(session);
        if (signal) {
            if (signal.aborted) this.cancel(invocation);
            else signal.addEventListener("abort", () => this.cancel(invocation), { once: true });
        }
        return invocation.result;
    }

    stopSession(session) {
        this.requireOpen().send({ type: "stopSession", sessionId: session.sessionId });
    }

    /** Ends the connection, sending error with `code` first when given; the client reconnects. */
    disconnect(code) {
        const connection = this.connection;
        if (!connection) return;
        if (code) connection.refuse(code);
        else connection.close();
    }

    /** Resolves with the next face the session received, in arrival order: { face, arrivedAtMs }. */
    waitForFace(session, timeoutMs = 2000) {
        return session.nextFace(timeoutMs, this.clock);
    }

    async waitForConnection(timeoutMs) {
        const deadline = Date.now() + timeoutMs;
        while (!(this.connection && this.connection.open)) {
            if (Date.now() > deadline) throw new Error("The companion did not connect.");
            await new Promise((resolve) => setTimeout(resolve, 2));
        }
    }
}

class TestHostSession {
    constructor(sessionId, contributionId) {
        this.sessionId = sessionId;
        this.contributionId = contributionId;
        this.refusedCode = undefined;
        this.refusal = deferred();
        this.faces = [];
        this.waiters = [];
    }

    addFace(face) {
        const waiter = this.waiters.shift();
        if (waiter) waiter(face);
        else this.faces.push(face);
    }

    nextFace(timeoutMs, clock) {
        if (this.faces.length > 0) return Promise.resolve(this.faces.shift());
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                const at = this.waiters.indexOf(take);
                if (at >= 0) this.waiters.splice(at, 1);
                reject(new Error("No face arrived within " + timeoutMs + " ms."));
            }, timeoutMs);
            const take = (face) => {
                clearTimeout(timer);
                resolve(face);
            };
            this.waiters.push(take);
        });
    }
}

/**
 * Starts a test host for `manifest` and runs a real CompanionClient with `handler` against it.
 * Resolves once the companion is connected. Options: hostId (default: the first active registry
 * host in the manifest's hosts, else its first host), limits, capabilities, uiLanguage, clock
 * (a ManualClock to control time), retry.
 */
async function startTestHost({ manifest, handler, hostId, limits, capabilities, uiLanguage, clock, retry, connectTimeoutMs = 5000 } = {}) {
    const validated = validateManifest(manifest);
    if (!validated.manifest) {
        throw new TypeError("The manifest is invalid: " + validated.diagnostics.filter((d) => d.severity === "Error").map((d) => d.toString()).join("; "));
    }
    const resolvedManifest = validated.manifest;
    const active = firstActiveHost(resolvedManifest.hosts);
    const id = hostId || (active ? active.id : resolvedManifest.hosts[0]);
    if (!isHostId(id)) throw new TypeError("hostId does not match the host-id grammar.");
    const hostLimits = limits || protocol3Limits;
    if (!limitsWithinRanges(hostLimits)) throw new RangeError("limits are outside the ranges of the contract.");
    const language = uiLanguage || "en-US";
    if (!isLanguageTag(language)) throw new TypeError("uiLanguage is not a language tag.");
    const host = new TestHost({
        manifest: resolvedManifest,
        hostId: id,
        limits: Object.freeze({ ...hostLimits }),
        capabilities: Object.freeze([...(capabilities || [])]),
        uiLanguage: language,
        clock: clock || systemClock,
    });
    const pipeName = createPipeName(id, "test", "0000000000000000", host.registrationId);
    const pairingText = JSON.stringify({
        pairingVersion: 3,
        mode: "Persistent",
        hostId: id,
        pipeName,
        registrationId: host.registrationId,
        extensionId: resolvedManifest.id,
        secret: host.secret.toString("base64"),
    });
    const read = readPairing(Buffer.from(pairingText, "utf8"), { expectedExtensionId: resolvedManifest.id, manifestHosts: resolvedManifest.hosts });
    if (!read.pairing) throw new TypeError("The test host could not pair: " + read.diagnostics.map((d) => d.code).join(", "));
    const client = new CompanionClient({ pairing: read.pairing, manifest: resolvedManifest, handler, retry });
    const internals = client[kInternals];
    internals.clock = host.clock;
    internals.transport = {
        connect() {
            const [companionSide, hostSide] = duplexPair();
            host.accept(hostSide);
            return Promise.resolve({ socket: companionSide });
        },
    };
    internals.observer = { sessionStarted: (sessionId, refusal) => host.sessionStarted(sessionId, refusal) };
    client.on("status", (status) => host.statuses.push(status));
    client.on("handlerFaulted", (fault) => host.faults.push(fault));
    const stop = new AbortController();
    const running = client.run(stop.signal);
    running.catch(() => {});
    host.client = client;
    host.close = async () => {
        stop.abort();
        await running;
        read.pairing.dispose();
    };
    try {
        await host.waitForConnection(connectTimeoutMs);
        const deadline = Date.now() + connectTimeoutMs;
        while (client.state !== "Connected") {
            if (Date.now() > deadline) throw new Error("The companion did not connect.");
            await new Promise((resolve) => setImmediate(resolve));
        }
    } catch (error) {
        await host.close();
        throw error;
    }
    return host;
}

module.exports = { startTestHost, TestHost };
