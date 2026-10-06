// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

// The whole path: a test host running the real SDK client, the companion's bridge, a real
// WebSocket, the UXP client and platform, and an in-memory Photoshop document.

const test = require("node:test");
const assert = require("node:assert/strict");
const constants = require("../uxp/constants.js");
const { bridgeAndHost, attachPanel, fakePhotoshop, until, delay } = require("./helpers.cjs");

function holdModal(model) {
    let release;
    model.photoshop.core.executeAsModal = (callback) => new Promise((resolve) => {
        release = async () => {
            await callback(model.context);
            resolve();
        };
    });
    return { entered: () => until(() => !!release, "the modal scope"), release: () => release() };
}

function sentByCompanion(host, type, sessionId) {
    return host.transcript.filter((entry) => entry.from === "Companion" && entry.type === type && (!sessionId || entry.sessionId === sessionId));
}

test("without Photoshop, picks fail as AppUnavailable and faces say the app is unavailable", async (t) => {
    const { host } = await bridgeAndHost(t);
    const toggle = await host.startSession(constants.TOGGLE_ID, { mode: "toggle" });
    const status = await host.startSession(constants.STATUS_ID, { display: "selection" });
    assert.deepEqual(await host.invoke(toggle), { kind: "Failed", failure: "AppUnavailable" });
    await until(() => sentByCompanion(host, "fail", status.sessionId).length === 1, "the status session's failure");
    assert.equal(sentByCompanion(host, "fail", toggle.sessionId).length, 1);
});

test("independent placements: their own settings, live faces on change, one change per pick, and a fresh session after a settings change", async (t) => {
    const { host, bridge } = await bridgeAndHost(t);
    const model = fakePhotoshop();
    const panel = attachPanel(t, bridge, model);
    await panel.statuses.next("connected");
    const hide = await host.startSession(constants.TOGGLE_ID, { mode: "hide" });
    const selection = await host.startSession(constants.STATUS_ID, { display: "selection" });
    assert.equal((await host.waitForFace(selection, 3000)).face.line1, "One");
    const first = await host.waitForFace(hide, 3000);
    assert.equal(first.face.line1, "Shown");
    assert.equal(first.face.state, "On");
    assert.deepEqual(await host.invoke(hide), { kind: "Done" });
    assert.equal(model.layer.visible, false);
    assert.equal(model.mutations, 1);
    const hidden = await host.waitForFace(hide, 3000);
    assert.equal(hidden.face.line1, "Hidden");
    assert.equal(hidden.face.state, "Off");
    assert.equal(hidden.face.detail, "The selected layer is hidden.");
    host.stopSession(hide);
    const show = await host.startSession(constants.TOGGLE_ID, { mode: "show" });
    assert.deepEqual(await host.invoke(show), { kind: "Done" });
    assert.equal(model.layer.visible, true);
    assert.equal(model.mutations, 2);
    assert.deepEqual(await host.invoke(selection), { kind: "Refused" }, "a passive status is never invoked");
});

test("a cancelled pick reaches a pending modal command and prevents the change", async (t) => {
    const { host, bridge } = await bridgeAndHost(t);
    const model = fakePhotoshop();
    const modal = holdModal(model);
    const panel = attachPanel(t, bridge, model);
    await panel.statuses.next("connected");
    const session = await host.startSession(constants.TOGGLE_ID, { mode: "toggle" });
    const invocation = host.startInvoke(session);
    await modal.entered();
    host.cancel(invocation);
    await panel.inbound.next("cancel");
    await modal.release();
    await delay(30);
    assert.deepEqual(await invocation.result, { kind: "Cancelled" });
    assert.equal(model.mutations, 0);
});

test("when Photoshop disconnects, live faces fail as AppUnavailable and a pending pick fails; reconnecting restores sessions without replay", async (t) => {
    const { host, bridge, statuses } = await bridgeAndHost(t);
    const model = fakePhotoshop();
    const modal = holdModal(model);
    const panel = attachPanel(t, bridge, model);
    await panel.statuses.next("connected");
    const session = await host.startSession(constants.TOGGLE_ID, { mode: "toggle" });
    await host.waitForFace(session, 3000);
    const invocation = host.startInvoke(session);
    await modal.entered();
    panel.socket.terminate();
    assert.deepEqual(await invocation.result, { kind: "Failed", failure: "AppUnavailable" });
    await until(() => sentByCompanion(host, "fail", session.sessionId).length === 1, "the AppUnavailable face");
    assert.ok(statuses.includes("Photoshop disconnected; its live state is unavailable."));
    await modal.release();
    const again = fakePhotoshop();
    const second = attachPanel(t, bridge, again);
    await second.statuses.next("connected");
    const restored = await second.inbound.next("startSession");
    assert.equal(restored.sessionId, session.sessionId);
    assert.equal((await host.waitForFace(session, 3000)).face.line1, "Shown");
    assert.equal(model.mutations + again.mutations, 0, "reconnection never replays a pick");
    assert.equal(second.inbound.messages.some((message) => message.type === "invoke"), false);
});

test("a panel that cannot prove the bridge key is closed and never runs a pick", async (t) => {
    const { host, bridge } = await bridgeAndHost(t);
    const model = fakePhotoshop();
    const panel = attachPanel(t, bridge, model, { ...bridge.config, secret: "00".repeat(32) });
    await panel.statuses.next("disconnected");
    assert.equal(panel.lost, true);
    const session = await host.startSession(constants.TOGGLE_ID, { mode: "toggle" });
    assert.deepEqual(await host.invoke(session), { kind: "Failed", failure: "AppUnavailable" });
    assert.equal(model.mutations, 0);
});
