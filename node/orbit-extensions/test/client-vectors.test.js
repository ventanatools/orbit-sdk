// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * Every fixtures/wire/v3 vector a companion receives, run through the real client. The Node SDK
 * cannot verify a pipe's owner (contract §10), so every server is unverified until its challenge
 * verifies: the handshake vectors' "unverified" expectations apply.
 */

const test = require("node:test");
const assert = require("node:assert/strict");
const { fixtureJson } = require("./support.js");
const { ClientHarness, ScriptedPeer } = require("./harness.js");
const wire = require("../wire.js");
const { clientInfo } = require("../lib/identity.js");
const { computeManifestHash } = require("../index.js");
const { countdown, settle, until } = require("./support.js");

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

test("hello carries this package's name and version, the offered capabilities and the manifest hash", async () => {
    const harness = new ClientHarness().start();
    try {
        const peer = await harness.nextPeer();
        const hello = await peer.readHello();
        assert.deepEqual(hello.client, { name: clientInfo.name, version: clientInfo.version });
        assert.deepEqual([...hello.capabilities], ["x.test-echo"]);
        assert.equal(hello.manifestHash, computeManifestHash(countdown()));
        assert.equal(hello.registrationId, "00112233445566778899aabbccddeeff");
        assert.equal(hello.minVersion, 3);
        assert.equal(hello.maxVersion, 3);
        assert.equal((await harness.waitForState("Connecting")).attempt, 1);
        assert.equal(harness.statuses[0].state, "Connecting");
    } finally {
        await harness.stop();
    }
});

const handshakes = fixtureJson("wire/v3/handshake.json").cases.filter((item) => item.sdk);

for (const item of handshakes) {
    test(`fixtures/wire/v3/handshake.json: ${item.name}`, async () => {
        const expected = item.sdk.unverified;
        const challengeValid = !!(item.proofs && item.proofs.challengeValid);
        const harness = new ClientHarness().start();
        try {
            const peer = await harness.nextPeer();
            await peer.readHello();
            let readySent = false;
            for (const step of item.steps.slice(1)) {
                if (typeof step.atMs === "number" && step.atMs > harness.clock.now()) {
                    if (readySent) await harness.waitForState("Connected");
                    await settle(5);
                    harness.clock.advance(step.atMs - harness.clock.now());
                    await settle(5);
                }
                if (step.from === "Companion") {
                    const type = JSON.parse(step.frame).type;
                    const message = await peer.read(step.phase, type);
                    assert.equal(message.type, type);
                    continue;
                }
                if (step.phase === "PeerVerified") readySent = true;
                const parsed = wire.readMessage(step.frame, "Host", step.phase);
                if (parsed.message && parsed.message.type === "challenge" && challengeValid) {
                    // The SDK's hello has its own nonce and manifest hash, so a valid proof is recomputed over it.
                    peer.send(peer.buildChallenge({
                        version: parsed.message.version,
                        hostId: parsed.message.host.id,
                        hostVersion: parsed.message.host.version,
                        capabilities: parsed.message.capabilities,
                    }));
                    continue;
                }
                peer.sendJson(step.frame);
            }
            if (expected.state === "Connected") {
                await harness.waitForState("Connected");
                await settle(10);
                assert.equal(harness.client.state, "Connected");
                return;
            }
            const status = await harness.waitForStatus((s) => s.state === expected.state, expected.state);
            assert.equal(status.reason, expected.reason ?? undefined);
            if (typeof expected.retryAtMostMs === "number") {
                assert.ok(typeof status.retryInMs === "number" && status.retryInMs >= 0 && status.retryInMs <= expected.retryAtMostMs, String(status.retryInMs));
            }
        } finally {
            await harness.stop();
        }
    });
}

async function connectTo(harness, phase) {
    const peer = await harness.nextPeer();
    await peer.readHello();
    if (phase !== "Handshake") {
        peer.send(peer.buildChallenge());
        await peer.read("Handshake", "authenticate");
    }
    if (phase === "Authenticated") {
        peer.send(ScriptedPeer.buildReady());
        await harness.waitForState("Connected");
    }
    return peer;
}

for (const item of fixtureJson("wire/v3/messages-invalid.json").cases.filter((entry) => entry.sender === "Host")) {
    test(`fixtures/wire/v3/messages-invalid.json: the client closes on ${item.name}`, async () => {
        const harness = new ClientHarness().start();
        try {
            const peer = await connectTo(harness, item.phase);
            peer.sendBody(frameOf(item));
            assert.equal(await peer.readClose(), item.expect);
            const status = await harness.waitForStatus((s) => s.state === "Waiting" || s.state === "Stopped", "closed");
            assert.equal(status.reason, item.expect);
        } finally {
            await harness.stop();
        }
    });
}

for (const item of fixtureJson("wire/v3/messages-ignored.json").cases.filter((entry) => entry.sender === "Host")) {
    test(`fixtures/wire/v3/messages-ignored.json: the client accepts or ignores ${item.name}`, async () => {
        const harness = new ClientHarness().start();
        try {
            const parsed = wire.readMessage(item.frame, "Host", item.phase);
            if (item.phase === "Handshake" && parsed.message && parsed.message.type === "challenge") {
                const peer = await harness.nextPeer();
                await peer.readHello();
                peer.send(peer.buildChallenge({ capabilities: parsed.message.capabilities }));
                await peer.read("Handshake", "authenticate");
                peer.send(ScriptedPeer.buildReady({ capabilities: parsed.message.capabilities }));
                await harness.waitForState("Connected");
                return;
            }
            const peer = await connectTo(harness, item.phase);
            peer.sendJson(item.frame);
            if (item.unknownCode) {
                const status = await harness.waitForStatus((s) => s.state === "Waiting", "waiting");
                assert.equal(status.reason, item.unknownCode);
                assert.ok(status.retryInMs >= 0 && status.retryInMs <= 30000);
                return;
            }
            if (item.phase === "PeerVerified") {
                await harness.waitForState("Connected");
            } else {
                // A host ping is the barrier: its pong proves the frame was read and the connection kept.
                const barrier = parsed.message && parsed.message.type === "ping" ? parsed.message.id : 99;
                if (!(parsed.message && parsed.message.type === "ping")) peer.send({ type: "ping", id: barrier });
                assert.equal((await peer.read("Authenticated", "pong")).id, barrier);
            }
            await settle(5);
            assert.equal(harness.client.state, "Connected");
        } finally {
            await harness.stop();
        }
    });
}

const sessions = fixtureJson("wire/v3/sessions.json");

for (const item of sessions.cases) {
    test(`fixtures/wire/v3/sessions.json: ${item.name}`, async () => {
        const seen = [];
        const handler = { runSession: async (session) => seen.push(session) };
        const harness = new ClientHarness({ handler, manifest: fixtureJson(sessions.manifest) }).start();
        try {
            const peer = await harness.connected();
            const sessionId = "0f0e0d0c0b0a09080706050403020100";
            peer.send({ type: "startSession", sessionId, contributionId: item.contributionId, settings: item.settings });
            if (item.code === null) {
                await until(() => seen.length === 1, "the session");
                assert.equal(seen[0].id, sessionId);
                assert.deepEqual({ ...seen[0].settings }, item.settings);
                return;
            }
            const refused = await peer.read("Authenticated", "sessionRefused");
            assert.equal(refused.sessionId, sessionId);
            assert.equal(refused.code, item.code);
            await settle(5);
            assert.equal(seen.length, 0);
            assert.equal(harness.client.state, "Connected");
        } finally {
            await harness.stop();
        }
    });
}
