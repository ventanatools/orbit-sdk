// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/** The CompanionClient's behaviour (contract §9.2, §10): backoff, the state table, faces, handlers and liveness. */

const test = require("node:test");
const assert = require("node:assert/strict");
const { ClientHarness, ScriptedPeer, newId } = require("./harness.js");
const { countdown, fixtureJson, settle, until } = require("./support.js");
const { Outcome, Failure, FaceState, PublishResult } = require("../index.js");
const wire = require("../wire.js");
const { kInternals } = require("../lib/client/client.js");

const TIMER = "example.countdown/timer";
const STATUS = "example.countdown/status";
const TIMER_SETTINGS = { mode: "start", duration: "five-minutes" };

function waitingDelays(harness, count) {
    return harness.statuses.filter((s) => s.state === "Waiting").slice(0, count).map((s) => s.retryInMs);
}

async function collectWaits(harness, count) {
    for (let i = 0; i < count; i++) {
        const status = await harness.waitForStatus((s, at) => s.state === "Waiting" && harness.statuses.filter((x) => x.state === "Waiting").indexOf(s) === i, "wait " + i);
        harness.clock.advance(status.retryInMs);
        await settle(3);
    }
    return waitingDelays(harness, count);
}

test("normal backoff doubles from 1 s to 30 s", async () => {
    const harness = new ClientHarness({ failures: Array(7).fill("host.pipe-busy") }).start();
    try {
        assert.deepEqual(await collectWaits(harness, 7), [1000, 2000, 4000, 8000, 16000, 30000, 30000]);
        assert.ok(harness.statuses.filter((s) => s.state === "Waiting").every((s) => s.reason === "host.pipe-busy" && s.serverVerified === false));
    } finally {
        await harness.stop();
    }
});

test("an absent host is probed at most every 5 s, and jitter never lifts a delay above its cap", async () => {
    const absent = new ClientHarness({ failures: Array(5).fill("host.not-running") }).start();
    try {
        assert.deepEqual(await collectWaits(absent, 5), [1000, 2000, 4000, 5000, 5000]);
    } finally {
        await absent.stop();
    }
    const high = new ClientHarness({ failures: Array(7).fill("host.pipe-busy"), random: () => 1 }).start();
    try {
        assert.deepEqual(await collectWaits(high, 7), [1200, 2400, 4800, 9600, 19200, 30000, 30000]);
    } finally {
        await high.stop();
    }
});

test("a connection that stays up 60 s resets the backoff", async () => {
    const harness = new ClientHarness({ failures: ["host.pipe-busy", "host.pipe-busy"] }).start();
    try {
        await collectWaits(harness, 2);
        const peer = await harness.connected();
        for (let i = 0; i < 3; i++) {
            // Host pings keep the connection busy, so the client never needs to ping.
            await harness.clock.advanceAsync(20000, 1000);
            peer.send({ type: "ping", id: i + 1 });
            assert.equal((await peer.read("Authenticated", "pong")).id, i + 1);
        }
        peer.close();
        const status = await harness.waitForStatus((s) => s.state === "Waiting" && s.reason === undefined, "waiting after close");
        assert.equal(status.retryInMs, 1000);
        assert.equal(status.attempt, 1);
    } finally {
        await harness.stop();
    }
});

test("codes after the challenge verified come from a verified server: proof-invalid and access-revoked stop", async () => {
    const proof = new ClientHarness().start();
    try {
        const peer = await proof.nextPeer();
        await peer.readHello();
        peer.send(peer.buildChallenge());
        await peer.read("Handshake", "authenticate");
        peer.send({ type: "error", code: "auth.proof-invalid" });
        const status = await proof.waitForState("Stopped");
        assert.equal(status.reason, "auth.proof-invalid");
        assert.equal(status.serverVerified, true);
        assert.equal(status.host.id, "example-host");
        assert.equal(status.protocolVersion, 3);
    } finally {
        await proof.stop();
    }
    const revoked = new ClientHarness().start();
    try {
        const peer = await revoked.connected();
        peer.send({ type: "error", code: "host.access-revoked" });
        assert.equal((await revoked.waitForState("Stopped")).reason, "host.access-revoked");
    } finally {
        await revoked.stop();
    }
});

test("host.reloaded reconnects at once, once; then normal backoff", async () => {
    const harness = new ClientHarness().start();
    try {
        const first = await harness.connected();
        first.send({ type: "error", code: "host.reloaded" });
        const second = await harness.connected();
        assert.equal(harness.statuses.some((s) => s.state === "Waiting"), false);
        second.send({ type: "error", code: "host.reloaded" });
        const status = await harness.waitForState("Waiting");
        assert.equal(status.reason, "host.reloaded");
        assert.equal(status.retryInMs, 1000);
    } finally {
        await harness.stop();
    }
});

test("a pairing for another extension or an unlisted host stops before connecting", async () => {
    const harness = new ClientHarness({ manifest: fixtureJson("manifests/valid/lone-leaf.json") }).start();
    try {
        const status = await harness.waitForState("Stopped");
        assert.equal(status.reason, "pairing.extension-mismatch");
        assert.equal(harness.attempts, 0);
    } finally {
        await harness.stop();
    }
    const manifest = countdown();
    manifest.hosts = ["other-host"];
    const unlisted = new ClientHarness({ manifest }).start();
    try {
        assert.equal((await unlisted.waitForState("Stopped")).reason, "pairing.host-not-listed");
    } finally {
        await unlisted.stop();
    }
});

test("manifest.mismatch waits the longest delay and reloads the manifest before the retry", async () => {
    const harness = new ClientHarness().start();
    let reloads = 0;
    const changed = countdown();
    changed.contributions[1].provides = ["face", "invoke"];
    harness.client[kInternals].reloadManifest = async () => {
        reloads++;
        return changed;
    };
    try {
        const peer = await harness.nextPeer();
        const firstHello = await peer.readHello();
        peer.send(peer.buildChallenge());
        await peer.read("Handshake", "authenticate");
        peer.send({ type: "error", code: "manifest.mismatch" });
        const status = await harness.waitForState("Waiting");
        assert.equal(status.retryInMs, 30000);
        harness.clock.advance(30000);
        const next = await harness.nextPeer();
        const hello = await next.readHello();
        assert.equal(reloads, 1);
        assert.notEqual(hello.manifestHash, firstHello.manifestHash);
    } finally {
        await harness.stop();
    }
});

test("an unknown error code after connecting waits with normal backoff", async () => {
    const harness = new ClientHarness().start();
    try {
        const peer = await harness.connected();
        peer.send({ type: "error", code: "future.reason", retryAfterMs: 120000 });
        const status = await harness.waitForState("Waiting");
        assert.equal(status.reason, "future.reason");
        assert.equal(status.serverVerified, true);
        assert.equal(status.retryInMs, 120000);
    } finally {
        await harness.stop();
    }
});

function faceHandler(run) {
    return { runSession: run };
}

test("100 quick setFace calls send at most the first and the newest", async () => {
    let session;
    const harness = new ClientHarness({ handler: faceHandler(async (s, signal) => {
        session = s;
        for (let i = 0; i < 100; i++) assert.equal(s.setFace({ line1: String(i), goodForSeconds: 5 }), PublishResult.Accepted);
        await new Promise((resolve) => signal.addEventListener("abort", resolve));
    }) }).start();
    try {
        const peer = await harness.connected();
        peer.send({ type: "startSession", sessionId: newId(), contributionId: STATUS, settings: {} });
        const first = await peer.read("Authenticated", "setFace");
        assert.equal(first.face.line1.text, "0");
        await until(() => session !== undefined, "the session");
        await settle(5);
        harness.clock.advance(500);
        await settle(5);
        const second = await peer.read("Authenticated", "setFace");
        assert.equal(second.face.line1.text, "99");
        harness.clock.advance(5000);
        await settle(5);
        assert.equal(peer.frames.length, 0);
    } finally {
        await harness.stop();
    }
});

test("setFace never throws for text and sends it cleaned", async () => {
    const flag = "🏴󠁧󠁢󠁥󠁮󠁧󠁿";
    const harness = new ClientHarness({ handler: faceHandler(async (s, signal) => {
        s.setFace({ line1: "x".repeat(500), line2: "Meeting​ " + flag + " in 5", detail: "  padded\n detail  ", glyph: "", state: FaceState.Playing, goodForSeconds: 1.5 });
        await new Promise((resolve) => signal.addEventListener("abort", resolve));
    }) }).start();
    try {
        const peer = await harness.connected();
        peer.send({ type: "startSession", sessionId: newId(), contributionId: STATUS, settings: {} });
        const message = await peer.read("Authenticated", "setFace");
        assert.equal(message.face.line1.text, "x".repeat(40));
        assert.equal(message.face.line2.text, "Meeting 🏴 in 5");
        assert.equal(message.face.detail, "padded  detail");
        assert.deepEqual(message.face.picture, { $type: "glyph", glyph: "" });
        assert.equal(message.face.state, "Playing");
        assert.equal(message.face.goodForSeconds, 2);
    } finally {
        await harness.stop();
    }
});

test("setFace, clearFace and fail check their arguments, then provides, then the session's state", async () => {
    const results = {};
    let resume;
    const resumed = new Promise((resolve) => {
        resume = resolve;
    });
    const harness = new ClientHarness({ handler: {
        async runSession(s) {
            if (s.contributionId === STATUS) {
                assert.throws(() => s.setFace({ goodForSeconds: 0 }), RangeError);
                assert.throws(() => s.setFace({ goodForSeconds: 86401 }), RangeError);
                assert.throws(() => s.setFace({ goodForSeconds: 5, glyph: "A" }), TypeError);
                assert.throws(() => s.setFace({ goodForSeconds: 5, state: "Bright" }), RangeError);
                assert.throws(() => s.setFace({ goodForSeconds: 5, image: "x.png" }), TypeError);
                assert.throws(() => s.setFace({ goodForSeconds: 5, line1: 7 }), TypeError);
                assert.throws(() => s.setFace(undefined), TypeError);
                assert.throws(() => s.fail("LlmUnavailable"), RangeError);
                assert.throws(() => s.fail("Oops"), RangeError);
                assert.equal(s.supports("x.test-echo"), false);
                await resumed;
                results.afterStop = [s.setFace({ goodForSeconds: 5 }), s.clearFace(), s.fail(Failure.Network), s.isActive];
            }
        },
    }, manifest: (() => {
        const manifest = countdown();
        manifest.contributions[1].provides = ["face"];
        manifest.contributions.push({ id: "example.countdown/run", name: "Run", description: "Runs.", glyph: "", provides: ["invoke"] });
        return manifest;
    })() }).start();
    try {
        const peer = await harness.connected();
        const sessionId = newId();
        peer.send({ type: "startSession", sessionId, contributionId: STATUS, settings: {} });
        await until(() => harness.started.length === 1, "the session");
        peer.send({ type: "stopSession", sessionId });
        await settle(5);
        resume();
        await until(() => results.afterStop, "the handler");
        assert.deepEqual(results.afterStop, ["SessionEnded", "SessionEnded", "SessionEnded", false]);
        assert.equal(peer.frames.length, 0);
    } finally {
        await harness.stop();
    }
});

test("an invoke-only contribution cannot publish", async () => {
    let error;
    const manifest = countdown();
    manifest.contributions.push({ id: "example.countdown/run", name: "Run", description: "Runs.", glyph: "", provides: ["invoke"] });
    const harness = new ClientHarness({ manifest, handler: {
        async invoke(invocation) {
            try {
                invocation.session.setFace({ goodForSeconds: 5 });
            } catch (e) {
                error = e;
            }
            return Outcome.Done;
        },
    } }).start();
    try {
        const peer = await harness.connected();
        const sessionId = newId();
        peer.send({ type: "startSession", sessionId, contributionId: "example.countdown/run", settings: {} });
        peer.send({ type: "invoke", requestId: newId(), sessionId });
        assert.equal((await peer.read("Authenticated", "result")).outcome, "Done");
        assert.ok(error instanceof Error);
        assert.match(error.message, /does not provide face/);
    } finally {
        await harness.stop();
    }
});

test("a session handler that throws raises a fault and clears its face", async () => {
    const harness = new ClientHarness({ handler: faceHandler(async (s) => {
        s.setFace({ line1: "up", goodForSeconds: 5 });
        throw new Error("author bug");
    }) }).start();
    try {
        const peer = await harness.connected();
        const sessionId = newId();
        peer.send({ type: "startSession", sessionId, contributionId: STATUS, settings: {} });
        assert.equal((await peer.read("Authenticated", "setFace")).sessionId, sessionId);
        harness.clock.advance(500);
        await settle(5);
        assert.equal((await peer.read("Authenticated", "clearFace")).sessionId, sessionId);
        await until(() => harness.faults.length === 1, "the fault");
        assert.equal(harness.faults[0].kind, "Exception");
        assert.equal(harness.faults[0].contributionId, STATUS);
        assert.equal(harness.faults[0].sessionId, sessionId);
        assert.equal(harness.faults[0].error.message, "author bug");
        assert.equal(harness.client.state, "Connected");
    } finally {
        await harness.stop();
    }
});

test("invocation results: valid outcomes go out; invalid results and exceptions answer Failed and raise a fault", async () => {
    const answers = [
        Outcome.Done,
        { outcome: Outcome.Failed, failure: Failure.Network },
        { outcome: Outcome.Failed },
        Outcome.Refused,
        "Maybe",
        { outcome: Outcome.Done, failure: Failure.Network },
        { outcome: Outcome.Failed, failure: "LlmUnavailable" },
        undefined,
    ];
    let index = 0;
    const harness = new ClientHarness({ handler: {
        async invoke(invocation, signal) {
            assert.equal(invocation.session.contributionId, TIMER);
            assert.ok(signal instanceof AbortSignal);
            const i = index++;
            if (i === answers.length) throw new Error("boom");
            return answers[i];
        },
    } }).start();
    try {
        const peer = await harness.connected();
        const sessionId = newId();
        peer.send({ type: "startSession", sessionId, contributionId: TIMER, settings: TIMER_SETTINGS });
        const expected = [["Done"], ["Failed", "Network"], ["Failed"], ["Refused"], ["Failed"], ["Failed"], ["Failed"], ["Failed"], ["Failed"]];
        for (const [outcome, failure] of expected) {
            peer.send({ type: "invoke", requestId: newId(), sessionId });
            const result = await peer.read("Authenticated", "result");
            assert.equal(result.outcome, outcome);
            assert.equal(result.failure, failure);
        }
        await until(() => harness.faults.length === 5, "five faults");
        assert.deepEqual(harness.faults.map((f) => f.kind), ["InvalidResult", "InvalidResult", "InvalidResult", "InvalidResult", "Exception"]);
        assert.equal(harness.faults[4].error.message, "boom");
    } finally {
        await harness.stop();
    }
});

test("an invocation of a face-only contribution, or without an invoke handler, is answered without the author", async () => {
    const harness = new ClientHarness({ handler: { runSession: async () => {} } }).start();
    try {
        const peer = await harness.connected();
        const status = newId();
        const timer = newId();
        peer.send({ type: "startSession", sessionId: status, contributionId: STATUS, settings: {} });
        peer.send({ type: "startSession", sessionId: timer, contributionId: TIMER, settings: TIMER_SETTINGS });
        peer.send({ type: "invoke", requestId: newId(), sessionId: status });
        assert.equal((await peer.read("Authenticated", "result")).outcome, "Refused");
        peer.send({ type: "invoke", requestId: newId(), sessionId: timer });
        assert.equal((await peer.read("Authenticated", "result")).outcome, "Unsupported");
        peer.send({ type: "invoke", requestId: newId(), sessionId: newId() });
        assert.equal((await peer.read("Authenticated", "result")).outcome, "Refused");
    } finally {
        await harness.stop();
    }
});

test("cancel aborts the handler and sends nothing; stopSession during an invocation answers Refused", async () => {
    const harness = new ClientHarness({ handler: {
        invoke(invocation, signal) {
            return new Promise((resolve, reject) => {
                signal.addEventListener("abort", () => reject(signal.reason));
            });
        },
    } }).start();
    try {
        const peer = await harness.connected();
        const sessionId = newId();
        peer.send({ type: "startSession", sessionId, contributionId: TIMER, settings: TIMER_SETTINGS });
        const cancelled = newId();
        peer.send({ type: "invoke", requestId: cancelled, sessionId });
        await settle(5);
        peer.send({ type: "cancel", requestId: cancelled });
        await settle(10);
        assert.equal(peer.frames.length, 0);
        const stopped = newId();
        peer.send({ type: "invoke", requestId: stopped, sessionId });
        await settle(5);
        peer.send({ type: "stopSession", sessionId });
        const result = await peer.read("Authenticated", "result");
        assert.equal(result.requestId, stopped);
        assert.equal(result.outcome, "Refused");
        assert.equal(harness.faults.length, 0);
    } finally {
        await harness.stop();
    }
});

test("the deadline follows invokeTimeoutMs, and a handler that ignores cancellation raises IgnoredCancellation", async () => {
    const harness = new ClientHarness({ handler: { invoke: () => new Promise(() => {}) } }).start();
    try {
        const peer = await harness.connected({ limits: { ...wire.protocol3Limits, invokeTimeoutMs: 4000 } });
        const sessionId = newId();
        peer.send({ type: "startSession", sessionId, contributionId: TIMER, settings: TIMER_SETTINGS });
        const requestId = newId();
        peer.send({ type: "invoke", requestId, sessionId });
        await settle(5);
        harness.clock.advance(999);
        await settle(5);
        assert.equal(peer.frames.length, 0);
        harness.clock.advance(1);
        await settle(5);
        const result = await peer.read("Authenticated", "result");
        assert.equal(result.outcome, "Failed");
        harness.clock.advance(5000);
        await until(() => harness.faults.length === 1, "the fault");
        assert.equal(harness.faults[0].kind, "IgnoredCancellation");
        assert.equal(harness.faults[0].requestId, requestId);
    } finally {
        await harness.stop();
    }
});

test("handlers that ignore cancellation are counted across reconnects up to 64 sessions", async () => {
    const harness = new ClientHarness({ handler: faceHandler(() => new Promise(() => {})) }).start();
    try {
        const first = await harness.connected();
        for (let i = 0; i < 64; i++) first.send({ type: "startSession", sessionId: newId(), contributionId: STATUS, settings: {} });
        await until(() => harness.started.length === 64, "64 sessions");
        assert.ok(harness.started.every((s) => s.refusal === null));
        first.close();
        await harness.waitForState("Waiting");
        harness.clock.advance(1000);
        const second = await harness.nextPeer();
        await second.connect();
        await until(() => harness.statuses.filter((s) => s.state === "Connected").length === 2, "reconnected");
        const sessionId = newId();
        second.send({ type: "startSession", sessionId, contributionId: STATUS, settings: {} });
        const refused = await second.read("Authenticated", "sessionRefused");
        assert.equal(refused.code, "session.capacity");
        await until(() => harness.faults.some((f) => f.kind === "SessionCapacity"), "the capacity fault");
        assert.equal(harness.client.state, "Connected");
    } finally {
        await harness.stop();
    }
});

test("a reused session or request id closes the connection", async () => {
    const harness = new ClientHarness({ handler: { invoke: async () => Outcome.Done } }).start();
    try {
        const peer = await harness.connected();
        const sessionId = newId();
        peer.send({ type: "startSession", sessionId, contributionId: TIMER, settings: TIMER_SETTINGS });
        const requestId = newId();
        peer.send({ type: "invoke", requestId, sessionId });
        assert.equal((await peer.read("Authenticated", "result")).outcome, "Done");
        peer.send({ type: "invoke", requestId, sessionId });
        assert.equal(await peer.readClose(), "invoke.replay");
        await harness.waitForState("Waiting");
        harness.clock.advance(1000);
        const next = await harness.nextPeer();
        await next.connect();
        const replayed = newId();
        next.send({ type: "startSession", sessionId: replayed, contributionId: STATUS, settings: {} });
        next.send({ type: "stopSession", sessionId: replayed });
        next.send({ type: "startSession", sessionId: replayed, contributionId: STATUS, settings: {} });
        assert.equal(await next.readClose(), "session.replay");
    } finally {
        await harness.stop();
    }
});

test("the client pings after its interval without a frame, and closes when no pong comes", async () => {
    const harness = new ClientHarness().start();
    try {
        const peer = await harness.connected();
        peer.autoPong = false;
        harness.clock.advance(29999);
        await settle(5);
        assert.equal(peer.frames.length, 0);
        harness.clock.advance(1);
        await settle(5);
        const ping = await peer.read("Authenticated", "ping");
        assert.equal(ping.id, 1);
        harness.clock.advance(10000);
        await settle(5);
        assert.equal(await peer.readClose(), "protocol.ping-timeout");
        assert.equal((await harness.waitForState("Waiting")).reason, "protocol.ping-timeout");
    } finally {
        await harness.stop();
    }
});

test("a renewed face is republished at 80% of its lifetime only while its handler runs", async () => {
    let finish;
    const harness = new ClientHarness({ handler: faceHandler(async (s) => {
        s.setFace({ line1: "kept", goodForSeconds: 10, renew: true });
        await new Promise((resolve) => {
            finish = resolve;
        });
    }) }).start();
    try {
        const peer = await harness.connected();
        peer.send({ type: "startSession", sessionId: newId(), contributionId: STATUS, settings: {} });
        await peer.read("Authenticated", "setFace");
        for (let i = 0; i < 3; i++) {
            harness.clock.advance(8000);
            await settle(5);
            assert.equal((await peer.read("Authenticated", "setFace")).face.line1.text, "kept");
        }
        finish();
        await settle(10);
        harness.clock.advance(8000);
        await settle(5);
        const types = peer.frames.map((body) => wire.readMessage(body, "Companion", "Authenticated").message.type);
        assert.deepEqual(types.filter((type) => type !== "ping"), []);
    } finally {
        await harness.stop();
    }
});

test("rate.throttled holds face commands for retryAfterMs and keeps the connection", async () => {
    let session;
    const harness = new ClientHarness({ handler: faceHandler(async (s, signal) => {
        session = s;
        await new Promise((resolve) => signal.addEventListener("abort", resolve));
    }) }).start();
    try {
        const peer = await harness.connected();
        peer.send({ type: "startSession", sessionId: newId(), contributionId: STATUS, settings: {} });
        await until(() => session !== undefined, "the session");
        peer.send({ type: "error", code: "rate.throttled", retryAfterMs: 3000 });
        peer.send({ type: "ping", id: 5 });
        assert.equal((await peer.read("Authenticated", "pong")).id, 5);
        session.setFace({ line1: "held", goodForSeconds: 5 });
        harness.clock.advance(2999);
        await settle(5);
        assert.equal(peer.frames.length, 0);
        harness.clock.advance(1);
        await settle(5);
        assert.equal((await peer.read("Authenticated", "setFace")).face.line1.text, "held");
        assert.equal(harness.client.state, "Connected");
    } finally {
        await harness.stop();
    }
});

test("a host that floods frames beyond the hard budget is closed with rate.exceeded", async () => {
    const harness = new ClientHarness().start();
    try {
        const peer = await harness.connected();
        for (let i = 0; i < 1100; i++) peer.send({ type: "pong", id: 1 + i });
        assert.equal(await peer.readClose(), "rate.exceeded");
    } finally {
        await harness.stop();
    }
});

test("events are delivered asynchronously and in order, and a throwing listener never stops the client", async () => {
    const harness = new ClientHarness();
    const order = [];
    const reported = [];
    harness.client.on("status", () => {
        throw new Error("listener bug");
    });
    harness.client.on("status", (status) => order.push(status.state));
    harness.client[require("../lib/client/client.js").kSubscriberFaulted] = (error) => reported.push(error.message);
    harness.start();
    assert.deepEqual(order, []);
    try {
        await harness.connected();
        assert.deepEqual(order, ["Connecting", "Connected"]);
        assert.deepEqual(reported, ["listener bug", "listener bug"]);
        assert.equal(harness.client.emit("status", {}), false);
    } finally {
        await harness.stop();
    }
    assert.equal(order[order.length - 1], "Stopped");
});

test("many session and invocation cycles on one connection stay connected", async () => {
    const harness = new ClientHarness({ handler: {
        runSession: async (s, signal) => {
            await new Promise((resolve) => signal.addEventListener("abort", resolve));
        },
        invoke: async () => Outcome.Done,
    } }).start();
    try {
        const peer = await harness.connected();
        for (let i = 0; i < 300; i++) {
            const sessionId = newId();
            peer.send({ type: "startSession", sessionId, contributionId: TIMER, settings: TIMER_SETTINGS });
            peer.send({ type: "invoke", requestId: newId(), sessionId });
            assert.equal((await peer.read("Authenticated", "result")).outcome, "Done");
            peer.send({ type: "stopSession", sessionId });
            if (i % 50 === 0) harness.clock.advance(1000);
        }
        await settle(10);
        assert.equal(harness.client.state, "Connected");
        assert.equal(harness.faults.length, 0);
    } finally {
        await harness.stop();
    }
});

test("run refuses a second concurrent run and needs a transport off Windows", async () => {
    const harness = new ClientHarness().start();
    try {
        await assert.rejects(harness.client.run(new AbortController().signal), /already running/);
    } finally {
        await harness.stop();
    }
    assert.throws(() => new (require("../index.js").CompanionClient)({ pairing: {}, manifest: countdown(), handler: {} }), TypeError);
});
