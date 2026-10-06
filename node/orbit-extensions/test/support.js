// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/** Shared test helpers: the repository's fixtures/ folder and small async utilities. */

const fs = require("node:fs");
const path = require("node:path");

const root = path.resolve(__dirname, "..", "..", "..");
const fixtures = path.join(root, "fixtures");

function fixturePath(relative) {
    return path.join(fixtures, ...relative.split("/"));
}

function fixtureBytes(relative) {
    return fs.readFileSync(fixturePath(relative));
}

function fixtureJson(relative) {
    return JSON.parse(fixtureBytes(relative).toString("utf8"));
}

/** The test host every fixture uses (contract Appendix A), as a known active host. */
const exampleHost = Object.freeze({ id: "example-host", displayName: "Example host", aliases: Object.freeze([]), status: "Active" });
const testOptions = Object.freeze({ knownHosts: [exampleHost] });

/** Lets promises, immediates and I/O callbacks run. */
async function settle(rounds = 3) {
    for (let i = 0; i < rounds; i++) await new Promise((resolve) => setImmediate(resolve));
}

/** Waits until `predicate()` is true, polling real time. */
async function until(predicate, what = "condition", timeoutMs = 3000) {
    const deadline = Date.now() + timeoutMs;
    while (!predicate()) {
        if (Date.now() > deadline) throw new Error("Timed out waiting for " + what + ".");
        await new Promise((resolve) => setTimeout(resolve, 2));
    }
}

/** The (code, path, severity, file, line, column) of diagnostics, as the *.expected.json files write them. */
function expectedShape(diagnostics) {
    return diagnostics.map((d) => {
        const row = { code: d.code, path: d.path, severity: d.severity };
        if (d.file !== undefined) row.file = d.file;
        if (d.line !== undefined) row.line = d.line;
        if (d.column !== undefined) row.column = d.column;
        return row;
    });
}

/** The countdown fixture manifest, as an object. */
function countdown() {
    return fixtureJson("manifests/valid/countdown.json");
}

module.exports = { root, fixtures, fixturePath, fixtureBytes, fixtureJson, exampleHost, testOptions, settle, until, expectedShape, countdown };
