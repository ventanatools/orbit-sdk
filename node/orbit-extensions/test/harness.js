// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * A client harness: a real CompanionClient whose transport hands each connection to a scripted
 * host peer over an in-memory duplex stream, on a manual clock.
 */

const { CompanionClient, readPairing } = require("../index.js");
const wire = require("../wire.js");
const { ManualClock } = require("../testing.js");
const { kInternals } = require("../lib/client/client.js");
const { duplexPair } = require("../lib/client/transport.js");
const { fixtureBytes, countdown, settle } = require("./support.js");

const SECRET = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
const REGISTRATION = "00112233445566778899aabbccddeeff";
const HOST = "example-host";

function newId() {
    return require("node:crypto").randomBytes(16).toString("hex");
}

/** The host side of one connection, driven by a test. */
class ScriptedPeer {
    constructor(socket, clock) {
        this.socket = socket;
        this.clock = clock;
        this.frames = [];
        this.waiters = [];
        this.ended = false;
        this.pingsReceived = [];
        this.autoPong = true;
        this.hello = null;
        this.transcript = null;
        this.reader = new wire.FrameReader({
            maxFrameBytes: 65536,
            onFrame: (body) => this.onFrame(body),
            onError: () => {},
        });
        socket.on("data", (chunk) => this.reader.push(chunk));
        socket.on("error", () => {});
        socket.on("end", () => this.onEnd());
        socket.on("close", () => this.onEnd());
    }

    onFrame(body) {
        this.frames.push(body);
        this.wake();
    }

    onEnd() {
        if (this.ended) return;
        this.ended = true;
        this.wake();
    }

    wake() {
        const waiters = this.waiters;
        this.waiters = [];
        for (const resolve of waiters) resolve();
    }

    async nextBody(timeoutMs = 3000) {
        const deadline = Date.now() + timeoutMs;
        while (this.frames.length === 0) {
            if (this.ended) return null;
            if (Date.now() > deadline) throw new Error("No frame from the companion.");
            await Promise.race([new Promise((resolve) => this.waiters.push(resolve)), new Promise((resolve) => setTimeout(resolve, 20))]);
        }
        return this.frames.shift();
    }

    /** The next companion message for `phase`; companion pings are answered (and counted) unless `type` is ping. */
    async read(phase = "Authenticated", type) {
        while (true) {
            const body = await this.nextBody();
            if (body === null) return null;
            const read = wire.readMessage(body, "Companion", phase);
            if (!read.message) throw new Error("The companion sent a frame the host would refuse: " + (read.violation || read.ignored));
            if (read.message.type === "ping" && type !== "ping") {
                this.pingsReceived.push(read.message.id);
                if (this.autoPong) this.send({ type: "pong", id: read.message.id });
                continue;
            }
            if (type && read.message.type !== type) throw new Error("Expected " + type + " but read " + read.message.type + ".");
            return read.message;
        }
    }

    /** Reads until the connection ends; returns the code of the last error frame, if any. */
    async readClose() {
        let code;
        while (true) {
            const body = await this.nextBody();
            if (body === null) return code;
            const read = wire.readMessage(body, "Companion", "Authenticated");
            const handshake = read.violation ? wire.readMessage(body, "Companion", "Handshake") : read;
            const message = handshake.message;
            if (message && message.type === "error") code = message.code;
        }
    }

    async readHello() {
        this.hello = await this.read("Handshake", "hello");
        return this.hello;
    }

    buildChallenge({ version = 3, hostId = HOST, hostVersion = "2026.10.1", capabilities = [], secret = SECRET } = {}) {
        this.transcript = {
            hostId,
            hostVersion,
            registrationId: this.hello.registrationId,
            clientNonce: this.hello.clientNonce,
            serverNonce: wire.newNonce(),
            version,
            minVersion: this.hello.minVersion,
            maxVersion: this.hello.maxVersion,
            clientCapabilities: this.hello.capabilities,
            hostCapabilities: capabilities,
            manifestHash: this.hello.manifestHash,
        };
        return {
            type: "challenge",
            serverNonce: this.transcript.serverNonce,
            version,
            capabilities,
            host: { id: hostId, version: hostVersion },
            proof: wire.computeProof(secret, this.transcript, "server"),
        };
    }

    static buildReady({ version = 3, hostId = HOST, hostVersion = "2026.10.1", capabilities = [], limits = wire.protocol3Limits, uiLanguage = "en-US" } = {}) {
        return { type: "ready", version, capabilities, host: { id: hostId, version: hostVersion }, uiLanguage, limits };
    }

    send(message) {
        this.sendBody(wire.writeMessage(message));
    }

    sendJson(text) {
        this.sendBody(Buffer.from(text, "utf8"));
    }

    sendBody(body) {
        if (this.socket.destroyed || this.socket.writableEnded) return;
        const header = Buffer.alloc(4);
        header.writeUInt32LE(body.length, 0);
        this.socket.write(Buffer.concat([header, body]));
    }

    /** The whole handshake: hello, a valid challenge, authenticate, ready. */
    async connect(script = {}) {
        await this.readHello();
        this.send(this.buildChallenge(script));
        const authenticate = await this.read("Handshake", "authenticate");
        if (!wire.verifyProof(SECRET, authenticate.proof, this.transcript, "client")) throw new Error("The companion's proof did not verify.");
        this.send(ScriptedPeer.buildReady(script));
        return authenticate;
    }

    close() {
        this.socket.end();
    }
}

class ClientHarness {
    constructor({ handler = {}, manifest = countdown(), clock = new ManualClock(), retry, random = () => 0.5, failures = [] } = {}) {
        const { pairing } = readPairing(fixtureBytes("pairing/valid-lf.json"), { expectedExtensionId: "example.countdown", manifestHosts: [HOST] });
        this.pairing = pairing;
        this.clock = clock;
        this.client = new CompanionClient({ pairing, manifest, handler, retry });
        this.statuses = [];
        this.faults = [];
        this.peers = [];
        this.taken = 0;
        this.peerWaiters = [];
        this.failures = [...failures];
        this.attempts = 0;
        this.started = [];
        const internals = this.client[kInternals];
        internals.clock = clock;
        internals.random = random;
        internals.observer = { sessionStarted: (id, refusal) => this.started.push({ id, refusal }) };
        internals.transport = {
            connect: async () => {
                this.attempts++;
                if (this.failures.length > 0) return { failure: this.failures.shift() };
                const [companion, host] = duplexPair();
                const peer = new ScriptedPeer(host, clock);
                this.peers.push(peer);
                const waiters = this.peerWaiters;
                this.peerWaiters = [];
                for (const resolve of waiters) resolve();
                return { socket: companion };
            },
        };
        this.client.on("status", (status) => this.statuses.push(status));
        this.client.on("handlerFaulted", (fault) => this.faults.push(fault));
        this.controller = new AbortController();
        this.running = null;
    }

    start() {
        this.running = this.client.run(this.controller.signal);
        this.running.catch(() => {});
        return this;
    }

    /** The next connection's peer, in order. */
    async nextPeer() {
        const index = this.taken++;
        const deadline = Date.now() + 3000;
        while (this.peers.length <= index) {
            if (Date.now() > deadline) throw new Error("The client did not connect.");
            await Promise.race([new Promise((resolve) => this.peerWaiters.push(resolve)), new Promise((resolve) => setTimeout(resolve, 20))]);
        }
        return this.peers[index];
    }

    /** A connected peer after the whole handshake. */
    async connected(script) {
        const peer = await this.nextPeer();
        await peer.connect(script);
        await this.waitForState("Connected");
        return peer;
    }

    async waitForStatus(predicate, what = "status", timeoutMs = 3000) {
        const deadline = Date.now() + timeoutMs;
        let seen = 0;
        while (true) {
            for (; seen < this.statuses.length; seen++) {
                if (predicate(this.statuses[seen])) return this.statuses[seen];
            }
            if (Date.now() > deadline) throw new Error("Timed out waiting for " + what + "; saw " + JSON.stringify(this.statuses.map((s) => [s.state, s.reason])));
            await new Promise((resolve) => setTimeout(resolve, 2));
        }
    }

    waitForState(state) {
        return this.waitForStatus((status) => status.state === state, state);
    }

    async stop() {
        this.controller.abort();
        if (this.running) await this.running;
        this.pairing.dispose();
    }
}

module.exports = { ClientHarness, ScriptedPeer, SECRET, REGISTRATION, HOST, newId, settle };
