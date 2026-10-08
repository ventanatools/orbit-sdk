// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * createTestSession (contract §9.3 RecordingSession, §10): a session with no connection that
 * records what a handler publishes, as cleaned for sending, so a handler can be tested on its own.
 */

const { randomBytes } = require("node:crypto");
const { validateManifest, findContribution } = require("../manifest.js");
const { systemClock } = require("../wire/limits.js");
const { isLanguageTag } = require("../text.js");
const { Session, kEnd, toAuthorFace, isAbortCompletion } = require("../client/session.js");
const { checkSessionSettings, completeSettings } = require("../client/settings.js");

const KINDS = { set: "SetFace", clear: "ClearFace", fail: "Fail" };

function newId() {
    return randomBytes(16).toString("hex");
}

class RecordingSession {
    #controller = new AbortController();
    #publications = [];
    #lastFace = undefined;
    #faceCursor = 0;
    #waiters = new Set();
    #clock;
    #start;

    constructor(contribution, settings, uiLanguage, clock) {
        this.#clock = clock;
        this.#start = clock.now();
        const record = (kind, command) => {
            const publication = { kind, atMs: this.#clock.now() - this.#start };
            if (command.face) publication.face = toAuthorFace(command.face, command.renew);
            if (command.failure) publication.failure = command.failure;
            this.#publications.push(Object.freeze(publication));
            if (kind !== "AfterStop") this.#lastFace = publication.face;
            for (const wake of [...this.#waiters]) wake();
        };
        this.session = new Session({
            id: newId(),
            contributionId: contribution.id,
            provides: contribution.provides,
            settings,
            uiLanguage,
            hostCapabilities: [],
        }, {
            publish: (session, command) => {
                record(KINDS[command.kind], command);
                return "Accepted";
            },
            publishedAfterEnd: (session, command) => record("AfterStop", command),
            faceWithoutProvides: () => {},
        });
        Object.freeze(this);
    }

    /** Every publication: { kind: "SetFace" | "ClearFace" | "Fail" | "AfterStop", face?, failure?, atMs }. */
    get publications() {
        return Object.freeze([...this.#publications]);
    }

    /** The last face published before stop, as cleaned for sending; undefined after a clear or fail. */
    get lastFace() {
        return this.#lastFace;
    }

    /** The signal a handler receives; aborted by stop(). */
    get signal() {
        return this.#controller.signal;
    }

    /** Ends the session as stopSession would. */
    stop() {
        this.session[kEnd]();
        this.#controller.abort();
    }

    /**
     * Waits for the next face the handler shows (a SetFace), in publication order: the first call
     * resolves with the first face, each later call with the face after the one the previous call
     * resolved with, at once when it is already recorded. The timeout is real time, whatever the
     * session's clock, so a test that drives a ManualClock cannot hang here.
     */
    waitForFace(timeoutMs = 2000) {
        return this.#waitFor(() => {
            while (this.#faceCursor < this.#publications.length) {
                const publication = this.#publications[this.#faceCursor++];
                if (publication.kind === "SetFace" && publication.face) return { value: publication.face };
            }
            return undefined;
        }, timeoutMs, "No further face was published within " + timeoutMs + " ms.");
    }

    /** Waits until at least `count` publications of any kind are recorded; the timeout is real time. */
    waitForPublications(count, timeoutMs = 2000) {
        if (!Number.isInteger(count) || count < 0) throw new TypeError("count must be a non-negative integer.");
        return this.#waitFor(() => (this.#publications.length >= count ? { value: undefined } : undefined), timeoutMs,
            "Fewer than " + count + " publications were recorded within " + timeoutMs + " ms.");
    }

    #waitFor(check, timeoutMs, message) {
        const found = check();
        if (found) return Promise.resolve(found.value);
        return new Promise((resolve, reject) => {
            const wake = () => {
                const result = check();
                if (!result) return;
                done();
                resolve(result.value);
            };
            const timer = setTimeout(() => {
                done();
                reject(new Error(message));
            }, timeoutMs);
            const done = () => {
                clearTimeout(timer);
                this.#waiters.delete(wake);
            };
            this.#waiters.add(wake);
        });
    }

    /**
     * Runs `handler.runSession(session, signal)`. The handler runs up to its first await before
     * run returns, so a face it publishes before awaiting is already recorded; use waitForFace or
     * waitForPublications before asserting on later faces. An AbortError after stop is a normal
     * completion; with `timeoutMs`, rejects when the handler has not ended in time.
     */
    async run(handler, { timeoutMs } = {}) {
        if (!handler || typeof handler.runSession !== "function") return;
        const signal = this.#controller.signal;
        let running;
        try {
            running = Promise.resolve(handler.runSession(this.session, signal));
        } catch (error) {
            running = Promise.reject(error);
        }
        let timer;
        try {
            if (typeof timeoutMs === "number") {
                await Promise.race([running, new Promise((resolve, reject) => {
                    timer = setTimeout(() => reject(new Error("The session handler did not end within " + timeoutMs + " ms.")), timeoutMs);
                })]);
            } else {
                await running;
            }
        } catch (error) {
            if (!isAbortCompletion(error, signal)) throw error;
        } finally {
            clearTimeout(timer);
        }
    }
}

/**
 * A recording session for `contributionId` of `manifest`: settings are completed with defaults and
 * checked against the manifest (throwing TypeError when they do not match).
 */
function createTestSession({ manifest, contributionId, settings, uiLanguage = "en-US", clock = systemClock } = {}) {
    const validated = validateManifest(manifest);
    if (!validated.manifest) {
        throw new TypeError("The manifest is invalid: " + validated.diagnostics.filter((d) => d.severity === "Error").map((d) => d.toString()).join("; "));
    }
    const contribution = findContribution(validated.manifest, contributionId);
    if (!contribution) throw new TypeError("The contribution is not in the manifest.");
    if (!isLanguageTag(uiLanguage)) throw new TypeError("uiLanguage is not a language tag.");
    const complete = completeSettings(contribution, settings);
    const code = checkSessionSettings(validated.manifest, contributionId, complete);
    if (code !== null) throw new TypeError("The settings do not match the manifest (" + code + ").");
    return new RecordingSession(contribution, complete, uiLanguage, clock);
}

/** An invocation of a recording session's session, for calling an invoke handler directly. */
function createTestInvocation(recording, requestId) {
    if (!recording || !(recording.session instanceof Session)) throw new TypeError("createTestInvocation needs a recording session.");
    return Object.freeze({ requestId: requestId || newId(), session: recording.session });
}

module.exports = { createTestSession, createTestInvocation, RecordingSession };
