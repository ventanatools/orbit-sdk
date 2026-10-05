"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { createHmac } = require("node:crypto");
const { WebSocket } = require("ws");
const { createPlatform, EVENTS } = require("../uxp/platform.js");
const { parseRecordV2, isFace, ACTION_ID, STATUS_ID } = require("../uxp/wire-v2.js");
const { parsePairing, proofV2 } = require("../companion/protocol-v2.cjs");
const { bridgeProof } = require("../uxp/vendor/bridge-crypto.js");
const { startBridgeV2 } = require("../companion/bridge-v2.cjs");
const { connectSessionV2 } = require("../uxp/client-v2.js");
const { fakeOrbit, fakePhotoshop, delay } = require("./helpers.cjs");
const vector = require("./fixtures/protocol-v2.json");
const start = (id, actionId = ACTION_ID, settings = { mode: "toggle" }) => ({ type: "startSession", sessionId: id.repeat(32), actionId, settings });
const invoke = (id, session) => ({ type: "invoke", requestId: id.repeat(32), sessionId: session.sessionId, actionId: session.actionId, settings: session.settings });

async function until(predicate) {
    const deadline = Date.now() + 3000;
    while (!predicate()) { if (Date.now() > deadline) throw new Error("test.timeout"); await delay(5); }
}

test("v2 nested parser rejects duplicate settings and face members, unsupported arrays and excessive depth", () => {
    for (const text of ['{"settings":{"mode":"show","mode":"hide"}}',
        '{"command":{"$type":"clearFace","\\u0024type":"fail"}}', '{"settings":[]}',
        '{"a":'.repeat(10) + '0' + '}'.repeat(10), '{"type":"ready",}']) assert.throws(() => parseRecordV2(text));
    assert.equal(parseRecordV2('{"settings":{"mode":"hide"}}').settings.mode, "hide");
    assert.equal(isFace({ type: "face", sessionId: "a".repeat(32), command: { $type: "setFace", face: { picture: { $type: "image" } } } }), false);
});

test("v2 authentication has an independent version domain and strict pipe pairing", () => {
    const pairing = { protocolVersion: 2, pipeName: `Orbit.Extensions.v2.dev.0123456789abcdef.${vector.registrationId}`,
        registrationId: vector.registrationId, extensionId: "example.photoshop", secret: vector.secret };
    assert.equal(parsePairing(JSON.stringify(pairing)).protocolVersion, 2);
    assert.throws(() => parsePairing(JSON.stringify({ ...pairing, pipeName: pairing.pipeName.replace(".v2.", ".v1.") })));
    for (const role of ["client", "server"]) {
        const expected = createHmac("sha256", Buffer.from(vector.secret, "base64"))
            .update(["Orbit.Extensions.v2", role, vector.registrationId, vector.clientNonce, vector.serverNonce].join("\n")).digest("base64");
        const actual = proofV2(vector.secret, role, vector.registrationId, vector.clientNonce, vector.serverNonce);
        assert.equal(actual, expected);
        assert.equal(actual, vector[`${role}Proof`]);
        const hex = createHmac("sha256", Buffer.alloc(32)).update(`Orbit.Photoshop.Bridge.v2\n${role}\n${"11".repeat(32)}\n${"22".repeat(32)}`).digest("hex");
        assert.equal(bridgeProof("00".repeat(32), role, "11".repeat(32), "22".repeat(32)), hex);
    }
});

test("independent configured instances invoke their own choice and renew bounded live state", async t => {
    const model = fakePhotoshop(), faces = [];
    let added = 0, removed = 0, callback;
    model.photoshop.action = {
        addNotificationListener: async (events, fn) => { assert.deepEqual(events, EVENTS); added++; callback = fn; },
        removeNotificationListener: async (_, fn) => { assert.equal(fn, callback); removed++; }
    };
    const platform = createPlatform(model.photoshop, { refreshMs: 50, minPushMs: 10 });
    t.after(() => platform.close());
    assert.equal(added, 0);
    const hide = start("a", ACTION_ID, { mode: "hide" }), show = start("b", ACTION_ID, { mode: "show" });
    const status = start("c", STATUS_ID, { display: "selection" });
    for (const row of [hide, show, status]) platform.start(row, value => faces.push(value));
    await until(() => faces.length >= 3);
    assert.equal(added, 1);
    assert.equal(faces.find(value => value.sessionId === status.sessionId).command.face.line1.value, "One");
    assert.ok(faces.every(isFace));
    assert.equal(await platform.invoke(invoke("1", hide), () => false), "done");
    assert.equal(model.layer.visible, false);
    assert.equal(await platform.invoke(invoke("2", show), () => false), "done");
    assert.equal(model.layer.visible, true);
    assert.equal(model.mutations, 2);
    assert.equal(await platform.invoke(invoke("3", status), () => false), "unsupported");
    assert.equal(await platform.invoke({ ...invoke("4", hide), settings: { mode: "show" } }, () => false), "refused");
    const before = faces.length;
    for (let i = 0; i < 100; i++) callback();
    await until(() => faces.length > before);
    assert.ok(faces.length <= before + 3, "one burst is coalesced");
    platform.stop(hide.sessionId); platform.stop(show.sessionId); platform.stop(status.sessionId);
    await until(() => removed === 1);
    const stopped = faces.length;
    await delay(100);
    assert.equal(faces.length, stopped);
});

test("settings replacement cancels an old modal target; a fresh session alone may mutate", async t => {
    const model = fakePhotoshop(), platform = createPlatform(model.photoshop);
    t.after(() => platform.close());
    const old = start("a", ACTION_ID, { mode: "hide" });
    platform.start(old, () => {});
    let release;
    model.photoshop.core.executeAsModal = fn => new Promise(resolve => { release = async () => { await fn(model.context); resolve(); }; });
    const pending = platform.invoke(invoke("1", old), () => false);
    platform.stop(old.sessionId);
    platform.start(start("b", ACTION_ID, { mode: "show" }), () => {});
    await release();
    assert.equal(await pending, "refused");
    assert.equal(model.mutations, 0);
});

test("async event registration is removed when the last session closes before registration finishes", async () => {
    const model = fakePhotoshop();
    let finishAdd, removed = 0;
    model.photoshop.action = { addNotificationListener: () => new Promise(resolve => { finishAdd = resolve; }),
        removeNotificationListener: async () => { removed++; } };
    const platform = createPlatform(model.photoshop);
    platform.start(start("a"), () => {});
    const closing = platform.close();
    finishAdd();
    await closing;
    assert.equal(removed, 1);
});

test("v2 real transport carries independent settings, live faces, invocation and fresh reconnect sessions", { skip: process.platform !== "win32" }, async t => {
    const host = await fakeOrbit({ fragment: true });
    const bridge = await startBridgeV2({ pairing: host.pairing, port: 0, reconnectMs: 30 });
    const model = fakePhotoshop();
    let connected = false;
    const socket = new WebSocket(bridge.bridgeConfig.url);
    const client = connectSessionV2(socket, bridge.bridgeConfig, createPlatform(model.photoshop),
        status => { if (status.startsWith("Connected.")) connected = true; });
    t.after(async () => { client.close(); await bridge.close(); await host.close(); });
    await host.inbox.next("authenticated"); await until(() => connected);
    const hide = start("a", ACTION_ID, { mode: "hide" });
    const status = start("b", STATUS_ID, { display: "selection" });
    host.send(hide); host.send(status);
    await until(() => host.inbox.messages.some(m => m.type === "face" && m.sessionId === status.sessionId && m.command.face?.line1.value === "One"));
    host.send(invoke("1", hide));
    assert.equal((await host.inbox.next("result")).outcome, "done");
    assert.equal(model.layer.visible, false);
    await until(() => host.inbox.messages.some(m => m.type === "face" && m.sessionId === hide.sessionId && m.command.face?.line1.value === "Hidden"));
    host.send({ type: "stopSession", sessionId: hide.sessionId });
    const show = start("c", ACTION_ID, { mode: "show" });
    host.send(show); host.send(invoke("2", show));
    assert.equal((await host.inbox.next("result")).outcome, "done");
    assert.equal(model.layer.visible, true);
    host.disconnect(); await host.inbox.next("authenticated");
    const fresh = start("d", ACTION_ID, { mode: "hide" });
    host.send(fresh);
    await until(() => host.inbox.messages.some(m => m.type === "face" && m.sessionId === fresh.sessionId));
    assert.equal(model.mutations, 2, "reconnection must not replay either command");
    host.send(invoke("3", fresh));
    assert.equal((await host.inbox.next("result")).outcome, "done");
    assert.equal(model.mutations, 3);
});
