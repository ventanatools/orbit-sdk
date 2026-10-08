// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { fixturePath, countdown } = require("./support.js");
const { createTestSession, createTestInvocation, startTestHost, assertManifestValid, ManualClock } = require("../testing.js");
const { Outcome, FaceState, Failure } = require("../index.js");

const TIMER = "example.countdown/timer";
const STATUS = "example.countdown/status";

test("createTestSession fills defaults, records publications as cleaned for sending, and stop ends the session", async () => {
    const recording = createTestSession({ manifest: countdown(), contributionId: TIMER, settings: { mode: "pause" } });
    assert.deepEqual({ ...recording.session.settings }, { mode: "pause", duration: "five-minutes" });
    assert.equal(recording.session.uiLanguage, "en-US");
    const handler = {
        async runSession(session, signal) {
            session.setFace({ line1: "  4:59  ", detail: "x".repeat(700), state: FaceState.Playing, goodForSeconds: 5, renew: true });
            session.fail(Failure.NoResult);
            await new Promise((resolve, reject) => signal.addEventListener("abort", () => reject(signal.reason)));
            session.setFace({ line1: "late", goodForSeconds: 5 });
        },
    };
    const running = recording.run(handler, { timeoutMs: 2000 });
    await new Promise((resolve) => setImmediate(resolve));
    recording.stop();
    await running;
    const kinds = recording.publications.map((p) => p.kind);
    assert.deepEqual(kinds, ["SetFace", "Fail"]);
    assert.equal(recording.publications[0].face.line1, "4:59");
    assert.equal(recording.publications[0].face.detail.length, 160);
    assert.equal(recording.publications[0].face.renew, true);
    assert.equal(recording.publications[1].failure, "NoResult");
    assert.equal(recording.lastFace, undefined);
    assert.equal(recording.session.isActive, false);
    assert.equal(recording.signal.aborted, true);
});

test("createTestSession records a publish after stop as AfterStop and refuses settings the manifest does not have", () => {
    const recording = createTestSession({ manifest: countdown(), contributionId: STATUS });
    recording.stop();
    assert.equal(recording.session.setFace({ line1: "late", goodForSeconds: 5 }), "SessionEnded");
    assert.deepEqual(recording.publications.map((p) => p.kind), ["AfterStop"]);
    assert.throws(() => createTestSession({ manifest: countdown(), contributionId: TIMER, settings: { mode: "stop" } }), /session.settings-invalid/);
    assert.throws(() => createTestSession({ manifest: countdown(), contributionId: "example.countdown/missing" }), TypeError);
    const invocation = createTestInvocation(createTestSession({ manifest: countdown(), contributionId: TIMER }));
    assert.equal(invocation.session.contributionId, TIMER);
    assert.match(invocation.requestId, /^[0-9a-f]{32}$/);
});

test("startTestHost runs a real client: sessions, faces, invocations, cancellation and the transcript", async () => {
    const clock = new ManualClock();
    const handler = {
        async runSession(session, signal) {
            session.setFace({ line1: session.settings.mode || "status", goodForSeconds: 5 });
            await new Promise((resolve) => signal.addEventListener("abort", resolve));
        },
        invoke(invocation, signal) {
            if (invocation.session.settings.mode === "reset") {
                return new Promise((resolve, reject) => signal.addEventListener("abort", () => reject(signal.reason)));
            }
            return Outcome.Done;
        },
    };
    const host = await startTestHost({ manifest: countdown(), handler, clock, hostId: "example-host" });
    try {
        assert.equal(host.client.state, "Connected");
        const timer = await host.startSession(TIMER, { mode: "start" });
        assert.equal(timer.refusedCode, undefined);
        const face = await host.waitForFace(timer, 2000);
        assert.equal(face.face.line1, "start");
        assert.deepEqual(await host.invoke(timer), { kind: "Done" });
        const reset = await host.startSession(TIMER, { mode: "reset" });
        const invocation = host.startInvoke(reset);
        await new Promise((resolve) => setTimeout(resolve, 10));
        host.cancel(invocation);
        assert.deepEqual(await invocation.result, { kind: "Cancelled" });
        const refused = await host.startSession(TIMER, { mode: "launch" });
        assert.equal(refused.refusedCode, "session.settings-invalid");
        host.stopSession(timer);
        const types = host.transcript.map((entry) => entry.from + ":" + entry.type);
        assert.ok(types.includes("Companion:hello"));
        assert.ok(types.includes("Host:ready"));
        assert.ok(types.includes("Companion:sessionRefused"));
        assert.ok(host.transcriptJsonLines().split("\n").length > 5);
        assert.ok(host.statuses.some((s) => s.state === "Connected"));
    } finally {
        await host.close();
    }
});

test("startTestHost times an invocation out at the host's invokeTimeoutMs when the client never answers", async () => {
    const clock = new ManualClock();
    const host = await startTestHost({ manifest: countdown(), handler: { invoke: () => new Promise(() => {}) }, clock });
    try {
        const session = await host.startSession(TIMER);
        const invocation = host.startInvoke(session);
        await new Promise((resolve) => setTimeout(resolve, 10));
        await clock.advanceAsync(12000, 1000);
        assert.deepEqual(await invocation.result, { kind: "Failed" });
        await clock.advanceAsync(5000, 1000);
        await new Promise((resolve) => setTimeout(resolve, 10));
        assert.ok(host.faults.some((fault) => fault.kind === "IgnoredCancellation"));
    } finally {
        await host.close();
    }
});

test("startTestHost reconnects after a disconnect; host.reloaded reconnects at once", async () => {
    const host = await startTestHost({ manifest: countdown(), handler: {} });
    try {
        host.disconnect("host.reloaded");
        const deadline = Date.now() + 3000;
        while (host.statuses.filter((s) => s.state === "Connected").length < 2) {
            if (Date.now() > deadline) throw new Error("no reconnection");
            await new Promise((resolve) => setTimeout(resolve, 5));
        }
        assert.equal(host.statuses.some((s) => s.state === "Waiting"), false);
        assert.ok(host.transcript.some((entry) => entry.from === "Host" && entry.type === "error"));
    } finally {
        await host.close();
    }
});

test("assertManifestValid throws listing every error, for a path or an object", () => {
    assert.equal(assertManifestValid(fixturePath("manifests/valid/countdown.json")).id, "example.countdown");
    assert.equal(assertManifestValid(countdown()).id, "example.countdown");
    assert.throws(() => assertManifestValid(fixturePath("manifests/invalid/id-grammar.json")), (error) => {
        assert.equal(error.name, "ConformanceError");
        assert.match(error.message, /id\.grammar \/id: /);
        assert.match(error.message, /id-grammar\.json\(\d+,\d+\)/);
        assert.equal(error.failures.length, 1);
        return true;
    });
    const manifest = countdown();
    manifest.name = "";
    manifest.version = "1";
    assert.throws(() => assertManifestValid(manifest), (error) => error.failures.length === 2);
    const folder = fs.mkdtempSync(path.join(os.tmpdir(), "sdk-assert-"));
    try {
        assert.throws(() => assertManifestValid(path.join(folder, "missing.json")), { code: "ENOENT" });
    } finally {
        fs.rmSync(folder, { recursive: true, force: true });
    }
});

test("the documented pattern sees the first face every time: run, waitForFace, assert, stop, await", async () => {
    const widget = {
        async runSession(session, signal) {
            session.setFace({ line1: "4:59", goodForSeconds: 60, renew: true });
            if (!signal.aborted) await new Promise((resolve) => signal.addEventListener("abort", resolve, { once: true }));
        },
    };
    for (let attempt = 0; attempt < 100; attempt++) {
        const recording = createTestSession({ manifest: countdown(), contributionId: STATUS });
        const run = recording.run(widget);
        const face = await recording.waitForFace(2000);
        assert.equal(face.line1, "4:59");
        recording.stop();
        await run;
        assert.deepEqual(recording.publications.map((p) => p.kind), ["SetFace"]);
    }
});

test("run starts the handler before it returns, so a stop right away comes after the first face", async () => {
    const recording = createTestSession({ manifest: countdown(), contributionId: STATUS });
    const run = recording.run({
        async runSession(session, signal) {
            session.setFace({ line1: "first", goodForSeconds: 5 });
            if (!signal.aborted) await new Promise((resolve) => signal.addEventListener("abort", resolve, { once: true }));
        },
    });
    assert.equal(recording.lastFace.line1, "first");
    recording.stop();
    await run;
    assert.deepEqual(recording.publications.map((p) => p.kind), ["SetFace"]);
});

test("waitForFace resolves faces in order and its timeout is real time; waitForPublications counts every kind", async () => {
    const clock = new ManualClock();
    const recording = createTestSession({ manifest: countdown(), contributionId: STATUS, clock });
    const { session } = recording;
    session.setFace({ line1: "one", goodForSeconds: 5 });
    session.clearFace();
    session.setFace({ line1: "two", goodForSeconds: 5 });
    assert.equal((await recording.waitForFace(1000)).line1, "one");
    assert.equal((await recording.waitForFace(1000)).line1, "two");
    const third = recording.waitForFace(10000);
    session.setFace({ line1: "three", goodForSeconds: 5 });
    assert.equal((await third).line1, "three");
    // The manual clock never moves, yet the timeout ends the wait.
    await assert.rejects(recording.waitForFace(50), /No further face/);
    await recording.waitForPublications(4, 1000);
    const fifth = recording.waitForPublications(5, 10000);
    recording.stop();
    session.setFace({ line1: "late", goodForSeconds: 5 });
    await fifth;
    await assert.rejects(recording.waitForFace(50), /No further face/);
    assert.throws(() => recording.waitForPublications(-1), TypeError);
});
