// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * hello.client (contract §7.3.1, §10): this package's own name and version, read from its
 * package.json at run time, cut to the client grammars when needed.
 */

const packageJson = require("../package.json");

function fit(value, max, allowed, fallback) {
    const kept = [...String(value || "")].filter((c) => allowed.test(c)).slice(0, max).join("");
    return kept.length === 0 ? fallback : kept;
}

/** The version within [0-9A-Za-z.+-]{1,32}: as it is when it fits, else without build metadata. */
function fitVersion(version) {
    const value = String(version || "");
    if (/^[0-9A-Za-z.+-]{1,32}$/.test(value)) return value;
    const plus = value.indexOf("+");
    return fit(plus >= 0 ? value.slice(0, plus) : value, 32, /[0-9A-Za-z.+-]/, "0.0.0");
}

const clientInfo = Object.freeze({
    name: fit(packageJson.name, 64, /[A-Za-z0-9@._/+-]/, "client"),
    version: fitVersion(packageJson.version),
});

module.exports = { clientInfo, fitVersion, packageName: packageJson.name, packageVersion: packageJson.version };
