// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * One connection (contract §7): the companion side of the handshake, then the frame reader, the
 * paced writer, sessions, invocations, liveness and rate limits. Author code never runs on the
 * reader's stack, and nothing an author does makes the SDK send a frame the host would close on.
 */

const { FrameReader, MAX_HANDSHAKE_FRAME_BYTES } = require("../wire/framing.js");
const { readMessage, writeMessage } = require("../wire/messages.js");
const { newNonce, PROTOCOL_MIN, PROTOCOL_MAX } = require("../wire/handshake.js");
const { TokenBucket, protocol3Limits, limitsForCompanion } = require("../wire/limits.js");
const { reasonInfo } = require("../codes.js");
const { findContribution, provided } = require("../manifest.js");
const { clientInfo } = require("../identity.js");
const { Session, kEnd, normalizeResult, isAbortCompletion } = require("./session.js");
const { checkSessionSettings } = require("./settings.js");

const REPLAY_CAPACITY = 1024;
const HOST_FRAME_RATE = 512;
const HOST_FRAME_BURST = 1024;
const HANDSHAKE_TIMEOUT_MS = 5000;
const WRITE_TIMEOUT_MS = 5000;
const ERROR_FLUSH_TIMEOUT_MS = 1000;
const MAX_RENEWAL_MS = 86400000;

/** A bounded recency window of ended ids (contract §7.6.3). */
class ReplayWindow {
    constructor(capacity) {
        this.capacity = capacity;
        this.ids = new Set();
    }

    has(id) {
        return this.ids.has(id);
    }

    add(id) {
        if (this.ids.has(id)) return;
        this.ids.add(id);
        if (this.ids.size > this.capacity) this.ids.delete(this.ids.values().next().value);
    }
}

function deferred() {
    let resolve;
    const promise = new Promise((r) => {
        resolve = r;
    });
    return { promise, resolve };
}

class Connection {
    constructor(client, socket, pairing, manifest, manifestHash, attempt) {
        this.client = client;
        this.socket = socket;
        this.pairing = pairing;
        this.manifest = manifest;
        this.manifestHash = manifestHash;
        this.attempt = attempt;
        this.clock = client.clock;
        this.observer = client.observer;
        this.hostFrames = new TokenBucket(HOST_FRAME_RATE, HOST_FRAME_BURST, this.clock);
        this.phase = "Handshake";
        this.serverVerified = false;
        this.host = undefined;
        this.version = undefined;
        this.limits = protocol3Limits;
        this.readyPingIntervalMs = protocol3Limits.pingIntervalMs;
        this.effective = [];
        this.uiLanguage = "en-US";
        this.facePacing = null;
        this.faceSpacingMs = 500;
        this.faceHoldUntil = 0;
        this.facePending = [];
        this.sessions = new Map();
        this.invokes = new Map();
        this.endedSessions = new ReplayWindow(REPLAY_CAPACITY);
        this.completedRequests = new ReplayWindow(REPLAY_CAPACITY);
        this.timers = { handshake: null, ping: null, pong: null, pump: null, pumpAt: Infinity };
        this.nextPingId = 0;
        this.outstandingPing = 0;
        this.hostPinged = false;
        this.lastHostPing = 0;
        this.closing = false;
        this.connected = false;
        this.reason = undefined;
        this.retryAfterMs = undefined;
        this.peerMessage = undefined;
        this.connectedAt = -1;
        this.done = deferred();
        this.hello = null;
        this.challenge = null;
        this.reader = new FrameReader({
            maxFrameBytes: MAX_HANDSHAKE_FRAME_BYTES,
            clock: this.clock,
            onFrame: (body) => this.onFrame(body),
            onError: (code) => this.onFrameError(code),
        });
    }

    /** Runs the connection until it closes; resolves with its outcome. */
    run(signal) {
        const onAbort = () => this.close(undefined, false);
        if (signal) {
            if (signal.aborted) onAbort();
            else signal.addEventListener("abort", onAbort, { once: true });
        }
        this.socket.on("data", (chunk) => {
            if (!this.closing) this.reader.push(chunk);
        });
        this.socket.on("error", () => {});
        this.socket.on("end", () => this.close(undefined, false));
        this.socket.on("close", () => {
            this.close(undefined, false);
            this.finish();
        });
        if (!this.closing) this.startHandshake();
        return this.done.promise.then((outcome) => {
            if (signal) signal.removeEventListener("abort", onAbort);
            return outcome;
        });
    }

    finish() {
        this.reader.close();
        this.done.resolve({
            reason: this.reason,
            retryAfterMs: this.retryAfterMs,
            serverVerified: this.serverVerified,
            connectedForMs: this.connectedAt >= 0 ? this.clock.now() - this.connectedAt : 0,
            host: this.host,
            version: this.version,
            peerMessage: this.peerMessage,
        });
    }

    // ---------------------------------------------------------------- handshake (contract §7.3)

    startHandshake() {
        this.timers.handshake = this.clock.setTimeout(() => {
            if (!this.connected) this.close("auth.timeout", false);
        }, HANDSHAKE_TIMEOUT_MS);
        this.hello = {
            type: "hello",
            minVersion: PROTOCOL_MIN,
            maxVersion: PROTOCOL_MAX,
            registrationId: this.pairing.registrationId,
            clientNonce: newNonce(),
            capabilities: [...this.client.offeredCapabilities],
            client: { name: clientInfo.name, version: clientInfo.version },
            manifestHash: this.manifestHash,
        };
        this.phase = "Handshake";
        this.write(writeMessage(this.hello));
    }

    /** Ends the handshake with `code`: writes an error frame for a wire code (best effort), never for a local one. */
    failHandshake(code, supported) {
        if (this.closing) return;
        if (reasonInfo(code).disposition === "Local") {
            this.close(code, false);
            return;
        }
        const error = { type: "error", code };
        if (supported) error.supported = supported;
        this.closeAfter(code, writeMessage(error));
    }

    onChallenge(challenge) {
        const hello = this.hello;
        if (challenge.version < hello.minVersion || challenge.version > hello.maxVersion
            || challenge.version < PROTOCOL_MIN || challenge.version > PROTOCOL_MAX) {
            this.failHandshake("protocol.version-unsupported", { minVersion: PROTOCOL_MIN, maxVersion: PROTOCOL_MAX });
            return;
        }
        if (challenge.host.id !== this.pairing.hostId) {
            this.failHandshake("auth.host-mismatch");
            return;
        }
        const transcript = {
            hostId: challenge.host.id,
            hostVersion: challenge.host.version,
            registrationId: hello.registrationId,
            clientNonce: hello.clientNonce,
            serverNonce: challenge.serverNonce,
            version: challenge.version,
            minVersion: hello.minVersion,
            maxVersion: hello.maxVersion,
            clientCapabilities: hello.capabilities,
            hostCapabilities: challenge.capabilities,
            manifestHash: hello.manifestHash,
        };
        let verified = false;
        try {
            verified = this.pairing.verifyProof(challenge.proof, transcript, "server");
        } catch {
            verified = false;
        }
        if (!verified) {
            this.failHandshake("auth.server-proof-invalid");
            return;
        }
        this.serverVerified = true;
        this.host = Object.freeze({ id: challenge.host.id, version: challenge.host.version });
        this.version = challenge.version;
        this.challenge = challenge;
        this.phase = "PeerVerified";
        this.write(writeMessage({ type: "authenticate", proof: this.pairing.computeProof(transcript, "client") }));
    }

    onReady(ready) {
        const challenge = this.challenge;
        const sameCapabilities = ready.capabilities.length === challenge.capabilities.length
            && ready.capabilities.every((id, i) => id === challenge.capabilities[i]);
        if (ready.version !== challenge.version || !sameCapabilities || ready.host.id !== challenge.host.id
            || ready.host.version !== challenge.host.version) {
            this.failHandshake("protocol.message-invalid");
            return;
        }
        const hostSide = new Set(challenge.capabilities);
        this.effective = [...new Set(this.client.offeredCapabilities.filter((id) => hostSide.has(id)))];
        this.limits = limitsForCompanion(ready.limits);
        this.readyPingIntervalMs = ready.limits.pingIntervalMs;
        this.uiLanguage = ready.uiLanguage;
        this.facePacing = new TokenBucket(Math.max(0.5, this.limits.messageRate / 2), Math.max(8, Math.floor(this.limits.messageBurst / 2)), this.clock);
        this.faceSpacingMs = 1000 / this.limits.faceChangesPerSecond;
        this.connectedAt = this.clock.now();
        this.reader.setMaxFrameBytes(this.limits.maxFrameBytes);
        this.clock.clearTimeout(this.timers.handshake);
        this.timers.handshake = null;
        this.phase = "Authenticated";
        this.connected = true;
        this.resetPingTimer();
        this.client.reportConnected(this.attempt, this.serverVerified, this.host, this.version);
    }

    // ---------------------------------------------------------------- reading

    onFrameError(code) {
        if (this.connected) this.close(code, true);
        else this.failHandshake(code);
    }

    onFrame(body) {
        if (this.closing) return;
        if (!this.hostFrames.tryTake(TokenBucket.costOf(body.length))) {
            this.onFrameError("rate.exceeded");
            return;
        }
        if (this.connected) this.resetPingTimer();
        const read = readMessage(body, "Host", this.phase);
        if (!this.connected) {
            this.onHandshakeFrame(read);
            return;
        }
        if (read.violation) {
            this.close(read.violation, true);
            return;
        }
        if (!read.message) return;
        this.dispatch(read.message);
        if (this.observer && this.observer.frameProcessed) this.observer.frameProcessed(read.message);
    }

    onHandshakeFrame(read) {
        if (read.violation) {
            this.failHandshake(read.violation);
            return;
        }
        const message = read.message;
        if (!message) {
            this.failHandshake("protocol.unexpected");
            return;
        }
        if (message.type === "error") {
            this.recordPeerError(message);
            this.close(message.code, false);
            return;
        }
        if (this.phase === "Handshake" && message.type === "challenge") {
            this.onChallenge(message);
            return;
        }
        if (this.phase === "PeerVerified" && message.type === "ready") {
            this.onReady(message);
            return;
        }
        this.failHandshake("protocol.unexpected");
    }

    dispatch(message) {
        switch (message.type) {
            case "startSession":
                this.startSession(message);
                break;
            case "stopSession":
                this.stopSession(message.sessionId);
                break;
            case "invoke":
                this.invoke(message);
                break;
            case "cancel":
                this.cancel(message.requestId);
                break;
            case "ping":
                this.hostPing(message.id);
                break;
            case "pong":
                this.hostPong(message.id);
                break;
            case "error":
                this.hostError(message);
                break;
            default:
                break;
        }
    }

    // ---------------------------------------------------------------- sessions (contract §7.6)

    startSession(message) {
        if (this.closing) return;
        if (this.sessions.has(message.sessionId) || this.endedSessions.has(message.sessionId)) {
            this.close("session.replay", true);
            return;
        }
        let code = checkSessionSettings(this.manifest, message.contributionId, message.settings);
        let capacityFault = false;
        if (code === null && this.sessions.size >= this.limits.maxSessions) code = "session.capacity";
        if (code === null && !this.client.tracker.tryEnterSession()) {
            code = "session.capacity";
            capacityFault = true;
        }
        if (code !== null) {
            this.endedSessions.add(message.sessionId);
            this.write(writeMessage({ type: "sessionRefused", sessionId: message.sessionId, code }));
            if (this.observer && this.observer.sessionStarted) this.observer.sessionStarted(message.sessionId, code);
            if (capacityFault) this.client.raiseHandlerFaulted("SessionCapacity", message.contributionId, message.sessionId, undefined, undefined);
            return;
        }
        const contribution = findContribution(this.manifest, message.contributionId);
        const entry = {
            controller: new AbortController(),
            ended: deferred(),
            isEnded: false,
            handlerRunning: true,
            stopped: false,
            pending: null,
            queued: false,
            nextFaceAt: 0,
            generation: 0,
            renewal: null,
            session: null,
        };
        entry.session = new Session({
            id: message.sessionId,
            contributionId: contribution.id,
            provides: contribution.provides,
            settings: message.settings,
            uiLanguage: this.uiLanguage,
            hostCapabilities: this.effective,
        }, this.sinkFor(entry));
        this.sessions.set(message.sessionId, entry);
        if (this.observer && this.observer.sessionStarted) this.observer.sessionStarted(message.sessionId, null);
        setImmediate(() => this.runSessionHandler(entry));
    }

    sinkFor(entry) {
        return {
            publish: (session, command) => this.publish(entry, command),
            publishedAfterEnd: (session) => {
                if (this.observer && this.observer.publishedAfterEnd) this.observer.publishedAfterEnd(session.id);
            },
            faceWithoutProvides: (session) => {
                if (this.observer && this.observer.faceWithoutProvides) this.observer.faceWithoutProvides(session.id);
            },
        };
    }

    async runSessionHandler(entry) {
        const handler = this.client.handler;
        const signal = entry.controller.signal;
        let fault = null;
        try {
            if (handler && typeof handler.runSession === "function") await handler.runSession(entry.session, signal);
        } catch (error) {
            if (!isAbortCompletion(error, signal)) fault = error === undefined ? new Error("The session handler threw undefined.") : error;
        } finally {
            entry.handlerRunning = false;
            entry.isEnded = true;
            entry.ended.resolve();
            this.client.tracker.exitSession();
            if (this.observer && this.observer.sessionHandlerEnded) this.observer.sessionHandlerEnded(entry.session.id);
        }
        if (fault === null) return;
        this.client.raiseHandlerFaulted("Exception", entry.session.contributionId, entry.session.id, undefined, fault);
        if (entry.session.provides.includes("face") && this.isCurrent(entry)) {
            this.stopRenewal(entry);
            this.enqueue(entry, { kind: "clear" });
            this.pump();
        }
    }

    stopSession(sessionId) {
        const entry = this.sessions.get(sessionId);
        if (!entry) return;
        this.sessions.delete(sessionId);
        this.retire(entry);
        this.endedSessions.add(sessionId);
        const calls = [...this.invokes.values()].filter((call) => call.entry === entry);
        for (const call of calls) call.sessionStopped = true;
        this.cancelAndWatch(entry, entry.session.contributionId, entry.session.id, undefined);
        for (const call of calls) this.cancelAndWatch(call, entry.session.contributionId, entry.session.id, call.requestId);
    }

    /** Ends a session's publishing. */
    retire(entry) {
        entry.stopped = true;
        entry.session[kEnd]();
        this.stopRenewal(entry);
        if (entry.queued) {
            const at = this.facePending.indexOf(entry);
            if (at >= 0) this.facePending.splice(at, 1);
            entry.queued = false;
        }
        entry.pending = null;
    }

    /**
     * Aborts a handler's signal off the reader's stack, then raises IgnoredCancellation when it is
     * still running after the stop timeout.
     */
    cancelAndWatch(owner, contributionId, sessionId, requestId) {
        if (owner.isEnded) return;
        setImmediate(() => {
            try {
                owner.controller.abort();
            } catch {
                // An author's abort listener threw; the handler still has to end.
            }
        });
        const timer = this.clock.setTimeout(() => {
            if (!owner.isEnded) this.client.raiseHandlerFaulted("IgnoredCancellation", contributionId, sessionId, requestId, undefined);
        }, this.client.handlerStopMs);
        owner.ended.promise.then(() => this.clock.clearTimeout(timer));
    }

    isCurrent(entry) {
        return !this.closing && !entry.stopped && this.sessions.get(entry.session.id) === entry;
    }

    // ---------------------------------------------------------------- invocations (contract §7.8)

    invoke(message) {
        if (this.closing) return;
        if (this.invokes.has(message.requestId) || this.completedRequests.has(message.requestId)) {
            this.close("invoke.replay", true);
            return;
        }
        const entry = this.sessions.get(message.sessionId);
        if (!entry || !entry.session.provides.includes("invoke") || this.invokes.size >= this.limits.maxPendingInvokes
            || !this.client.tracker.tryEnterInvocation()) {
            this.completedRequests.add(message.requestId);
            this.write(writeMessage({ type: "result", requestId: message.requestId, outcome: "Refused" }));
            return;
        }
        const call = {
            requestId: message.requestId,
            entry,
            invocation: Object.freeze({ requestId: message.requestId, session: entry.session }),
            controller: new AbortController(),
            ended: deferred(),
            isEnded: false,
            settled: false,
            hostCancelled: false,
            sessionStopped: false,
            deadline: null,
        };
        this.invokes.set(message.requestId, call);
        const deadline = Math.max(1000, this.limits.invokeTimeoutMs - 3000);
        call.deadline = this.clock.setTimeout(() => this.onDeadline(call), deadline);
        setImmediate(() => this.runInvocation(call));
    }

    async runInvocation(call) {
        const handler = this.client.handler;
        const signal = call.controller.signal;
        let value;
        let fault = null;
        let cancelled = false;
        try {
            value = handler && typeof handler.invoke === "function" ? await handler.invoke(call.invocation, signal) : "Unsupported";
        } catch (error) {
            if (isAbortCompletion(error, signal)) cancelled = true;
            else fault = error === undefined ? new Error("The invoke handler threw undefined.") : error;
        } finally {
            this.clock.clearTimeout(call.deadline);
            call.isEnded = true;
            call.ended.resolve();
            this.client.tracker.exitInvocation();
            if (this.observer && this.observer.invocationHandlerEnded) this.observer.invocationHandlerEnded(call.requestId);
        }
        const normalized = fault === null && !cancelled ? normalizeResult(value) : null;
        const kind = fault !== null ? "Exception" : !cancelled && normalized === null ? "InvalidResult" : null;
        if (!call.settled) {
            this.settle(call);
            if (!this.closing && !call.hostCancelled) {
                let answer = null;
                if (kind !== null) answer = { type: "result", requestId: call.requestId, outcome: "Failed" };
                else if (cancelled) answer = call.sessionStopped ? { type: "result", requestId: call.requestId, outcome: "Refused" } : null;
                else answer = { type: "result", requestId: call.requestId, ...normalized };
                if (answer) this.write(writeMessage(answer));
            }
        }
        if (kind !== null) {
            this.client.raiseHandlerFaulted(kind, call.entry.session.contributionId, call.entry.session.id, call.requestId, fault === null ? undefined : fault);
        }
    }

    onDeadline(call) {
        if (call.settled) return;
        this.settle(call);
        if (!this.closing) this.write(writeMessage({ type: "result", requestId: call.requestId, outcome: "Failed" }));
        if (this.observer && this.observer.invocationDeadlineReached) this.observer.invocationDeadlineReached(call.requestId);
        this.cancelAndWatch(call, call.entry.session.contributionId, call.entry.session.id, call.requestId);
    }

    cancel(requestId) {
        const call = this.invokes.get(requestId);
        if (!call || call.settled) return;
        call.hostCancelled = true;
        this.settle(call);
        this.cancelAndWatch(call, call.entry.session.contributionId, call.entry.session.id, requestId);
    }

    settle(call) {
        call.settled = true;
        this.invokes.delete(call.requestId);
        this.completedRequests.add(call.requestId);
    }

    // ---------------------------------------------------------------- faces (contract §7.7, §9.2)

    publish(entry, command) {
        if (this.closing || this.sessions.get(entry.session.id) !== entry || entry.stopped) {
            if (this.observer && this.observer.publishedAfterEnd) this.observer.publishedAfterEnd(entry.session.id);
            return "SessionEnded";
        }
        this.stopRenewal(entry);
        this.enqueue(entry, command);
        if (command.kind === "set" && command.renew) this.startRenewal(entry, command);
        this.pump();
        return "Accepted";
    }

    /** Makes `command` the session's one pending face command. */
    enqueue(entry, command) {
        let message;
        if (command.kind === "set") message = { type: "setFace", sessionId: entry.session.id, face: command.face };
        else if (command.kind === "clear") message = { type: "clearFace", sessionId: entry.session.id };
        else message = { type: "fail", sessionId: entry.session.id, failure: command.failure };
        entry.pending = writeMessage(message);
        if (!entry.queued) {
            entry.queued = true;
            this.facePending.push(entry);
        }
    }

    startRenewal(entry, command) {
        const generation = entry.generation;
        const interval = command.face.goodForSeconds * 800;
        const publishedAt = this.clock.now();
        const tick = () => {
            if (entry.generation !== generation) return;
            if (!this.isCurrent(entry) || !entry.handlerRunning || entry.controller.signal.aborted
                || this.clock.now() - publishedAt > MAX_RENEWAL_MS) {
                this.stopRenewal(entry);
                return;
            }
            this.enqueue(entry, command);
            entry.renewal = this.clock.setTimeout(tick, interval);
            this.pump();
        };
        entry.renewal = this.clock.setTimeout(tick, interval);
    }

    /** Ends any renewal and makes every older renewal timer a no-op. */
    stopRenewal(entry) {
        entry.generation++;
        if (entry.renewal !== null) this.clock.clearTimeout(entry.renewal);
        entry.renewal = null;
    }

    /** Writes the face frames the pacing allows, and schedules itself for the rest. */
    pump() {
        if (this.closing || !this.connected || !this.facePacing) return;
        while (this.facePending.length > 0) {
            const now = this.clock.now();
            if (this.faceHoldUntil > now) {
                this.schedulePump(this.faceHoldUntil - now);
                return;
            }
            let wait = null;
            let index = -1;
            for (let i = 0; i < this.facePending.length; i++) {
                const entry = this.facePending[i];
                if (entry.nextFaceAt > now) {
                    const until = entry.nextFaceAt - now;
                    wait = wait === null ? until : Math.min(wait, until);
                    continue;
                }
                index = i;
                break;
            }
            if (index < 0) {
                if (wait !== null) this.schedulePump(wait);
                return;
            }
            const entry = this.facePending[index];
            const bytes = entry.pending;
            if (!this.facePacing.tryTake(TokenBucket.costOf(bytes.length))) {
                this.schedulePump(this.facePacing.retryAfterMs);
                return;
            }
            this.facePending.splice(index, 1);
            entry.queued = false;
            entry.pending = null;
            entry.nextFaceAt = now + this.faceSpacingMs;
            this.write(bytes);
        }
    }

    schedulePump(delayMs) {
        const at = this.clock.now() + Math.max(0, delayMs);
        if (this.timers.pump !== null && this.timers.pumpAt <= at) return;
        if (this.timers.pump !== null) this.clock.clearTimeout(this.timers.pump);
        this.timers.pumpAt = at;
        this.timers.pump = this.clock.setTimeout(() => {
            this.timers.pump = null;
            this.timers.pumpAt = Infinity;
            this.pump();
        }, Math.max(1, Math.ceil(delayMs)));
    }

    // ---------------------------------------------------------------- liveness (contract §7.9)

    resetPingTimer() {
        if (this.closing) return;
        if (this.timers.ping !== null) this.clock.clearTimeout(this.timers.ping);
        this.timers.ping = this.clock.setTimeout(() => this.onPingDue(), this.limits.pingIntervalMs);
    }

    hostPing(id) {
        const now = this.clock.now();
        if (this.hostPinged && now - this.lastHostPing < this.readyPingIntervalMs / 2) {
            this.close("protocol.unexpected", true);
            return;
        }
        this.hostPinged = true;
        this.lastHostPing = now;
        // The pong is handed to the transport at once, so it is never unsent when the next ping arrives.
        this.write(writeMessage({ type: "pong", id }));
    }

    hostPong(id) {
        if (this.outstandingPing !== 0 && id === this.outstandingPing) {
            this.outstandingPing = 0;
            if (this.timers.pong !== null) this.clock.clearTimeout(this.timers.pong);
            this.timers.pong = null;
        }
        // Otherwise protocol.pong-unknown: ignored.
    }

    onPingDue() {
        this.timers.ping = null;
        if (this.closing || this.outstandingPing !== 0) return;
        this.nextPingId = this.nextPingId === 2147483647 ? 1 : this.nextPingId + 1;
        const id = this.nextPingId;
        this.outstandingPing = id;
        this.write(writeMessage({ type: "ping", id }));
        this.timers.pong = this.clock.setTimeout(() => {
            if (this.outstandingPing === id) this.close("protocol.ping-timeout", true);
        }, this.limits.pongTimeoutMs);
    }

    hostError(error) {
        if (reasonInfo(error.code).disposition === "Advisory") {
            if (typeof error.retryAfterMs === "number") {
                this.faceHoldUntil = this.clock.now() + error.retryAfterMs;
                this.pump();
            }
            return;
        }
        this.recordPeerError(error);
        this.close(error.code, false);
    }

    recordPeerError(error) {
        this.retryAfterMs = error.retryAfterMs;
        this.peerMessage = error.message;
    }

    // ---------------------------------------------------------------- writing

    /** Writes one frame body; a write the transport has not taken within 5 seconds closes with frame.write-timeout. */
    write(body) {
        if (this.socket.destroyed || this.socket.writableEnded) return;
        const frame = Buffer.alloc(4 + body.length);
        frame.writeUInt32LE(body.length, 0);
        body.copy(frame, 4);
        const timer = this.clock.setTimeout(() => this.close("frame.write-timeout", false), WRITE_TIMEOUT_MS);
        try {
            this.socket.write(frame, () => this.clock.clearTimeout(timer));
        } catch {
            this.clock.clearTimeout(timer);
            this.close(undefined, false);
        }
    }

    // ---------------------------------------------------------------- closing

    /** Closes after writing `body` (an error frame): at most one second for it to go out. */
    closeAfter(reason, body) {
        if (this.closing) return;
        this.beginClose(reason);
        if (this.socket.destroyed || this.socket.writableEnded) {
            this.socket.destroy();
            return;
        }
        const frame = Buffer.alloc(4 + body.length);
        frame.writeUInt32LE(body.length, 0);
        body.copy(frame, 4);
        const bound = this.clock.setTimeout(() => this.socket.destroy(), ERROR_FLUSH_TIMEOUT_MS);
        try {
            this.socket.end(frame, () => {
                this.clock.clearTimeout(bound);
                this.socket.destroy();
            });
        } catch {
            this.clock.clearTimeout(bound);
            this.socket.destroy();
        }
    }

    /**
     * Closes the connection with `reason`: retires every session and invocation and, when
     * `sendError` and the code goes on the wire, writes an error frame first. Only the first call counts.
     */
    close(reason, sendError) {
        if (this.closing) return;
        if (sendError && this.connected && reason !== undefined && reasonInfo(reason).disposition !== "Local") {
            this.closeAfter(reason, writeMessage({ type: "error", code: reason }));
            return;
        }
        this.beginClose(reason);
        this.socket.destroy();
    }

    beginClose(reason) {
        this.closing = true;
        this.reason = reason;
        this.reader.close();
        for (const name of ["handshake", "ping", "pong", "pump"]) {
            if (this.timers[name] !== null) this.clock.clearTimeout(this.timers[name]);
            this.timers[name] = null;
        }
        const sessions = [...this.sessions.values()];
        this.sessions.clear();
        for (const entry of sessions) this.retire(entry);
        const calls = [...this.invokes.values()];
        this.invokes.clear();
        for (const call of calls) call.settled = true;
        this.facePending.length = 0;
        for (const entry of sessions) this.cancelAndWatch(entry, entry.session.contributionId, entry.session.id, undefined);
        for (const call of calls) this.cancelAndWatch(call, call.entry.session.contributionId, call.entry.session.id, call.requestId);
    }
}

module.exports = { Connection, ReplayWindow };
