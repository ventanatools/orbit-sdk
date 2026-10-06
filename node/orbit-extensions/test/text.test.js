// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const { fixtureJson, testOptions } = require("./support.js");
const text = require("../lib/text.js");
const { classifyId, isInNamespace, MAX_LENGTH } = require("../lib/ids.js");
const { validateManifest } = require("../index.js");

const rules = fixtureJson("text-rules.json");
const ids = fixtureJson("ids.json");

test("fixtures/ids.json: every id keeps its declared code", () => {
    assert.equal(ids.maxLength, MAX_LENGTH);
    for (const entry of ids.cases) {
        const origin = entry.origin === "first-party" ? "FirstParty" : "ThirdParty";
        assert.equal(classifyId(entry.id, origin), entry.code, JSON.stringify(entry));
    }
});

test("fixtures/ids.json: the lone-leaf and namespace cases hold in a manifest", () => {
    const base = fixtureJson("manifests/valid/lone-leaf.json");
    for (const entry of ids.leafCases) {
        const manifest = { ...base, id: entry.extension, contributions: entry.contributions.map((id) => ({ ...base.contributions[0], id })) };
        const result = validateManifest(manifest, testOptions);
        const errors = result.diagnostics.filter((d) => d.severity === "Error");
        if (entry.code === null) {
            assert.deepEqual(errors, [], JSON.stringify(entry));
        } else {
            assert.deepEqual(errors.map((d) => [d.code, d.path]), [[entry.code, "/contributions/" + entry.index + "/id"]], JSON.stringify(entry));
        }
    }
});

test("namespace matching requires a complete root boundary", () => {
    assert.equal(isInNamespace("now-playing/play-pause", "now-playing"), true);
    assert.equal(isInNamespace("now-playing", "now-playing"), true);
    assert.equal(isInNamespace("now-playing-x/play-pause", "now-playing"), false);
    assert.equal(isInNamespace("now-playing/", "now-playing"), false);
    assert.equal(isInNamespace("contoso.statusbar/build", "contoso.status"), false);
    assert.equal(isInNamespace(undefined, "clock"), false);
});

test("fixtures/text-rules.json: declaration text follows the rule", () => {
    for (const row of rules.declaration) {
        assert.equal(text.checkDeclarationText(row.text, row.maxUnits), row.code, JSON.stringify(row));
    }
});

test("fixtures/text-rules.json: face text is cleaned and cut at text elements", () => {
    for (const row of rules.cleaning) {
        assert.equal(text.clean(row.text, row.maxElements, row.maxUnits), row.expected, JSON.stringify(row));
    }
});

test("fixtures/text-rules.json: each face part is cleaned with its display limits", () => {
    for (const row of rules.face) {
        const limits = rules.faceLimits[row.part];
        const cleaned = text.clean(row.text, limits.elements, limits.units);
        assert.equal(cleaned, row.expected, row.part);
        assert.ok(cleaned.length <= limits.units);
    }
});

test("fixtures/text-rules.json: the shared grammars accept and refuse their cases", () => {
    const check = {
        settingId: text.isSettingId,
        choiceValue: text.isChoiceValue,
        hostId: text.isHostId,
        languageTag: text.isLanguageTag,
        httpsUrl: text.isHttpsUrl,
        glyph: text.isGlyph,
    };
    for (const row of rules.grammars) {
        assert.equal(check[row.kind](row.value), row.valid, JSON.stringify(row));
    }
});

test("the disallowed-character class refuses every format character but the five allowed", () => {
    for (const allowed of [0x200c, 0x200d, 0x200e, 0x200f, 0x061c]) assert.equal(text.isDisallowed(allowed), false);
    for (const refused of [0x200b, 0x2060, 0x00ad, 0xe0001, 0xe007f, 0xfeff, 0xfffd, 0x1fffe, 0x10ffff, 0xd800, -1, 0x110000]) {
        assert.equal(text.isDisallowed(refused), true, refused.toString(16));
    }
    assert.equal(text.isDisallowed(0x41), false);
    assert.equal(text.textElements("👨‍👩‍👧"), 1);
});

test("keys are canonical Base64 of exactly 32 bytes", () => {
    const valid = Buffer.alloc(32).toString("base64");
    assert.equal(text.isKey32(valid), true);
    assert.equal(text.isKey32(valid.slice(0, -1)), false);
    assert.equal(text.isKey32(Buffer.alloc(31).toString("base64")), false);
    assert.equal(text.isKey32("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB="), false);
    assert.equal(text.isKey32("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAE="), true);
    assert.equal(text.isKey32(" AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="), false);
    assert.equal(text.isGuid("0".repeat(32)), false);
    assert.equal(text.isGuid("00112233445566778899aabbccddeeff"), true);
    assert.equal(text.isPackageVersion("1.2.3"), true);
    assert.equal(text.isPackageVersion("01.2.3"), false);
    assert.equal(text.isDottedCode("a.b"), true);
    assert.equal(text.isDottedCode("a"), false);
});
