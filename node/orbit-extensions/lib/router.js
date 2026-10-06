// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/** Routes sessions and invocations to per-contribution functions or handlers (contract §9.2, §10). */
class ContributionRouter {
    #sessions = new Map();
    #invocations = new Map();
    #handlers = new Map();

    /** Maps the session handler of `contributionId`: run(session, signal). */
    mapSession(contributionId, run) {
        if (typeof contributionId !== "string" || typeof run !== "function") throw new TypeError("mapSession needs a contribution id and a function.");
        if (this.#handlers.has(contributionId) || this.#sessions.has(contributionId)) throw new Error("The contribution already has a session handler.");
        this.#sessions.set(contributionId, run);
        return this;
    }

    /** Maps the invocation handler of `contributionId`: invoke(invocation, signal). */
    mapInvoke(contributionId, invoke) {
        if (typeof contributionId !== "string" || typeof invoke !== "function") throw new TypeError("mapInvoke needs a contribution id and a function.");
        if (this.#handlers.has(contributionId) || this.#invocations.has(contributionId)) throw new Error("The contribution already has an invocation handler.");
        this.#invocations.set(contributionId, invoke);
        return this;
    }

    /** Maps a whole handler ({ runSession?, invoke? }) to `contributionId`. */
    map(contributionId, handler) {
        if (typeof contributionId !== "string" || !handler || typeof handler !== "object") throw new TypeError("map needs a contribution id and a handler.");
        if (this.#sessions.has(contributionId) || this.#invocations.has(contributionId) || this.#handlers.has(contributionId)) {
            throw new Error("The contribution is already mapped.");
        }
        this.#handlers.set(contributionId, handler);
        return this;
    }

    /** The ids of contributions whose face or invoke has no handler; runCompanion prints them at start. */
    findUnmapped(manifest) {
        const unmapped = [];
        for (const contribution of (manifest && manifest.contributions) || []) {
            if (!contribution || typeof contribution.id !== "string" || this.#handlers.has(contribution.id)) continue;
            const provides = Array.isArray(contribution.provides) ? contribution.provides : [];
            if ((provides.includes("face") && !this.#sessions.has(contribution.id))
                || (provides.includes("invoke") && !this.#invocations.has(contribution.id))) {
                unmapped.push(contribution.id);
            }
        }
        return unmapped;
    }

    runSession(session, signal) {
        const handler = this.#handlers.get(session.contributionId);
        if (handler) return typeof handler.runSession === "function" ? handler.runSession(session, signal) : undefined;
        const run = this.#sessions.get(session.contributionId);
        return run ? run(session, signal) : undefined;
    }

    invoke(invocation, signal) {
        const id = invocation.session.contributionId;
        const handler = this.#handlers.get(id);
        if (handler) return typeof handler.invoke === "function" ? handler.invoke(invocation, signal) : "Unsupported";
        const invoke = this.#invocations.get(id);
        return invoke ? invoke(invocation, signal) : "Unsupported";
    }
}

module.exports = { ContributionRouter };
