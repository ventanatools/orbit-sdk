// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { EventEmitter, once } = require("node:events");
const { createHmac } = require("node:crypto");
const { parseRecordV2 } = require("../uxp/wire-v2.js");
const { equalProof, bytes32, encodeFrame, FramedSocket } = require("../companion/protocol.cjs");
const { proofV2, parsePairing } = require("../companion/protocol-v2.cjs");
const { parseBridgeConfig } = require("../uxp/client-v2.js");
const { bridgeProof } = require("../uxp/vendor/bridge-crypto.js");
const vector = require("./fixtures/protocol-v2.json");
const { independentFrame, delay } = require("./helpers.cjs");

class StubSocket extends EventEmitter {
    destroyed = false;
    writableLength = 0;
    writes = [];
    write(data) { this.writes.push(data); }
    destroy() { if (!this.destroyed) { this.destroyed = true; this.emit("close"); } }
}

test("exact HMAC vector, role separation, and canonical Base64", () => {
    for (const role of ["server", "client"]) {
        assert.equal(proofV2(vector.secret, role, vector.registrationId, vector.clientNonce, vector.serverNonce), vector[`${role}Proof`]);
    }
    assert.equal(equalProof(vector.serverProof, vector.clientProof), false);
    assert.equal(equalProof(vector.serverProof, vector.serverProof), true);
    assert.equal(equalProof("nonsense", vector.serverProof), false);
    assert.throws(() => bytes32(vector.secret.slice(0, -2) + "9="));
});

test("bundled UXP HMAC agrees with independent Node crypto", () => {
    const secret = "00".repeat(32), cn = "11".repeat(32), sn = "22".repeat(32);
    for (const role of ["server", "client"]) {
        const expected = createHmac("sha256", Buffer.from(secret, "hex"))
            .update(`Orbit.Photoshop.Bridge.v2\n${role}\n${cn}\n${sn}`).digest("hex");
        assert.equal(bridgeProof(secret, role, cn, sn), expected);
    }
});

for (const [name, text] of Object.entries({ duplicate: '{"type":"ready","type":"ready"}',
    escapedDuplicate: '{"type":"ready","\\u0074ype":"ready"}', nestedDuplicate: '{"settings":{"mode":"show","mode":"hide"}}',
    trailingComma: '{"type":"ready",}', trailingData: '{"type":"ready"}{}', control: '{"type":"a\nb"}' })) {
    test(`strict parser rejects ${name}`, () => assert.throws(() => parseRecordV2(text)));
}

test("pairing is strict and cannot redirect to arbitrary pipes or another extension", () => {
    const pairing = { protocolVersion: 2, pipeName: `Orbit.Extensions.v2.dev.0123456789abcdef.${vector.registrationId}`,
        registrationId: vector.registrationId, extensionId: "example.photoshop", secret: vector.secret };
    assert.equal(parsePairing(JSON.stringify(pairing)).registrationId, vector.registrationId);
    for (const change of [{ pipeName: "evil" }, { extensionId: "other.extension" }, { secret: "" },
        { path: "program.exe" }, { protocolVersion: 1 }, { protocolVersion: 3 },
        { pipeName: pairing.pipeName.replace(".v2.", ".v1.") }, { registrationId: "a".repeat(32) }]) {
        assert.throws(() => parsePairing(JSON.stringify({ ...pairing, ...change })));
    }
});

test("UXP configuration admits only v2 and the exact canonical bridge endpoint", () => {
    const config = { protocolVersion: 2, url: "ws://127.0.0.1:38475/orbit-photoshop", secret: "00".repeat(32) };
    assert.equal(parseBridgeConfig(JSON.stringify(config)).protocolVersion, 2);
    for (const change of [{ protocolVersion: 1 }, { protocolVersion: 3 }, { secret: "" },
        { url: "ws://localhost:38475/orbit-photoshop" }, { url: "ws://127.0.0.1:38476/orbit-photoshop" },
        { url: "ws://127.0.0.1:38475/other" }, { extra: "unknown" }])
        assert.throws(() => parseBridgeConfig(JSON.stringify({ ...config, ...change })));
});

test("framing uses byte count, reassembles every split, and reads coalesced frames", () => {
    const message = { type: "test", value: "é" };
    const frame = encodeFrame(message);
    assert.equal(frame.readUInt32LE(), Buffer.byteLength(JSON.stringify(message)));
    assert.deepEqual(frame, independentFrame(message));
    for (let split = 1; split < frame.length; split++) {
        const socket = new StubSocket(), stream = new FramedSocket(socket);
        const received = [];
        stream.on("message", value => received.push({ ...value }));
        socket.emit("data", frame.subarray(0, split));
        assert.equal(received.length, 0);
        socket.emit("data", Buffer.concat([frame.subarray(split), frame]));
        assert.deepEqual(received, [message, message]);
        stream.destroy();
    }
});

test("framing rejects zero, oversized, invalid UTF8, and duplicate fields", () => {
    const zero = Buffer.alloc(4), oversized = Buffer.alloc(4);
    oversized.writeUInt32LE(65537);
    const invalid = Buffer.from([2, 0, 0, 0, 0xc0, 0x80]);
    const body = Buffer.from('{"type":"a","type":"b"}');
    const duplicate = Buffer.alloc(body.length + 4); duplicate.writeUInt32LE(body.length); body.copy(duplicate, 4);
    for (const frame of [zero, oversized, invalid, duplicate]) {
        const socket = new StubSocket(), stream = new FramedSocket(socket);
        stream.on("message", () => assert.fail("invalid frame dispatched"));
        socket.emit("data", frame);
        assert.equal(socket.destroyed, true);
    }
});

test("partial-frame deadline does not extend with arriving bytes", async () => {
    const socket = new StubSocket(), stream = new FramedSocket(socket, { partialMs: 40 });
    const closed = once(stream, "close");
    socket.emit("data", Buffer.from([20]));
    await delay(20);
    socket.emit("data", Buffer.from([0]));
    await closed;
    assert.equal(socket.destroyed, true);
});

test("frame rate is bounded", () => {
    const socket = new StubSocket(), stream = new FramedSocket(socket);
    let count = 0;
    stream.on("message", () => count++);
    socket.emit("data", Buffer.concat(Array.from({ length: 129 }, () => independentFrame({ type: "ready" }))));
    assert.equal(count, 128);
    assert.equal(socket.destroyed, true);
});
