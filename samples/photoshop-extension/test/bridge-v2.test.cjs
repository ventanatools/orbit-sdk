// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { once } = require("node:events");
const { WebSocket } = require("ws");
const { startBridgeV2 } = require("../companion/bridge-v2.cjs");
const { connectSessionV2 } = require("../uxp/client-v2.js");
const { createPlatform } = require("../uxp/platform.js");
const { ACTION_ID, STATUS_ID } = require("../uxp/wire-v2.js");
const { Inbox, fakeOrbit, fakePhotoshop, delay } = require("./helpers.cjs");
const windows = { skip: process.platform !== "win32", timeout: 10000 };

async function fixture(t, hostOptions = {}, bridgeOptions = {}) {
    const host = await fakeOrbit(hostOptions);
    const bridge = await startBridgeV2({ pairing: host.pairing, port: 0, ...bridgeOptions });
    t.after(async () => { await bridge.close(); await host.close(); });
    return { host, bridge };
}

function attachPhotoshop(t, bridge, model, config = bridge.bridgeConfig) {
    const socket = new WebSocket(bridge.bridgeConfig.url);
    const statuses = new Inbox(), inbound = new Inbox();
    const platform = createPlatform(model.photoshop);
    const session = connectSessionV2(socket, config, platform, status => {
        statuses.push({ type: status.startsWith("Connected.") ? "connected" : "disconnected" });
    });
    socket.on("message", data => inbound.push(JSON.parse(data.toString("utf8"))));
    t.after(() => session.close());
    return { socket, session, statuses, inbound };
}

function demand(host, actionId = ACTION_ID, settings = { mode: "toggle" }, id = "a") {
    const session = { type: "startSession", sessionId: id.repeat(32), actionId, settings };
    host.send(session);
    return { type: "invoke", requestId: id.repeat(32), sessionId: session.sessionId, actionId, settings };
}

async function until(predicate) {
    const deadline = Date.now() + 3000;
    while (!predicate()) { if (Date.now() >= deadline) throw new Error("test.timeout"); await delay(5); }
}

function holdModal(model) {
    let release;
    model.photoshop.core.executeAsModal = callback => new Promise(resolve => {
        release = async () => { await callback(model.context); resolve(); };
    });
    return { entered: () => until(() => !!release), release: () => release() };
}

test("missing Photoshop refuses a configured action and passive sessions cannot invoke", windows, async t => {
    const { host } = await fixture(t);
    await host.inbox.next("authenticated");
    host.send(demand(host));
    assert.equal((await host.inbox.next("result")).outcome, "refused");
    host.send(demand(host, STATUS_ID, { display: "selection" }, "b"));
    assert.equal((await host.inbox.next("result")).outcome, "unsupported");
});

test("companion rejects an impostor Orbit server before sending its client proof", windows, async t => {
    const { host, bridge } = await fixture(t, { badProof: true }, { reconnectMs: 10000 });
    await host.inbox.next("disconnected");
    assert.equal(bridge.orbitReady, false);
    assert.equal(host.inbox.messages.some(message => message.type === "authenticated"), false);
});

test("UXP rejects an impostor bridge proof and never invokes", windows, async t => {
    const { host, bridge } = await fixture(t);
    const model = fakePhotoshop();
    const client = attachPhotoshop(t, bridge, model, { ...bridge.bridgeConfig, secret: "00".repeat(32) });
    await client.statuses.next("disconnected");
    await host.inbox.next("authenticated");
    host.send(demand(host));
    assert.equal((await host.inbox.next("result")).outcome, "refused");
    assert.equal(model.mutations, 0);
});

test("Orbit cancellation reaches a pending modal command and prevents mutation", windows, async t => {
    const { host, bridge } = await fixture(t);
    const model = fakePhotoshop(), modal = holdModal(model);
    const client = attachPhotoshop(t, bridge, model);
    await Promise.all([host.inbox.next("authenticated"), client.statuses.next("connected")]);
    const message = demand(host);
    host.send(message);
    await modal.entered();
    host.send({ type: "cancel", requestId: message.requestId });
    await client.inbound.next("cancel");
    await modal.release();
    await delay(30);
    assert.equal(model.mutations, 0);
    assert.equal(host.inbox.messages.some(row => row.type === "result"), false);
});

test("disconnect retires in-flight work and reconnect never replays it", windows, async t => {
    const { host, bridge } = await fixture(t, {}, { reconnectMs: 20 });
    const model = fakePhotoshop(), modal = holdModal(model);
    const client = attachPhotoshop(t, bridge, model);
    await Promise.all([host.inbox.next("authenticated"), client.statuses.next("connected")]);
    host.send(demand(host));
    await modal.entered();
    host.disconnect();
    await client.inbound.next("stopSession");
    await host.inbox.next("authenticated");
    await modal.release();
    await delay(30);
    assert.equal(model.mutations, 0);
    assert.equal(host.connections, 2);
    assert.equal(host.inbox.messages.some(row => row.type === "result"), false);
});

test("duplicate invoke disconnects instead of toggling twice", windows, async t => {
    const { host, bridge } = await fixture(t, {}, { reconnectMs: 10000 });
    const model = fakePhotoshop();
    const client = attachPhotoshop(t, bridge, model);
    await Promise.all([host.inbox.next("authenticated"), client.statuses.next("connected")]);
    const message = demand(host);
    host.send(message);
    assert.equal((await host.inbox.next("result")).outcome, "done");
    host.send(message);
    await host.inbox.next("disconnected");
    assert.equal(model.mutations, 1);
});

test("bridge invocation timeout cancels without a late mutation", windows, async t => {
    const { host, bridge } = await fixture(t, {}, { invokeMs: 80 });
    const model = fakePhotoshop(), modal = holdModal(model);
    const client = attachPhotoshop(t, bridge, model);
    await Promise.all([host.inbox.next("authenticated"), client.statuses.next("connected")]);
    host.send(demand(host));
    await modal.entered();
    assert.equal((await host.inbox.next("result")).outcome, "refused");
    await client.inbound.next("cancel");
    await modal.release();
    assert.equal(model.mutations, 0);
});

test("unauthenticated, oversized, duplicate-field and v1 WebSocket frames close", windows, async t => {
    const { bridge } = await fixture(t, {}, { handshakeMs: 60 });
    for (const payload of [null, "x".repeat(65537), '{"type":"hello","type":"hello"}',
        JSON.stringify({ type: "hello", protocolVersion: 1, clientNonce: "00".repeat(32) })]) {
        const socket = new WebSocket(bridge.bridgeConfig.url);
        socket.on("error", () => {});
        const closed = once(socket, "close");
        await once(socket, "open");
        if (payload) socket.send(payload);
        await closed;
        assert.equal(socket.readyState, WebSocket.CLOSED);
    }
});

for (const scenario of ["unscoped invocation", "undeclared action"]) {
    test("host rejects " + scenario + " instead of dispatching it", windows, async t => {
        const { host } = await fixture(t, {}, { reconnectMs: 10000 });
        await host.inbox.next("authenticated");
        const message = { type: "invoke", requestId: "1".repeat(32), actionId: ACTION_ID };
        if (scenario === "undeclared action") {
            message.actionId = "example.photoshop/arbitrary-code";
            message.sessionId = "a".repeat(32);
            message.settings = { mode: "toggle" };
        }
        host.send(message);
        await host.inbox.next("disconnected");
        assert.equal(host.inbox.messages.some(row => row.type === "result"), false);
    });
}
