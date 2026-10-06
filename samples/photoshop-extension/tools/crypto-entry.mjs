// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

import { hmac } from "@noble/hashes/hmac.js";
import { sha256 } from "@noble/hashes/sha2.js";
import { bytesToHex, hexToBytes } from "@noble/hashes/utils.js";

// This transcript is deliberately ASCII. UXP need not implement TextEncoder,
// Node crypto or WebCrypto subtle. The algorithm comes from noble-hashes.
export function bridgeProof(secret, role, clientNonce, serverNonce) {
    if (!/^[0-9a-f]{64}$/.test(secret) || !/^[0-9a-f]{64}$/.test(clientNonce) ||
        !/^[0-9a-f]{64}$/.test(serverNonce) || (role !== "server" && role !== "client")) {
        throw new Error("proof.invalid");
    }
    const text = `Orbit.Photoshop.Bridge.v2\n${role}\n${clientNonce}\n${serverNonce}`;
    const bytes = Uint8Array.from(text, c => c.charCodeAt(0));
    return bytesToHex(hmac(sha256, hexToBytes(secret), bytes));
}

export function randomNonce() {
    return bytesToHex(crypto.getRandomValues(new Uint8Array(32)));
}

export function matchesProof(left, right) {
    if (typeof left !== "string" || typeof right !== "string" ||
        !/^[0-9a-f]{64}$/.test(left) || !/^[0-9a-f]{64}$/.test(right)) return false;
    let different = 0;
    for (let i = 0; i < 64; i++) different |= left.charCodeAt(i) ^ right.charCodeAt(i);
    return different === 0;
}
