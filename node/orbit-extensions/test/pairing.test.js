// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const util = require("node:util");
const { fixturePath, fixtureBytes, fixtureJson, expectedShape } = require("./support.js");
const { readPairing, readPairingFile, defaultPairingPath } = require("../index.js");
const { parsePipeName } = require("../wire.js");

const OPTIONS = Object.freeze({ expectedExtensionId: "example.countdown", manifestHosts: ["example-host"] });
const SECRET = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";
const transcriptVector = fixtureJson("wire/v3/transcript.json").cases[0];

for (const file of fs.readdirSync(fixturePath("pairing")).filter((name) => name.endsWith(".json") && !name.endsWith(".expected.json")).sort()) {
    const name = file.slice(0, -5);
    test(`fixtures/pairing/${file} gives its expected diagnostics`, () => {
        const result = readPairing(fixtureBytes("pairing/" + file), OPTIONS);
        try {
            assert.deepEqual(expectedShape(result.diagnostics), fixtureJson("pairing/" + name + ".expected.json").diagnostics);
            if (name.startsWith("valid-")) {
                assert.ok(result.pairing);
                assert.equal(result.pairing.hostId, "example-host");
                assert.equal(result.pairing.registrationId, "00112233445566778899aabbccddeeff");
                assert.equal(result.pairing.extensionId, "example.countdown");
                assert.equal(result.pairing.pairingVersion, 3);
                assert.equal(result.pairing.mode, "Persistent");
                assert.equal(parsePipeName(result.pairing.pipeName).edition, "store");
            } else {
                assert.equal(result.pairing, undefined);
                assert.ok(/^(pairing|json)\./.test(result.diagnostics[0].code));
            }
        } finally {
            if (result.pairing) result.pairing.dispose();
        }
    });
}

test("proofs match the transcript vector and the secret is never exposed", () => {
    const { pairing } = readPairing(fixtureBytes("pairing/valid-lf.json"), OPTIONS);
    const transcript = transcriptVector.transcript;
    assert.equal(pairing.computeProof(transcript, "server"), transcriptVector.serverProof);
    assert.equal(pairing.computeProof(transcript, "client"), transcriptVector.clientProof);
    assert.equal(pairing.verifyProof(transcriptVector.serverProof, transcript, "server"), true);
    assert.equal(pairing.verifyProof(transcriptVector.serverProof, transcript, "client"), false);
    assert.equal(pairing.verifyProof(undefined, transcript, "server"), false);
    assert.equal(pairing.verifyProof("not base64", transcript, "server"), false);
    for (const shown of [String(pairing), JSON.stringify(pairing), util.inspect(pairing), Object.keys(pairing).join(",")]) {
        assert.equal(shown.includes(SECRET), false);
        assert.equal(shown.includes("secret"), false);
    }
    assert.ok(String(pairing).startsWith("Pairing"));
    pairing.dispose();
    pairing.dispose();
    assert.throws(() => pairing.computeProof(transcript, "client"));
    assert.throws(() => pairing.verifyProof(undefined, transcript, "client"));
});

test("identity, versions, pipes and ambiguous members give fixed codes", () => {
    const text = fixtureBytes("pairing/valid-lf.json").toString("utf8");
    const cases = [
        ["\"pairingVersion\": 3", "\"pairingVersion\": 1", "pairing.version-unsupported"],
        ["\"pairingVersion\": 3", "\"pairingVersion\": 4", "pairing.version-unsupported"],
        ["\"pairingVersion\": 3", "\"pairingVersion\": 3, \"pairingVersion\": 3", "json.duplicate-member"],
        ["\"pairingVersion\": 3", "\"pairingVersion\": 3, \"entrypoint\": \"run.exe\"", "json.unknown-member"],
        ["Ventana.Extensions.v3.example-host", "Ventana.Extensions.v2.example-host", "pairing.pipe-name-invalid"],
        [".store.", ".STORE.", "pairing.pipe-name-invalid"],
        [".store.", ".store.extra.", "pairing.pipe-name-invalid"],
        ["\"extensionId\": \"example.countdown\"", "\"extensionId\": \"other.sdk\"", "pairing.extension-mismatch"],
        ["\"registrationId\": \"00112233445566778899aabbccddeeff\"", "\"registrationId\": \"00112233445566778899AABBCCDDEEFF\"", "pairing.registration-invalid"],
        [".a8c06b3027d3fc4a.", ".a8c06b3027d3fc4g.", "pairing.pipe-name-invalid"],
    ];
    for (const [before, after, code] of cases) {
        assert.ok(text.includes(before));
        const result = readPairing(Buffer.from(text.replace(before, after), "utf8"), OPTIONS);
        assert.equal(result.pairing, undefined);
        assert.ok(result.diagnostics.some((d) => d.code === code), code);
        for (const d of result.diagnostics) {
            assert.equal(d.message.includes("AAECAw"), false);
            assert.equal(d.message.includes("run.exe"), false);
        }
    }
    for (const length of [0, 31, 33]) {
        const changed = text.replace(SECRET, Buffer.alloc(length).toString("base64"));
        assert.deepEqual(readPairing(Buffer.from(changed, "utf8"), OPTIONS).diagnostics.map((d) => d.code), ["pairing.secret-invalid"]);
    }
});

test("the expected extension id is checked, and the default path is per user and per host", () => {
    assert.throws(() => readPairing(Buffer.from("{}"), { expectedExtensionId: "example.countdown/timer" }), TypeError);
    assert.throws(() => readPairing(Buffer.from("{}"), {}), TypeError);
    const file = defaultPairingPath("example-host", "example.countdown");
    assert.equal(file, path.join(os.homedir(), ".ventana", "pairings", "example-host", "example.countdown.pairing.json"));
    assert.throws(() => defaultPairingPath("Example Host", "example.countdown"), TypeError);
    assert.throws(() => defaultPairingPath("example-host", "example.countdown/timer"), TypeError);
});

test("readPairingFile reads at most 4,097 bytes and file errors reject", async () => {
    const folder = fs.mkdtempSync(path.join(os.tmpdir(), "sdk-pairing-"));
    try {
        const file = path.join(folder, "example.countdown.pairing.json");
        fs.copyFileSync(fixturePath("pairing/valid-crlf.json"), file);
        const read = await readPairingFile(file, OPTIONS);
        assert.ok(read.pairing);
        read.pairing.dispose();
        fs.writeFileSync(file, Buffer.alloc(10000, 0x20));
        assert.deepEqual((await readPairingFile(file, OPTIONS)).diagnostics.map((d) => d.code), ["pairing.too-large"]);
        await assert.rejects(readPairingFile(path.join(folder, "missing.json"), OPTIONS), { code: "ENOENT" });
    } finally {
        fs.rmSync(folder, { recursive: true, force: true });
    }
});
