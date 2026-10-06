// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * The closed enumerations of the contract as frozen objects of their wire tokens (contract §7.13).
 * Tokens cross the wire through these tables, never through a conversion of names.
 */

function tokens(...names) {
    return Object.freeze(Object.fromEntries(names.map((name) => [name, name])));
}

/** result.outcome (contract §7.8). */
const Outcome = tokens("Done", "Refused", "Failed", "Unsupported");

/** face.state (contract §7.7.1). */
const FaceState = tokens("None", "Playing", "Paused", "On", "Off");

/**
 * The failure tokens of protocol 3 (contract §7.8). LlmUnavailable is reserved for the capability
 * failure.llm-unavailable, which no host of this version makes effective, so it is not listed.
 */
const Failure = tokens("UnsupportedInput", "NeedsSetup", "Network", "NoResult", "AppUnavailable");

/** What Session.setFace, clearFace and fail return. */
const PublishResult = tokens("Accepted", "SessionEnded");

/** Where a CompanionClient is. */
const ConnectionState = tokens("NotStarted", "Connecting", "Connected", "Waiting", "Stopped");

/** How an author's handler misbehaved. */
const HandlerFault = tokens("Exception", "IgnoredCancellation", "SessionCapacity", "InvalidResult");

/** disclosures.network (contract §3.2.2). */
const NetworkUse = tokens("None", "LocalNetwork", "Internet");

/** setting kind (contract §3.3.1); Toggle, Text and Number are reserved for later schemas. */
const SettingKind = tokens("Choice");

/** provides values (contract §3.3). */
const Provides = Object.freeze({ Invoke: "invoke", Face: "face" });

module.exports = { Outcome, FaceState, Failure, PublishResult, ConnectionState, HandlerFault, NetworkUse, SettingKind, Provides };
