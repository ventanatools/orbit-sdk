// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * The test kit (contract §9.3, §10): createTestSession records what a handler publishes,
 * startTestHost runs a real CompanionClient against an in-memory host, and assertManifestValid
 * throws listing every error a host would refuse a manifest for.
 */

const { readManifestFileSync, validateManifest } = require("./lib/manifest.js");
const { createTestSession, createTestInvocation } = require("./lib/testing/session.js");
const { startTestHost } = require("./lib/testing/host.js");
const { ManualClock } = require("./lib/testing/clock.js");

function formatDiagnostic(diagnostic, file) {
    const where = diagnostic.line !== undefined ? file + "(" + diagnostic.line + "," + diagnostic.column + "): " : file ? file + ": " : "";
    return where + diagnostic.toString();
}

/**
 * Throws an Error listing every error a host would refuse the manifest for. `pathOrManifest` is
 * the path of extension.json (read synchronously) or a manifest object. Returns the manifest.
 */
function assertManifestValid(pathOrManifest, options) {
    const isPath = typeof pathOrManifest === "string";
    const read = isPath ? readManifestFileSync(pathOrManifest, options) : validateManifest(pathOrManifest, options);
    const errors = read.diagnostics.filter((diagnostic) => diagnostic.severity === "Error");
    if (errors.length > 0 || !read.manifest) {
        const lines = errors.map((diagnostic) => formatDiagnostic(diagnostic, isPath ? pathOrManifest : ""));
        const error = new Error("The manifest is not valid:\n" + lines.join("\n"));
        error.name = "ConformanceError";
        error.failures = Object.freeze(lines);
        throw error;
    }
    return read.manifest;
}

module.exports = { createTestSession, createTestInvocation, startTestHost, assertManifestValid, ManualClock };
