// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { fixtureJson } = require("./support.js");
const wire = require("../wire.js");
const { ManualClock } = require("../testing.js");

/** The frame bytes of a messages-*.json case: frame (text), frameBase64 (bytes) or frameRepeat. */
function frameOf(item) {
    if (typeof item.frame === "string") return Buffer.from(item.frame, "utf8");
    if (typeof item.frameBase64 === "string") return Buffer.from(item.frameBase64, "base64");
    const repeat = item.frameRepeat;
    const prefix = Buffer.from(repeat.prefix, "utf8");
    const suffix = Buffer.from(repeat.suffix, "utf8");
    const fill = Buffer.from(repeat.fill, "utf8");
    const count = (repeat.length - prefix.length - suffix.length) / fill.length;
    return Buffer.concat([prefix, Buffer.concat(Array.from({ length: count }, () => fill)), suffix]);
}

test("fixtures/wire/v3/transcript.json: transcripts and proofs match the implementation", () => {
    const vectors = fixtureJson("wire/v3/transcript.json");
    for (const item of vectors.cases) {
        assert.equal(wire.transcriptBytes(item.transcript, "server").toString("utf8"), item.serverTranscript, item.name);
        assert.equal(wire.computeProof(vectors.secret, item.transcript, "server"), item.serverProof, item.name);
        if (typeof item.clientProof === "string") {
            assert.equal(wire.computeProof(vectors.secret, item.transcript, "client"), item.clientProof, item.name);
            assert.equal(wire.verifyProof(vectors.secret, item.clientProof, item.transcript, "client"), true, item.name);
        }
    }
    assert.equal(wire.userHash(vectors.userHash.sid), vectors.userHash.hash);
    const pipe = vectors.pipeName;
    assert.equal(wire.createPipeName(pipe.hostId, pipe.edition, vectors.userHash.hash, pipe.registrationId), pipe.name);
    assert.deepEqual({ ...wire.parsePipeName(pipe.name) }, { hostId: pipe.hostId, edition: pipe.edition, userHash: vectors.userHash.hash, registrationId: pipe.registrationId });
});

test("transcripts sort capabilities ordinally and refuse separators", () => {
    const base = fixtureJson("wire/v3/transcript.json").cases[0].transcript;
    const text = wire.transcriptBytes({ ...base, clientCapabilities: ["b.z", "a.y", "B.x"] }, "client").toString("utf8");
    assert.ok(text.includes("\nB.x,a.y,b.z\n\n"));
    assert.ok(text.startsWith("Ventana.Extensions.v3\nclient\nexample-host\n"));
    assert.throws(() => wire.transcriptBytes({ ...base, clientCapabilities: ["a,b"] }, "server"), TypeError);
    assert.throws(() => wire.transcriptBytes({ ...base, hostId: "a\nb" }, "server"), TypeError);
    assert.throws(() => wire.transcriptBytes(base, "other"), TypeError);
    assert.throws(() => wire.computeProof(Buffer.alloc(31), base, "server"), TypeError);
    assert.equal(wire.verifyProof(Buffer.alloc(31), "x", base, "server"), false);
    const nonce = wire.newNonce();
    assert.notEqual(nonce, wire.newNonce());
    assert.equal(Buffer.from(nonce, "base64").length, 32);
});

test("negotiation picks the highest version both sides support", () => {
    assert.equal(wire.negotiate(3, 3, 3, 3), 3);
    assert.equal(wire.negotiate(1, 5, 3, 4), 4);
    assert.equal(wire.negotiate(3, 4, 3, 3), 3);
    assert.equal(wire.negotiate(4, 5, 3, 3), null);
    assert.equal(wire.negotiate(1, 2, 3, 3), null);
});

test("fixtures/wire/v3/messages-valid.json: every valid frame reads, and canonical frames round-trip", () => {
    for (const item of fixtureJson("wire/v3/messages-valid.json").cases) {
        const frame = frameOf(item);
        const result = wire.readMessage(frame, item.sender, item.phase);
        assert.equal(result.violation, undefined, item.name);
        assert.equal(result.ignored, undefined, item.name);
        assert.equal(result.message.type, item.type, item.name);
        const written = wire.writeMessage(result.message);
        if (item.canonical) assert.equal(written.toString("latin1"), frame.toString("utf8"), item.name);
        for (const b of written) assert.ok(b >= 0x20 && b <= 0x7e, item.name);
        const again = wire.readMessage(written, item.sender, item.phase);
        assert.deepEqual(wire.writeMessage(again.message), written, item.name);
    }
});

test("fixtures/wire/v3/messages-invalid.json: every invalid frame is refused with its code", () => {
    for (const item of fixtureJson("wire/v3/messages-invalid.json").cases) {
        const result = wire.readMessage(frameOf(item), item.sender, item.phase);
        assert.equal(result.message, undefined, item.name);
        assert.equal(result.ignored, undefined, item.name);
        assert.equal(result.violation, item.expect, item.name);
    }
});

test("fixtures/wire/v3/messages-ignored.json: unknown members, types and codes are accepted or ignored", () => {
    for (const item of fixtureJson("wire/v3/messages-ignored.json").cases) {
        const result = wire.readMessage(frameOf(item), item.sender, item.phase);
        assert.equal(result.violation, undefined, item.name);
        if (item.expect === "ignored") {
            assert.equal(result.message, undefined, item.name);
            assert.equal(result.ignored, item.code, item.name);
            continue;
        }
        assert.equal(result.message.type, item.type, item.name);
        if (item.unknownCode) {
            assert.equal(result.message.code, item.unknownCode);
            assert.equal(wire.isKnownReason(result.message.code), false);
        }
    }
});

test("the contract's setFace example is canonical, and frames with unpaired surrogates are refused", () => {
    const frame = "{\"type\":\"setFace\",\"sessionId\":\"0f0e0d0c0b0a09080706050403020100\",\"face\":{\"picture\":{\"$type\":\"glyph\",\"glyph\":\"\\uE916\"},\"line1\":{\"$type\":\"text\",\"text\":\"4:59\"},\"line2\":{\"$type\":\"text\",\"text\":\"Focus\"},\"state\":\"Playing\",\"detail\":\"Four minutes and fifty-nine seconds left.\",\"goodForSeconds\":5}}";
    const result = wire.readMessage(frame, "Companion", "Authenticated");
    assert.deepEqual(result.message.face.picture, { $type: "glyph", glyph: "\uE916" });
    assert.equal(result.message.face.state, "Playing");
    assert.equal(wire.writeMessage(result.message).toString("latin1"), frame);
    for (const bad of ["{\"type\":\"ping\",\"id\":1,\"\\ud800\":1}", "{\"type\":\"ping\",\"id\":1,\"x\":1,\"x\":\"\\udc00\"}", "{\"type\":\"ping\",\"id\":1,\"x\":[\"a\\udbffb\"]}"]) {
        assert.equal(wire.readMessage(bad, "Host", "Authenticated").violation, "frame.json-invalid");
    }
    assert.throws(() => wire.writeMessage({ type: "unknown" }), TypeError);
    assert.throws(() => wire.writeMessage({ type: "result", requestId: "00112233445566778899aabbccddeeff", outcome: "Maybe" }), TypeError);
    assert.throws(() => wire.readMessage("{}", "Nobody", "Handshake"), TypeError);
});

test("fixtures/wire/v3/token-bucket.json: buckets take and refuse as the vectors say", () => {
    for (const item of fixtureJson("wire/v3/token-bucket.json").cases) {
        const clock = new ManualClock();
        const bucket = new wire.TokenBucket(item.rate, item.burst, clock);
        let now = 0;
        for (const step of item.steps) {
            clock.advance(step.atMs - now);
            now = step.atMs;
            const cost = wire.TokenBucket.costOf(step.bytes);
            assert.equal(cost, step.cost, item.name);
            assert.equal(bucket.tryTake(cost), step.take, item.name + " at " + step.atMs);
            assert.equal(bucket.retryAfterMs, step.retryAfterMs, item.name + " at " + step.atMs);
        }
    }
    assert.equal(wire.TokenBucket.costOf(1), 1);
    assert.equal(wire.TokenBucket.costOf(1024), 1);
    assert.equal(wire.TokenBucket.costOf(1025), 2);
    assert.equal(wire.TokenBucket.costOf(65536), 64);
    assert.throws(() => wire.TokenBucket.costOf(0), RangeError);
    assert.throws(() => new wire.TokenBucket(0, 1), RangeError);
    assert.throws(() => new wire.TokenBucket(1, 0), RangeError);
});

test("ready limits: the ranges and the companion's values", () => {
    const limits = wire.protocol3Limits;
    assert.equal(wire.limitsWithinRanges(limits), true);
    assert.equal(wire.limitsWithinRanges({ ...limits, messageBurst: 7, maxFrameBytes: 8192 }), false);
    assert.equal(wire.limitsWithinRanges({ ...limits, pingIntervalMs: 10000, pongTimeoutMs: 10000 }), false);
    const companion = wire.limitsForCompanion({ ...limits, maxSessions: 8, pingIntervalMs: 5000, pongTimeoutMs: 1000 });
    assert.equal(companion.maxSessions, 8);
    assert.equal(companion.pingIntervalMs, 30000);
    assert.equal(companion.pongTimeoutMs, 10000);
});

test("framing reassembles every split and refuses bad lengths and slow frames", () => {
    const message = { type: "ping", id: 7 };
    const frame = wire.encodeFrame(message);
    assert.equal(frame.readUInt32LE(0), frame.length - 4);
    for (let split = 1; split < frame.length; split++) {
        const bodies = [];
        const reader = new wire.FrameReader({ maxFrameBytes: 65536, onFrame: (body) => bodies.push(body.toString("latin1")), onError: (code) => assert.fail(code) });
        reader.push(frame.subarray(0, split));
        assert.equal(bodies.length, 0);
        reader.push(Buffer.concat([frame.subarray(split), frame]));
        assert.deepEqual(bodies, [frame.subarray(4).toString("latin1"), frame.subarray(4).toString("latin1")]);
        reader.close();
    }
    for (const [length, max] of [[0, 8192], [8193, 8192], [65537, 65536]]) {
        const errors = [];
        const reader = new wire.FrameReader({ maxFrameBytes: max, onFrame: () => assert.fail("frame"), onError: (code) => errors.push(code) });
        const header = Buffer.alloc(4);
        header.writeUInt32LE(length, 0);
        reader.push(header);
        assert.deepEqual(errors, ["frame.too-large"]);
    }
    const clock = new ManualClock();
    const errors = [];
    const reader = new wire.FrameReader({ clock, onFrame: () => assert.fail("frame"), onError: (code) => errors.push(code) });
    reader.push(Buffer.from([20]));
    clock.advance(2500);
    reader.push(Buffer.from([0]));
    clock.advance(2499);
    assert.deepEqual(errors, []);
    clock.advance(1);
    assert.deepEqual(errors, ["frame.timeout"]);
    assert.throws(() => wire.encodeFrame(Buffer.alloc(0)), RangeError);
});
