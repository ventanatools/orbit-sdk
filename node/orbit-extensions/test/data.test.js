// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { fixtureBytes, fixtureJson } = require("./support.js");
const sdk = require("../index.js");
const wire = require("../wire.js");

const lib = path.join(__dirname, "..", "lib");

test("lib/hosts.json is a byte-for-byte copy of fixtures/hosts.json", () => {
    assert.deepEqual(fs.readFileSync(path.join(lib, "hosts.json")), fixtureBytes("hosts.json"));
});

for (const [copy, original] of [
    ["codes/diagnostics.json", "codes/diagnostics.json"],
    ["codes/reason-codes.json", "codes/reason-codes.json"],
    ["codes/precedence.json", "codes/precedence.json"],
    ["codes/capabilities.json", "wire/v3/capabilities.json"],
]) {
    test(`lib/${copy} is a byte-for-byte copy of fixtures/${original}`, () => {
        assert.deepEqual(fs.readFileSync(path.join(lib, ...copy.split("/"))), fixtureBytes(original));
    });
}

test("hosts and findHost read the registry", () => {
    const registry = fixtureJson("hosts.json");
    assert.equal(sdk.hosts.length, registry.hosts.length);
    for (const entry of registry.hosts) {
        const host = sdk.findHost(entry.id);
        assert.equal(host.id, entry.id);
        assert.equal(host.displayName, entry.displayName);
        assert.equal(host.packageExtension, entry.packageExtension);
        assert.equal(host.status, entry.status);
        assert.ok(Object.isFrozen(host));
    }
    assert.deepEqual([...sdk.reservedHostIds], registry.reservedIds);
    assert.equal(sdk.findHost("example-host"), undefined);
    assert.equal(sdk.findHost(undefined), undefined);
    const active = registry.hosts.find((h) => h.status === "Active");
    assert.equal(sdk.firstActiveHost(["example-host", ...registry.hosts.map((h) => h.id)]).id, active.id);
    assert.ok(Object.isFrozen(sdk.hosts));
});

test("the reserved publishers are the set of fixtures/reserved-publishers.json", () => {
    const listed = fixtureJson("reserved-publishers.json").reservedPublishers;
    assert.deepEqual([...sdk.reservedPublishers].sort(), [...listed].sort());
    for (const publisher of listed) {
        assert.equal(sdk.classifyId(publisher + ".tools/run"), "id.root-reserved");
        assert.equal(sdk.classifyId(publisher + ".tools/run", "ThirdParty", []), null);
        assert.equal(sdk.classifyId(publisher + "s.tools/run"), null);
        assert.equal(sdk.classifyId(publisher, "FirstParty"), null);
    }
    assert.equal(sdk.classifyId("contoso.tools/run", "ThirdParty", ["contoso"]), "id.root-reserved");
});

test("the token tables match fixtures/wire/v3/enums.json", () => {
    const enums = fixtureJson("wire/v3/enums.json");
    const tokens = (name) => enums[name].values.map((row) => row.token);
    assert.deepEqual(Object.values(sdk.FaceState), tokens("FaceState"));
    assert.deepEqual(Object.values(sdk.Failure), tokens("Failure"));
    assert.deepEqual(enums.Failure.reserved.map((row) => row.token), ["LlmUnavailable"]);
    assert.equal(Object.values(sdk.Failure).includes("LlmUnavailable"), false);
    assert.deepEqual(Object.values(sdk.Outcome), tokens("Outcome"));
    assert.deepEqual(Object.values(sdk.NetworkUse), tokens("NetworkUse"));
    assert.deepEqual(Object.values(sdk.SettingKind), tokens("SettingKind"));
    assert.deepEqual(Object.values(sdk.Provides), tokens("Provides"));
    for (const name of ["Outcome", "FaceState", "Failure", "PublishResult", "ConnectionState", "HandlerFault"]) {
        assert.ok(Object.isFrozen(sdk[name]), name);
        for (const [key, value] of Object.entries(sdk[name])) assert.equal(key, value);
    }
});

test("the reason-code catalog and help links follow fixtures/codes/reason-codes.json", () => {
    const table = fixtureJson("codes/reason-codes.json").codes;
    assert.equal(Object.keys(wire.ReasonCodes).length, table.length);
    for (const row of table) {
        const info = wire.ReasonCodes[row.code];
        assert.equal(info.pre, row.pre);
        assert.equal(info.violation, row.violation);
        assert.equal(info.disposition.toLowerCase(), row.disposition);
        assert.equal(info.fix, row.fix === null ? undefined : row.fix);
        assert.equal(info.anchor, row.code.replace(/\./g, "-"));
        assert.equal(wire.helpUri(row.code, "example-host"), "https://dev.ventana.tools/go/example-host/codes#" + info.anchor);
    }
    assert.equal(wire.isKnownReason("future.reason"), false);
    assert.equal(wire.isReasonCode("future.reason"), true);
    assert.equal(wire.isReasonCode("Future.Reason"), false);
    const unknown = wire.reasonInfo("future.reason");
    assert.equal(unknown.disposition, "Close");
    assert.equal(unknown.pre, false);
    assert.equal(unknown.violation, false);
    assert.throws(() => wire.helpUri("auth.proof-invalid", "Not A Host"), TypeError);
});

test("the capability registry matches fixtures/wire/v3/capabilities.json", () => {
    const registry = fixtureJson("wire/v3/capabilities.json");
    assert.deepEqual(wire.capabilities.map((row) => row.id), registry.capabilities.map((row) => row.id));
    assert.equal(wire.isExperimentalCapability("x.test-echo"), true);
    assert.equal(wire.isExperimentalCapability("face.image"), false);
    assert.equal(wire.isExperimentalCapability("x."), false);
});

test("the client name and version come from this package's package.json", () => {
    const { clientInfo, fitVersion } = require("../lib/identity.js");
    const packageJson = require("../package.json");
    assert.equal(clientInfo.name, packageJson.name);
    assert.equal(clientInfo.version, packageJson.version);
    assert.equal(fitVersion("0.1.0-preview.1+" + "a".repeat(40)), "0.1.0-preview.1");
    assert.equal(fitVersion("1.2.3"), "1.2.3");
});
