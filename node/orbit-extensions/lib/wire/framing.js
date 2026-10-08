// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * Frames (contract §7.2): a 4-byte unsigned little-endian length, then that many bytes of UTF-8
 * JSON. Until ready, every frame is at most 8,192 bytes; after it, at most limits.maxFrameBytes.
 * Once the first byte of a frame arrives, the whole frame must arrive within 5 seconds.
 */

const { systemClock } = require("./limits.js");
const { writeMessage } = require("./messages.js");

const MAX_FRAME_BYTES = 65536;
const MAX_HANDSHAKE_FRAME_BYTES = 8192;
const PARTIAL_FRAME_TIMEOUT_MS = 5000;

/** The frame of a message object (written canonically) or of a frame body (a Buffer or Uint8Array). */
function encodeFrame(record) {
    const body = record instanceof Uint8Array ? Buffer.from(record) : writeMessage(record);
    if (body.length === 0 || body.length > MAX_FRAME_BYTES) throw new RangeError("The frame body is empty or longer than 65,536 bytes.");
    const frame = Buffer.alloc(4 + body.length);
    frame.writeUInt32LE(body.length, 0);
    body.copy(frame, 4);
    return frame;
}

/**
 * Splits a byte stream into frame bodies. Feed chunks with `push`; each complete body is passed to
 * `onFrame`. A length of 0 or over the current limit calls `onError("frame.too-large")`, and a
 * frame that does not finish within 5 seconds of its first byte calls `onError("frame.timeout")`.
 * After an error the reader accepts nothing more.
 */
class FrameReader {
    constructor({ maxFrameBytes = MAX_HANDSHAKE_FRAME_BYTES, onFrame, onError, clock = systemClock } = {}) {
        if (typeof onFrame !== "function" || typeof onError !== "function") throw new TypeError("onFrame and onError are required.");
        this.maxFrameBytes = maxFrameBytes;
        this.onFrame = onFrame;
        this.onError = onError;
        this.clock = clock;
        this.header = Buffer.alloc(4);
        this.headerUsed = 0;
        this.body = null;
        this.bodyUsed = 0;
        this.timer = null;
        this.failed = false;
        this.closed = false;
    }

    /** Sets the largest body allowed from now on (1 to 65,536). */
    setMaxFrameBytes(value) {
        if (!Number.isInteger(value) || value < 1 || value > MAX_FRAME_BYTES) throw new RangeError("The frame limit is out of range.");
        this.maxFrameBytes = value;
    }

    /** Whether a frame has started and not finished. */
    get inFrame() {
        return this.headerUsed > 0 || this.body !== null;
    }

    push(chunk) {
        let offset = 0;
        while (offset < chunk.length && !this.failed && !this.closed) {
            if (!this.inFrame) {
                this.timer = this.clock.setTimeout(() => this.fail("frame.timeout"), PARTIAL_FRAME_TIMEOUT_MS);
            }
            if (this.body === null) {
                const count = Math.min(4 - this.headerUsed, chunk.length - offset);
                chunk.copy(this.header, this.headerUsed, offset, offset + count);
                offset += count;
                this.headerUsed += count;
                if (this.headerUsed < 4) continue;
                const length = this.header.readUInt32LE(0);
                if (length === 0 || length > this.maxFrameBytes) {
                    this.fail("frame.too-large");
                    return;
                }
                this.body = Buffer.alloc(length);
                this.bodyUsed = 0;
            }
            const count = Math.min(this.body.length - this.bodyUsed, chunk.length - offset);
            chunk.copy(this.body, this.bodyUsed, offset, offset + count);
            offset += count;
            this.bodyUsed += count;
            if (this.bodyUsed < this.body.length) continue;
            const body = this.body;
            this.body = null;
            this.bodyUsed = 0;
            this.headerUsed = 0;
            this.clock.clearTimeout(this.timer);
            this.timer = null;
            this.onFrame(body);
        }
    }

    fail(code) {
        if (this.failed || this.closed) return;
        this.failed = true;
        this.clock.clearTimeout(this.timer);
        this.timer = null;
        this.onError(code);
    }

    /** Stops the partial-frame timer; nothing more is accepted. */
    close() {
        this.closed = true;
        this.clock.clearTimeout(this.timer);
        this.timer = null;
    }
}

module.exports = { MAX_FRAME_BYTES, MAX_HANDSHAKE_FRAME_BYTES, PARTIAL_FRAME_TIMEOUT_MS, encodeFrame, FrameReader };
