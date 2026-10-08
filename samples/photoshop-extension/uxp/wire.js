// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

// The bridge's messages between the Node companion and the UXP panel, and a strict JSON parser.
// UXP cannot load the Node SDK, so the panel validates every message itself; the companion uses
// the same checks. Both peers refuse unknown or duplicate members and unexpected messages.
"use strict";

var constants = require("./constants.js");

var OUTCOMES = ["Done", "Refused", "Failed", "Unsupported"];
var FAILURES = ["UnsupportedInput", "NeedsSetup", "Network", "NoResult", "AppUnavailable"];
var LINES = ["Shown", "Hidden", "None", "One", "Many"];
var STATES = ["None", "On", "Off"];

/** A strict JSON parser: one object, no duplicate members at any depth, bounded size and depth. */
function parseRecord(text, maxChars) {
    if (typeof text !== "string" || !text.length || text.length > (maxChars || 65536)) throw new Error("record.size");
    var i = 0;
    var count = 0;
    function space() {
        while (i < text.length && (text[i] === " " || text[i] === "\t" || text[i] === "\n" || text[i] === "\r")) i++;
    }
    function string() {
        var start = i++;
        while (i < text.length) {
            var c = text[i++];
            if (c === "\\") i++;
            else if (c === "\"") return JSON.parse(text.slice(start, i));
        }
        throw new Error("record.string");
    }
    function value(depth) {
        if (depth > 8 || ++count > 256) throw new Error("record.depth");
        space();
        if (text[i] === "\"") return string();
        if (text[i] === "{") {
            i++;
            var result = Object.create(null);
            space();
            if (text[i] === "}") {
                i++;
                return result;
            }
            while (true) {
                if (text[i] !== "\"") throw new Error("record.key");
                var key = string();
                if (Object.prototype.hasOwnProperty.call(result, key)) throw new Error("record.duplicate");
                space();
                if (text[i++] !== ":") throw new Error("record.colon");
                result[key] = value(depth + 1);
                space();
                if (text[i] === "}") {
                    i++;
                    return result;
                }
                if (text[i++] !== ",") throw new Error("record.end");
                space();
            }
        }
        var words = [["true", true], ["false", false]];
        for (var w = 0; w < words.length; w++) {
            if (text.slice(i, i + words[w][0].length) === words[w][0]) {
                i += words[w][0].length;
                return words[w][1];
            }
        }
        var match = /^-?(?:0|[1-9][0-9]*)/.exec(text.slice(i));
        if (!match) throw new Error("record.scalar");
        i += match[0].length;
        var number = Number(match[0]);
        if (!Number.isSafeInteger(number)) throw new Error("record.number");
        return number;
    }
    var root = value(0);
    space();
    if (i !== text.length || !root || typeof root !== "object") throw new Error("record.trailing");
    return root;
}

/** Whether `record` has exactly the members `expected`. */
function fields(record, expected) {
    if (!record || typeof record !== "object") return false;
    var keys = Object.keys(record);
    return keys.length === expected.length && expected.every(function (key) { return Object.prototype.hasOwnProperty.call(record, key); });
}

function isId(value) {
    return typeof value === "string" && /^[0-9a-f]{32}$/.test(value);
}

function isHex64(value) {
    return typeof value === "string" && /^[0-9a-f]{64}$/.test(value);
}

/** The settings each contribution declares in extension.json, checked as the host sends them. */
function settingsFor(contributionId, settings) {
    if (!settings || typeof settings !== "object" || Array.isArray(settings)) return false;
    if (contributionId === constants.TOGGLE_ID) return fields(settings, ["mode"]) && ["toggle", "show", "hide"].indexOf(settings.mode) >= 0;
    if (contributionId === constants.STATUS_ID) return fields(settings, ["display"]) && ["visibility", "selection"].indexOf(settings.display) >= 0;
    return false;
}

function sameSettings(first, second) {
    var keys = Object.keys(first);
    return keys.length === Object.keys(second).length && keys.every(function (key) { return first[key] === second[key]; });
}

function isStart(message) {
    return fields(message, ["type", "sessionId", "contributionId", "settings"]) && message.type === "startSession"
        && isId(message.sessionId) && settingsFor(message.contributionId, message.settings);
}

function isStop(message) {
    return fields(message, ["type", "sessionId"]) && message.type === "stopSession" && isId(message.sessionId);
}

function isInvoke(message) {
    return fields(message, ["type", "requestId", "sessionId", "contributionId", "settings"]) && message.type === "invoke"
        && isId(message.requestId) && isId(message.sessionId) && settingsFor(message.contributionId, message.settings);
}

function isCancel(message) {
    return fields(message, ["type", "requestId"]) && message.type === "cancel" && isId(message.requestId);
}

function isResult(message) {
    if (!message || message.type !== "result" || !isId(message.requestId) || OUTCOMES.indexOf(message.outcome) < 0) return false;
    if (fields(message, ["type", "requestId", "outcome"])) return true;
    return fields(message, ["type", "requestId", "outcome", "failure"]) && message.outcome === "Failed" && FAILURES.indexOf(message.failure) >= 0;
}

/** A face the panel publishes: the short values only, never document content. */
function isFace(message) {
    if (!message || !isId(message.sessionId)) return false;
    if (message.type === "clearFace") return fields(message, ["type", "sessionId"]);
    if (message.type === "fail") return fields(message, ["type", "sessionId", "failure"]) && FAILURES.indexOf(message.failure) >= 0;
    if (message.type !== "face" || !fields(message, ["type", "sessionId", "face"])) return false;
    var face = message.face;
    return fields(face, ["line1", "state", "detail", "goodForSeconds"]) && LINES.indexOf(face.line1) >= 0
        && STATES.indexOf(face.state) >= 0 && typeof face.detail === "string" && face.detail.length >= 1 && face.detail.length <= 160
        && face.goodForSeconds === constants.FACE_SECONDS;
}

/** The bridge file the companion writes: { bridgeVersion, url, secret }. */
function parseBridgeConfig(text) {
    var value = parseRecord(text, 2048);
    if (!fields(value, ["bridgeVersion", "url", "secret"]) || value.bridgeVersion !== constants.BRIDGE_VERSION
        || value.url !== constants.BRIDGE_URL || !isHex64(value.secret)) {
        throw new Error("bridge.config");
    }
    return value;
}

module.exports = {
    OUTCOMES: OUTCOMES,
    FAILURES: FAILURES,
    parseRecord: parseRecord,
    fields: fields,
    isId: isId,
    isHex64: isHex64,
    settingsFor: settingsFor,
    sameSettings: sameSettings,
    isStart: isStart,
    isStop: isStop,
    isInvoke: isInvoke,
    isCancel: isCancel,
    isResult: isResult,
    isFace: isFace,
    parseBridgeConfig: parseBridgeConfig,
};
