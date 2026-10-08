// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/** The named-pipe transport against a scripted host on a real Windows pipe. */

const test = require("node:test");
const assert = require("node:assert/strict");
const net = require("node:net");
const { randomBytes } = require("node:crypto");
const { once } = require("node:events");
const { countdown, until } = require("./support.js");
const { CompanionClient, readPairing, Outcome } = require("../index.js");
const wire = require("../wire.js");

const windows = { skip: process.platform !== "win32" ? "The companion transport is a Windows named pipe." : false, timeout: 20000 };

function pairingFor(registrationId, secret) {
    const pipeName = wire.createPipeName("example-host", "test", randomBytes(8).toString("hex"), registrationId);
    const text = JSON.stringify({ pairingVersion: 3, mode: "Persistent", hostId: "example-host", pipeName, registrationId, extensionId: "example.countdown", secret: secret.toString("base64") });
    return readPairing(Buffer.from(text), { expectedExtensionId: "example.countdown", manifestHosts: ["example-host"] }).pairing;
}

/** A minimal protocol-3 host on a real pipe. */
async function pipeHost(pipeName, secret) {
    const events = [];
    const sockets = new Set();
    const server = net.createServer((socket) => {
        sockets.add(socket);
        socket.on("error", () => {});
        socket.on("close", () => sockets.delete(socket));
        let phase = "Handshake";
        let transcript;
        const send = (message) => socket.write(wire.encodeFrame(message));
        const reader = new wire.FrameReader({
            maxFrameBytes: 65536,
            onError: () => socket.destroy(),
            onFrame: (body) => {
                const read = wire.readMessage(body, "Companion", phase);
                if (!read.message) {
                    socket.destroy();
                    return;
                }
                const message = read.message;
                events.push(message.type);
                if (message.type === "hello") {
                    transcript = {
                        hostId: "example-host", hostVersion: "1.0.0", registrationId: message.registrationId, clientNonce: message.clientNonce,
                        serverNonce: wire.newNonce(), version: 3, minVersion: message.minVersion, maxVersion: message.maxVersion,
                        clientCapabilities: message.capabilities, hostCapabilities: [], manifestHash: message.manifestHash,
                    };
                    send({ type: "challenge", serverNonce: transcript.serverNonce, version: 3, capabilities: [], host: { id: "example-host", version: "1.0.0" }, proof: wire.computeProof(secret, transcript, "server") });
                } else if (message.type === "authenticate") {
                    if (!wire.verifyProof(secret, message.proof, transcript, "client")) {
                        send({ type: "error", code: "auth.proof-invalid" });
                        socket.end();
                        return;
                    }
                    phase = "Authenticated";
                    send({ type: "ready", version: 3, capabilities: [], host: { id: "example-host", version: "1.0.0" }, uiLanguage: "en-US", limits: wire.protocol3Limits });
                    send({ type: "startSession", sessionId: "0f0e0d0c0b0a09080706050403020100", contributionId: "example.countdown/timer", settings: { mode: "start", duration: "one-minute" } });
                    send({ type: "invoke", requestId: "00112233445566778899aabbccddeeff", sessionId: "0f0e0d0c0b0a09080706050403020100" });
                } else if (message.type === "result") {
                    events.push("result:" + message.outcome);
                }
            },
        });
        socket.on("data", (chunk) => reader.push(chunk));
    });
    server.listen("\\\\.\\pipe\\" + pipeName);
    await once(server, "listening");
    return {
        events,
        close: async () => {
            for (const socket of sockets) socket.destroy();
            await new Promise((resolve) => server.close(resolve));
        },
    };
}

test("a missing pipe is host.not-running at once", windows, async () => {
    const pairing = pairingFor(randomBytes(16).toString("hex"), randomBytes(32));
    const client = new CompanionClient({ pairing, manifest: countdown(), handler: {}, retry: { initialMs: 50, maxMs: 100, hostAbsentMaxMs: 50 } });
    const statuses = [];
    client.on("status", (status) => statuses.push(status));
    const stop = new AbortController();
    const running = client.run(stop.signal);
    try {
        await until(() => statuses.some((s) => s.state === "Waiting"), "waiting", 5000);
        const waiting = statuses.find((s) => s.state === "Waiting");
        assert.equal(waiting.reason, "host.not-running");
        assert.equal(waiting.serverVerified, false);
    } finally {
        stop.abort();
        await running;
        pairing.dispose();
    }
});

test("the client connects over a real named pipe, authenticates and answers an invocation", windows, async () => {
    const secret = randomBytes(32);
    const pairing = pairingFor(randomBytes(16).toString("hex"), secret);
    const host = await pipeHost(pairing.pipeName, secret);
    const client = new CompanionClient({ pairing, manifest: countdown(), handler: { invoke: async () => Outcome.Done } });
    const statuses = [];
    client.on("status", (status) => statuses.push(status));
    const stop = new AbortController();
    const running = client.run(stop.signal);
    try {
        await until(() => host.events.includes("result:Done"), "the result", 10000);
        const connected = statuses.find((s) => s.state === "Connected");
        assert.equal(connected.serverVerified, true);
        assert.deepEqual(connected.host, { id: "example-host", version: "1.0.0" });
        assert.deepEqual(host.events.slice(0, 2), ["hello", "authenticate"]);
    } finally {
        stop.abort();
        await running;
        await host.close();
        pairing.dispose();
    }
});

test("a server that cannot prove the secret is never sent the client's proof", windows, async () => {
    const pairing = pairingFor(randomBytes(16).toString("hex"), randomBytes(32));
    const squatter = await pipeHost(pairing.pipeName, randomBytes(32));
    const client = new CompanionClient({ pairing, manifest: countdown(), handler: {} });
    const statuses = [];
    client.on("status", (status) => statuses.push(status));
    const stop = new AbortController();
    const running = client.run(stop.signal);
    try {
        await until(() => statuses.some((s) => s.state === "Waiting"), "waiting", 10000);
        const waiting = statuses.find((s) => s.state === "Waiting");
        assert.equal(waiting.reason, "auth.server-proof-invalid");
        assert.equal(waiting.serverVerified, false);
        assert.ok(waiting.retryInMs <= 30000);
        assert.deepEqual(squatter.events, ["hello"]);
    } finally {
        stop.abort();
        await running;
        await squatter.close();
        pairing.dispose();
    }
});
