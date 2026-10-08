// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * The diagnostic code table (contract §4.4), the one-diagnostic-per-path precedence (§4.2) and
 * the reason-code catalog (§8.3). lib/codes/ holds checked-in copies of the repository's
 * fixtures/codes/*.json and fixtures/wire/v3/capabilities.json; a test compares them byte for byte.
 */

const diagnosticsTable = require("./codes/diagnostics.json");
const precedenceTable = require("./codes/precedence.json");
const reasonTable = require("./codes/reason-codes.json");
const capabilityTable = require("./codes/capabilities.json");
const { isDottedCode, isHostId } = require("./text.js");

const HELP_ROOT = "https://dev.ventana.tools/go/";

const diagnosticInfo = new Map(diagnosticsTable.codes.map((row) => [row.code, Object.freeze({ ...row })]));

const ranks = new Map();
for (const tier of precedenceTable.tiers) {
    for (const code of tier.codes) {
        if (!ranks.has(code)) ranks.set(code, ranks.size);
    }
}

/** The position of `code` in the precedence order; unlisted codes come last. */
function precedenceRank(code) {
    return ranks.has(code) ? ranks.get(code) : Number.MAX_SAFE_INTEGER;
}

const DISPOSITIONS = Object.freeze({ close: "Close", advisory: "Advisory", refuse: "Refuse", ignore: "Ignore", local: "Local" });

function freezeReason(row) {
    return Object.freeze({
        code: row.code,
        disposition: DISPOSITIONS[row.disposition] || "Close",
        seenIn: Object.freeze([...row.seenIn]),
        pre: row.pre === true,
        violation: row.violation === true,
        meaning: row.meaning,
        fix: typeof row.fix === "string" ? row.fix : undefined,
        anchor: typeof row.anchor === "string" ? row.anchor : undefined,
    });
}

/** Every reason code of the catalog, keyed by code. */
const ReasonCodes = Object.freeze(Object.fromEntries(reasonTable.codes.map((row) => [row.code, freezeReason(row)])));

/** The rules of a code no catalog has (contract §7.5): it closes, and it is never terminal, a violation or advisory. */
const UNKNOWN_REASON = Object.freeze({
    code: undefined,
    disposition: "Close",
    seenIn: Object.freeze([]),
    pre: false,
    violation: false,
    meaning: undefined,
    fix: undefined,
    anchor: undefined,
});

/** Whether the catalog has `code`. */
function isKnownReason(code) {
    return typeof code === "string" && Object.prototype.hasOwnProperty.call(ReasonCodes, code);
}

/** The catalog's information for `code`, or the rules for an unknown code. */
function reasonInfo(code) {
    return isKnownReason(code) ? ReasonCodes[code] : UNKNOWN_REASON;
}

/** Whether `code` is a grammar-valid reason code, known or not (an open registry). */
function isReasonCode(code) {
    return isDottedCode(code);
}

/** https://dev.ventana.tools/go/<host-id>/codes#<anchor>, or undefined for a code without an anchor. */
function helpUri(code, hostId) {
    if (!isHostId(hostId)) throw new TypeError("The host id does not match the host-id grammar.");
    const info = reasonInfo(code);
    return info.anchor ? HELP_ROOT + hostId + "/codes#" + info.anchor : undefined;
}

/** The capability registry (contract §7.4). */
const capabilities = Object.freeze(capabilityTable.capabilities.map((row) => Object.freeze({ ...row })));
const capabilityIds = new Set(capabilities.map((row) => row.id));

function isKnownCapability(id) {
    return capabilityIds.has(id);
}

/** "x." segment ( "." segment )*: an experimental capability id. */
function isExperimentalCapability(id) {
    return typeof id === "string" && id.startsWith("x.") && isDottedCode(id);
}

/**
 * A diagnostic (contract §4.1): code, path (RFC 6901), a fixed message, severity, and where
 * possible the file, line and UTF-8 byte column. The message never contains a value from the file.
 */
class Diagnostic {
    constructor(code, path, file, line, column) {
        const info = diagnosticInfo.get(code);
        this.code = code;
        this.path = path;
        this.message = info ? info.message : (isKnownReason(code) ? ReasonCodes[code].meaning : "Unknown problem.");
        this.severity = info ? info.severity : "Error";
        if (file !== undefined && file !== null) this.file = file;
        if (line !== undefined && line !== null) this.line = line;
        if (column !== undefined && column !== null) this.column = column;
        Object.freeze(this);
    }

    /** "<code> <path>: <message>" */
    toString() {
        return this.code + " " + this.path + ": " + this.message;
    }
}

/** The fix a diagnostic code's catalog entry gives; `{tool}` stands for the tool's command name. */
function diagnosticFix(code) {
    const info = diagnosticInfo.get(code);
    if (info) return info.fix;
    return isKnownReason(code) ? ReasonCodes[code].fix : undefined;
}

/**
 * Collects diagnostics with the rules of contract §4.2: at most one per file and path (the most
 * specific code wins), and at most 200, the last of which is then diagnostics.truncated.
 */
class DiagnosticBag {
    constructor() {
        this.items = [];
        this.index = new Map();
    }

    get hasErrors() {
        return this.items.some((item) => item.severity === "Error");
    }

    add(diagnostic) {
        const key = (diagnostic.file || "") + "\u0000" + diagnostic.path;
        const at = this.index.get(key);
        if (at !== undefined) {
            if (precedenceRank(diagnostic.code) < precedenceRank(this.items[at].code)) this.items[at] = diagnostic;
            return;
        }
        this.index.set(key, this.items.length);
        this.items.push(diagnostic);
    }

    report(code, path, file, line, column) {
        this.add(new Diagnostic(code, path, file, line, column));
    }

    toList() {
        if (this.items.length < 200) return Object.freeze([...this.items]);
        return Object.freeze([...this.items.slice(0, 199), new Diagnostic("diagnostics.truncated", "")]);
    }
}

module.exports = {
    Diagnostic,
    DiagnosticBag,
    diagnosticFix,
    precedenceRank,
    ReasonCodes,
    reasonInfo,
    isKnownReason,
    isReasonCode,
    helpUri,
    capabilities,
    isKnownCapability,
    isExperimentalCapability,
};
