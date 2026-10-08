// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const net = require("node:net");
const { once } = require("node:events");
const { randomBytes } = require("node:crypto");
const { WebSocket } = require("ws");
const constants = require("../uxp/constants.js");
const { startBridge, bridgeProof } = require("../companion/bridge.cjs");
const { bridgeFilePath, writeBridgeFile, removeBridgeFile } = require("../companion/bridge-file.cjs");
const { bridgeAndHost, openPeer, delay, until } = require("./helpers.cjs");

async function challenge(peer, config) {
    const clientNonce = randomBytes(32).toString("hex");
    peer.socket.send(JSON.stringify({ type: "hello", bridgeVersion: 3, clientNonce }));
    const response = await peer.inbox.next("challenge");
    assert.equal(response.proof, bridgeProof(config.secret, "server", clientNonce, response.serverNonce),
        "the bridge must prove possession of its key before the peer authenticates");
    return bridgeProof(config.secret, "client", clientNonce, response.serverNonce);
}

async function statusCode(bridge, options) {
    return new Promise((resolve, reject) => {
        const socket = new WebSocket(options.url || bridge.config.url, options.origin === undefined ? {} : { origin: options.origin });
        socket.on("error", () => {});
        socket.once("open", () => {
            socket.terminate();
            reject(new Error("The socket was unexpectedly admitted."));
        });
        socket.once("unexpected-response", (_, response) => {
            response.resume();
            resolve(response.statusCode);
            socket.terminate();
        });
    });
}

test("the exact file:// origin and no origin authenticate; every other origin and path is refused with 403", async (t) => {
    const { bridge } = await bridgeAndHost(t);
    for (const origin of ["file://", undefined]) {
        const peer = await openPeer(bridge, origin);
        const proof = await challenge(peer, bridge.config);
        peer.socket.send(JSON.stringify({ type: "authenticate", proof }));
        assert.deepEqual(await peer.inbox.next("ready"), { type: "ready" });
        peer.socket.terminate();
        await peer.closed;
        await until(() => !bridge.photoshopConnected, "the panel to leave");
    }
    for (const origin of ["null", "http://example.com", "https://localhost", "file://elsewhere", "file:///"]) {
        assert.equal(await statusCode(bridge, { origin }), 403, origin);
    }
    assert.equal(await statusCode(bridge, { origin: "file://", url: bridge.config.url.replace(constants.BRIDGE_PATH, "/other") }), 403);
});

for (const mode of ["missing", "bad", "timeout"]) {
    test(`an allowed origin does not bypass a ${mode} client proof`, async (t) => {
        const { bridge, host } = await bridgeAndHost(t, { handshakeMs: 300 });
        const peer = await openPeer(bridge);
        await challenge(peer, bridge.config);
        const session = await host.startSession(constants.TOGGLE_ID, { mode: "hide" });
        assert.deepEqual(await host.invoke(session), { kind: "Failed", failure: "AppUnavailable" });
        if (mode === "missing") peer.socket.send(JSON.stringify({ type: "authenticate" }));
        else if (mode === "bad") peer.socket.send(JSON.stringify({ type: "authenticate", proof: "00".repeat(32) }));
        await peer.closed;
        assert.equal(peer.inbox.messages.some((m) => m.type === "ready" || m.type === "startSession" || m.type === "invoke"), false);
        assert.equal(bridge.photoshopConnected, false);
    });
}

test("a socket must say hello within one second, and sockets that have not authenticated are counted apart", async (t) => {
    const { bridge } = await bridgeAndHost(t, { helloMs: 150, maxPending: 2 });
    const silent = await openPeer(bridge);
    await silent.closed;
    const authenticated = await openPeer(bridge);
    authenticated.socket.send(JSON.stringify({ type: "authenticate", proof: "" }));
    await authenticated.closed;
    const panel = await openPeer(bridge);
    const proof = await challenge(panel, bridge.config);
    panel.socket.send(JSON.stringify({ type: "authenticate", proof }));
    await panel.inbox.next("ready");
    const first = await openPeer(bridge);
    const second = await openPeer(bridge);
    assert.equal(await statusCode(bridge, { origin: "file://" }), 403);
    first.socket.terminate();
    second.socket.terminate();
    await Promise.all([first.closed, second.closed]);
    await delay(20);
    const third = await openPeer(bridge);
    third.socket.terminate();
});

test("unauthenticated, oversized, duplicate-member, binary and protocol-2 messages close the socket", async (t) => {
    const { bridge } = await bridgeAndHost(t, { handshakeMs: 200 });
    for (const payload of [null, "x".repeat(65537), "{\"type\":\"hello\",\"type\":\"hello\"}", Buffer.from([1, 2, 3]),
        JSON.stringify({ type: "hello", bridgeVersion: 2, clientNonce: "00".repeat(32) })]) {
        const socket = new WebSocket(bridge.config.url, { origin: "file://" });
        socket.on("error", () => {});
        const closed = once(socket, "close");
        await once(socket, "open");
        if (payload) socket.send(payload, { binary: Buffer.isBuffer(payload) });
        await closed;
        assert.equal(socket.readyState, WebSocket.CLOSED);
    }
});

test("a port that is in use gives a specific error", async () => {
    const holder = net.createServer();
    holder.listen(0, constants.LISTEN_HOST);
    await once(holder, "listening");
    try {
        await assert.rejects(startBridge({ port: holder.address().port }), { code: "EADDRINUSE" });
    } finally {
        await new Promise((resolve) => holder.close(resolve));
    }
});

test("the bridge file lives in the per-user samples folder, is replaced atomically and deleted on exit", async () => {
    assert.equal(bridgeFilePath({ LOCALAPPDATA: path.join("C:", "Local") }), path.join("C:", "Local", "VentanaTools", "Samples", "Photoshop", "bridge.json"));
    const folder = fs.mkdtempSync(path.join(os.tmpdir(), "photoshop-bridge-"));
    try {
        const file = bridgeFilePath({ LOCALAPPDATA: folder });
        const first = { bridgeVersion: 3, url: constants.BRIDGE_URL, secret: "11".repeat(32) };
        await writeBridgeFile(file, first);
        await writeBridgeFile(file, { ...first, secret: "22".repeat(32) });
        assert.equal(JSON.parse(fs.readFileSync(file, "utf8")).secret, "22".repeat(32));
        assert.deepEqual(fs.readdirSync(path.dirname(file)), ["bridge.json"]);
        await removeBridgeFile(file);
        await removeBridgeFile(file);
        assert.equal(fs.existsSync(file), false);
    } finally {
        fs.rmSync(folder, { recursive: true, force: true });
    }
});

test("each launch has a fresh bridge key that is never the pairing secret", async (t) => {
    const first = await startBridge({ port: 0 });
    const second = await startBridge({ port: 0 });
    t.after(async () => {
        await first.close();
        await second.close();
    });
    assert.match(first.config.secret, /^[0-9a-f]{64}$/);
    assert.notEqual(first.config.secret, second.config.secret);
    assert.deepEqual(Object.keys(first.config), ["bridgeVersion", "url", "secret"]);
});
