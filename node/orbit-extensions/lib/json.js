// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * A strict JSON reader over UTF-8 bytes (contract §3.1, §4.1, §7.2): one value, no comments or
 * trailing commas, valid UTF-8, at most 8 levels of nesting, member names compared ordinally
 * after unescaping, and the line and UTF-8 byte column of every token. An escape that forms an
 * unpaired surrogate is kept as that one UTF-16 code unit (as `JSON.parse` keeps it) and flagged,
 * so files can report it through their value rules and frames can refuse it.
 */

const Kind = Object.freeze({
    Object: 1,
    Array: 2,
    String: 3,
    Number: 4,
    True: 5,
    False: 6,
    Null: 7,
});

const MAX_DEPTH = 8;

class SyntaxFailure extends Error {
    constructor(offset) {
        super("json.syntax");
        this.offset = offset;
    }
}

/** RFC 6901 escaping of one reference token. */
function escapePointer(token) {
    return token.replace(/~/g, "~0").replace(/\//g, "~1");
}

function appendPointer(path, token) {
    return path + "/" + (typeof token === "number" ? String(token) : escapePointer(token));
}

/** Whether every character is printable ASCII (U+0020 to U+007E). */
function isPrintableAscii(text) {
    for (let i = 0; i < text.length; i++) {
        const c = text.charCodeAt(i);
        if (c < 0x20 || c > 0x7e) return false;
    }
    return true;
}

/**
 * The pointer of an unknown member: the parent's path when the name is longer than 64
 * characters or holds anything but printable ASCII (contract §4.1).
 */
function pointerForUnknown(parent, name) {
    const truncated = name.length > 64 || !isPrintableAscii(name);
    return { path: truncated ? parent : appendPointer(parent, name), truncated };
}

/** The offset of the first byte that is not valid UTF-8, or -1. */
function firstInvalidUtf8(bytes) {
    let i = 0;
    const n = bytes.length;
    while (i < n) {
        const b = bytes[i];
        if (b < 0x80) {
            i++;
            continue;
        }
        let need;
        let min;
        if (b >= 0xc2 && b <= 0xdf) {
            need = 1;
            min = 0x80;
        } else if (b >= 0xe0 && b <= 0xef) {
            need = 2;
            min = 0x800;
        } else if (b >= 0xf0 && b <= 0xf4) {
            need = 3;
            min = 0x10000;
        } else {
            return i;
        }
        let cp = b & (need === 1 ? 0x1f : need === 2 ? 0x0f : 0x07);
        for (let k = 1; k <= need; k++) {
            if (i + k >= n) return i;
            const c = bytes[i + k];
            if ((c & 0xc0) !== 0x80) return i;
            cp = (cp << 6) | (c & 0x3f);
        }
        if (cp < min || cp > 0x10ffff || (cp >= 0xd800 && cp <= 0xdfff)) return i;
        i += need + 1;
    }
    return -1;
}

/** 1-based line and UTF-8 byte column of `offset` (contract §4.1). */
function position(bytes, offset) {
    const end = Math.max(0, Math.min(offset, bytes.length));
    let line = 1;
    let lastNewline = -1;
    for (let i = 0; i < end; i++) {
        if (bytes[i] === 0x0a) {
            line++;
            lastNewline = i;
        }
    }
    return { line, column: end - lastNewline };
}

/** Whether a string holds an unpaired surrogate. */
function hasUnpairedSurrogate(text) {
    for (let i = 0; i < text.length; i++) {
        const c = text.charCodeAt(i);
        if (c >= 0xd800 && c <= 0xdbff) {
            const next = i + 1 < text.length ? text.charCodeAt(i + 1) : 0;
            if (next >= 0xdc00 && next <= 0xdfff) {
                i++;
                continue;
            }
            return true;
        }
        if (c >= 0xdc00 && c <= 0xdfff) return true;
    }
    return false;
}

function isHex(b) {
    return (b >= 0x30 && b <= 0x39) || (b >= 0x41 && b <= 0x46) || (b >= 0x61 && b <= 0x66);
}

/**
 * Parses a document (a UTF-8 byte order mark must already be removed). String members of the
 * root object named in `rawRootMembers` are not decoded to text; read them with `copyRawUtf8`.
 * Returns { root, failure, failurePath, failureLine, failureColumn, duplicates, unpairedSurrogate }.
 */
function parse(bytes, rawRootMembers) {
    const bad = firstInvalidUtf8(bytes);
    if (bad >= 0) {
        const at = position(bytes, bad);
        return { root: null, failure: "utf8", failurePath: "", failureLine: at.line, failureColumn: at.column, duplicates: [], unpairedSurrogate: false };
    }

    const frames = [];
    const duplicates = [];
    let unpaired = false;
    let root = null;
    let i = 0;
    const n = bytes.length;

    function skipSpace() {
        while (i < n) {
            const b = bytes[i];
            if (b === 0x20 || b === 0x09 || b === 0x0a || b === 0x0d) i++;
            else break;
        }
    }

    function childPath(frame, name) {
        return frame.opaque || !isPrintableAscii(name) ? frame.path : frame.path + "/" + escapePointer(name);
    }

    function pathOfNext() {
        if (frames.length === 0) return "";
        const frame = frames[frames.length - 1];
        if (frame.node.kind === Kind.Object) return frame.pendingName === null ? frame.path : childPath(frame, frame.pendingName);
        return frame.opaque ? frame.path : frame.path + "/" + String(frame.index);
    }

    function isOpaqueNext() {
        if (frames.length === 0) return false;
        const frame = frames[frames.length - 1];
        return frame.opaque || (frame.node.kind === Kind.Object && frame.pendingName !== null && !isPrintableAscii(frame.pendingName));
    }

    function attach(node) {
        if (frames.length === 0) {
            root = node;
            return;
        }
        const frame = frames[frames.length - 1];
        if (frame.node.kind === Kind.Object) {
            const name = frame.pendingName;
            if (frame.pendingDuplicate) duplicates.push({ path: childPath(frame, name), value: node });
            else frame.node.members.push({ name, value: node });
            frame.pendingName = null;
            frame.pendingDuplicate = false;
        } else {
            frame.node.items.push(node);
            frame.index++;
        }
    }

    /** Reads a string token at `i` (an opening quote); returns its value, or null for a raw string. */
    function readString(keepRaw) {
        const start = i;
        i++; // opening quote
        let text = "";
        let segment = i;
        let flagged = false;
        while (true) {
            if (i >= n) throw new SyntaxFailure(i);
            const b = bytes[i];
            if (b === 0x22) {
                if (!keepRaw) text += bytes.toString("utf8", segment, i);
                i++;
                break;
            }
            if (b < 0x20) throw new SyntaxFailure(i);
            if (b !== 0x5c) {
                i++;
                continue;
            }
            if (!keepRaw) text += bytes.toString("utf8", segment, i);
            if (i + 1 >= n) throw new SyntaxFailure(i + 1);
            const e = bytes[i + 1];
            let ch;
            switch (e) {
                case 0x22: ch = "\""; break;
                case 0x5c: ch = "\\"; break;
                case 0x2f: ch = "/"; break;
                case 0x62: ch = "\b"; break;
                case 0x66: ch = "\f"; break;
                case 0x6e: ch = "\n"; break;
                case 0x72: ch = "\r"; break;
                case 0x74: ch = "\t"; break;
                case 0x75: {
                    for (let k = 2; k < 6; k++) {
                        if (i + k >= n) throw new SyntaxFailure(i + k);
                        if (!isHex(bytes[i + k])) throw new SyntaxFailure(i + k);
                    }
                    ch = String.fromCharCode(parseInt(bytes.toString("latin1", i + 2, i + 6), 16));
                    i += 6;
                    segment = i;
                    if (!keepRaw) text += ch;
                    else flagged = true;
                    continue;
                }
                default:
                    throw new SyntaxFailure(i + 1);
            }
            if (!keepRaw) text += ch;
            i += 2;
            segment = i;
        }
        if (keepRaw) {
            if (flagged) {
                const copy = copyRawUtf8(bytes, { offset: start, rawLength: i - start });
                if (copy === null) unpaired = true;
                else copy.fill(0);
            }
            return null;
        }
        if (hasUnpairedSurrogate(text)) unpaired = true;
        return text;
    }

    function readLiteral(word, kind) {
        for (let k = 0; k < word.length; k++) {
            if (i + k >= n || bytes[i + k] !== word.charCodeAt(k)) throw new SyntaxFailure(Math.min(i + k, n));
        }
        const node = { kind, offset: i };
        i += word.length;
        return node;
    }

    function readNumber() {
        const start = i;
        if (bytes[i] === 0x2d) i++;
        if (i >= n) throw new SyntaxFailure(i);
        if (bytes[i] === 0x30) {
            i++;
        } else if (bytes[i] >= 0x31 && bytes[i] <= 0x39) {
            while (i < n && bytes[i] >= 0x30 && bytes[i] <= 0x39) i++;
        } else {
            throw new SyntaxFailure(i);
        }
        if (i < n && bytes[i] === 0x2e) {
            i++;
            if (i >= n || bytes[i] < 0x30 || bytes[i] > 0x39) throw new SyntaxFailure(i);
            while (i < n && bytes[i] >= 0x30 && bytes[i] <= 0x39) i++;
        }
        if (i < n && (bytes[i] === 0x65 || bytes[i] === 0x45)) {
            i++;
            if (i < n && (bytes[i] === 0x2b || bytes[i] === 0x2d)) i++;
            if (i >= n || bytes[i] < 0x30 || bytes[i] > 0x39) throw new SyntaxFailure(i);
            while (i < n && bytes[i] >= 0x30 && bytes[i] <= 0x39) i++;
        }
        if (i < n) {
            const b = bytes[i];
            const delimiter = b === 0x2c || b === 0x5d || b === 0x7d || b === 0x20 || b === 0x09 || b === 0x0a || b === 0x0d;
            if (!delimiter) throw new SyntaxFailure(i);
        }
        return { kind: Kind.Number, offset: start, text: bytes.toString("latin1", start, i) };
    }

    /** Reads one value at `i` (whitespace already skipped). Returns a failure object for depth, else undefined. */
    function readValue() {
        if (i >= n) throw new SyntaxFailure(i);
        const b = bytes[i];
        if (b === 0x7b || b === 0x5b) return readContainer(b === 0x7b);
        if (b === 0x22) {
            const offset = i;
            const raw = rawRootMembers && frames.length === 1 && frames[0].node.kind === Kind.Object
                && frames[0].pendingName !== null && rawRootMembers.has(frames[0].pendingName);
            const value = readString(raw);
            attach(raw ? { kind: Kind.String, offset, raw: true, rawLength: i - offset } : { kind: Kind.String, offset, text: value });
            return undefined;
        }
        if (b === 0x74) {
            attach(readLiteral("true", Kind.True));
            return undefined;
        }
        if (b === 0x66) {
            attach(readLiteral("false", Kind.False));
            return undefined;
        }
        if (b === 0x6e) {
            attach(readLiteral("null", Kind.Null));
            return undefined;
        }
        if (b === 0x2d || (b >= 0x30 && b <= 0x39)) {
            attach(readNumber());
            return undefined;
        }
        throw new SyntaxFailure(i);
    }

    function readContainer(isObject) {
        const offset = i;
        const path = pathOfNext();
        if (frames.length + 1 > MAX_DEPTH) {
            return { failure: "depth", path, offset };
        }
        const node = isObject ? { kind: Kind.Object, offset, members: [] } : { kind: Kind.Array, offset, items: [] };
        const opaque = isOpaqueNext();
        attach(node);
        const frame = { node, path, opaque, seen: new Set(), pendingName: null, pendingDuplicate: false, index: 0 };
        frames.push(frame);
        i++;
        skipSpace();
        const close = isObject ? 0x7d : 0x5d;
        if (i < n && bytes[i] === close) {
            i++;
            frames.pop();
            return undefined;
        }
        while (true) {
            if (isObject) {
                if (i >= n || bytes[i] !== 0x22) throw new SyntaxFailure(i);
                const name = readString(false);
                skipSpace();
                if (i >= n || bytes[i] !== 0x3a) throw new SyntaxFailure(i);
                i++;
                frame.pendingName = name;
                frame.pendingDuplicate = frame.seen.has(name);
                frame.seen.add(name);
                skipSpace();
            }
            const nested = readValue();
            if (nested) return nested;
            skipSpace();
            if (i >= n) throw new SyntaxFailure(i);
            if (bytes[i] === 0x2c) {
                i++;
                skipSpace();
                continue;
            }
            if (bytes[i] === close) {
                i++;
                frames.pop();
                return undefined;
            }
            throw new SyntaxFailure(i);
        }
    }

    try {
        skipSpace();
        const nested = readValue();
        if (nested) {
            const at = position(bytes, nested.offset);
            return { root: null, failure: "depth", failurePath: nested.path, failureLine: at.line, failureColumn: at.column, duplicates: [], unpairedSurrogate: false };
        }
        skipSpace();
        if (i < n) throw new SyntaxFailure(i);
    } catch (error) {
        if (!(error instanceof SyntaxFailure)) throw error;
        const at = position(bytes, error.offset);
        return { root: null, failure: "syntax", failurePath: pathOfNext(), failureLine: at.line, failureColumn: at.column, duplicates: [], unpairedSurrogate: false };
    }

    return { root, failure: null, failurePath: "", failureLine: null, failureColumn: null, duplicates, unpairedSurrogate: unpaired };
}

/**
 * Unescapes a raw string node's value into a new Buffer of UTF-8 bytes, or returns null when an
 * escape forms an unpaired surrogate (which has no UTF-8 form). The caller zeroes the buffer.
 */
function copyRawUtf8(bytes, node) {
    const end = node.offset + node.rawLength - 1; // the closing quote
    const out = Buffer.alloc(Math.max(1, node.rawLength));
    let length = 0;
    let i = node.offset + 1;
    const units = [];
    function flushUnits() {
        if (units.length === 0) return true;
        const text = String.fromCharCode(...units);
        units.length = 0;
        if (hasUnpairedSurrogate(text)) return false;
        length += out.write(text, length, "utf8");
        return true;
    }
    while (i < end) {
        const b = bytes[i];
        if (b !== 0x5c) {
            if (!flushUnits()) {
                out.fill(0);
                return null;
            }
            out[length++] = b;
            i++;
            continue;
        }
        const e = bytes[i + 1];
        if (e === 0x75) {
            units.push(parseInt(bytes.toString("latin1", i + 2, i + 6), 16));
            i += 6;
            continue;
        }
        if (!flushUnits()) {
            out.fill(0);
            return null;
        }
        const map = { 0x22: 0x22, 0x5c: 0x5c, 0x2f: 0x2f, 0x62: 0x08, 0x66: 0x0c, 0x6e: 0x0a, 0x72: 0x0d, 0x74: 0x09 };
        out[length++] = map[e];
        i += 2;
    }
    if (!flushUnits()) {
        out.fill(0);
        return null;
    }
    const result = Buffer.from(out.subarray(0, length));
    out.fill(0);
    return result;
}

const INT64_MIN = -(2n ** 63n);
const INT64_MAX = 2n ** 63n - 1n;

/**
 * A number token without a fraction or exponent that fits in a signed 64-bit integer, as a
 * number (exact up to 2^53, which every range of the contract is well inside); otherwise null.
 */
function integerOf(node) {
    if (!node || node.kind !== Kind.Number || typeof node.text !== "string" || !/^-?[0-9]+$/.test(node.text)) return null;
    const big = BigInt(node.text);
    if (big < INT64_MIN || big > INT64_MAX) return null;
    return Number(big);
}

module.exports = {
    Kind,
    MAX_DEPTH,
    parse,
    position,
    copyRawUtf8,
    integerOf,
    firstInvalidUtf8,
    escapePointer,
    appendPointer,
    pointerForUnknown,
    isPrintableAscii,
    hasUnpairedSurrogate,
};
