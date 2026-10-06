// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/** ready.limits (contract §7.10) and the token bucket of contract §B.3. */

const LIMIT_NAMES = Object.freeze([
    "maxFrameBytes", "maxSessions", "maxPendingInvokes", "invokeTimeoutMs", "faceChangesPerSecond", "faceBurst",
    "messageRate", "messageBurst", "hardMessageRate", "hardMessageBurst", "pingIntervalMs", "pongTimeoutMs",
]);

/** The values protocol-3 hosts send. */
const protocol3Limits = Object.freeze({
    maxFrameBytes: 65536,
    maxSessions: 64,
    maxPendingInvokes: 32,
    invokeTimeoutMs: 15000,
    faceChangesPerSecond: 2,
    faceBurst: 4,
    messageRate: 128,
    messageBurst: 256,
    hardMessageRate: 512,
    hardMessageBurst: 1024,
    pingIntervalMs: 30000,
    pongTimeoutMs: 10000,
});

function within(value, min, max) {
    return Number.isInteger(value) && value >= min && value <= max;
}

/** Every range and cross-member rule of contract §7.10. */
function limitsWithinRanges(l) {
    if (!l || typeof l !== "object") return false;
    return within(l.maxFrameBytes, 8192, 65536)
        && within(l.maxSessions, 1, 64)
        && within(l.maxPendingInvokes, 1, 32)
        && within(l.invokeTimeoutMs, 4000, 60000)
        && within(l.faceChangesPerSecond, 1, 2)
        && within(l.faceBurst, 1, 4)
        && within(l.messageRate, 1, 128)
        && within(l.messageBurst, Math.floor((l.maxFrameBytes + 1023) / 1024), 256)
        && within(l.hardMessageRate, l.messageRate, 512)
        && within(l.hardMessageBurst, l.messageBurst, 1024)
        && within(l.pingIntervalMs, 5000, 300000)
        && within(l.pongTimeoutMs, 1000, 60000)
        && l.pingIntervalMs > l.pongTimeoutMs;
}

/** The values a companion uses: the smaller of the host's and the default for capacity and rate, the larger for liveness timers. */
function limitsForCompanion(l) {
    const d = protocol3Limits;
    return Object.freeze({
        maxFrameBytes: Math.min(l.maxFrameBytes, d.maxFrameBytes),
        maxSessions: Math.min(l.maxSessions, d.maxSessions),
        maxPendingInvokes: Math.min(l.maxPendingInvokes, d.maxPendingInvokes),
        invokeTimeoutMs: Math.min(l.invokeTimeoutMs, d.invokeTimeoutMs),
        faceChangesPerSecond: Math.min(l.faceChangesPerSecond, d.faceChangesPerSecond),
        faceBurst: Math.min(l.faceBurst, d.faceBurst),
        messageRate: Math.min(l.messageRate, d.messageRate),
        messageBurst: Math.min(l.messageBurst, d.messageBurst),
        hardMessageRate: Math.min(l.hardMessageRate, d.hardMessageRate),
        hardMessageBurst: Math.min(l.hardMessageBurst, d.hardMessageBurst),
        pingIntervalMs: Math.max(l.pingIntervalMs, d.pingIntervalMs),
        pongTimeoutMs: Math.max(l.pongTimeoutMs, d.pongTimeoutMs),
    });
}

/** The system clock: monotonic milliseconds. */
const systemClock = Object.freeze({
    now: () => performance.now(),
    setTimeout: (callback, ms) => setTimeout(callback, Math.max(0, ms)),
    clearTimeout: (handle) => clearTimeout(handle),
});

/**
 * A token bucket (contract §B.3): `burst` tokens at first, refilled at `ratePerSecond`, on a
 * monotonic clock. A frame costs one token per started 1,024 bytes (contract §7.10).
 */
class TokenBucket {
    constructor(ratePerSecond, burst, clock = systemClock) {
        if (!(ratePerSecond > 0) || !Number.isFinite(ratePerSecond)) throw new RangeError("The rate must be greater than zero.");
        if (!Number.isInteger(burst) || burst < 1) throw new RangeError("The burst must be at least 1.");
        this.rate = ratePerSecond;
        this.burst = burst;
        this.clock = clock;
        this.tokens = burst;
        this.last = clock.now();
        this.retryAfterMs = 0;
    }

    /** The cost of a frame: one token per started 1,024 bytes of its body. */
    static costOf(frameBytes) {
        if (!Number.isInteger(frameBytes) || frameBytes < 1) throw new RangeError("The frame length must be at least 1.");
        return Math.floor((frameBytes + 1023) / 1024);
    }

    /** Refills, then takes `tokens` when the bucket holds them; when not, retryAfterMs says how long to wait. */
    tryTake(tokens = 1) {
        if (!Number.isInteger(tokens) || tokens < 1) throw new RangeError("The cost must be at least 1.");
        const now = this.clock.now();
        const elapsed = (now - this.last) / 1000;
        this.tokens = Math.min(this.burst, this.tokens + this.rate * elapsed);
        this.last = now;
        if (this.tokens >= tokens) {
            this.tokens -= tokens;
            this.retryAfterMs = 0;
            return true;
        }
        this.retryAfterMs = Math.ceil(((tokens - this.tokens) / this.rate) * 1000);
        return false;
    }
}

module.exports = { LIMIT_NAMES, protocol3Limits, limitsWithinRanges, limitsForCompanion, systemClock, TokenBucket };
