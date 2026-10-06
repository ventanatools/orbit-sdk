// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * A manual clock for tests: time moves only when `advance` is called, and timers due by then run
 * in time order. Pass it to startTestHost so the SDK's backoff, deadlines, pings and renewal run
 * without real waiting.
 */
class ManualClock {
    #now = 0;
    #next = 1;
    #timers = new Map();

    now() {
        return this.#now;
    }

    setTimeout(callback, ms) {
        const handle = this.#next++;
        this.#timers.set(handle, { at: this.#now + Math.max(0, Number(ms) || 0), callback, order: handle });
        return handle;
    }

    clearTimeout(handle) {
        this.#timers.delete(handle);
    }

    /** The number of timers not yet run. */
    get pending() {
        return this.#timers.size;
    }

    /** Moves time forward by `ms`, running every timer that falls due, in time order. */
    advance(ms) {
        const target = this.#now + Math.max(0, Number(ms) || 0);
        while (true) {
            let due = null;
            let handle = null;
            for (const [key, timer] of this.#timers) {
                if (timer.at <= target && (due === null || timer.at < due.at || (timer.at === due.at && timer.order < due.order))) {
                    due = timer;
                    handle = key;
                }
            }
            if (due === null) break;
            this.#timers.delete(handle);
            this.#now = Math.max(this.#now, due.at);
            due.callback();
        }
        this.#now = target;
    }

    /** Moves time forward in steps, letting promises and I/O run between them. */
    async advanceAsync(ms, stepMs = ms) {
        let left = Math.max(0, Number(ms) || 0);
        do {
            const step = Math.min(left, stepMs > 0 ? stepMs : left);
            this.advance(step);
            left -= step;
            await new Promise((resolve) => setImmediate(resolve));
            await new Promise((resolve) => setImmediate(resolve));
        } while (left > 0);
    }
}

module.exports = { ManualClock };
