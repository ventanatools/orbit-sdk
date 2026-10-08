// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * The reason → state table of contract §9.2 (and §10: every server is unverified until its
 * challenge proof verifies). Normal backoff is initialMs, doubling, times uniform(1 − jitter,
 * 1 + jitter), and never more than maxMs. A server that is not verified can never stop the client
 * or keep it waiting longer than maxMs.
 */

const { isKnownReason } = require("../codes.js");

const VERIFIED_RETRY_AFTER_CAP_MS = 300000;
const DEFAULT_RETRY = Object.freeze({ initialMs: 1000, maxMs: 30000, hostAbsentMaxMs: 5000, jitter: 0.2, stableMs: 60000 });

const TERMINAL_FROM_VERIFIED = new Set([
    "auth.registration-mismatch", "protocol.version-unsupported", "auth.host-mismatch", "auth.server-proof-invalid",
    "auth.proof-invalid", "host.access-revoked",
]);

function resolveRetry(retry) {
    const r = { ...DEFAULT_RETRY, ...(retry || {}) };
    for (const name of ["initialMs", "maxMs", "hostAbsentMaxMs", "stableMs"]) {
        if (typeof r[name] !== "number" || !(r[name] > 0) || r[name] > 86400000) {
            throw new RangeError("retry." + name + " must be greater than zero and at most one day.");
        }
    }
    if (r.maxMs < r.initialMs) throw new RangeError("retry.maxMs must be at least retry.initialMs.");
    if (typeof r.jitter !== "number" || !(r.jitter >= 0 && r.jitter < 1)) throw new RangeError("retry.jitter must be at least 0 and less than 1.");
    return Object.freeze(r);
}

class RetryPolicy {
    constructor(retry, random = Math.random) {
        this.retry = retry;
        this.random = random;
        this.failures = 0;
        this.reloadUsed = false;
    }

    reset() {
        this.failures = 0;
        this.reloadUsed = false;
    }

    /** { kind: "Wait" | "Immediate" | "Stop", delayMs } */
    next(reason, retryAfterMs, serverVerified) {
        if (typeof reason !== "string") return this.wait(this.normal());
        if (reason.startsWith("pairing.")) return { kind: "Stop", delayMs: 0 };
        if (TERMINAL_FROM_VERIFIED.has(reason)) return serverVerified ? { kind: "Stop", delayMs: 0 } : this.wait(this.normal());
        if (reason === "manifest.mismatch" || reason === "auth.identity-changed") return this.wait(this.retry.maxMs);
        if (reason === "host.not-running" || reason === "host.turned-off") return this.wait(Math.min(this.normal(), this.retry.hostAbsentMaxMs));
        if (reason === "host.paused" || (!isKnownReason(reason) && typeof retryAfterMs === "number")) {
            const normal = this.normal();
            const retryAfter = Math.min(Math.max(retryAfterMs || 0, 0), VERIFIED_RETRY_AFTER_CAP_MS);
            return this.wait(serverVerified
                ? Math.max(normal, Math.min(retryAfter, VERIFIED_RETRY_AFTER_CAP_MS))
                : Math.min(Math.max(normal, retryAfter), this.retry.maxMs));
        }
        if (reason === "host.reloaded" && !this.reloadUsed) {
            this.reloadUsed = true;
            return { kind: "Immediate", delayMs: 0 };
        }
        return this.wait(this.normal());
    }

    normal() {
        const exponent = Math.min(this.failures, 30);
        this.failures++;
        const base = Math.min(this.retry.initialMs * Math.pow(2, exponent), this.retry.maxMs);
        const jitter = 1 + this.retry.jitter * (2 * this.random() - 1);
        return Math.max(0, Math.min(base * jitter, this.retry.maxMs));
    }

    wait(delayMs) {
        return { kind: "Wait", delayMs };
    }
}

module.exports = { RetryPolicy, resolveRetry, DEFAULT_RETRY };
