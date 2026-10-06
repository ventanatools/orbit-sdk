// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * The handshake (contract §7.3): nonces, version negotiation, the proof transcript and proofs,
 * and pipe names (§2.5).
 */

const { createHash, createHmac, randomBytes, timingSafeEqual } = require("node:crypto");
const { decodeKey32, isHostId, isEdition, isUserHash, isGuid } = require("../text.js");

const PROTOCOL_MIN = 3;
const PROTOCOL_MAX = 3;
const LABEL = "Ventana.Extensions.v3";
const PIPE_PREFIX = "Ventana.Extensions.v3.";
const ROLES = new Set(["server", "client"]);

/** 32 fresh random bytes in canonical Base64. */
function newNonce() {
    return randomBytes(32).toString("base64");
}

/** The highest version in both ranges, or null when they do not overlap. */
function negotiate(clientMin, clientMax, hostMin, hostMax) {
    const high = Math.min(clientMax, hostMax);
    const low = Math.max(clientMin, hostMin);
    return clientMin <= clientMax && hostMin <= hostMax && high >= low ? high : null;
}

function field(value) {
    if (typeof value !== "string" || value.includes("\n") || value.includes(",")) {
        throw new TypeError("A transcript value is not a string, or contains a line feed or a comma.");
    }
    return value;
}

function decimal(value) {
    if (!Number.isInteger(value)) throw new TypeError("A transcript version is not an integer.");
    return String(value);
}

function capabilityList(values) {
    if (!Array.isArray(values)) throw new TypeError("A capability list is not an array.");
    return values.map(field).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0)).join(",");
}

/**
 * The UTF-8 transcript (contract §7.3.4) for `role` ("server" or "client"). `transcript` holds
 * hostId, hostVersion, registrationId, clientNonce, serverNonce, version, minVersion, maxVersion,
 * clientCapabilities, hostCapabilities and manifestHash.
 */
function transcriptBytes(transcript, role) {
    if (!ROLES.has(role)) throw new TypeError("The role is not server or client.");
    if (!transcript || typeof transcript !== "object") throw new TypeError("The transcript is not an object.");
    const fields = [
        LABEL,
        role,
        field(transcript.hostId),
        field(transcript.hostVersion),
        field(transcript.registrationId),
        field(transcript.clientNonce),
        field(transcript.serverNonce),
        decimal(transcript.version),
        decimal(transcript.minVersion),
        decimal(transcript.maxVersion),
        capabilityList(transcript.clientCapabilities),
        capabilityList(transcript.hostCapabilities),
        field(transcript.manifestHash),
    ];
    return Buffer.from(fields.join("\n"), "utf8");
}

function secretBytes(secret) {
    if (typeof secret === "string") {
        const decoded = decodeKey32(secret);
        if (!decoded) throw new TypeError("The secret is not 32 bytes of canonical Base64.");
        return { bytes: decoded, owned: true };
    }
    if (secret instanceof Uint8Array && secret.length === 32) return { bytes: secret, owned: false };
    throw new TypeError("The secret is not 32 bytes.");
}

/** A proof: canonical Base64 of HMAC-SHA256 keyed with the 32-byte secret over the transcript. */
function computeProof(secret, transcript, role) {
    const key = secretBytes(secret);
    try {
        return createHmac("sha256", key.bytes).update(transcriptBytes(transcript, role)).digest("base64");
    } finally {
        if (key.owned) key.bytes.fill(0);
    }
}

/** Verifies a peer's proof in constant time over the decoded bytes. */
function verifyProof(secret, proof, transcript, role) {
    let key;
    try {
        key = secretBytes(secret);
    } catch {
        return false;
    }
    try {
        const received = decodeKey32(proof);
        if (!received) return false;
        const expected = createHmac("sha256", key.bytes).update(transcriptBytes(transcript, role)).digest();
        return timingSafeEqual(received, expected);
    } catch {
        return false;
    } finally {
        if (key.owned) key.bytes.fill(0);
    }
}

/** Builds a pipe name from its parts (contract §2.5). */
function createPipeName(hostId, edition, userHash, registrationId) {
    if (!isHostId(hostId)) throw new TypeError("The host id does not match the host-id grammar.");
    if (!isEdition(edition)) throw new TypeError("The edition does not match the edition grammar.");
    if (!isUserHash(userHash)) throw new TypeError("The user hash is not 16 lowercase hexadecimal digits.");
    if (!isGuid(registrationId)) throw new TypeError("The registration id is not a GUID in wire form.");
    return PIPE_PREFIX + hostId + "." + edition + "." + userHash + "." + registrationId;
}

/** The parts of a pipe name when it matches the grammar exactly, else null. */
function parsePipeName(value) {
    if (typeof value !== "string" || !value.startsWith(PIPE_PREFIX)) return null;
    const segments = value.slice(PIPE_PREFIX.length).split(".");
    if (segments.length !== 4 || !isHostId(segments[0]) || !isEdition(segments[1]) || !isUserHash(segments[2]) || !isGuid(segments[3])) {
        return null;
    }
    return Object.freeze({ hostId: segments[0], edition: segments[1], userHash: segments[2], registrationId: segments[3] });
}

/** The user hash: the first 16 lowercase hexadecimal digits of SHA-256 over the UTF-8 SID string. */
function userHash(userSid) {
    if (typeof userSid !== "string" || userSid.length === 0) throw new TypeError("The SID must be a non-empty string.");
    return createHash("sha256").update(userSid, "utf8").digest("hex").slice(0, 16);
}

module.exports = {
    PROTOCOL_MIN,
    PROTOCOL_MAX,
    LABEL,
    PIPE_PREFIX,
    newNonce,
    negotiate,
    transcriptBytes,
    computeProof,
    verifyProof,
    createPipeName,
    parsePipeName,
    userHash,
};
