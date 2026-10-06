// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { fixturePath, fixtureBytes, fixtureJson, exampleHost, testOptions, expectedShape, countdown } = require("./support.js");
const { readManifest, readManifestFile, validateManifest, computeManifestHash, hosts } = require("../index.js");
const { canonicalProjection } = require("../lib/hash.js");
const { parse, Kind } = require("../lib/json.js");

const WORKED_EXAMPLE_HASH = "1f7c38cf0bc0408b9b62e5d90fdc796c479fe791e7b54fdb701c82cedccb2e8f";

function names(folder, filter) {
    return fs.readdirSync(fixturePath(folder)).filter(filter).sort();
}

for (const file of names("manifests/valid", (name) => name.endsWith(".json"))) {
    test(`fixtures/manifests/valid/${file} reads without diagnostics`, () => {
        const result = readManifest(fixtureBytes("manifests/valid/" + file), testOptions);
        assert.deepEqual(expectedShape(result.diagnostics), []);
        assert.ok(result.manifest);
        assert.ok(Object.isFrozen(result.manifest));
        const again = validateManifest(result.manifest, testOptions);
        assert.deepEqual(again.diagnostics, []);
        const written = Buffer.from(JSON.stringify(result.manifest, null, 2), "utf8");
        const reread = readManifest(written, testOptions);
        assert.ok(reread.manifest);
        assert.equal(computeManifestHash(reread.manifest), computeManifestHash(result.manifest));
    });
}

for (const file of names("manifests/invalid", (name) => name.endsWith(".json") && !name.endsWith(".expected.json") && !name.endsWith(".options.json"))) {
    const name = file.slice(0, -5);
    test(`fixtures/manifests/invalid/${file} gives exactly its expected diagnostics`, () => {
        let options = testOptions;
        const optionsPath = fixturePath("manifests/invalid/" + name + ".options.json");
        if (fs.existsSync(optionsPath)) {
            const raw = JSON.parse(fs.readFileSync(optionsPath, "utf8"));
            options = { ...testOptions, ...raw };
        }
        const result = readManifest(fixtureBytes("manifests/invalid/" + file), options);
        const expected = fixtureJson("manifests/invalid/" + name + ".expected.json").diagnostics;
        assert.deepEqual(expectedShape(result.diagnostics), expected);
        assert.equal(result.diagnostics.length, 1);
        assert.equal(result.manifest, expected.some((d) => d.severity === "Error") ? undefined : result.manifest);
    });
}

function setPointer(document, pointer, value) {
    if (pointer === "") return value;
    const parts = pointer.slice(1).split("/").map((part) => part.replace(/~1/g, "/").replace(/~0/g, "~"));
    let target = document;
    for (const part of parts.slice(0, -1)) target = target[Array.isArray(target) ? Number(part) : part];
    const last = parts[parts.length - 1];
    target[Array.isArray(target) ? Number(last) : last] = value;
    return document;
}

// The precedence cases are applied to parsed trees, so a number such as 3.0 keeps its text.
function memberOf(node, name) {
    return node.members.find((member) => member.name === name);
}

function treeChild(node, part) {
    return node.kind === Kind.Array ? node.items[Number(part)] : memberOf(node, part).value;
}

function setTree(root, pointer, value) {
    const parts = pointer.slice(1).split("/").map((part) => part.replace(/~1/g, "/").replace(/~0/g, "~"));
    let target = root;
    for (const part of parts.slice(0, -1)) target = treeChild(target, part);
    const last = parts[parts.length - 1];
    if (target.kind === Kind.Array) target.items[Number(last)] = value;
    else if (memberOf(target, last)) memberOf(target, last).value = value;
    else target.members.push({ name: last, value });
}

function removeTree(root, pointer) {
    const parts = pointer.slice(1).split("/").map((part) => part.replace(/~1/g, "/").replace(/~0/g, "~"));
    let target = root;
    for (const part of parts.slice(0, -1)) target = treeChild(target, part);
    const last = parts[parts.length - 1];
    if (target.kind === Kind.Array) target.items.splice(Number(last), 1);
    else target.members = target.members.filter((member) => member.name !== last);
}

function writeTree(node) {
    switch (node.kind) {
        case Kind.Object: return "{" + node.members.map((m) => JSON.stringify(m.name) + ":" + writeTree(m.value)).join(",") + "}";
        case Kind.Array: return "[" + node.items.map(writeTree).join(",") + "]";
        case Kind.String: return JSON.stringify(node.text);
        case Kind.Number: return node.text;
        case Kind.True: return "true";
        case Kind.False: return "false";
        default: return "null";
    }
}

test("fixtures/codes/precedence.json: one diagnostic per path, the most specific code", () => {
    const precedence = parse(fixtureBytes("codes/precedence.json")).root;
    for (const item of memberOf(precedence, "cases").value.items) {
        const root = parse(fixtureBytes("manifests/valid/countdown.json")).root;
        for (const pointer of memberOf(item, "remove").value.items) removeTree(root, pointer.text);
        for (const set of memberOf(item, "set").value.members) setTree(root, set.name, set.value);
        const result = readManifest(Buffer.from(writeTree(root), "utf8"), testOptions);
        const atPath = result.diagnostics.filter((d) => d.path === memberOf(item, "path").value.text);
        assert.deepEqual(atPath.map((d) => d.code), [memberOf(item, "expected").value.text], memberOf(item, "name").value.text);
    }
});

// The C# model's undefined enumeration values and flags numbers have no JavaScript form: a manifest
// object built in code carries its tokens as strings, so those four cases do not apply here.
const CSHARP_ONLY = new Set(["kind-undefined", "network-undefined", "provides-none", "provides-undefined-flag"]);

test("fixtures/manifests/built-in-code.json: manifests built in code report nulls without positions", () => {
    const fixture = fixtureJson("manifests/built-in-code.json");
    let ran = 0;
    for (const item of fixture.cases) {
        if (CSHARP_ONLY.has(item.name)) continue;
        let model = fixtureJson(fixture.base);
        for (const [pointer, value] of Object.entries(item.set)) model = setPointer(model, pointer, value);
        const result = validateManifest(model, testOptions);
        assert.deepEqual(result.diagnostics.map((d) => ({ code: d.code, path: d.path })), item.expected, item.name);
        for (const diagnostic of result.diagnostics) {
            assert.equal(diagnostic.line, undefined);
            assert.equal(diagnostic.column, undefined);
        }
        assert.equal(result.manifest, undefined);
        ran++;
    }
    assert.equal(ran, fixture.cases.length - CSHARP_ONLY.size);
});

test("fixtures/wire/v3/manifest-hash.json: every manifest has its canonical projection and hash", () => {
    const vectors = fixtureJson("wire/v3/manifest-hash.json");
    for (const item of vectors.cases) {
        assert.equal(canonicalProjection(item.manifest), item.canonical, item.name);
        assert.equal(computeManifestHash(item.manifest), item.hash, item.name);
    }
});

test("the worked example of contract section 3.9 has the published hash, with and without its host and $schema", () => {
    const manifest = readManifest(fixtureBytes("manifests/valid/countdown.json"), testOptions).manifest;
    assert.equal(computeManifestHash(manifest), WORKED_EXAMPLE_HASH);
    const active = hosts.find((host) => host.status === "Active");
    const text = fixtureBytes("manifests/valid/countdown.json").toString("utf8")
        .replace("\"schemaVersion\": 3,", `"$schema": "https://dev.ventana.tools/schemas/extensions/${active.id}/manifest.v3.json",\n  "schemaVersion": 3,`)
        .replace("\"example-host\"", JSON.stringify(active.id));
    const result = readManifest(Buffer.from(text, "utf8"));
    assert.deepEqual(result.diagnostics, []);
    assert.equal(computeManifestHash(result.manifest), WORKED_EXAMPLE_HASH);
});

test("readManifestFile reads at most the limit plus one byte, and file errors reject", async () => {
    const folder = fs.mkdtempSync(path.join(os.tmpdir(), "sdk-manifest-"));
    try {
        const file = path.join(folder, "extension.json");
        fs.copyFileSync(fixturePath("manifests/valid/countdown.json"), file);
        const read = await readManifestFile(file, testOptions);
        assert.ok(read.manifest);
        fs.writeFileSync(file, Buffer.alloc(70000, 0x20));
        const large = await readManifestFile(file);
        assert.deepEqual(large.diagnostics.map((d) => d.code), ["json.too-large"]);
        await assert.rejects(readManifestFile(path.join(folder, "missing.json")), { code: "ENOENT" });
    } finally {
        fs.rmSync(folder, { recursive: true, force: true });
    }
});

test("a UTF-8 byte order mark is ignored and positions count after it", () => {
    const bytes = Buffer.concat([Buffer.from([0xef, 0xbb, 0xbf]), fixtureBytes("manifests/invalid/json-unknown-member.json")]);
    const result = readManifest(bytes, testOptions);
    assert.deepEqual(expectedShape(result.diagnostics), fixtureJson("manifests/invalid/json-unknown-member.expected.json").diagnostics);
});

test("reporting stops at 200 diagnostics, the last of which is diagnostics.truncated", () => {
    const manifest = countdown();
    manifest.contributions = [];
    for (let i = 0; i < 30; i++) {
        manifest.contributions.push({ id: "X" + i, name: " ", description: " ", glyph: "A", provides: ["run", "run"], settings: [{ id: "1", kind: "Toggle", name: " " }] });
    }
    const result = validateManifest(manifest, testOptions);
    assert.equal(result.diagnostics.length, 200);
    assert.equal(result.diagnostics[199].code, "diagnostics.truncated");
});

test("diagnostics never carry a value from the file", () => {
    const manifest = countdown();
    manifest.name = "SECRET-VALUE\u0000";
    manifest.unexpectedSecret = "SECRET-VALUE";
    const result = validateManifest(manifest, testOptions);
    assert.ok(result.diagnostics.length >= 2);
    for (const diagnostic of result.diagnostics) {
        assert.equal(diagnostic.message.includes("SECRET-VALUE"), false);
        assert.equal(String(diagnostic).startsWith(diagnostic.code + " " + diagnostic.path + ": "), true);
    }
});

test("an unknown host is a warning only, and a known registry host needs no option", () => {
    const manifest = countdown();
    const warned = validateManifest(manifest);
    assert.ok(warned.manifest);
    assert.deepEqual(warned.diagnostics.map((d) => [d.code, d.severity]), [["manifest.host-unknown", "Warning"]]);
    const known = validateManifest(manifest, { knownHosts: [exampleHost] });
    assert.deepEqual(known.diagnostics, []);
});
