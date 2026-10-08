// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * Reads and validates manifests (contract §3) with every rule and code of contract §4.4. Content
 * problems are always diagnostics, never exceptions. A valid manifest is returned as a frozen
 * plain object with the members of the file.
 */

const { Kind, parse, appendPointer, integerOf } = require("./json.js");
const { DiagnosticBag, isKnownCapability, isExperimentalCapability } = require("./codes.js");
const { Validator, schemaUrlsForHosts, openJsonFile } = require("./validator.js");
const { isGlyph, isHostId, isLanguageTag, isHttpsUrl, isPackageVersion, isSegment } = require("./text.js");
const { classifyId, isInNamespace, reservedPublishers } = require("./ids.js");
const { hosts: registryHosts, findHost } = require("./hosts.js");
const { readBounded, readBoundedSync } = require("./files.js");

const MAX_BYTES = 65536;
const CURRENT_SCHEMA_VERSION = 3;

const ROOT_MEMBERS = ["$schema", "schemaVersion", "id", "name", "description", "version", "hosts", "glyph", "publisher",
    "supportUrl", "defaultLanguage", "disclosures", "requires", "contributions"];
const CONTRIBUTION_MEMBERS = ["id", "name", "description", "glyph", "provides", "settings"];
const SETTING_MEMBERS = ["id", "kind", "name", "description", "default", "choices"];
const CHOICE_MEMBERS = ["value", "name"];
const PUBLISHER_MEMBERS = ["name", "url"];
const DISCLOSURE_MEMBERS = ["network", "privacyUrl"];
const REQUIRES_MEMBERS = ["capabilities"];
const ROOT_RENAMED = ["manifestVersion"];
const CONTRIBUTION_RENAMED = ["capabilities"];
const NETWORK_USES = ["None", "LocalNetwork", "Internet"];
const SETTING_KINDS = ["Choice"];
const PROVIDES = ["invoke", "face"];

function resolveOptions(options) {
    const o = options || {};
    return {
        hostId: typeof o.hostId === "string" ? o.hostId : undefined,
        supportedCapabilities: Array.isArray(o.supportedCapabilities) ? o.supportedCapabilities : undefined,
        allowExperimentalCapabilities: o.allowExperimentalCapabilities === true,
        origin: o.origin === "FirstParty" ? "FirstParty" : "ThirdParty",
        reservedPublishers: Array.isArray(o.reservedPublishers) ? o.reservedPublishers : reservedPublishers,
        knownHosts: Array.isArray(o.knownHosts) ? o.knownHosts : registryHosts,
    };
}

/** Validates a manifest tree; returns false when it cannot be a manifest at all. */
function validateTree(root, v, options) {
    if (root.kind === Kind.Null && root.fromCode) {
        v.report("manifest.null", "", root);
        return false;
    }
    if (!v.expect(root, "", Kind.Object)) return false;

    // A different schema version makes every other rule meaningless; report only that.
    const versionMember = root.members.find((member) => member.name === "schemaVersion");
    if (versionMember) {
        const schema = integerOf(versionMember.value);
        if (schema !== null && schema !== CURRENT_SCHEMA_VERSION) {
            v.report("schema.version-unsupported", "/schemaVersion", versionMember.value);
            return false;
        }
    }

    const m = v.members(root, "", ROOT_MEMBERS, ROOT_RENAMED);
    if (m.has("schemaVersion")) v.asInteger(m.get("schemaVersion"), "/schemaVersion");
    else if (!root.members.some((member) => member.name === "manifestVersion")) v.report("json.required-missing", "/schemaVersion", root);

    const id = extensionId(m, root, v, options);
    const name = v.required(m, "name", "", root);
    if (name) v.declarationText(v.asString(name, "/name"), name, "/name", 80, "chrome.label-required");
    const description = v.required(m, "description", "", root);
    if (description) v.declarationText(v.asString(description, "/description"), description, "/description", 512);
    const versionNode = v.required(m, "version", "", root);
    if (versionNode) {
        const version = v.asString(versionNode, "/version");
        if (version !== null && !isPackageVersion(version)) v.report("manifest.version-invalid", "/version", versionNode);
    }

    const hostList = hosts(m, root, v, options);
    if (m.has("glyph")) glyph(m.get("glyph"), "/glyph", v);
    if (m.has("publisher")) {
        const publisher = m.get("publisher");
        if (v.expect(publisher, "/publisher", Kind.Object)) {
            const pm = v.members(publisher, "/publisher", PUBLISHER_MEMBERS);
            const publisherName = v.required(pm, "name", "/publisher", publisher);
            if (publisherName) v.declarationText(v.asString(publisherName, "/publisher/name"), publisherName, "/publisher/name", 80, "chrome.label-required");
            url(pm, "url", "/publisher", v);
        }
    }
    url(m, "supportUrl", "", v);
    if (m.has("defaultLanguage")) {
        const language = m.get("defaultLanguage");
        const tag = v.asString(language, "/defaultLanguage");
        if (tag !== null && !isLanguageTag(tag)) v.report("language.invalid", "/defaultLanguage", language);
    }
    if (m.has("disclosures")) {
        const disclosures = m.get("disclosures");
        if (v.expect(disclosures, "/disclosures", Kind.Object)) {
            const dm = v.members(disclosures, "/disclosures", DISCLOSURE_MEMBERS);
            const network = v.required(dm, "network", "/disclosures", disclosures);
            if (network && v.expect(network, "/disclosures/network", Kind.String) && !NETWORK_USES.includes(network.text)) {
                v.report("enum.undefined", "/disclosures/network", network);
            }
            url(dm, "privacyUrl", "/disclosures", v);
        }
    }
    if (m.has("requires")) {
        const requires = m.get("requires");
        if (v.expect(requires, "/requires", Kind.Object)) requirements(requires, v, options);
    }
    contributions(m, root, v, options, id);
    v.schemaMember(m, "", hostList ? schemaUrlsForHosts(hostList, "manifest", options.knownHosts) : null);
    return true;
}

function extensionId(m, root, v, options) {
    const node = v.required(m, "id", "", root, "id.required");
    if (!node) return null;
    const id = v.asString(node, "/id");
    if (id === null) return null;
    let code = classifyId(id, options.origin, options.reservedPublishers);
    if (code === null && id.includes("/")) code = "id.grammar";
    if (code !== null) {
        v.report(code, "/id", node);
        return null;
    }
    return id;
}

function hosts(m, root, v, options) {
    const node = v.required(m, "hosts", "", root);
    if (!node) return null;
    const items = v.array(node, "/hosts", 1, 8);
    if (!items) return null;
    const list = [];
    const seen = new Set();
    items.forEach((item, i) => {
        const path = appendPointer("/hosts", i);
        const host = v.asString(item, path, true);
        if (host === null) return;
        if (!isHostId(host)) {
            v.report("manifest.host-invalid", path, item);
            return;
        }
        if (seen.has(host)) {
            v.report("manifest.host-duplicate", path, item);
            return;
        }
        seen.add(host);
        list.push(host);
        const current = options.hostId !== undefined && host === options.hostId;
        const known = findHost(host, options.knownHosts);
        if (!current && !(known && known.status === "Active")) v.report("manifest.host-unknown", path, item);
    });
    if (options.hostId !== undefined && items.length >= 1 && items.length <= 8) {
        const accepted = new Set([options.hostId]);
        const self = findHost(options.hostId, options.knownHosts);
        if (self) {
            accepted.add(self.id);
            for (const alias of self.aliases || []) accepted.add(alias);
        }
        if (!list.some((host) => accepted.has(host))) v.report("manifest.host-not-listed", "/hosts", node);
    }
    return list;
}

function requirements(requires, v, options) {
    const rm = v.members(requires, "/requires", REQUIRES_MEMBERS);
    const node = v.required(rm, "capabilities", "/requires", requires);
    if (!node) return;
    const items = v.array(node, "/requires/capabilities", 1, 16);
    if (!items) return;
    const seen = new Set();
    items.forEach((item, i) => {
        const path = appendPointer("/requires/capabilities", i);
        const id = v.asString(item, path, true);
        if (id === null) return;
        if (id.length > 64) {
            v.report("string.too-long", path, item);
            return;
        }
        if (seen.has(id)) {
            v.report("requires.capability-duplicate", path, item);
            return;
        }
        seen.add(id);
        if (options.supportedCapabilities) {
            const allowed = options.supportedCapabilities.includes(id) && (!isExperimentalCapability(id) || options.allowExperimentalCapabilities);
            if (!allowed) v.report("requires.capability-unsupported", path, item);
        } else if (!isKnownCapability(id)) {
            v.report("requires.capability-unknown", path, item);
        }
    });
}

function contributions(m, root, v, options, extension) {
    const node = v.required(m, "contributions", "", root);
    if (!node) return;
    const items = v.array(node, "/contributions", 1, 32);
    if (!items) return;
    const ids = [];
    items.forEach((item, i) => {
        const path = appendPointer("/contributions", i);
        if (!v.expect(item, path, Kind.Object, true)) return;
        const found = contribution(item, path, v, options);
        if (found) ids.push(found);
    });
    const seen = new Set();
    for (const { id, path, at } of ids) {
        if (seen.has(id)) v.report("id.duplicate", path, at);
        seen.add(id);
        if (extension === null) continue;
        const leaf = id === extension;
        if ((leaf && items.length !== 1) || (!leaf && !isInNamespace(id, extension))) v.report("id.outside-namespace", path, at);
    }
}

function contribution(item, path, v, options) {
    const cm = v.members(item, path, CONTRIBUTION_MEMBERS, CONTRIBUTION_RENAMED);
    let result = null;
    const idPath = appendPointer(path, "id");
    const idNode = v.required(cm, "id", path, item, "id.required");
    if (idNode) {
        const id = v.asString(idNode, idPath);
        if (id !== null) {
            const code = classifyId(id, options.origin, options.reservedPublishers);
            if (code !== null) v.report(code, idPath, idNode);
            else result = { id, path: idPath, at: idNode };
        }
    }
    const name = v.required(cm, "name", path, item);
    if (name) {
        const namePath = appendPointer(path, "name");
        v.declarationText(v.asString(name, namePath), name, namePath, 80, "chrome.label-required");
    }
    const description = v.required(cm, "description", path, item);
    if (description) {
        const descriptionPath = appendPointer(path, "description");
        v.declarationText(v.asString(description, descriptionPath), description, descriptionPath, 512);
    }
    const glyphNode = v.required(cm, "glyph", path, item, "chrome.glyph-required");
    if (glyphNode) glyph(glyphNode, appendPointer(path, "glyph"), v);
    const providesPath = appendPointer(path, "provides");
    if (cm.has("provides")) provides(cm.get("provides"), providesPath, v);
    else if (!(item.members || []).some((member) => member.name === "capabilities")) v.report("json.required-missing", providesPath, item);
    if (cm.has("settings")) settings(cm.get("settings"), appendPointer(path, "settings"), v);
    return result;
}

function provides(node, path, v) {
    const items = v.array(node, path, 1, 2);
    if (!items) return;
    const seen = new Set();
    items.forEach((item, i) => {
        const itemPath = appendPointer(path, i);
        if (!v.expect(item, itemPath, Kind.String, true)) return;
        if (!PROVIDES.includes(item.text)) v.report("contribution.provides-unknown", itemPath, item);
        else if (seen.has(item.text)) v.report("contribution.provides-duplicate", itemPath, item);
        else seen.add(item.text);
    });
}

function settings(node, path, v) {
    const items = v.array(node, path, 0, 16);
    if (!items) return;
    const ids = new Set();
    items.forEach((item, i) => {
        const settingPath = appendPointer(path, i);
        if (!v.expect(item, settingPath, Kind.Object, true)) return;
        const sm = v.members(item, settingPath, SETTING_MEMBERS);
        const idPath = appendPointer(settingPath, "id");
        const idNode = v.required(sm, "id", settingPath, item, "setting.id-required");
        if (idNode) {
            const id = v.asString(idNode, idPath);
            if (id !== null) {
                if (/^\p{White_Space}*$/u.test(id)) v.report("setting.id-required", idPath, idNode);
                else if (!/^[a-z][A-Za-z0-9]*$/.test(id)) v.report("setting.id-grammar", idPath, idNode);
                else if (id.length > 32) v.report("string.too-long", idPath, idNode);
                else if (ids.has(id)) v.report("setting.id-duplicate", idPath, idNode);
                else ids.add(id);
            }
        }
        let choice = true;
        const kindPath = appendPointer(settingPath, "kind");
        const kind = v.required(sm, "kind", settingPath, item);
        if (kind && v.expect(kind, kindPath, Kind.String) && !SETTING_KINDS.includes(kind.text)) {
            v.report("setting.kind-unsupported", kindPath, kind);
            choice = false;
        }
        const name = v.required(sm, "name", settingPath, item);
        if (name) {
            const namePath = appendPointer(settingPath, "name");
            v.declarationText(v.asString(name, namePath), name, namePath, 80, "setting.label-required");
        }
        if (sm.has("description")) {
            const description = sm.get("description");
            const descriptionPath = appendPointer(settingPath, "description");
            v.declarationText(v.asString(description, descriptionPath), description, descriptionPath, 512);
        }
        let defaultValue = null;
        let defaultNode = null;
        const defaultPath = appendPointer(settingPath, "default");
        if (sm.has("default")) {
            defaultNode = sm.get("default");
            defaultValue = v.asString(defaultNode, defaultPath);
        } else if (choice) {
            v.report("setting.default-required", defaultPath, item);
        }
        let values = null;
        const choicesPath = appendPointer(settingPath, "choices");
        if (sm.has("choices")) values = choices(sm.get("choices"), choicesPath, v);
        else if (choice) v.report("setting.choices-required", choicesPath, item);
        if (choice && defaultValue !== null && defaultNode && values && !values.has(defaultValue)) {
            v.report("setting.default-unknown", defaultPath, defaultNode);
        }
    });
}

function choices(node, path, v) {
    const items = v.array(node, path, 2, 32);
    if (!items) return null;
    const values = new Set();
    items.forEach((item, i) => {
        const choicePath = appendPointer(path, i);
        if (!v.expect(item, choicePath, Kind.Object, true)) return;
        const cm = v.members(item, choicePath, CHOICE_MEMBERS);
        const valuePath = appendPointer(choicePath, "value");
        const valueNode = v.required(cm, "value", choicePath, item, "choice.value-required");
        if (valueNode) {
            const value = v.asString(valueNode, valuePath);
            if (value !== null) {
                if (value.length === 0) v.report("choice.value-required", valuePath, valueNode);
                else if (!isSegment(value)) v.report("choice.value-grammar", valuePath, valueNode);
                else if (value.length > 64) v.report("string.too-long", valuePath, valueNode);
                else if (values.has(value)) v.report("choice.value-duplicate", valuePath, valueNode);
                else values.add(value);
            }
        }
        const name = v.required(cm, "name", choicePath, item);
        if (name) {
            const namePath = appendPointer(choicePath, "name");
            v.declarationText(v.asString(name, namePath), name, namePath, 80, "choice.label-required");
        }
    });
    return values;
}

function glyph(node, path, v) {
    const value = v.asString(node, path);
    if (value === null) return;
    if (value.length === 0) v.report("chrome.glyph-required", path, node);
    else if (!isGlyph(value)) v.report("chrome.glyph-invalid", path, node);
}

function url(members, name, parent, v) {
    if (!members.has(name)) return;
    const node = members.get(name);
    const path = appendPointer(parent, name);
    const value = v.asString(node, path);
    if (value !== null && !isHttpsUrl(value)) v.report("url.invalid", path, node);
}

// ---------------------------------------------------------------- trees and objects

/** The plain, deeply frozen value of a valid tree. */
function toValue(node) {
    switch (node.kind) {
        case Kind.Object: {
            const result = {};
            for (const member of node.members) {
                if (!Object.prototype.hasOwnProperty.call(result, member.name)) {
                    Object.defineProperty(result, member.name, { value: toValue(member.value), enumerable: true });
                }
            }
            return Object.freeze(result);
        }
        case Kind.Array:
            return Object.freeze(node.items.map(toValue));
        case Kind.String:
            return node.text;
        case Kind.Number:
            return integerOf(node) ?? Number(node.text);
        case Kind.True:
            return true;
        case Kind.False:
            return false;
        default:
            return null;
    }
}

// Which members of a manifest built in code are lists: a null list is null.member, and every other
// null member is treated as absent (contract §9.1 Validate, fixtures/manifests/built-in-code.json).
const LIST_MEMBERS = new Set(["hosts", "contributions", "settings", "capabilities", "provides"]);

/**
 * The tree of a manifest built in code (a JavaScript object). Nodes carry no position; `null`
 * and `undefined` members are absent, except lists, whose null is null.member; a null list entry
 * is null.element.
 */
function fromObject(value, memberName) {
    if (value === null || value === undefined) return { kind: Kind.Null, offset: -1, fromCode: true };
    if (Array.isArray(value)) {
        return { kind: Kind.Array, offset: -1, items: value.map((item) => fromObject(item, undefined)) };
    }
    switch (typeof value) {
        case "string":
            return { kind: Kind.String, offset: -1, text: value };
        case "number":
            return { kind: Kind.Number, offset: -1, text: String(value) };
        case "bigint":
            return { kind: Kind.Number, offset: -1, text: String(value) };
        case "boolean":
            return { kind: value ? Kind.True : Kind.False, offset: -1 };
        case "object": {
            const node = { kind: Kind.Object, offset: -1, members: [] };
            for (const [name, member] of Object.entries(value)) {
                if ((member === null || member === undefined) && !LIST_MEMBERS.has(name)) continue;
                if (member === undefined) continue;
                node.members.push({ name, value: fromObject(member, name) });
            }
            return node;
        }
        default:
            // Functions and symbols are not JSON; report them as a wrong type.
            return { kind: Kind.True, offset: -1, notJson: true };
    }
}

function result(manifest, bag) {
    const diagnostics = bag.toList();
    const valid = !diagnostics.some((diagnostic) => diagnostic.severity === "Error");
    return valid && manifest ? { manifest, diagnostics } : { manifest: undefined, diagnostics };
}

/**
 * Reads a manifest from its UTF-8 bytes (a Buffer or Uint8Array). Returns { manifest, diagnostics };
 * `manifest` is undefined whenever any diagnostic is an error.
 */
function readManifest(bytes, options, file) {
    const resolved = resolveOptions(options);
    const buffer = Buffer.isBuffer(bytes) ? bytes : Buffer.from(bytes);
    const bag = new DiagnosticBag();
    const document = openJsonFile(buffer, MAX_BYTES, file, bag);
    if (document === null) return result(undefined, bag);
    const parsed = parse(document);
    const v = new Validator(bag, document, file);
    if (!parsed.root) {
        v.parseFailure(parsed);
        return result(undefined, bag);
    }
    v.duplicates(parsed);
    const valid = validateTree(parsed.root, v, resolved);
    return result(valid && !bag.hasErrors ? toValue(parsed.root) : undefined, bag);
}

/** Reads a manifest file, never reading more than 65,536 + 1 bytes. File-system failures reject. */
async function readManifestFile(path, options) {
    const bytes = await readBounded(path, MAX_BYTES);
    return readManifest(bytes, options);
}

/** The synchronous form of readManifestFile. */
function readManifestFileSync(path, options) {
    return readManifest(readBoundedSync(path, MAX_BYTES), options);
}

/** Validates a manifest built in code. Diagnostics carry no line or column. */
function validateManifest(manifest, options) {
    const resolved = resolveOptions(options);
    const bag = new DiagnosticBag();
    const tree = fromObject(manifest, undefined);
    const v = new Validator(bag, null, undefined);
    const valid = validateTree(tree, v, resolved);
    return result(valid && !bag.hasErrors ? toValue(tree) : undefined, bag);
}

/** The contribution of a manifest by id, or undefined. */
function findContribution(manifest, contributionId) {
    return (manifest && Array.isArray(manifest.contributions) ? manifest.contributions : []).find((item) => item && item.id === contributionId);
}

/** Whether a contribution provides `token` ("invoke" or "face"). */
function provided(contribution, token) {
    return !!contribution && Array.isArray(contribution.provides) && contribution.provides.includes(token);
}

module.exports = {
    MAX_BYTES,
    CURRENT_SCHEMA_VERSION,
    readManifest,
    readManifestFile,
    readManifestFileSync,
    validateManifest,
    findContribution,
    provided,
};
