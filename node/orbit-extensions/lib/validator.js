// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * Validation primitives over a parsed JSON tree that report diagnostics with the paths,
 * positions and precedence of contract §4. Nodes built from a JavaScript object carry no
 * position; a `null` in such an object is `fromCode` and reported as null.member or null.element.
 */

const { Kind, position, appendPointer, pointerForUnknown, integerOf } = require("./json.js");
const { checkDeclarationText, textElements, isHostId } = require("./text.js");
const { findHost } = require("./hosts.js");

const SCHEMA_ROOT = "https://dev.ventana.tools/schemas/extensions/";

class Validator {
    /**
     * @param {import("./codes.js").DiagnosticBag} bag
     * @param {Buffer|null} document the parsed bytes, for positions; null for a tree built in code
     * @param {string|undefined} file
     */
    constructor(bag, document, file) {
        this.bag = bag;
        this.document = document;
        this.file = file;
    }

    report(code, path, at) {
        let line;
        let column;
        if (this.document && at && typeof at.offset === "number" && at.offset >= 0) {
            const p = position(this.document, at.offset);
            line = p.line;
            column = p.column;
        }
        this.bag.report(code, path, this.file, line, column);
    }

    /** The members of `node` by name; reports unknown members (json.member-renamed for a name in `renamed`). */
    members(node, path, known, renamed) {
        const result = new Map();
        for (const member of node.members || []) {
            if (known.includes(member.name)) {
                if (!result.has(member.name)) result.set(member.name, member.value);
                continue;
            }
            const unknown = pointerForUnknown(path, member.name);
            const code = renamed && renamed.includes(member.name) ? "json.member-renamed" : "json.unknown-member";
            this.report(code, unknown.path, unknown.truncated ? node : member.value);
        }
        return result;
    }

    /** Checks that `node` has `kind`; reports json.null-not-allowed, null.member, null.element or json.type-mismatch. */
    expect(node, path, kind, element = false) {
        if (node.kind === Kind.Null) {
            this.report(node.fromCode ? (element ? "null.element" : "null.member") : "json.null-not-allowed", path, node);
            return false;
        }
        if (node.kind === kind) return true;
        this.report("json.type-mismatch", path, node);
        return false;
    }

    /** A member that must be present: reports `missingCode` at the member's path when it is not. */
    required(members, name, path, parent, missingCode = "json.required-missing") {
        if (members.has(name)) return members.get(name);
        this.report(missingCode, appendPointer(path, name), parent);
        return null;
    }

    asString(node, path, element = false) {
        return this.expect(node, path, Kind.String, element) ? node.text : null;
    }

    asInteger(node, path) {
        if (!this.expect(node, path, Kind.Number)) return null;
        const value = integerOf(node);
        if (value !== null) return value;
        this.report("json.type-mismatch", path, node);
        return null;
    }

    /** Checks a declaration string (contract §3.6); `labelCode` replaces text.empty for a name, which also gets text.long. */
    declarationText(value, at, path, maxUnits, labelCode) {
        if (value === null || value === undefined) return false;
        const code = checkDeclarationText(value, maxUnits);
        if (code !== null) {
            this.report(code === "text.empty" && labelCode ? labelCode : code, path, at);
            return false;
        }
        if (labelCode && textElements(value) > 32) this.report("text.long", path, at);
        return true;
    }

    /** The items of an array member with its length checked, or null after reporting why there are none. */
    array(node, path, min, max) {
        if (!this.expect(node, path, Kind.Array)) return null;
        const items = node.items;
        if (items.length < min) this.report("list.too-short", path, node);
        else if (items.length > max) this.report("list.too-long", path, node);
        return items;
    }

    duplicates(parse) {
        for (const duplicate of parse.duplicates) this.report("json.duplicate-member", duplicate.path, duplicate.value);
    }

    /** A $schema member: at most 512 UTF-16 code units, and schema.uri-mismatch for an http(s) URL not in `published`. */
    schemaMember(members, path, published) {
        if (!members.has("$schema")) return;
        const node = members.get("$schema");
        const memberPath = appendPointer(path, "$schema");
        const value = this.asString(node, memberPath);
        if (value === null) return;
        if (value.length > 512) {
            this.report("string.too-long", memberPath, node);
            return;
        }
        if (published && isHttpUrl(value) && !published.includes(value)) this.report("schema.uri-mismatch", memberPath, node);
    }

    parseFailure(parse) {
        this.bag.report(parse.failure === "depth" ? "json.depth" : "json.syntax", parse.failurePath, this.file, parse.failureLine, parse.failureColumn);
    }
}

function isHttpUrl(value) {
    let url;
    try {
        url = new URL(value);
    } catch {
        return false;
    }
    return url.protocol === "https:" || url.protocol === "http:";
}

/** The host-keyed schema URL (contract §2.2, §11.5). */
function hostKeyedSchema(hostId, name) {
    return SCHEMA_ROOT + hostId + "/" + name + ".v3.json";
}

/** The host-keyed URLs of `name` for every listed host, by id and by known aliases. */
function schemaUrlsForHosts(hostIds, name, knownHosts) {
    const urls = [];
    for (const id of hostIds) {
        if (!isHostId(id)) continue;
        urls.push(hostKeyedSchema(id, name));
        const host = findHost(id, knownHosts);
        if (host) {
            if (isHostId(host.id)) urls.push(hostKeyedSchema(host.id, name));
            for (const alias of host.aliases || []) {
                if (isHostId(alias)) urls.push(hostKeyedSchema(alias, name));
            }
        }
    }
    return urls;
}

/** The checks every JSON file reader shares before parsing: size and encoding. Returns the document, or null. */
function openJsonFile(bytes, maxBytes, file, bag, tooLarge = "json.too-large", encoding = "json.encoding") {
    if (bytes.length > maxBytes) {
        bag.report(tooLarge, "", file);
        return null;
    }
    if (hasWideByteOrderMark(bytes)) {
        bag.report(encoding, "", file);
        return null;
    }
    return stripUtf8Bom(bytes);
}

function hasWideByteOrderMark(bytes) {
    return (bytes.length >= 2 && bytes[0] === 0xff && bytes[1] === 0xfe)
        || (bytes.length >= 2 && bytes[0] === 0xfe && bytes[1] === 0xff)
        || (bytes.length >= 4 && bytes[0] === 0x00 && bytes[1] === 0x00 && bytes[2] === 0xfe && bytes[3] === 0xff);
}

function stripUtf8Bom(bytes) {
    return bytes.length >= 3 && bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf ? bytes.subarray(3) : bytes;
}

module.exports = { Validator, schemaUrlsForHosts, hostKeyedSchema, openJsonFile, hasWideByteOrderMark, stripUtf8Bom };
