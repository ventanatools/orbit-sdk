// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * Reads and writes protocol-3 messages (contract §7.2, §7.3, §7.5, §7.11). readMessage validates
 * one frame body against every rule and never throws for any content; writeMessage writes the
 * canonical form: compact JSON, type first, members in the order of the message table, optional
 * members left out (picture and state always written), settings keys sorted ordinally, and every
 * character outside printable ASCII escaped as \uXXXX with uppercase hexadecimal.
 */

const { Kind, parse, integerOf } = require("../json.js");
const text = require("../text.js");
const { classifyId } = require("../ids.js");
const { reasonInfo, isKnownReason } = require("../codes.js");
const { limitsWithinRanges, LIMIT_NAMES } = require("./limits.js");

const MAX_FRAME_BYTES = 65536;
const MAX_HANDSHAKE_FRAME_BYTES = 8192;

const SENDERS = new Set(["Host", "Companion"]);
const PHASES = new Set(["Handshake", "PeerVerified", "Authenticated"]);
const ALL_PHASES = ["Handshake", "PeerVerified", "Authenticated"];

const TYPES = new Map([
    ["hello", { senders: ["Companion"], phases: ["Handshake"] }],
    ["authenticate", { senders: ["Companion"], phases: ["Handshake"] }],
    ["challenge", { senders: ["Host"], phases: ["Handshake"] }],
    ["ready", { senders: ["Host"], phases: ["PeerVerified"] }],
    ["error", { senders: ["Host", "Companion"], phases: ALL_PHASES }],
    ["startSession", { senders: ["Host"], phases: ["Authenticated"] }],
    ["stopSession", { senders: ["Host"], phases: ["Authenticated"] }],
    ["invoke", { senders: ["Host"], phases: ["Authenticated"] }],
    ["cancel", { senders: ["Host"], phases: ["Authenticated"] }],
    ["setFace", { senders: ["Companion"], phases: ["Authenticated"] }],
    ["clearFace", { senders: ["Companion"], phases: ["Authenticated"] }],
    ["fail", { senders: ["Companion"], phases: ["Authenticated"] }],
    ["result", { senders: ["Companion"], phases: ["Authenticated"] }],
    ["sessionRefused", { senders: ["Companion"], phases: ["Authenticated"] }],
    ["ping", { senders: ["Host", "Companion"], phases: ["Authenticated"] }],
    ["pong", { senders: ["Host", "Companion"], phases: ["Authenticated"] }],
]);

const REFUSAL_CODES = ["session.capacity", "session.unknown-contribution", "session.settings-invalid"];
const FACE_STATES = ["None", "Playing", "Paused", "On", "Off"];
const FAILURES = ["UnsupportedInput", "NeedsSetup", "Network", "NoResult", "AppUnavailable"];
const OUTCOMES = ["Done", "Refused", "Failed", "Unsupported"];
const INT32_MAX = 2147483647;
const INT32_MIN = -2147483648;

function hasNull(node) {
    switch (node.kind) {
        case Kind.Null:
            return true;
        case Kind.Object:
            return node.members.some((member) => hasNull(member.value));
        case Kind.Array:
            return node.items.some(hasNull);
        default:
            return false;
    }
}

function freezeDeep(value) {
    if (value && typeof value === "object") {
        for (const key of Object.keys(value)) freezeDeep(value[key]);
        Object.freeze(value);
    }
    return value;
}

/** Validates members, remembering the first failure. */
class Parser {
    constructor(invalid) {
        this.invalid = invalid;
        this.failure = null;
    }

    fail(code) {
        if (this.failure === null) this.failure = code;
    }

    /** Rule 2 of contract §7.11 for one object's members. */
    members(node, known) {
        const result = new Map();
        for (const member of node.members) {
            if (known.includes(member.name)) {
                if (!result.has(member.name)) result.set(member.name, member.value);
            } else if (!text.isMemberName(member.name) || known.some((name) => name.toLowerCase() === member.name.toLowerCase())) {
                this.fail("protocol.member-invalid");
            }
        }
        return result;
    }

    required(m, name) {
        if (m.has(name)) return m.get(name);
        this.fail(this.invalid);
        return null;
    }

    str(m, name) {
        const node = this.required(m, name);
        if (!node) return null;
        if (node.kind !== Kind.String) {
            this.fail(this.invalid);
            return null;
        }
        return node.text;
    }

    int(m, name, min, max) {
        const node = this.required(m, name);
        if (!node) return 0;
        const value = integerOf(node);
        if (value === null || value < min || value > max) {
            this.fail(this.invalid);
            return 0;
        }
        return value;
    }

    guid(m, name) {
        const value = this.str(m, name);
        if (value !== null && !text.isGuid(value)) this.fail(this.invalid);
        return value;
    }

    key(m, name) {
        const value = this.str(m, name);
        if (value !== null && !text.isKey32(value)) this.fail(this.invalid);
        return value;
    }

    hash(m, name) {
        const value = this.str(m, name);
        if (value !== null && !text.isSha256(value)) this.fail(this.invalid);
        return value;
    }

    capabilityList(m) {
        const node = this.required(m, "capabilities");
        if (!node) return null;
        if (node.kind !== Kind.Array || node.items.length > 32) {
            this.fail(this.invalid);
            return null;
        }
        const ids = [];
        for (const item of node.items) {
            if (item.kind !== Kind.String || !text.isDottedCode(item.text) || ids.includes(item.text)) {
                this.fail(this.invalid);
                return null;
            }
            ids.push(item.text);
        }
        return ids;
    }

    host(m) {
        const node = this.required(m, "host");
        if (!node) return null;
        if (node.kind !== Kind.Object) {
            this.fail(this.invalid);
            return null;
        }
        const h = this.members(node, ["id", "version"]);
        const id = this.str(h, "id");
        const version = this.str(h, "version");
        if ((id !== null && !text.isHostId(id)) || (version !== null && !text.isPeerVersion(version))) this.fail(this.invalid);
        return { id, version };
    }

    client(m) {
        const node = this.required(m, "client");
        if (!node) return null;
        if (node.kind !== Kind.Object) {
            this.fail(this.invalid);
            return null;
        }
        const c = this.members(node, ["name", "version"]);
        const name = this.str(c, "name");
        const version = this.str(c, "version");
        if ((name !== null && !text.isClientName(name)) || (version !== null && !text.isPeerVersion(version))) this.fail(this.invalid);
        return { name, version };
    }

    limits(m) {
        const node = this.required(m, "limits");
        if (!node) return null;
        if (node.kind !== Kind.Object) {
            this.fail(this.invalid);
            return null;
        }
        const l = this.members(node, LIMIT_NAMES);
        const limits = {};
        for (const name of LIMIT_NAMES) limits[name] = this.int(l, name, INT32_MIN, INT32_MAX);
        if (this.failure === null && !limitsWithinRanges(limits)) this.fail(this.invalid);
        return limits;
    }

    failureToken(m) {
        const token = this.str(m, "failure");
        if (token === null) return null;
        if (!FAILURES.includes(token)) {
            this.fail("face.failure-invalid");
            return null;
        }
        return token;
    }

    variant(m) {
        return this.failure === null ? this.str(m, "$type") : null;
    }

    picture(node) {
        if (node.kind !== Kind.Object) {
            this.fail(this.invalid);
            return null;
        }
        const m = this.members(node, ["$type", "glyph"]);
        const type = this.variant(m);
        if (type === null) return null;
        if (type === "none") return this.failure === null ? { $type: "none" } : null;
        if (type === "glyph") {
            const glyph = this.str(m, "glyph");
            if (glyph !== null && !text.isGlyph(glyph)) this.fail("face.glyph-invalid");
            return this.failure === null ? { $type: "glyph", glyph } : null;
        }
        this.fail("protocol.variant-unknown");
        return null;
    }

    line(node) {
        if (node.kind !== Kind.Object) {
            this.fail(this.invalid);
            return null;
        }
        const m = this.members(node, ["$type", "text"]);
        const type = this.variant(m);
        if (type === null) return null;
        if (type === "text") {
            const value = this.str(m, "text");
            return this.failure === null ? { $type: "text", text: value } : null;
        }
        this.fail("protocol.variant-unknown");
        return null;
    }

    face(node) {
        if (node.kind !== Kind.Object) {
            this.fail(this.invalid);
            return null;
        }
        const m = this.members(node, ["picture", "line1", "line2", "state", "detail", "goodForSeconds"]);
        let picture = { $type: "none" };
        if (m.has("picture")) picture = this.picture(m.get("picture")) || { $type: "none" };
        const line1 = m.has("line1") ? this.line(m.get("line1")) : null;
        const line2 = m.has("line2") ? this.line(m.get("line2")) : null;
        let state = "None";
        if (m.has("state")) {
            const token = this.str(m, "state");
            if (token !== null) {
                if (FACE_STATES.includes(token)) state = token;
                else this.fail("face.state-invalid");
            }
        }
        const detail = m.has("detail") ? this.str(m, "detail") : null;
        let goodForSeconds = 0;
        const lifetime = this.required(m, "goodForSeconds");
        if (lifetime) {
            const seconds = integerOf(lifetime);
            if (seconds === null) this.fail(this.invalid);
            else if (seconds < 1 || seconds > 86400) this.fail("face.lifetime-invalid");
            else goodForSeconds = seconds;
        }
        if (this.failure !== null) return null;
        const face = { picture };
        if (line1) face.line1 = line1;
        if (line2) face.line2 = line2;
        face.state = state;
        if (detail !== null) face.detail = detail;
        face.goodForSeconds = goodForSeconds;
        return face;
    }

    message(type, root, phase) {
        switch (type) {
            case "hello": {
                const m = this.members(root, ["type", "minVersion", "maxVersion", "registrationId", "clientNonce", "capabilities", "client", "manifestHash"]);
                const minVersion = this.int(m, "minVersion", 1, 65535);
                const maxVersion = this.int(m, "maxVersion", 1, 65535);
                const registrationId = this.guid(m, "registrationId");
                const clientNonce = this.key(m, "clientNonce");
                const capabilities = this.capabilityList(m);
                const client = this.client(m);
                const manifestHash = this.hash(m, "manifestHash");
                if (this.failure === null && minVersion > maxVersion) this.fail(this.invalid);
                return { type, minVersion, maxVersion, registrationId, clientNonce, capabilities, client, manifestHash };
            }
            case "challenge": {
                const m = this.members(root, ["type", "serverNonce", "version", "capabilities", "host", "proof"]);
                const serverNonce = this.key(m, "serverNonce");
                const version = this.int(m, "version", 1, 65535);
                const capabilities = this.capabilityList(m);
                const host = this.host(m);
                const proof = this.key(m, "proof");
                return { type, serverNonce, version, capabilities, host, proof };
            }
            case "authenticate": {
                const m = this.members(root, ["type", "proof"]);
                return { type, proof: this.key(m, "proof") };
            }
            case "ready": {
                const m = this.members(root, ["type", "version", "capabilities", "host", "uiLanguage", "limits"]);
                const version = this.int(m, "version", 1, 65535);
                const capabilities = this.capabilityList(m);
                const host = this.host(m);
                const uiLanguage = this.str(m, "uiLanguage");
                if (uiLanguage !== null && !text.isLanguageTag(uiLanguage)) this.fail(this.invalid);
                const limits = this.limits(m);
                return { type, version, capabilities, host, uiLanguage, limits };
            }
            case "startSession": {
                const m = this.members(root, ["type", "sessionId", "contributionId", "settings"]);
                const sessionId = this.guid(m, "sessionId");
                const contributionId = this.str(m, "contributionId");
                if (contributionId !== null && classifyId(contributionId, "ThirdParty", []) !== null) this.fail(this.invalid);
                let settings = null;
                const node = this.required(m, "settings");
                if (node) {
                    if (node.kind !== Kind.Object || node.members.length > 16) {
                        this.fail(this.invalid);
                    } else {
                        settings = Object.create(null);
                        for (const member of node.members) {
                            if (!text.isSettingId(member.name) || member.value.kind !== Kind.String || !text.isChoiceValue(member.value.text)) {
                                this.fail(this.invalid);
                                break;
                            }
                            settings[member.name] = member.value.text;
                        }
                    }
                }
                return { type, sessionId, contributionId, settings };
            }
            case "stopSession":
            case "clearFace": {
                const m = this.members(root, ["type", "sessionId"]);
                return { type, sessionId: this.guid(m, "sessionId") };
            }
            case "invoke": {
                const m = this.members(root, ["type", "requestId", "sessionId"]);
                const requestId = this.guid(m, "requestId");
                const sessionId = this.guid(m, "sessionId");
                return { type, requestId, sessionId };
            }
            case "cancel": {
                const m = this.members(root, ["type", "requestId"]);
                return { type, requestId: this.guid(m, "requestId") };
            }
            case "setFace": {
                const m = this.members(root, ["type", "sessionId", "face"]);
                const sessionId = this.guid(m, "sessionId");
                const node = this.required(m, "face");
                const face = node ? this.face(node) : null;
                return { type, sessionId, face };
            }
            case "fail": {
                const m = this.members(root, ["type", "sessionId", "failure"]);
                const sessionId = this.guid(m, "sessionId");
                const failure = this.failureToken(m);
                return { type, sessionId, failure };
            }
            case "result": {
                const m = this.members(root, ["type", "requestId", "outcome", "failure"]);
                const requestId = this.guid(m, "requestId");
                let outcome = null;
                const token = this.str(m, "outcome");
                if (token !== null) {
                    if (OUTCOMES.includes(token)) outcome = token;
                    else this.fail("invoke.outcome-invalid");
                }
                let failure;
                if (m.has("failure")) {
                    failure = this.failureToken(m);
                    if (this.failure === null && outcome !== "Failed") this.fail("invoke.failure-unexpected");
                }
                const message = { type, requestId, outcome };
                if (failure) message.failure = failure;
                return message;
            }
            case "sessionRefused": {
                const m = this.members(root, ["type", "sessionId", "code"]);
                const sessionId = this.guid(m, "sessionId");
                const code = this.str(m, "code");
                if (code !== null && !REFUSAL_CODES.includes(code)) this.fail(this.invalid);
                return { type, sessionId, code };
            }
            case "ping":
            case "pong": {
                const m = this.members(root, ["type", "id"]);
                return { type, id: this.int(m, "id", 1, INT32_MAX) };
            }
            default:
                return this.error(root, phase);
        }
    }

    error(root, phase) {
        const m = this.members(root, ["type", "code", "message", "sessionId", "requestId", "retryAfterMs", "supported"]);
        const code = this.str(m, "code");
        if (code !== null && !text.isDottedCode(code)) this.fail(this.invalid);
        const message = m.has("message") ? this.str(m, "message") : null;
        if (message !== null && !text.isPrintableAscii(message, 256)) this.fail(this.invalid);
        const sessionId = m.has("sessionId") ? this.guid(m, "sessionId") : null;
        const requestId = m.has("requestId") ? this.guid(m, "requestId") : null;
        const retryAfterMs = m.has("retryAfterMs") ? this.int(m, "retryAfterMs", 0, 300000) : null;
        let supported = null;
        if (m.has("supported")) {
            const node = m.get("supported");
            if (node.kind !== Kind.Object) {
                this.fail(this.invalid);
            } else {
                const s = this.members(node, ["minVersion", "maxVersion"]);
                const minVersion = this.int(s, "minVersion", 1, 65535);
                const maxVersion = this.int(s, "maxVersion", 1, 65535);
                if (this.failure === null && minVersion > maxVersion) this.fail(this.invalid);
                supported = { minVersion, maxVersion };
            }
            if (this.failure === null && code !== "protocol.version-unsupported") this.fail(this.invalid);
        }
        if (this.failure === null && phase === "Handshake" && isKnownReason(code) && !reasonInfo(code).pre) this.fail("protocol.unexpected");
        const result = { type: "error", code };
        if (message !== null) result.message = message;
        if (sessionId !== null) result.sessionId = sessionId;
        if (requestId !== null) result.requestId = requestId;
        if (retryAfterMs !== null) result.retryAfterMs = retryAfterMs;
        if (supported !== null) result.supported = supported;
        return result;
    }
}

/**
 * Reads one frame body (a Buffer or Uint8Array; a string is encoded as UTF-8) sent by `sender`
 * ("Host" or "Companion") to a receiver in `phase` ("Handshake", "PeerVerified" or
 * "Authenticated"). Returns { message }, { violation } (the code to close with) or { ignored }.
 */
function readMessage(frame, sender, phase) {
    if (!SENDERS.has(sender)) throw new TypeError("The sender is not Host or Companion.");
    if (!PHASES.has(phase)) throw new TypeError("The phase is not Handshake, PeerVerified or Authenticated.");
    const bytes = typeof frame === "string" ? Buffer.from(frame, "utf8") : Buffer.isBuffer(frame) ? frame : Buffer.from(frame);
    if (bytes.length === 0 || bytes.length > MAX_FRAME_BYTES || (phase !== "Authenticated" && bytes.length > MAX_HANDSHAKE_FRAME_BYTES)) {
        return { violation: "frame.too-large" };
    }
    const parsed = parse(bytes);
    if (parsed.failure === "utf8") return { violation: "frame.utf8-invalid" };
    if (!parsed.root || parsed.root.kind !== Kind.Object || parsed.unpairedSurrogate) return { violation: "frame.json-invalid" };
    if (parsed.duplicates.length > 0) return { violation: "frame.json-duplicate" };
    const root = parsed.root;
    if (hasNull(root)) {
        const hello = root.members.some((member) => member.name === "type" && member.value.kind === Kind.String && member.value.text === "hello");
        return { violation: hello ? "protocol.hello-invalid" : "protocol.message-invalid" };
    }
    const typeMember = root.members.find((member) => member.name === "type");
    if (!typeMember || typeMember.value.kind !== Kind.String) return { violation: "protocol.message-invalid" };
    const type = typeMember.value.text;
    const rule = TYPES.get(type);
    if (!rule) {
        if (phase !== "Authenticated") return { violation: "protocol.unexpected" };
        return text.isMemberName(type) ? { ignored: "protocol.type-unknown" } : { violation: "protocol.message-invalid" };
    }
    if (!rule.senders.includes(sender) || !rule.phases.includes(phase)) return { violation: "protocol.unexpected" };
    const parser = new Parser(type === "hello" ? "protocol.hello-invalid" : "protocol.message-invalid");
    const message = parser.message(type, root, phase);
    if (parser.failure !== null) return { violation: parser.failure };
    return { message: freezeDeep(message) };
}

// ---------------------------------------------------------------- writing

function escapeString(value) {
    if (typeof value !== "string") throw new TypeError("A required string member is missing.");
    let out = "\"";
    for (let i = 0; i < value.length; i++) {
        const c = value.charCodeAt(i);
        switch (c) {
            case 0x22: out += "\\\""; break;
            case 0x5c: out += "\\\\"; break;
            case 0x08: out += "\\b"; break;
            case 0x0c: out += "\\f"; break;
            case 0x0a: out += "\\n"; break;
            case 0x0d: out += "\\r"; break;
            case 0x09: out += "\\t"; break;
            default:
                out += c < 0x20 || c > 0x7e ? "\\u" + c.toString(16).toUpperCase().padStart(4, "0") : value[i];
        }
    }
    return out + "\"";
}

function number(value) {
    if (!Number.isInteger(value)) throw new TypeError("A required integer member is missing.");
    return String(value);
}

function strings(values) {
    if (!Array.isArray(values)) throw new TypeError("A required list member is missing.");
    return "[" + values.map(escapeString).join(",") + "]";
}

function object(pairs) {
    return "{" + pairs.filter((pair) => pair !== null).map(([name, value]) => escapeString(name) + ":" + value).join(",") + "}";
}

function token(value, allowed) {
    if (!allowed.includes(value)) throw new TypeError("An enumeration value is not a protocol-3 token.");
    return escapeString(value);
}

function required(value) {
    if (value === null || value === undefined || typeof value !== "object") throw new TypeError("A required member is missing.");
    return value;
}

function hostObject(host) {
    const h = required(host);
    return object([["id", escapeString(h.id)], ["version", escapeString(h.version)]]);
}

function faceObject(face) {
    const f = required(face);
    const picture = f.picture === undefined ? { $type: "none" } : required(f.picture);
    let pictureText;
    if (picture.$type === "none") pictureText = object([["$type", escapeString("none")]]);
    else if (picture.$type === "glyph") pictureText = object([["$type", escapeString("glyph")], ["glyph", escapeString(picture.glyph)]]);
    else throw new TypeError("The picture variant is not part of protocol 3.");
    const line = (value) => {
        const l = required(value);
        if (l.$type !== "text") throw new TypeError("The line variant is not part of protocol 3.");
        return object([["$type", escapeString("text")], ["text", escapeString(l.text)]]);
    };
    return object([
        ["picture", pictureText],
        f.line1 === undefined || f.line1 === null ? null : ["line1", line(f.line1)],
        f.line2 === undefined || f.line2 === null ? null : ["line2", line(f.line2)],
        ["state", token(f.state === undefined ? "None" : f.state, FACE_STATES)],
        f.detail === undefined || f.detail === null ? null : ["detail", escapeString(f.detail)],
        ["goodForSeconds", number(f.goodForSeconds)],
    ]);
}

function optional(message, name, write) {
    return message[name] === undefined || message[name] === null ? null : [name, write(message[name])];
}

/** Writes one message as a canonical frame body (ASCII bytes). Throws TypeError for a malformed message. */
function writeMessage(message) {
    const m = required(message);
    const type = m.type;
    let members;
    switch (type) {
        case "hello":
            members = [
                ["minVersion", number(m.minVersion)], ["maxVersion", number(m.maxVersion)],
                ["registrationId", escapeString(m.registrationId)], ["clientNonce", escapeString(m.clientNonce)],
                ["capabilities", strings(m.capabilities)],
                ["client", object([["name", escapeString(required(m.client).name)], ["version", escapeString(m.client.version)]])],
                ["manifestHash", escapeString(m.manifestHash)],
            ];
            break;
        case "challenge":
            members = [
                ["serverNonce", escapeString(m.serverNonce)], ["version", number(m.version)], ["capabilities", strings(m.capabilities)],
                ["host", hostObject(m.host)], ["proof", escapeString(m.proof)],
            ];
            break;
        case "authenticate":
            members = [["proof", escapeString(m.proof)]];
            break;
        case "ready": {
            const limits = required(m.limits);
            members = [
                ["version", number(m.version)], ["capabilities", strings(m.capabilities)], ["host", hostObject(m.host)],
                ["uiLanguage", escapeString(m.uiLanguage)],
                ["limits", object(LIMIT_NAMES.map((name) => [name, number(limits[name])]))],
            ];
            break;
        }
        case "startSession": {
            const settings = required(m.settings);
            const keys = Object.keys(settings).sort((a, b) => (a < b ? -1 : a > b ? 1 : 0));
            members = [
                ["sessionId", escapeString(m.sessionId)], ["contributionId", escapeString(m.contributionId)],
                ["settings", object(keys.map((key) => [key, escapeString(settings[key])]))],
            ];
            break;
        }
        case "stopSession":
        case "clearFace":
            members = [["sessionId", escapeString(m.sessionId)]];
            break;
        case "invoke":
            members = [["requestId", escapeString(m.requestId)], ["sessionId", escapeString(m.sessionId)]];
            break;
        case "cancel":
            members = [["requestId", escapeString(m.requestId)]];
            break;
        case "setFace":
            members = [["sessionId", escapeString(m.sessionId)], ["face", faceObject(m.face)]];
            break;
        case "fail":
            members = [["sessionId", escapeString(m.sessionId)], ["failure", token(m.failure, FAILURES)]];
            break;
        case "result":
            members = [
                ["requestId", escapeString(m.requestId)], ["outcome", token(m.outcome, OUTCOMES)],
                m.failure === undefined || m.failure === null ? null : ["failure", token(m.failure, FAILURES)],
            ];
            break;
        case "sessionRefused":
            members = [["sessionId", escapeString(m.sessionId)], ["code", escapeString(m.code)]];
            break;
        case "ping":
        case "pong":
            members = [["id", number(m.id)]];
            break;
        case "error":
            members = [
                ["code", escapeString(m.code)],
                optional(m, "message", escapeString),
                optional(m, "sessionId", escapeString),
                optional(m, "requestId", escapeString),
                optional(m, "retryAfterMs", number),
                optional(m, "supported", (s) => object([["minVersion", number(s.minVersion)], ["maxVersion", number(s.maxVersion)]])),
            ];
            break;
        default:
            throw new TypeError("The message type is not part of protocol 3.");
    }
    return Buffer.from(object([["type", escapeString(type)], ...members]), "latin1");
}

module.exports = { readMessage, writeMessage, FACE_STATES, FAILURES, OUTCOMES, REFUSAL_CODES };
