// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * The extension id grammar (contract §2.4, §3.4, §3.5):
 *   segment     = [a-z0-9]+ ( "-" [a-z0-9]+ )*
 *   first party = segment
 *   third party = segment ( "." segment )+
 *   id          = root ( "/" segment )*
 */

const { reservedHostIds } = require("./hosts.js");
const { isSegment } = require("./text.js");

const MAX_LENGTH = 128;

const DEVICE_NAMES = new Set([
    "con", "prn", "aux", "nul",
    "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
    "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
]);

/**
 * The reserved publishers (contract §3.5): every reserved host id of the registry, which holds
 * each current, candidate and former product name, and the reserved names that are not products.
 * The repository's fixtures/reserved-publishers.json lists the same set; a test compares them.
 */
const reservedPublishers = Object.freeze([...reservedHostIds, "ventana", "ventanatools", "ext"]);

/**
 * Classifies `id` for `origin` ("FirstParty" or "ThirdParty"). Returns null when valid, else the
 * first code that applies: id.required, id.grammar, id.root-not-dotted or id.root-dotted,
 * id.root-reserved, id.too-long. A first segment that is a Windows device name is
 * id.root-reserved whatever list is passed.
 */
function classifyId(id, origin = "ThirdParty", reserved = reservedPublishers) {
    if (typeof id !== "string" || /^\p{White_Space}*$/u.test(id)) return "id.required";
    const slash = id.indexOf("/");
    if (slash === 0) return "id.grammar";
    const root = slash < 0 ? id : id.slice(0, slash);
    if (slash >= 0) {
        const rest = id.slice(slash + 1);
        if (rest.length === 0 || !rest.split("/").every(isSegment)) return "id.grammar";
    }
    const single = isSegment(root);
    const parts = root.split(".");
    const dotted = !single && parts.length >= 2 && parts.every(isSegment);
    if (!single && !dotted) return "id.grammar";
    if (origin === "FirstParty" && dotted) return "id.root-dotted";
    if (origin !== "FirstParty" && single) return "id.root-not-dotted";
    const first = parts[0];
    if (DEVICE_NAMES.has(first)) return "id.root-reserved";
    if (origin !== "FirstParty" && (reserved || reservedPublishers).includes(first)) return "id.root-reserved";
    return id.length > MAX_LENGTH ? "id.too-long" : null;
}

function isValidId(id, origin = "ThirdParty", reserved = reservedPublishers) {
    return classifyId(id, origin, reserved) === null;
}

/** Whether `id` is `root` itself or sits under it (`root` and a slash). Ordinal; the grammar is not checked. */
function isInNamespace(id, root) {
    if (typeof id !== "string" || typeof root !== "string" || id.length === 0 || root.length === 0) return false;
    return id === root || (id.length > root.length + 1 && id[root.length] === "/" && id.startsWith(root));
}

/** Whether `id` is an extension id (third party, no slash), whatever publisher it names. */
function isExtensionId(id) {
    return isValidId(id, "ThirdParty", []) && !id.includes("/");
}

module.exports = { MAX_LENGTH, reservedPublishers, classifyId, isValidId, isInNamespace, isExtensionId };
