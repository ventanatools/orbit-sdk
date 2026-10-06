// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

// Small grammar helpers shared by the strict v2 parser and Node bridge.
"use strict";

function fields(record, expected) {
    return !!record && Object.keys(record).length === expected.length &&
        expected.every(function (key) { return Object.prototype.hasOwnProperty.call(record, key); });
}

function isRequestId(value) { return typeof value === "string" && /^[0-9a-f]{32}$/.test(value); }
function isOutcome(value) { return ["done", "refused", "failed", "unsupported"].indexOf(value) >= 0; }
var ACTION_ID = "example.photoshop/toggle-layer";

module.exports = { fields: fields, isRequestId: isRequestId, isOutcome: isOutcome, ACTION_ID: ACTION_ID };
