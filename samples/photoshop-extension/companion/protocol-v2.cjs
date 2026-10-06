// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";
const { createHmac } = require("node:crypto");
const { bytes32 } = require("./protocol.cjs");
const { parseRecordV2, fields } = require("../uxp/wire-v2.js");

function parsePairing(text) {
    const value = parseRecordV2(text, 4096);
    if (!fields(value, ["protocolVersion", "pipeName", "registrationId", "extensionId", "secret"]) ||
        value.protocolVersion !== 2 || value.extensionId !== "example.photoshop" ||
        typeof value.registrationId !== "string" || !/^[0-9a-f]{32}$/.test(value.registrationId) ||
        typeof value.pipeName !== "string" ||
        !/^Orbit\.Extensions\.v2\.[a-z0-9-]{1,32}\.[0-9a-f]{16}\.[0-9a-f]{32}$/.test(value.pipeName) ||
        !value.pipeName.endsWith(`.${value.registrationId}`)) throw new Error("pairing.invalid");
    bytes32(value.secret);
    return value;
}

function proofV2(secret, role, registrationId, clientNonce, serverNonce) {
    if (role !== "server" && role !== "client") throw new Error("proof.role");
    return createHmac("sha256", bytes32(secret))
        .update(`Orbit.Extensions.v2\n${role}\n${registrationId}\n${clientNonce}\n${serverNonce}`, "utf8").digest("base64");
}

function bridgeProofV2(secret, role, clientNonce, serverNonce) {
    return createHmac("sha256", Buffer.from(secret, "hex"))
        .update(`Orbit.Photoshop.Bridge.v2\n${role}\n${clientNonce}\n${serverNonce}`, "utf8").digest("hex");
}
module.exports = { parsePairing, proofV2, bridgeProofV2 };
