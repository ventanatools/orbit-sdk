// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * The companion's check of a startSession (contract §7.6.1, §7.6.2): null when the contribution
 * is in the manifest and the settings are its complete set (one declared choice value per declared
 * setting, no other key); otherwise session.unknown-contribution or session.settings-invalid. A key
 * is never dropped or filled in silently.
 */

const { findContribution } = require("../manifest.js");

function checkSessionSettings(manifest, contributionId, settings) {
    const contribution = findContribution(manifest, contributionId);
    if (!contribution) return "session.unknown-contribution";
    const declared = Array.isArray(contribution.settings) ? contribution.settings : [];
    const keys = Object.keys(settings || {});
    if (keys.length !== declared.length) return "session.settings-invalid";
    for (const setting of declared) {
        if (!Object.prototype.hasOwnProperty.call(settings, setting.id)) return "session.settings-invalid";
        const value = settings[setting.id];
        if (!setting.choices.some((choice) => choice.value === value)) return "session.settings-invalid";
    }
    return null;
}

/** The complete settings of a contribution: every default, replaced by the given values. */
function completeSettings(contribution, settings) {
    const complete = Object.create(null);
    for (const setting of Array.isArray(contribution.settings) ? contribution.settings : []) complete[setting.id] = setting.default;
    for (const [key, value] of Object.entries(settings || {})) complete[key] = value;
    return complete;
}

module.exports = { checkSessionSettings, completeSettings };
