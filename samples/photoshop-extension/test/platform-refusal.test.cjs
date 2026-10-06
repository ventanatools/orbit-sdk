// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { createPlatform } = require("../uxp/platform.js");
const { ACTION_ID } = require("../uxp/wire-v2.js");
const { fakePhotoshop } = require("./helpers.cjs");

function fixture(t) {
    const model = fakePhotoshop(), platform = createPlatform(model.photoshop);
    t.after(() => platform.close());
    const session = { type: "startSession", sessionId: "a".repeat(32), actionId: ACTION_ID, settings: { mode: "toggle" } };
    platform.start(session, () => {});
    let sequence = 0;
    return { model, run: (canceled = () => false) => platform.invoke({ ...session, type: "invoke",
        requestId: (++sequence).toString(16).padStart(32, "0") }, canceled) };
}

test("one documented layer visibility mutation occurs inside a named modal scope", async t => {
    const { model, run } = fixture(t);
    let scopes = 0;
    model.photoshop.core.executeAsModal = async (callback, options) => {
        assert.equal(options.commandName, "Orbit: Change selected layer visibility");
        assert.equal(model.mutations, 0);
        scopes++;
        await callback(model.context);
        assert.equal(model.mutations, 1);
    };
    assert.equal(await run(), "done");
    assert.equal(scopes, 1);
    assert.equal(model.layer.visible, false);
});

for (const scenario of ["no-document", "multiple-layers", "changed-layer", "changed-document", "cancel", "modal-cancel"]) {
    test(`v2 refuses ${scenario} before opening history`, async t => {
        const { model, run } = fixture(t);
        let canceled = false;
        if (scenario === "no-document") model.photoshop.app.documents = [];
        if (scenario === "multiple-layers") model.document.activeLayers.push({ id: 3 });
        model.photoshop.core.executeAsModal = async callback => {
            if (scenario === "changed-layer") model.document.activeLayers = [{ id: 9, visible: true }];
            if (scenario === "changed-document") model.document.id = 9;
            if (scenario === "cancel") canceled = true;
            model.context.isCancelled = scenario === "modal-cancel";
            await callback(model.context);
        };
        assert.equal(await run(() => canceled), "refused");
        assert.equal(model.mutations, 0);
        assert.deepEqual(model.historyEvents, []);
    });
}

test("busy and unexpected modal failures never mutate", async t => {
    const { model, run } = fixture(t);
    model.photoshop.core.executeAsModal = async () => { throw { number: 9 }; };
    assert.equal(await run(), "refused");
    model.photoshop.core.executeAsModal = async () => { throw new Error("private Photoshop details"); };
    assert.equal(await run(), "failed");
    assert.equal(model.mutations, 0);
});

test("a second concurrent command refuses instead of queuing a stale toggle", async t => {
    const { model, run } = fixture(t);
    let release;
    model.photoshop.core.executeAsModal = callback => new Promise(resolve => {
        release = async () => { await callback(model.context); resolve(); };
    });
    const first = run();
    assert.equal(await run(), "refused");
    await release();
    assert.equal(await first, "done");
    assert.equal(model.mutations, 1);
});
