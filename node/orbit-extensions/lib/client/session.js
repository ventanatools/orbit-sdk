// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * Sessions, faces and invocation results (contract §7.7, §9.2, §10). Session.setFace, clearFace
 * and fail check, in order: their arguments (throwing TypeError or RangeError; never because of
 * what the text says or how long it is: the SDK cleans text as a host would), that the
 * contribution provides face (throwing an Error), and the session's state ("SessionEnded" after it
 * ended, "Accepted" otherwise).
 */

const { clean, isGlyph } = require("../text.js");
const { FaceState, Failure, Outcome, PublishResult } = require("../tokens.js");

const LINE1 = { elements: 40, units: 160 };
const LINE2 = { elements: 60, units: 240 };
const DETAIL = { elements: 160, units: 640 };
/** Ends a session's publishing; only its owner holds this key. */
const kEnd = Symbol("end");

const FACE_MEMBERS = new Set(["line1", "line2", "detail", "glyph", "state", "goodForSeconds", "renew"]);
const FACE_STATES = new Set(Object.values(FaceState));
const FAILURES = new Set(Object.values(Failure));
const OUTCOMES = new Set(Object.values(Outcome));

function optionalText(face, name) {
    const value = face[name];
    if (value === undefined || value === null) return undefined;
    if (typeof value !== "string") throw new TypeError("setFace: " + name + " must be a string.");
    return value;
}

/** Checks a face's arguments and returns the cleaned wire face and its renew flag. */
function prepareFace(face) {
    if (!face || typeof face !== "object" || Array.isArray(face)) throw new TypeError("setFace needs a face object.");
    for (const name of Object.keys(face)) {
        if (!FACE_MEMBERS.has(name)) throw new TypeError("setFace: unknown member " + JSON.stringify(name).slice(0, 40) + ".");
    }
    const seconds = face.goodForSeconds;
    if (typeof seconds !== "number" || !Number.isFinite(seconds) || seconds < 1 || seconds > 86400) {
        throw new RangeError("setFace: goodForSeconds must be between 1 and 86,400.");
    }
    if (face.glyph !== undefined && face.glyph !== null && !isGlyph(face.glyph)) {
        throw new TypeError("setFace: glyph must be one private-use character (U+E000 to U+F8FF), written as \\uXXXX.");
    }
    if (face.state !== undefined && !FACE_STATES.has(face.state)) {
        throw new RangeError("setFace: state must be None, Playing, Paused, On or Off.");
    }
    if (face.renew !== undefined && typeof face.renew !== "boolean") throw new TypeError("setFace: renew must be a boolean.");
    const line1 = optionalText(face, "line1");
    const line2 = optionalText(face, "line2");
    const detail = optionalText(face, "detail");
    const wire = { picture: face.glyph ? { $type: "glyph", glyph: face.glyph } : { $type: "none" } };
    const cleanLine1 = clean(line1, LINE1.elements, LINE1.units);
    if (cleanLine1 !== null) wire.line1 = { $type: "text", text: cleanLine1 };
    const cleanLine2 = clean(line2, LINE2.elements, LINE2.units);
    if (cleanLine2 !== null) wire.line2 = { $type: "text", text: cleanLine2 };
    wire.state = face.state === undefined ? "None" : face.state;
    const cleanDetail = clean(detail, DETAIL.elements, DETAIL.units);
    if (cleanDetail !== null) wire.detail = cleanDetail;
    wire.goodForSeconds = Math.ceil(seconds);
    return { face: Object.freeze(wire), renew: face.renew === true };
}

/** A wire face as an author's face object (the shape setFace takes), as a host holds it after cleaning. */
function toAuthorFace(wire, renew) {
    const face = {};
    if (wire.line1 && wire.line1.$type === "text") {
        const text = clean(wire.line1.text, LINE1.elements, LINE1.units);
        if (text !== null) face.line1 = text;
    }
    if (wire.line2 && wire.line2.$type === "text") {
        const text = clean(wire.line2.text, LINE2.elements, LINE2.units);
        if (text !== null) face.line2 = text;
    }
    if (typeof wire.detail === "string") {
        const text = clean(wire.detail, DETAIL.elements, DETAIL.units);
        if (text !== null) face.detail = text;
    }
    if (wire.picture && wire.picture.$type === "glyph") face.glyph = wire.picture.glyph;
    face.state = wire.state || "None";
    face.goodForSeconds = wire.goodForSeconds;
    if (renew !== undefined) face.renew = renew;
    return Object.freeze(face);
}

function isBaselineFailure(failure) {
    return FAILURES.has(failure);
}

/**
 * An invoke handler's return value as { outcome, failure? }, or null when it is not a valid
 * result (an unknown outcome, or a failure with an outcome other than Failed).
 */
function normalizeResult(value) {
    if (typeof value === "string") return OUTCOMES.has(value) ? { outcome: value } : null;
    if (!value || typeof value !== "object" || !OUTCOMES.has(value.outcome)) return null;
    const failure = value.failure;
    if (failure === undefined || failure === null) return { outcome: value.outcome };
    if (value.outcome !== Outcome.Failed || !isBaselineFailure(failure)) return null;
    return { outcome: value.outcome, failure };
}

/** One session (contract §7.6): a contribution with a fixed, complete set of settings. */
class Session {
    #sink;
    #capabilities;
    #active = true;

    constructor({ id, contributionId, provides, settings, uiLanguage, hostCapabilities }, sink) {
        this.id = id;
        this.contributionId = contributionId;
        this.provides = Object.freeze([...provides]);
        const map = Object.create(null);
        for (const [key, value] of Object.entries(settings || {})) map[key] = value;
        this.settings = Object.freeze(map);
        this.uiLanguage = uiLanguage;
        this.#capabilities = new Set(hostCapabilities || []);
        this.hostCapabilities = Object.freeze([...this.#capabilities].sort());
        this.#sink = sink;
        Object.freeze(this);
    }

    /** Whether the session is still running: false once the host stopped it or the connection ended. */
    get isActive() {
        return this.#active;
    }

    /** Whether `capabilityId` is effective on this session's connection. */
    supports(capabilityId) {
        return typeof capabilityId === "string" && this.#capabilities.has(capabilityId);
    }

    setFace(face) {
        const prepared = prepareFace(face);
        this.#requireFace();
        return this.#publish({ kind: "set", face: prepared.face, renew: prepared.renew });
    }

    clearFace() {
        this.#requireFace();
        return this.#publish({ kind: "clear" });
    }

    fail(failure) {
        if (!isBaselineFailure(failure)) {
            throw new RangeError("fail: the failure token is not one of UnsupportedInput, NeedsSetup, Network, NoResult or AppUnavailable.");
        }
        this.#requireFace();
        return this.#publish({ kind: "fail", failure });
    }

    [kEnd]() {
        this.#active = false;
    }

    #requireFace() {
        if (!this.provides.includes("face")) {
            this.#sink.faceWithoutProvides(this);
            throw new Error("This contribution does not provide face; add face to provides, or do not publish.");
        }
    }

    #publish(command) {
        if (!this.#active) {
            this.#sink.publishedAfterEnd(this, command);
            return PublishResult.SessionEnded;
        }
        return this.#sink.publish(this, command);
    }
}

/** Whether `error` is the normal end of a handler whose signal was aborted (contract §10). */
function isAbortCompletion(error, signal) {
    if (!signal || !signal.aborted) return false;
    return error === signal.reason || (!!error && (error.name === "AbortError" || error.code === "ABORT_ERR"));
}

module.exports = { Session, kEnd, prepareFace, toAuthorFace, normalizeResult, isBaselineFailure, isAbortCompletion, LINE1, LINE2, DETAIL };
