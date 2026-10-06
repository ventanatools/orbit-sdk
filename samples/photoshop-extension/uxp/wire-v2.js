// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

const { fields, isRequestId, isOutcome, ACTION_ID } = require("./wire.js");
const STATUS_ID = "example.photoshop/layer-status";

// V2 rejects duplicate members at every depth; JSON.parse alone accepts them.
function parseRecordV2(text, maxChars) {
    if (typeof text !== "string" || !text.length || text.length > (maxChars || 65536)) throw new Error("record.size");
    let i = 0, count = 0;
    function space() { while (i < text.length && /[\t\r\n ]/.test(text[i])) i++; }
    function string() {
        const start = i++;
        while (i < text.length) {
            const c = text[i++];
            if (c === "\\") i++;
            else if (c === '"') return JSON.parse(text.slice(start, i));
        }
        throw new Error("record.string");
    }
    function value(depth) {
        if (depth > 8 || ++count > 256) throw new Error("record.depth");
        space();
        if (text[i] === '"') return string();
        if (text[i] === "{") {
            i++;
            const result = Object.create(null);
            space();
            if (text[i] === "}") { i++; return result; }
            while (true) {
                if (text[i] !== '"') throw new Error("record.key");
                const key = string();
                if (Object.prototype.hasOwnProperty.call(result, key)) throw new Error("record.duplicate");
                space();
                if (text[i++] !== ":") throw new Error("record.colon");
                result[key] = value(depth + 1);
                space();
                if (text[i] === "}") { i++; return result; }
                if (text[i++] !== ",") throw new Error("record.end");
                space();
            }
        }
        for (const [word, result] of [["true", true], ["false", false], ["null", null]]) {
            if (text.slice(i, i + word.length) === word) { i += word.length; return result; }
        }
        const match = /^-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?/.exec(text.slice(i));
        if (!match) throw new Error("record.scalar");
        i += match[0].length;
        const result = Number(match[0]);
        if (!Number.isFinite(result)) throw new Error("record.number");
        return result;
    }
    const result = value(0);
    space();
    if (i !== text.length || !result || typeof result !== "object" || Array.isArray(result)) throw new Error("record.trailing");
    return result;
}

function settingsFor(actionId, settings) {
    if (!settings || typeof settings !== "object" || Array.isArray(settings)) return false;
    if (actionId === ACTION_ID) return fields(settings, ["mode"]) && ["toggle", "show", "hide"].includes(settings.mode);
    if (actionId === STATUS_ID) return fields(settings, ["display"]) && ["visibility", "selection"].includes(settings.display);
    return false;
}

function sameSettings(first, second) {
    const keys = Object.keys(first);
    return keys.length === Object.keys(second).length && keys.every(key => first[key] === second[key]);
}

function isStart(message) {
    return fields(message, ["type", "sessionId", "actionId", "settings"]) && message.type === "startSession" &&
        isRequestId(message.sessionId) && settingsFor(message.actionId, message.settings);
}

function isInvoke(message) {
    return fields(message, ["type", "requestId", "actionId", "sessionId", "settings"]) && message.type === "invoke" &&
        isRequestId(message.requestId) && isRequestId(message.sessionId) && settingsFor(message.actionId, message.settings);
}

function isFace(message) {
    if (!fields(message, ["type", "sessionId", "command"]) || message.type !== "face" || !isRequestId(message.sessionId)) return false;
    const command = message.command;
    if (!command || typeof command !== "object") return false;
    if (command.$type === "clearFace") return fields(command, ["$type"]);
    if (command.$type === "fail") return fields(command, ["$type", "failure"]) &&
        ["NeedsSetup", "NoData", "Network", "Unsupported"].includes(command.failure);
    if (!fields(command, ["$type", "face"]) || command.$type !== "setFace") return false;
    const face = command.face;
    return fields(face, ["picture", "line1", "state", "detail", "goodForSeconds"]) &&
        fields(face.picture, ["$type"]) && face.picture.$type === "none" &&
        fields(face.line1, ["$type", "value"]) && face.line1.$type === "text" &&
        ["Shown", "Hidden", "One", "Many", "None"].includes(face.line1.value) &&
        ["None", "On", "Off"].includes(face.state) && typeof face.detail === "string" && face.detail.length <= 160 &&
        face.goodForSeconds === 5;
}

module.exports = { parseRecordV2, fields, isRequestId, isOutcome, ACTION_ID, STATUS_ID,
    settingsFor, sameSettings, isStart, isInvoke, isFace };
