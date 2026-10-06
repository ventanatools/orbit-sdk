// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { once } = require("node:events");
const { randomBytes, createHmac } = require("node:crypto");
const { WebSocket } = require("ws");
const { startBridgeV2 } = require("../companion/bridge-v2.cjs");
const { ACTION_ID } = require("../uxp/wire.js");
const { Inbox, fakeOrbit } = require("./helpers.cjs");

const windows = { skip: process.platform !== "win32", timeout: 10000 };

async function fixture(t) {
    const host = await fakeOrbit();
    const bridge = await startBridgeV2({
        pairing: host.pairing, port: 0, handshakeMs: 600
    });
    const sockets = new Set();
    t.after(async () => {
        for (const socket of sockets) socket.terminate();
        await bridge.close();
        await host.close();
    });
    await host.inbox.next("authenticated");
    function socket(origin) {
        // Match the installed UXP transport alias without making these tests
        // depend on Windows' IPv4/IPv6 localhost preference.
        const url = bridge.bridgeConfig.url.replace("127.0.0.1", "localhost");
        const client = new WebSocket(url, { family: 4, origin });
        client.on("error", () => {});
        sockets.add(client);
        client.on("close", () => sockets.delete(client));
        return client;
    }
    return { host, bridge, socket };
}

async function openPeer(create, origin = "file://") {
    const socket = create(origin), inbox = new Inbox();
    const closed = new Promise(resolve => socket.once("close", resolve));
    socket.on("message", data => inbox.push(JSON.parse(data.toString("utf8"))));
    await once(socket, "open");
    return { socket, inbox, closed };
}

function proof(config, role, clientNonce, serverNonce) {
    // Independent Node implementation checks the real transport transcript.
    return createHmac("sha256", Buffer.from(config.secret, "hex"))
        .update(["Orbit.Photoshop.Bridge.v" + config.protocolVersion,
            role, clientNonce, serverNonce].join("\n"), "utf8").digest("hex");
}

async function challenge(peer, config) {
    const clientNonce = randomBytes(32).toString("hex");
    peer.socket.send(JSON.stringify({ type: "hello", protocolVersion: config.protocolVersion, clientNonce }));
    const response = await peer.inbox.next("challenge");
    assert.ok(response.proof === proof(config, "server", clientNonce, response.serverNonce),
        "the bridge must prove possession of its key before the peer authenticates");
    return proof(config, "client", clientNonce, response.serverNonce);
}

function invocation() {
    const start = {
        type: "startSession", sessionId: "a".repeat(32),
        actionId: ACTION_ID, settings: { mode: "hide" }
    };
    const invoke = { type: "invoke", requestId: "1".repeat(32), actionId: ACTION_ID,
        sessionId: start.sessionId, settings: start.settings };
    return { start, invoke };
}

{
    test("v2 exact file:// origin authenticates over localhost before dispatch", windows, async t => {
        const current = await fixture(t);
        const peer = await openPeer(current.socket);
        const clientProof = await challenge(peer, current.bridge.bridgeConfig);
        peer.socket.send(JSON.stringify({ type: "authenticate", proof: clientProof }));
        assert.deepEqual(await peer.inbox.next("ready"), { type: "ready" });

        const { start, invoke } = invocation();
        current.host.send(start);
        assert.deepEqual(await peer.inbox.next("startSession"), start);
        current.host.send(invoke);
        assert.deepEqual(await peer.inbox.next("invoke"), invoke);
        peer.socket.send(JSON.stringify({ type: "result", requestId: invoke.requestId, outcome: "done" }));
        assert.equal((await current.host.inbox.next("result")).outcome, "done");
    });

    test("v2 foreign web origins and file-origin lookalikes are rejected with 403", windows, async t => {
        const current = await fixture(t);
        for (const origin of ["http://example.com", "https://example.com",
            "https://localhost", "file://elsewhere", "file:///"]) {
            const statusCode = await new Promise((resolve, reject) => {
                const socket = current.socket(origin);
                socket.once("open", () => { socket.terminate(); reject(new Error("Origin unexpectedly admitted")); });
                socket.once("unexpected-response", (_, response) => {
                    response.resume();
                    resolve(response.statusCode);
                    socket.terminate();
                });
            });
            assert.equal(statusCode, 403);
        }
    });

    for (const mode of ["missing", "bad", "timeout"]) {
        test("v2 file:// origin does not bypass " + mode + " client proof", windows, async t => {
            const current = await fixture(t);
            const peer = await openPeer(current.socket);
            await challenge(peer, current.bridge.bridgeConfig);

            // An allowed Origin alone must not receive sessions or invocations.
            const { start, invoke } = invocation();
            current.host.send(start);
            current.host.send(invoke);
            assert.equal((await current.host.inbox.next("result")).outcome, "refused");
            if (mode === "missing")
                peer.socket.send(JSON.stringify({ type: "authenticate" }));
            else if (mode === "bad")
                peer.socket.send(JSON.stringify({ type: "authenticate", proof: "00".repeat(32) }));
            // timeout sends no authentication response.
            await peer.closed;
            assert.equal(peer.socket.readyState, WebSocket.CLOSED);
            assert.equal(peer.inbox.messages.some(message =>
                message.type === "ready" || message.type === "startSession" || message.type === "invoke"), false);
        });
    }
}
