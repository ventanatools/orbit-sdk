// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * The text and value rules every extension string follows: the declaration text rule (contract
 * §3.6), the glyph rule (§3.7), the URL rule (§3.8), face text cleaning (§7.7.3) and the shared
 * grammars of §2.4. Every function is pure and never throws for any input.
 */

const FORMAT = /^\p{Cf}$/u;
const WHITE_SPACE = /^\p{White_Space}$/u;
const MAX_ELEMENT_UNITS = 16;
const MAX_URL_LENGTH = 512;
const segmenter = new Intl.Segmenter("en", { granularity: "grapheme" });

const SEGMENT = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;

/** Whether `codePoint` is a disallowed character (contract §3.6); a surrogate code point stands for an unpaired surrogate. */
function isDisallowed(codePoint) {
    const cp = codePoint;
    if (!Number.isInteger(cp) || cp < 0 || cp > 0x10ffff) return true;
    if (cp <= 0x1f || (cp >= 0x7f && cp <= 0x9f)
        || cp === 0x2028 || cp === 0x2029
        || (cp >= 0x202a && cp <= 0x202e)
        || (cp >= 0x2066 && cp <= 0x2069)
        || cp === 0xfeff || cp === 0xfffd
        || (cp >= 0xfdd0 && cp <= 0xfdef)
        || (cp >= 0xd800 && cp <= 0xdfff)
        || (cp >= 0xe0000 && cp <= 0xe007f)) {
        return true;
    }
    if ((cp & 0xfffe) === 0xfffe) return true;
    if (cp === 0x200c || cp === 0x200d || cp === 0x200e || cp === 0x200f || cp === 0x061c) return false;
    return FORMAT.test(String.fromCodePoint(cp));
}

/** Unicode White_Space, as .NET's char.IsWhiteSpace. */
function isWhiteSpace(unit) {
    return WHITE_SPACE.test(String.fromCharCode(unit));
}

/** Code points of `text`, with an unpaired surrogate as its own code unit. */
function* codePoints(text) {
    for (let i = 0; i < text.length; i++) {
        const c = text.charCodeAt(i);
        if (c >= 0xd800 && c <= 0xdbff && i + 1 < text.length) {
            const d = text.charCodeAt(i + 1);
            if (d >= 0xdc00 && d <= 0xdfff) {
                yield { codePoint: ((c - 0xd800) << 10) + (d - 0xdc00) + 0x10000, index: i, units: 2 };
                i++;
                continue;
            }
        }
        yield { codePoint: c, index: i, units: 1 };
    }
}

function hasDisallowed(text) {
    for (const item of codePoints(text)) {
        if (isDisallowed(item.codePoint)) return true;
    }
    return false;
}

/** Whether the text is only White_Space and format characters. */
function isBlank(text) {
    for (const item of codePoints(text)) {
        if (item.units === 1 && isWhiteSpace(item.codePoint)) continue;
        if (item.codePoint >= 0xd800 && item.codePoint <= 0xdfff) return false;
        if (!FORMAT.test(String.fromCodePoint(item.codePoint))) return false;
    }
    return true;
}

function trimStart(text) {
    let start = 0;
    while (start < text.length && isWhiteSpace(text.charCodeAt(start))) start++;
    return text.slice(start);
}

function trimEnd(text) {
    let end = text.length;
    while (end > 0 && isWhiteSpace(text.charCodeAt(end - 1))) end--;
    return text.slice(0, end);
}

/** The number of text elements (extended grapheme clusters). */
function textElements(text) {
    let count = 0;
    for (const _ of segmenter.segment(text)) count++;
    return count;
}

/**
 * Checks one declaration string (contract §3.6) of at most `maxUnits` UTF-16 code units. Returns
 * null when valid, else the first code that applies: text.empty, string.too-long, text.empty,
 * text.whitespace, text.invalid-character.
 */
function checkDeclarationText(value, maxUnits) {
    if (typeof value !== "string" || value.length === 0) return "text.empty";
    if (value.length > maxUnits) return "string.too-long";
    if (isBlank(value)) return "text.empty";
    if (isWhiteSpace(value.charCodeAt(0)) || isWhiteSpace(value.charCodeAt(value.length - 1))) return "text.whitespace";
    return hasDisallowed(value) ? "text.invalid-character" : null;
}

/**
 * Cleans face text (contract §7.7.3): removes every disallowed character, except that line and
 * paragraph breaks and tabs become a space; replaces a text element longer than 16 UTF-16 code
 * units with U+FFFD; trims; and cuts at a text-element boundary within both limits. Returns null
 * when nothing remains.
 */
function clean(text, maxElements, maxUnits) {
    if (typeof text !== "string" || text.length === 0 || !(maxElements > 0) || !(maxUnits > 0)) return null;
    let kept = "";
    for (const item of codePoints(text)) {
        const cp = item.codePoint;
        if ((cp >= 0x09 && cp <= 0x0d) || cp === 0x85 || cp === 0x2028 || cp === 0x2029) kept += " ";
        else if (!isDisallowed(cp)) kept += text.substr(item.index, item.units);
    }
    const cleaned = trimEnd(trimStart(kept));
    let cut = "";
    let elements = 0;
    for (const { segment } of segmenter.segment(cleaned)) {
        if (elements >= maxElements) break;
        const element = segment.length > MAX_ELEMENT_UNITS ? "�" : segment;
        if (cut.length + element.length > maxUnits) break;
        cut += element;
        elements++;
    }
    const result = trimEnd(cut);
    return result.length === 0 ? null : result;
}

/** Exactly one UTF-16 code unit in U+E000 to U+F8FF (contract §3.7). */
function isGlyph(value) {
    if (typeof value !== "string" || value.length !== 1) return false;
    const c = value.charCodeAt(0);
    return c >= 0xe000 && c <= 0xf8ff;
}

/** A segment: lowercase ASCII letters and digits with single hyphens between them. */
function isSegment(value) {
    return typeof value === "string" && SEGMENT.test(value);
}

function isSettingId(value) {
    return typeof value === "string" && /^[a-z][A-Za-z0-9]{0,31}$/.test(value);
}

function isChoiceValue(value) {
    return typeof value === "string" && value.length >= 1 && value.length <= 64 && isSegment(value);
}

function isHostId(value) {
    return typeof value === "string" && value.length >= 1 && value.length <= 32 && /^[a-z]/.test(value) && isSegment(value);
}

function isLanguageTag(value) {
    return typeof value === "string" && value.length >= 1 && value.length <= 35 && /^[A-Za-z]{2,3}(?:-[A-Za-z0-9]{1,8})*$/.test(value);
}

/**
 * The URL rule (contract §3.8): an absolute URI with scheme https, an authority without user
 * information, only printable ASCII and no white space, and at most 512 characters.
 */
function isHttpsUrl(value) {
    if (typeof value !== "string" || value.length <= 8 || value.length > MAX_URL_LENGTH) return false;
    for (let i = 0; i < value.length; i++) {
        const c = value.charCodeAt(i);
        if (c < 0x21 || c > 0x7e) return false;
    }
    if (value.slice(0, 8).toLowerCase() !== "https://") return false;
    const rest = value.slice(8);
    const end = rest.search(/[/?#]/);
    const authority = end < 0 ? rest : rest.slice(0, end);
    if (authority.length === 0 || authority.includes("@") || authority[0] === ":") return false;
    let url;
    try {
        url = new URL(value);
    } catch {
        return false;
    }
    return url.protocol === "https:" && url.username === "" && url.password === "" && url.hostname.length > 0;
}

// ---------------------------------------------------------------- shared grammars (contract §2.4)

/** segment ( "." segment )+, at most 64 characters. */
function isDottedCode(value) {
    if (typeof value !== "string" || value.length === 0 || value.length > 64) return false;
    const parts = value.split(".");
    return parts.length >= 2 && parts.every(isSegment);
}

/** 32 lowercase hexadecimal digits, not all zero. */
function isGuid(value) {
    return typeof value === "string" && /^[0-9a-f]{32}$/.test(value) && /[1-9a-f]/.test(value);
}

function isSha256(value) {
    return typeof value === "string" && /^[0-9a-f]{64}$/.test(value);
}

const BASE64 = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

/** Decodes canonical standard Base64 with padding of exactly 32 bytes (44 characters); null otherwise. */
function decodeKey32(value) {
    if (typeof value !== "string" || value.length !== 44 || value[43] !== "=" || value[42] === "=") return null;
    for (let i = 0; i < 43; i++) {
        if (BASE64.indexOf(value[i]) < 0) return null;
    }
    if ((BASE64.indexOf(value[42]) & 0b11) !== 0) return null;
    const bytes = Buffer.from(value, "base64");
    if (bytes.length !== 32) {
        bytes.fill(0);
        return null;
    }
    return bytes;
}

function isKey32(value) {
    const bytes = decodeKey32(value);
    if (!bytes) return false;
    bytes.fill(0);
    return true;
}

/** The JSON member-name grammar [a-z][A-Za-z0-9]{0,31}. */
function isMemberName(value) {
    return typeof value === "string" && /^[a-z][A-Za-z0-9]{0,31}$/.test(value);
}

/** Package version: num "." num "." num, num = "0" / [1-9][0-9]{0,8}. */
function isPackageVersion(value) {
    return typeof value === "string" && value.length <= 29 && /^(?:0|[1-9][0-9]{0,8})\.(?:0|[1-9][0-9]{0,8})\.(?:0|[1-9][0-9]{0,8})$/.test(value);
}

/** Printable ASCII, 1 to `max` characters. */
function isPrintableAscii(value, max) {
    return typeof value === "string" && value.length >= 1 && value.length <= max && /^[\x20-\x7e]*$/.test(value);
}

/** [A-Za-z0-9@._/+-]{1,64}: the informational client name in hello. */
function isClientName(value) {
    return typeof value === "string" && /^[A-Za-z0-9@._/+-]{1,64}$/.test(value);
}

/** [0-9A-Za-z.+-]{1,32}: a client or host version in the handshake. */
function isPeerVersion(value) {
    return typeof value === "string" && /^[0-9A-Za-z.+-]{1,32}$/.test(value);
}

/** A pipe-name edition: 1*32( [a-z0-9] / "-" ). */
function isEdition(value) {
    return typeof value === "string" && /^[a-z0-9-]{1,32}$/.test(value);
}

function isUserHash(value) {
    return typeof value === "string" && /^[0-9a-f]{16}$/.test(value);
}

module.exports = {
    isDisallowed,
    isWhiteSpace,
    isBlank,
    textElements,
    checkDeclarationText,
    clean,
    isGlyph,
    isSegment,
    isSettingId,
    isChoiceValue,
    isHostId,
    isLanguageTag,
    isHttpsUrl,
    isDottedCode,
    isGuid,
    isSha256,
    decodeKey32,
    isKey32,
    isMemberName,
    isPackageVersion,
    isPrintableAscii,
    isClientName,
    isPeerVersion,
    isEdition,
    isUserHash,
};
