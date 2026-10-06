// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * The Node SDK for extension companions (contract §10): runCompanion, the CompanionClient, the
 * ContributionRouter, manifest and pairing readers, the manifest hash, the host registry and the
 * wire tokens. The wire protocol is in "./wire" and the test kit in "./testing".
 */

const { runCompanion, parseArguments } = require("./lib/companion.js");
const { CompanionClient } = require("./lib/client/client.js");
const { ContributionRouter } = require("./lib/router.js");
const { readManifest, readManifestFile, validateManifest } = require("./lib/manifest.js");
const { Pairing, readPairing, readPairingFile, defaultPairingPath } = require("./lib/pairing.js");
const { computeManifestHash } = require("./lib/hash.js");
const { hosts, reservedHostIds, findHost, firstActiveHost } = require("./lib/hosts.js");
const { reservedPublishers, classifyId } = require("./lib/ids.js");
const { Diagnostic, ReasonCodes, reasonInfo, helpUri } = require("./lib/codes.js");
const { checkDeclarationText, clean, isGlyph } = require("./lib/text.js");
const tokens = require("./lib/tokens.js");

module.exports = {
    runCompanion,
    parseArguments,
    CompanionClient,
    ContributionRouter,
    readManifest,
    readManifestFile,
    validateManifest,
    Pairing,
    readPairing,
    readPairingFile,
    defaultPairingPath,
    computeManifestHash,
    hosts,
    reservedHostIds,
    findHost,
    firstActiveHost,
    reservedPublishers,
    classifyId,
    Diagnostic,
    ReasonCodes,
    reasonInfo,
    helpUri,
    checkDeclarationText,
    cleanText: clean,
    isGlyph,
    Outcome: tokens.Outcome,
    FaceState: tokens.FaceState,
    Failure: tokens.Failure,
    PublishResult: tokens.PublishResult,
    ConnectionState: tokens.ConnectionState,
    HandlerFault: tokens.HandlerFault,
    NetworkUse: tokens.NetworkUse,
    SettingKind: tokens.SettingKind,
    Provides: tokens.Provides,
};
