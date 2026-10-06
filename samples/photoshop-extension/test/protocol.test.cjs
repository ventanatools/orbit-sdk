// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { createHmac } = require("node:crypto");
const constants = require("../uxp/constants.js");
const { parseRecord, parseBridgeConfig, isResult, isFace, isStart, isInvoke } = require("../uxp/wire.js");
const { bridgeProof: uxpProof } = require("../uxp/vendor/bridge-crypto.js");
const { bridgeProof } = require("../companion/bridge.cjs");

for (const [name, text] of Object.entries({
    duplicate: "{\"type\":\"ready\",\"type\":\"ready\"}",
    escapedDuplicate: "{\"type\":\"ready\",\"\\u0074ype\":\"ready\"}",
    nestedDuplicate: "{\"settings\":{\"mode\":\"show\",\"mode\":\"hide\"}}",
    trailingComma: "{\"type\":\"ready\",}",
    trailingData: "{\"type\":\"ready\"}{}",
    control: "{\"type\":\"a\nb\"}",
    tooDeep: "{\"a\":".repeat(10) + "0" + "}".repeat(10),
    fraction: "{\"goodForSeconds\":1.5}",
    nullValue: "{\"type\":null}",
})) {
    test(`the strict parser refuses ${name}`, () => assert.throws(() => parseRecord(text)));
}

test("the bridge proof is HMAC-SHA256 over the v3 label, the role and both nonces, in Node and in the UXP bundle", () => {
    const secret = "00".repeat(32);
    const clientNonce = "11".repeat(32);
    const serverNonce = "22".repeat(32);
    assert.equal(constants.HANDSHAKE_LABEL, "Example.Photoshop.Bridge.v3");
    for (const role of ["server", "client"]) {
        const expected = createHmac("sha256", Buffer.from(secret, "hex"))
            .update(["Example.Photoshop.Bridge.v3", role, clientNonce, serverNonce].join("\n")).digest("hex");
        assert.equal(bridgeProof(secret, role, clientNonce, serverNonce), expected);
        assert.equal(uxpProof(secret, role, clientNonce, serverNonce), expected);
    }
    assert.notEqual(bridgeProof(secret, "server", clientNonce, serverNonce), bridgeProof(secret, "client", clientNonce, serverNonce));
});

test("the panel admits only a protocol-3 bridge file for the canonical endpoint", () => {
    const config = { bridgeVersion: 3, url: "ws://127.0.0.1:38475/photoshop-bridge", secret: "00".repeat(32) };
    assert.equal(parseBridgeConfig(JSON.stringify(config)).bridgeVersion, 3);
    for (const change of [{ bridgeVersion: 2 }, { bridgeVersion: 4 }, { secret: "" }, { secret: "AA".repeat(32) },
        { url: "ws://localhost:38475/photoshop-bridge" }, { url: "ws://127.0.0.1:38476/photoshop-bridge" },
        { url: "ws://127.0.0.1:38475/other" }, { extra: "unknown" }]) {
        assert.throws(() => parseBridgeConfig(JSON.stringify({ ...config, ...change })), JSON.stringify(change));
    }
    assert.throws(() => parseBridgeConfig("x".repeat(2049)));
});

test("bridge messages are checked exactly", () => {
    const id = "a".repeat(32);
    assert.equal(isStart({ type: "startSession", sessionId: id, contributionId: constants.TOGGLE_ID, settings: { mode: "hide" } }), true);
    assert.equal(isStart({ type: "startSession", sessionId: id, contributionId: constants.TOGGLE_ID, settings: { mode: "delete" } }), false);
    assert.equal(isStart({ type: "startSession", sessionId: id, contributionId: "example.photoshop/run-script", settings: { mode: "hide" } }), false);
    assert.equal(isInvoke({ type: "invoke", requestId: id, sessionId: id, contributionId: constants.STATUS_ID, settings: { display: "selection" } }), true);
    assert.equal(isResult({ type: "result", requestId: id, outcome: "Done" }), true);
    assert.equal(isResult({ type: "result", requestId: id, outcome: "Failed", failure: "AppUnavailable" }), true);
    assert.equal(isResult({ type: "result", requestId: id, outcome: "Done", failure: "Network" }), false);
    assert.equal(isResult({ type: "result", requestId: id, outcome: "done" }), false);
    const face = { type: "face", sessionId: id, face: { line1: "Shown", state: "On", detail: "The selected layer is visible.", goodForSeconds: constants.FACE_SECONDS } };
    assert.equal(isFace(face), true);
    assert.equal(isFace({ ...face, face: { ...face.face, line1: "My secret layer" } }), false);
    assert.equal(isFace({ ...face, face: { ...face.face, picture: { $type: "image" } } }), false);
    assert.equal(isFace({ type: "fail", sessionId: id, failure: "AppUnavailable" }), true);
    assert.equal(isFace({ type: "fail", sessionId: id, failure: "NoData" }), false);
    assert.equal(isFace({ type: "clearFace", sessionId: id }), true);
});
