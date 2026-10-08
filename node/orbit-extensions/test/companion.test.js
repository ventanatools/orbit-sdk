// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { fixturePath, until, settle } = require("./support.js");
const { ScriptedPeer } = require("./harness.js");
const { runCompanion, parseArguments, ContributionRouter, Outcome } = require("../index.js");
const { ManualClock } = require("../testing.js");
const { kAppInternals } = require("../lib/companion.js");
const { duplexPair } = require("../lib/client/transport.js");
const { hosts } = require("../lib/hosts.js");

function output() {
    const lines = [];
    let partial = "";
    return {
        lines,
        write(text) {
            partial += text;
            const parts = partial.split("\n");
            partial = parts.pop();
            lines.push(...parts);
        },
    };
}

function workspace() {
    const folder = fs.mkdtempSync(path.join(os.tmpdir(), "sdk-companion-"));
    fs.copyFileSync(fixturePath("manifests/valid/countdown.json"), path.join(folder, "extension.json"));
    return folder;
}

function scriptedTransport(clock, peers) {
    return () => ({
        connect() {
            const [companion, host] = duplexPair();
            peers.push(new ScriptedPeer(host, clock));
            return Promise.resolve({ socket: companion });
        },
    });
}

test("parseArguments takes --manifest, --pairing and --verbose and leaves the rest", () => {
    const parsed = parseArguments(["--manifest", "m.json", "--fast", "--pairing", "p.json", "--verbose", "x"]);
    assert.equal(parsed.manifestPath, "m.json");
    assert.equal(parsed.pairingPath, "p.json");
    assert.equal(parsed.verbose, true);
    assert.deepEqual([...parsed.remaining], ["--fast", "x"]);
    assert.throws(() => parseArguments(["--manifest"]));
});

test("runCompanion exits 2 for a recognised option without its value", async () => {
    const out = output();
    assert.equal(await runCompanion(["--pairing"], {}, { output: out }), 2);
    assert.deepEqual(out.lines, ["ventana: --pairing needs a value."]);
});

test("runCompanion exits 3 for a missing or invalid manifest with watchFiles off, and prints each error", async () => {
    const folder = workspace();
    try {
        const out = output();
        const missing = path.join(folder, "missing.json");
        assert.equal(await runCompanion(["--manifest", missing], {}, { output: out, watchFiles: false }), 3);
        assert.match(out.lines[0], /^ventana: cannot read the manifest .*missing\.json \(ENOENT\)$/);
        const invalid = fixturePath("manifests/invalid/id-grammar.json");
        const second = output();
        assert.equal(await runCompanion(["--manifest", invalid], {}, { output: second, watchFiles: false }), 3);
        assert.deepEqual(second.lines, ["ventana: " + invalid + ": id.grammar /id: The id contains characters, segments or slashes the id grammar does not allow."]);
    } finally {
        fs.rmSync(folder, { recursive: true, force: true });
    }
});

test("runCompanion reports a missing pairing with the code, fix and help link, and exits 3 with watchFiles off", async () => {
    const folder = workspace();
    try {
        const out = output();
        const code = await runCompanion([], {}, { output: out, watchFiles: false, [kAppInternals]: { entryDirectory: folder, hookSigint: false } });
        assert.equal(code, 3);
        // The test host is not in the registry, so the line keeps "the host" and links nowhere.
        assert.deepEqual(out.lines, ["ventana: stopped (pairing.missing) In the host, choose Save connection info."]);
    } finally {
        fs.rmSync(folder, { recursive: true, force: true });
    }
});

test("first run: runCompanion waits for the pairing file, connects, prints status lines, and exits 0 when stopped", async () => {
    const folder = workspace();
    const clock = new ManualClock();
    const peers = [];
    const out = output();
    const stop = new AbortController();
    const statuses = [];
    try {
        const running = runCompanion([], { invoke: async () => Outcome.Done }, {
            output: out,
            signal: stop.signal,
            onStatus: (status) => statuses.push(status.state),
            [kAppInternals]: { entryDirectory: folder, clock, transportFor: scriptedTransport(clock, peers), hookSigint: false },
        });
        await until(() => out.lines.length >= 2, "the first-run lines");
        assert.equal(out.lines[0], "ventana: waiting (pairing.missing) In the host, choose Save connection info.");
        assert.equal(out.lines[1], "ventana: watching " + path.join(folder, "example.countdown.pairing.json"));
        fs.copyFileSync(fixturePath("pairing/valid-lf.json"), path.join(folder, "example.countdown.pairing.json"));
        clock.advance(1000);
        await until(() => peers.length === 1, "a connection");
        await peers[0].connect();
        await until(() => statuses.includes("Connected"), "connected");
        assert.ok(out.lines.includes("ventana: connecting"));
        assert.ok(out.lines.includes("ventana: connected"));
        stop.abort();
        assert.equal(await running, 0);
    } finally {
        fs.rmSync(folder, { recursive: true, force: true });
    }
});

test("runCompanion exits 4 when the client stops with watchFiles off, and prints faults with the author's stack", async () => {
    const folder = workspace();
    const clock = new ManualClock();
    const peers = [];
    const out = output();
    const faults = [];
    const router = new ContributionRouter().mapInvoke("example.countdown/timer", async () => {
        throw new Error("author bug");
    });
    try {
        fs.copyFileSync(fixturePath("pairing/valid-lf.json"), path.join(folder, "pairing.json.txt"));
        const running = runCompanion(["--pairing", path.join(folder, "pairing.json.txt"), "--verbose"], router, {
            output: out,
            watchFiles: false,
            onHandlerFaulted: (fault) => faults.push(fault),
            [kAppInternals]: { entryDirectory: folder, clock, transportFor: scriptedTransport(clock, peers), hookSigint: false },
        });
        await until(() => peers.length === 1, "a connection");
        const peer = peers[0];
        await peer.connect();
        await until(() => out.lines.includes("ventana: connected"), "connected");
        assert.deepEqual(out.lines.slice(0, 2), ["ventana: no handler is mapped for example.countdown/timer", "ventana: no handler is mapped for example.countdown/status"]);
        const sessionId = "0f0e0d0c0b0a09080706050403020100";
        peer.send({ type: "startSession", sessionId, contributionId: "example.countdown/timer", settings: { mode: "start", duration: "five-minutes" } });
        peer.send({ type: "invoke", requestId: "00112233445566778899aabbccddeeff", sessionId });
        assert.equal((await peer.read("Authenticated", "result")).outcome, "Failed");
        await until(() => faults.length === 1, "the fault");
        await settle(5);
        assert.ok(out.lines.includes("ventana: fault Exception in example.countdown/timer (session.handler-faulted) Fix the exception (the SDK passes it to the author)."));
        assert.ok(out.lines.some((line) => line.startsWith("Error: author bug")));
        peer.send({ type: "error", code: "host.access-revoked", message: "Revoked by the person." });
        assert.equal(await running, 4);
        assert.ok(out.lines.includes("ventana: stopped (host.access-revoked) Get new connection info after access is allowed again."));
        assert.ok(out.lines.includes("ventana: host message: Revoked by the person."));
        assert.equal(out.lines.some((line) => line.includes("AAECAw")), false);
    } finally {
        fs.rmSync(folder, { recursive: true, force: true });
    }
});

test("an invalid pairing file prints its code and the watched path", async () => {
    const folder = workspace();
    try {
        fs.copyFileSync(fixturePath("pairing/invalid-secret-short.json"), path.join(folder, "example.countdown.pairing.json"));
        const out = output();
        assert.equal(await runCompanion([], {}, { output: out, watchFiles: false, [kAppInternals]: { entryDirectory: folder, hookSigint: false } }), 3);
        assert.equal(out.lines[0], "ventana: stopped (pairing.secret-invalid) Save connection info again.");
    } finally {
        fs.rmSync(folder, { recursive: true, force: true });
    }
});

test("a registry host names the action and links to its help", async () => {
    const folder = workspace();
    try {
        const host = hosts.find((entry) => entry.status === "Active");
        const manifest = JSON.parse(fs.readFileSync(fixturePath("manifests/valid/countdown.json"), "utf8"));
        manifest.hosts = [host.id];
        const manifestPath = path.join(folder, "extension.json");
        fs.writeFileSync(manifestPath, JSON.stringify(manifest, null, 2));
        const out = output();
        const code = await runCompanion(["--manifest", manifestPath, "--pairing", path.join(folder, "missing.pairing.json")], {}, {
            output: out, watchFiles: false, [kAppInternals]: { entryDirectory: folder, hookSigint: false },
        });
        assert.equal(code, 3);
        assert.deepEqual(out.lines, ["ventana: stopped (pairing.missing) In " + host.displayName + ", choose Save connection info. https://dev.ventana.tools/go/"
            + host.id + "/codes#pairing-missing"]);
    } finally {
        fs.rmSync(folder, { recursive: true, force: true });
    }
});
