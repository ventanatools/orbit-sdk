// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * The wire protocol (contract §7) for hosts, tools and tests: framing, messages, the handshake,
 * pipe names, limits, the token bucket and the reason-code catalog.
 */

const { encodeFrame, FrameReader, MAX_FRAME_BYTES, MAX_HANDSHAKE_FRAME_BYTES } = require("./lib/wire/framing.js");
const { readMessage, writeMessage } = require("./lib/wire/messages.js");
const handshake = require("./lib/wire/handshake.js");
const { protocol3Limits, limitsWithinRanges, limitsForCompanion, TokenBucket } = require("./lib/wire/limits.js");
const { ReasonCodes, reasonInfo, isKnownReason, isReasonCode, helpUri, capabilities, isExperimentalCapability } = require("./lib/codes.js");

module.exports = {
    PROTOCOL_MIN: handshake.PROTOCOL_MIN,
    PROTOCOL_MAX: handshake.PROTOCOL_MAX,
    LABEL: handshake.LABEL,
    PIPE_PREFIX: handshake.PIPE_PREFIX,
    MAX_FRAME_BYTES,
    MAX_HANDSHAKE_FRAME_BYTES,
    encodeFrame,
    FrameReader,
    readMessage,
    writeMessage,
    newNonce: handshake.newNonce,
    negotiate: handshake.negotiate,
    transcriptBytes: handshake.transcriptBytes,
    computeProof: handshake.computeProof,
    verifyProof: handshake.verifyProof,
    createPipeName: handshake.createPipeName,
    parsePipeName: handshake.parsePipeName,
    userHash: handshake.userHash,
    protocol3Limits,
    limitsWithinRanges,
    limitsForCompanion,
    TokenBucket,
    ReasonCodes,
    reasonInfo,
    isKnownReason,
    isReasonCode,
    helpUri,
    capabilities,
    isExperimentalCapability,
};
