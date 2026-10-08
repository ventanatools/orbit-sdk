// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * The manifest hash (contract §B.2): SHA-256, in lowercase hexadecimal, of the RFC 8785 canonical
 * form of the manifest's contract projection, which covers schemaVersion, id and, for each
 * contribution, its id, provides and settings with their kinds and choice values. Names,
 * descriptions, glyphs, hosts and $schema do not change it.
 */

const { createHash } = require("node:crypto");

function ordinal(a, b) {
    return a < b ? -1 : a > b ? 1 : 0;
}

/** An RFC 8785 string; manifest ids and tokens never hold lone surrogates. */
function canonicalString(value) {
    if (typeof value !== "string") throw new TypeError("The manifest is not valid.");
    return JSON.stringify(value);
}

function canonicalList(values) {
    return [...values].sort(ordinal).map(canonicalString).join(",");
}

/** The RFC 8785 canonical form of the contract projection of a valid manifest. */
function canonicalProjection(manifest) {
    if (!manifest || typeof manifest.id !== "string" || !Array.isArray(manifest.contributions)) {
        throw new TypeError("The manifest is not valid.");
    }
    const contributions = [...manifest.contributions];
    for (const contribution of contributions) {
        if (!contribution || typeof contribution.id !== "string" || !Array.isArray(contribution.provides)) {
            throw new TypeError("The manifest is not valid.");
        }
    }
    contributions.sort((a, b) => ordinal(a.id, b.id));
    const parts = contributions.map((contribution) => {
        let text = "{\"id\":" + canonicalString(contribution.id) + ",\"provides\":[" + canonicalList(contribution.provides) + "]";
        const settings = contribution.settings === undefined ? [] : contribution.settings;
        if (!Array.isArray(settings)) throw new TypeError("The manifest is not valid.");
        if (settings.length > 0) {
            const sorted = [...settings];
            for (const setting of sorted) {
                if (!setting || typeof setting.id !== "string" || typeof setting.kind !== "string" || !Array.isArray(setting.choices)) {
                    throw new TypeError("The manifest is not valid.");
                }
            }
            sorted.sort((a, b) => ordinal(a.id, b.id));
            text += ",\"settings\":[" + sorted.map((setting) =>
                "{\"choices\":[" + canonicalList(setting.choices.map((choice) => {
                    if (!choice || typeof choice.value !== "string") throw new TypeError("The manifest is not valid.");
                    return choice.value;
                })) + "],\"id\":" + canonicalString(setting.id) + ",\"kind\":" + canonicalString(setting.kind) + "}").join(",") + "]";
        }
        return text + "}";
    });
    const schemaVersion = manifest.schemaVersion === undefined ? 3 : manifest.schemaVersion;
    if (!Number.isInteger(schemaVersion)) throw new TypeError("The manifest is not valid.");
    return "{\"contributions\":[" + parts.join(",") + "],\"id\":" + canonicalString(manifest.id) + ",\"schemaVersion\":" + String(schemaVersion) + "}";
}

/** The manifest hash of a valid manifest: 64 lowercase hexadecimal digits. */
function computeManifestHash(manifest) {
    return createHash("sha256").update(canonicalProjection(manifest), "utf8").digest("hex");
}

module.exports = { canonicalProjection, computeManifestHash };
