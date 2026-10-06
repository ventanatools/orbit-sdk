// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { createPlatform } = require("../uxp/platform.js");
const { ACTION_ID } = require("../uxp/wire-v2.js");
const { fakePhotoshop } = require("./helpers.cjs");

function harness(t, options) {
    const model = fakePhotoshop(options);
    const platform = createPlatform(model.photoshop), sessions = new Map();
    let sequence = 0;
    t.after(() => platform.close());
    return {
        model,
        close: () => platform.close(),
        retire: mode => platform.stop(sessions.get(mode).sessionId),
        run: (mode, canceled = () => false) => {
            if (!sessions.has(mode)) {
                const session = { type: "startSession", sessionId: ({ toggle: "a", show: "b", hide: "c" })[mode].repeat(32),
                    actionId: ACTION_ID, settings: { mode } };
                sessions.set(mode, session);
                platform.start(session, () => {});
            }
            const session = sessions.get(mode);
            return platform.invoke({ ...session, type: "invoke", requestId: (++sequence).toString(16).padStart(32, "0") }, canceled);
        }
    };
}

function holdSuspension(t, model) {
    let entered, release;
    const started = new Promise(resolve => { entered = resolve; });
    const gate = new Promise(resolve => { release = resolve; });
    const suspend = model.context.hostControl.suspendHistory;
    model.context.hostControl.suspendHistory = async options => {
        const token = await suspend(options);
        entered();
        await gate;
        return token;
    };
    t.after(() => release());
    return { started, release };
}

{
    test("v2 brackets the visibility mutation in one named document history state", async t => {
        const { model, run } = harness(t);
        assert.equal(await run("toggle"), "done");
        assert.deepEqual(model.historyEvents.map(event => event.type), ["suspend", "mutation", "resume"]);
        assert.equal(model.historyEvents[1].suspended, true);
        assert.equal(model.historyEvents[2].commit, true);
        assert.deepEqual(model.history, [{ documentID: 1,
            name: "Orbit: Change selected layer visibility",
            before: true, after: false }]);
        assert.equal(model.mutations, 1);
    });

    for (const scenario of ["cancel", "modal-cancel", "changed-layer", "changed-document", "no-document"]) {
        test(`v2 refuses ${scenario} while history suspension awaits the host`, async t => {
            const { model, run } = harness(t);
            const gate = holdSuspension(t, model);
            let canceled = false;
            const pending = run("toggle", () => canceled);
            await gate.started;
            assert.equal(model.mutations, 0);
            if (scenario === "cancel") canceled = true;
            if (scenario === "modal-cancel") model.context.isCancelled = true;
            if (scenario === "changed-layer") model.document.activeLayers = [{ id: 9, visible: true }];
            if (scenario === "changed-document") model.document.id = 9;
            if (scenario === "no-document") model.photoshop.app.documents = [];
            gate.release();
            assert.equal(await pending, "refused");
            assert.equal(model.mutations, 0);
            assert.deepEqual(model.history, []);
            assert.deepEqual(model.historyEvents.at(-1), { type: "resume", commit: false });
        });
    }

    test("v2 lets a setter exception leave modal scope and roll back the suspended change", async t => {
        const { model, run } = harness(t, { failAfterMutation: true });
        assert.equal(await run("toggle"), "failed");
        assert.equal(model.mutations, 1, "the simulated setter changes state before throwing");
        assert.equal(model.layer.visible, true, "the suspended mutation is rolled back");
        assert.deepEqual(model.history, []);
        assert.deepEqual(model.historyEvents.at(-1), { type: "autoResume", commit: false });
    });

    test("v2 a failed history commit cannot report done or retain the mutation", async t => {
        const { model, run } = harness(t);
        model.context.hostControl.resumeHistory = async () => { throw new Error("simulated commit failure"); };
        assert.equal(await run("toggle"), "failed");
        assert.equal(model.layer.visible, true);
        assert.deepEqual(model.history, []);
        assert.deepEqual(model.historyEvents.at(-1), { type: "autoResume", commit: false });
    });

    test("v2 host cancellation during an awaited API reports refused", async t => {
        const { model, run } = harness(t);
        model.context.hostControl.suspendHistory = async () => {
            model.context.isCancelled = true;
            throw new Error("host canceled");
        };
        assert.equal(await run("toggle"), "refused");
        assert.equal(model.mutations, 0);
    });

    test("v2 reports done only after history commit completes", async t => {
        const { model, run } = harness(t);
        const resume = model.context.hostControl.resumeHistory;
        let entered, release, settled = false;
        const started = new Promise(resolve => { entered = resolve; });
        const gate = new Promise(resolve => { release = resolve; });
        t.after(() => release());
        model.context.hostControl.resumeHistory = async (token, commit) => {
            entered();
            await gate;
            await resume(token, commit);
        };
        const pending = run("toggle").then(result => { settled = true; return result; });
        await started;
        assert.equal(model.mutations, 1);
        assert.equal(settled, false);
        assert.deepEqual(model.history, []);
        release();
        assert.equal(await pending, "done");
        assert.equal(model.history.length, 1);
    });
}

test("v2 Show and Hide no-ops neither write visibility nor open history", async t => {
    const { model, run } = harness(t);
    assert.equal(await run("show"), "done");
    assert.equal(model.mutations, 0);
    assert.deepEqual(model.historyEvents, []);
    assert.equal(await run("hide"), "done");
    const afterHide = model.historyEvents.length;
    assert.equal(await run("hide"), "done");
    assert.equal(model.historyEvents.length, afterHide);
    assert.equal(model.mutations, 1);
    assert.equal(await run("show"), "done");
    const afterShow = model.historyEvents.length;
    assert.equal(await run("show"), "done");
    assert.equal(model.historyEvents.length, afterShow);
    assert.equal(model.mutations, 2);
    assert.deepEqual(model.history.map(state => [state.before, state.after]), [[true, false], [false, true]]);
});

for (const scenario of ["retire", "close"]) {
    test(`v2 ${scenario} during history suspension cancels the old session without changing the document`, async t => {
        const fixture = harness(t), gate = holdSuspension(t, fixture.model);
        const pending = fixture.run("hide");
        await gate.started;
        if (scenario === "retire") fixture.retire("hide");
        else await fixture.close();
        gate.release();
        assert.equal(await pending, "refused");
        assert.equal(fixture.model.mutations, 0);
        assert.deepEqual(fixture.model.history, []);
        assert.deepEqual(fixture.model.historyEvents.at(-1), { type: "resume", commit: false });
    });
}
