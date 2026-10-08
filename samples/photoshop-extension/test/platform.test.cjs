// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const constants = require("../uxp/constants.js");
const { createPlatform, EVENTS, HISTORY_NAME } = require("../uxp/platform.js");
const { isFace } = require("../uxp/wire.js");
const { fakePhotoshop, until, delay } = require("./helpers.cjs");

function start(id, contributionId = constants.TOGGLE_ID, settings = { mode: "toggle" }) {
    return { type: "startSession", sessionId: id.repeat(32), contributionId, settings };
}

function invoke(id, session) {
    return { type: "invoke", requestId: id.repeat(32), sessionId: session.sessionId, contributionId: session.contributionId, settings: session.settings };
}

function harness(t, options) {
    const model = fakePhotoshop(options);
    const platform = createPlatform(model.photoshop);
    const sessions = new Map();
    let sequence = 0;
    t.after(() => platform.close());
    return {
        model,
        close: () => platform.close(),
        retire: (mode) => platform.stop(sessions.get(mode).sessionId),
        run: (mode, canceled = () => false) => {
            if (!sessions.has(mode)) {
                const session = start({ toggle: "a", show: "b", hide: "c" }[mode], constants.TOGGLE_ID, { mode });
                sessions.set(mode, session);
                platform.start(session, () => {});
            }
            const session = sessions.get(mode);
            return platform.invoke({ ...invoke("0", session), requestId: (++sequence).toString(16).padStart(32, "0") }, canceled)
                .then((result) => result.outcome);
        },
    };
}

function holdSuspension(t, model) {
    let entered;
    let release;
    const started = new Promise((resolve) => {
        entered = resolve;
    });
    const gate = new Promise((resolve) => {
        release = resolve;
    });
    const suspend = model.context.hostControl.suspendHistory;
    model.context.hostControl.suspendHistory = async (options) => {
        const token = await suspend(options);
        entered();
        await gate;
        return token;
    };
    t.after(() => release());
    return { started, release };
}

test("one visibility change happens inside a named modal scope and one named history step", async (t) => {
    const { model, run } = harness(t);
    let scopes = 0;
    const execute = model.photoshop.core.executeAsModal;
    model.photoshop.core.executeAsModal = async (callback, options) => {
        assert.equal(options.commandName, HISTORY_NAME);
        scopes++;
        return execute(callback, options);
    };
    assert.equal(await run("toggle"), "Done");
    assert.equal(scopes, 1);
    assert.deepEqual(model.historyEvents.map((event) => event.type), ["suspend", "mutation", "resume"]);
    assert.equal(model.historyEvents[1].suspended, true);
    assert.deepEqual(model.history, [{ documentID: 1, name: "Change selected layer visibility", before: true, after: false }]);
});

for (const scenario of ["cancel", "modal-cancel", "changed-layer", "changed-document", "no-document"]) {
    test(`a ${scenario} while history setup awaits the host refuses and rolls back`, async (t) => {
        const { model, run } = harness(t);
        const gate = holdSuspension(t, model);
        let canceled = false;
        const pending = run("toggle", () => canceled);
        await gate.started;
        if (scenario === "cancel") canceled = true;
        if (scenario === "modal-cancel") model.context.isCancelled = true;
        if (scenario === "changed-layer") model.document.activeLayers = [{ id: 9, visible: true }];
        if (scenario === "changed-document") model.document.id = 9;
        if (scenario === "no-document") model.photoshop.app.documents = [];
        gate.release();
        assert.equal(await pending, "Refused");
        assert.equal(model.mutations, 0);
        assert.deepEqual(model.history, []);
        assert.deepEqual(model.historyEvents.at(-1), { type: "resume", commit: false });
    });
}

for (const scenario of ["no-document", "multiple-layers", "changed-layer", "changed-document", "cancel", "modal-cancel"]) {
    test(`a ${scenario} before history setup refuses without opening history`, async (t) => {
        const { model, run } = harness(t);
        let canceled = false;
        if (scenario === "no-document") model.photoshop.app.documents = [];
        if (scenario === "multiple-layers") model.document.activeLayers.push({ id: 3 });
        model.photoshop.core.executeAsModal = async (callback) => {
            if (scenario === "changed-layer") model.document.activeLayers = [{ id: 9, visible: true }];
            if (scenario === "changed-document") model.document.id = 9;
            if (scenario === "cancel") canceled = true;
            model.context.isCancelled = scenario === "modal-cancel";
            await callback(model.context);
        };
        assert.equal(await run("toggle", () => canceled), "Refused");
        assert.equal(model.mutations, 0);
        assert.deepEqual(model.historyEvents, []);
    });
}

test("a setter or commit failure leaves the document unchanged and reports Failed", async (t) => {
    const setter = harness(t, { failAfterMutation: true });
    assert.equal(await setter.run("toggle"), "Failed");
    assert.equal(setter.model.layer.visible, true);
    assert.deepEqual(setter.model.history, []);
    const commit = harness(t);
    commit.model.context.hostControl.resumeHistory = async () => {
        throw new Error("simulated commit failure");
    };
    assert.equal(await commit.run("toggle"), "Failed");
    assert.equal(commit.model.layer.visible, true);
});

test("busy Photoshop refuses, a concurrent pick refuses instead of queuing, and Show or Hide no-ops write nothing", async (t) => {
    const busy = harness(t);
    busy.model.photoshop.core.executeAsModal = async () => {
        throw { number: 9 };
    };
    assert.equal(await busy.run("toggle"), "Refused");
    const concurrent = harness(t);
    let release;
    concurrent.model.photoshop.core.executeAsModal = (callback) => new Promise((resolve) => {
        release = async () => {
            await callback(concurrent.model.context);
            resolve();
        };
    });
    const first = concurrent.run("toggle");
    assert.equal(await concurrent.run("toggle"), "Refused");
    await release();
    assert.equal(await first, "Done");
    const idle = harness(t);
    assert.equal(await idle.run("show"), "Done");
    assert.equal(idle.model.mutations, 0);
    assert.deepEqual(idle.model.historyEvents, []);
});

for (const scenario of ["retire", "close"]) {
    test(`a ${scenario} during history setup cancels the old session's pick`, async (t) => {
        const fixture = harness(t);
        const gate = holdSuspension(t, fixture.model);
        const pending = fixture.run("hide");
        await gate.started;
        if (scenario === "retire") fixture.retire("hide");
        else await fixture.close();
        gate.release();
        assert.equal(await pending, "Refused");
        assert.equal(fixture.model.mutations, 0);
    });
}

test("faces are published when they change, events are coalesced, and the last stopped session removes listeners", async (t) => {
    const model = fakePhotoshop();
    const faces = [];
    let added = 0;
    let removed = 0;
    let callback;
    model.photoshop.action = {
        addNotificationListener: async (events, fn) => {
            assert.deepEqual(events, EVENTS);
            added++;
            callback = fn;
        },
        removeNotificationListener: async (_, fn) => {
            assert.equal(fn, callback);
            removed++;
        },
    };
    const platform = createPlatform(model.photoshop, { refreshMs: 30, minPushMs: 10 });
    t.after(() => platform.close());
    const hide = start("a", constants.TOGGLE_ID, { mode: "hide" });
    const status = start("c", constants.STATUS_ID, { display: "selection" });
    for (const row of [hide, status]) platform.start(row, (value) => faces.push(value));
    await until(() => faces.length >= 2, "two faces");
    assert.equal(added, 1);
    assert.ok(faces.every(isFace));
    assert.equal(faces.find((value) => value.sessionId === status.sessionId).face.line1, "One");
    await delay(100);
    assert.equal(faces.length, 2, "an unchanged state is not published again");
    assert.equal((await platform.invoke(invoke("1", hide), () => false)).outcome, "Done");
    await until(() => faces.some((value) => value.sessionId === hide.sessionId && value.face.line1 === "Hidden"), "the changed face");
    const before = faces.length;
    for (let i = 0; i < 100; i++) callback();
    await delay(60);
    assert.ok(faces.length <= before + 2, "a burst of events is coalesced");
    model.photoshop.app.documents = [];
    await until(() => faces.some((value) => value.type === "fail" && value.failure === "NeedsSetup"), "the setup failure");
    platform.stop(hide.sessionId);
    platform.stop(status.sessionId);
    await until(() => removed === 1, "listener removal");
});
