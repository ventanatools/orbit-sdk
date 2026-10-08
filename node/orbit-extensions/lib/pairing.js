// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * Pairing files (contract §6): the credential a host writes, on request, so a companion can
 * authenticate to one registration. Problems are diagnostics whose codes are the pairing.* reason
 * codes and the json.* codes of contract §4.4. The secret is never a JavaScript string: it is
 * decoded from the file's bytes into a private buffer, which `dispose()` zeroes; every other
 * buffer that held it is zeroed once read.
 */

const os = require("node:os");
const path = require("node:path");
const { Kind, parse, copyRawUtf8, integerOf } = require("./json.js");
const { DiagnosticBag } = require("./codes.js");
const { Validator, hasWideByteOrderMark, stripUtf8Bom } = require("./validator.js");
const { decodeKey32, isGuid, isHostId } = require("./text.js");
const { isExtensionId } = require("./ids.js");
const { parsePipeName, computeProof: proofWith, verifyProof: verifyWith } = require("./wire/handshake.js");
const { readBounded } = require("./files.js");

const MAX_BYTES = 4096;
const RAW = new Set(["secret"]);
const KNOWN = ["$schema", "pairingVersion", "mode", "hostId", "pipeName", "registrationId", "extensionId", "secret"];
const KNOWN_PER_LAUNCH = [...KNOWN, "credentialId", "expires", "packageFamilyName"];

/** A pairing: the credential for one registration. Its owner disposes it, which zeroes the secret. */
class Pairing {
    #secret;
    #disposed = false;

    constructor(hostId, pipeName, registrationId, extensionId, secret) {
        this.pairingVersion = 3;
        this.mode = "Persistent";
        this.hostId = hostId;
        this.pipeName = pipeName;
        this.registrationId = registrationId;
        this.extensionId = extensionId;
        this.#secret = secret;
        Object.freeze(this);
    }

    /** Computes a proof over `transcript` for `role` ("server" or "client") with this pairing's secret. */
    computeProof(transcript, role) {
        if (this.#disposed) throw new Error("The pairing was disposed.");
        return proofWith(this.#secret, transcript, role);
    }

    /** Verifies a peer's proof in constant time. */
    verifyProof(proof, transcript, role) {
        if (this.#disposed) throw new Error("The pairing was disposed.");
        return verifyWith(this.#secret, proof, transcript, role);
    }

    get disposed() {
        return this.#disposed;
    }

    /** Zeroes the secret; further proofs throw. */
    dispose() {
        if (!this.#disposed) {
            this.#secret.fill(0);
            this.#disposed = true;
        }
    }

    /** A description without the secret. */
    toString() {
        return "Pairing " + this.registrationId;
    }

    toJSON() {
        return { pairingVersion: 3, mode: "Persistent", hostId: this.hostId, pipeName: this.pipeName, registrationId: this.registrationId, extensionId: this.extensionId };
    }

    [Symbol.for("nodejs.util.inspect.custom")]() {
        return this.toString();
    }
}

function checkOptions(options) {
    if (!options || !isExtensionId(options.expectedExtensionId)) {
        throw new TypeError("expectedExtensionId must be the companion manifest's extension id.");
    }
    if (options.manifestHosts !== undefined && options.manifestHosts !== null && !Array.isArray(options.manifestHosts)) {
        throw new TypeError("manifestHosts must be an array of host ids.");
    }
}

function decodeSecret(document, node) {
    const utf8 = node.raw ? copyRawUtf8(document, node) : null;
    if (!utf8) return null;
    try {
        if (utf8.length !== 44) return null;
        for (const b of utf8) {
            if (b > 0x7e) return null;
        }
        return decodeKey32(utf8.toString("latin1"));
    } finally {
        utf8.fill(0);
    }
}

function validate(root, document, v, options) {
    if (!v.expect(root, "", Kind.Object)) return null;
    const versionMember = root.members.find((member) => member.name === "pairingVersion");
    if (versionMember) {
        const version = integerOf(versionMember.value);
        if (version !== null && version !== 3) {
            v.report("pairing.version-unsupported", "/pairingVersion", versionMember.value);
            return null;
        }
    }
    const perLaunch = root.members.some((member) => member.name === "mode" && member.value.kind === Kind.String && member.value.text === "PerLaunch");
    const m = v.members(root, "", perLaunch ? KNOWN_PER_LAUNCH : KNOWN);
    v.schemaMember(m, "", null);
    const versionNode = v.required(m, "pairingVersion", "", root);
    if (versionNode) v.asInteger(versionNode, "/pairingVersion");

    const modeNode = v.required(m, "mode", "", root);
    if (modeNode) {
        const mode = v.asString(modeNode, "/mode");
        if (mode !== null && mode !== "Persistent") v.report("pairing.mode-unsupported", "/mode", modeNode);
    }

    let hostId = null;
    const hostNode = v.required(m, "hostId", "", root);
    if (hostNode) {
        const host = v.asString(hostNode, "/hostId");
        if (host !== null) {
            if (!isHostId(host) || (Array.isArray(options.manifestHosts) && !options.manifestHosts.includes(host))) {
                v.report("pairing.host-not-listed", "/hostId", hostNode);
            } else {
                hostId = host;
            }
        }
    }

    let registrationId = null;
    const registrationNode = v.required(m, "registrationId", "", root);
    if (registrationNode) {
        const registration = v.asString(registrationNode, "/registrationId");
        if (registration !== null) {
            if (isGuid(registration)) registrationId = registration;
            else v.report("pairing.registration-invalid", "/registrationId", registrationNode);
        }
    }

    let pipeName = null;
    const pipeNode = v.required(m, "pipeName", "", root);
    if (pipeNode) {
        const pipe = v.asString(pipeNode, "/pipeName");
        if (pipe !== null) {
            const parts = parsePipeName(pipe);
            if (!parts || (hostId !== null && parts.hostId !== hostId) || (registrationId !== null && parts.registrationId !== registrationId)) {
                v.report("pairing.pipe-name-invalid", "/pipeName", pipeNode);
            } else {
                pipeName = pipe;
            }
        }
    }

    let extensionId = null;
    const extensionNode = v.required(m, "extensionId", "", root);
    if (extensionNode) {
        const extension = v.asString(extensionNode, "/extensionId");
        if (extension !== null) {
            if (extension === options.expectedExtensionId) extensionId = extension;
            else v.report("pairing.extension-mismatch", "/extensionId", extensionNode);
        }
    }

    let secret = null;
    const secretNode = v.required(m, "secret", "", root);
    if (secretNode && v.expect(secretNode, "/secret", Kind.String)) {
        secret = decodeSecret(document, secretNode);
        if (!secret) v.report("pairing.secret-invalid", "/secret", secretNode);
    }

    if (v.bag.hasErrors || hostId === null || registrationId === null || pipeName === null || extensionId === null || secret === null) {
        if (secret) secret.fill(0);
        return null;
    }
    return new Pairing(hostId, pipeName, registrationId, extensionId, secret);
}

/**
 * Reads a pairing file from its bytes. `options.expectedExtensionId` is the companion manifest's
 * id; `options.manifestHosts`, when given, lists the host ids the file's hostId must be one of.
 * Returns { pairing, diagnostics }; the caller owns the pairing and disposes it.
 */
function readPairing(bytes, options) {
    checkOptions(options);
    const input = Buffer.isBuffer(bytes) ? bytes : Buffer.from(bytes);
    const bag = new DiagnosticBag();
    let pairing = null;
    if (input.length > MAX_BYTES) {
        bag.report("pairing.too-large", "");
    } else if (hasWideByteOrderMark(input)) {
        bag.report("pairing.encoding", "");
    } else {
        const document = Buffer.from(stripUtf8Bom(input));
        try {
            const parsed = parse(document, RAW);
            if (parsed.failure === "utf8") {
                bag.report("pairing.encoding", "");
            } else {
                const v = new Validator(bag, document, undefined);
                if (!parsed.root) {
                    v.parseFailure(parsed);
                } else {
                    v.duplicates(parsed);
                    pairing = validate(parsed.root, document, v, options);
                }
            }
        } finally {
            document.fill(0);
        }
    }
    if (bag.hasErrors && pairing) {
        pairing.dispose();
        pairing = null;
    }
    return { pairing: pairing || undefined, diagnostics: bag.toList() };
}

/** Reads a pairing file, never reading more than 4,096 + 1 bytes. File-system failures reject. */
async function readPairingFile(file, options) {
    checkOptions(options);
    const bytes = await readBounded(file, MAX_BYTES);
    try {
        return readPairing(bytes, options);
    } finally {
        bytes.fill(0);
    }
}

/** The default location (contract §6.6): %USERPROFILE%\.ventana\pairings\<host-id>\<extension-id>.pairing.json */
function defaultPairingPath(hostId, extensionId) {
    if (!isHostId(hostId)) throw new TypeError("The host id does not match the host-id grammar.");
    if (!isExtensionId(extensionId)) throw new TypeError("The extension id is not an extension id.");
    return path.join(os.homedir(), ".ventana", "pairings", hostId, extensionId + ".pairing.json");
}

module.exports = { MAX_BYTES, Pairing, readPairing, readPairingFile, defaultPairingPath };
