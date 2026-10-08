// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * The host-id registry (contract §2.1, §2.3), read from lib/hosts.json: the checked-in copy of
 * the repository's fixtures/hosts.json, the one definition of every host id, display name and
 * package file extension. Nothing else in this package names a host.
 */

const registry = require("./hosts.json");

function freezeHost(entry) {
    return Object.freeze({
        id: entry.id,
        displayName: entry.displayName,
        packageExtension: typeof entry.packageExtension === "string" ? entry.packageExtension : undefined,
        aliases: Object.freeze([...(entry.aliases || [])]),
        status: entry.status === "Active" ? "Active" : "Reserved",
    });
}

/** Every registry entry, active or reserved. */
const hosts = Object.freeze(registry.hosts.map(freezeHost));

/** Every id no third party may claim, active or reserved, with or without an entry. */
const reservedHostIds = Object.freeze([...registry.reservedIds]);

/** The entry whose id or one of whose aliases equals `id`, ordinally, in `knownHosts` (default: the registry). */
function findHost(id, knownHosts = hosts) {
    if (typeof id !== "string") return undefined;
    for (const host of knownHosts) {
        if (!host) continue;
        if (host.id === id || (Array.isArray(host.aliases) && host.aliases.includes(id))) return host;
    }
    return undefined;
}

/** The entry of the first id in `hostIds` that names an active host, by id or alias. */
function firstActiveHost(hostIds, knownHosts = hosts) {
    for (const id of hostIds || []) {
        const host = findHost(id, knownHosts);
        if (host && host.status === "Active") return host;
    }
    return undefined;
}

module.exports = { hosts, reservedHostIds, findHost, firstActiveHost };
