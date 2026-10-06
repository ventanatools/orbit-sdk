// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

const { EventEmitter } = require("node:events");
const { timingSafeEqual } = require("node:crypto");
const { TextDecoder } = require("node:util");
const { parseRecordV2 } = require("../uxp/wire-v2.js");
const MAX_FRAME = 65536;
const decoder = new TextDecoder("utf-8", { fatal: true });

function bytes32(value) {
    if (typeof value !== "string" || !/^[A-Za-z0-9+/]{43}=$/.test(value)) throw new Error("base64.invalid");
    const bytes = Buffer.from(value, "base64");
    if (bytes.length !== 32 || bytes.toString("base64") !== value) throw new Error("base64.invalid");
    return bytes;
}

function equalProof(left, right) {
    try { return timingSafeEqual(bytes32(left), bytes32(right)); }
    catch { return false; }
}

function encodeFrame(record) {
    const body = Buffer.from(JSON.stringify(record), "utf8");
    if (body.length === 0 || body.length > MAX_FRAME) throw new Error("frame.size");
    const header = Buffer.alloc(4);
    header.writeUInt32LE(body.length);
    return Buffer.concat([header, body]);
}

// Allocates at most one bounded body, handles coalesced/fragmented frames, and
// never extends a partial-frame deadline just because another byte arrived.
class FramedSocket extends EventEmitter {
    constructor(socket, { partialMs = 5000, maxPerSecond = 128, parse = parseRecordV2 } = {}) {
        super();
        this.socket = socket;
        this.header = Buffer.alloc(4);
        this.headerUsed = 0;
        this.body = null;
        this.bodyUsed = 0;
        this.timer = null;
        this.partialMs = partialMs;
        this.maxPerSecond = maxPerSecond;
        this.parse = parse;
        this.received = [];
        this.closed = false;
        socket.on("data", data => this.accept(data));
        socket.on("error", () => this.destroy());
        socket.on("close", () => {
            this.closed = true;
            clearTimeout(this.timer);
            this.emit("close");
        });
    }

    accept(chunk) {
        let offset = 0;
        try {
            while (offset < chunk.length && !this.closed) {
                if (!this.timer) this.timer = setTimeout(() => this.destroy(), this.partialMs);
                if (!this.body) {
                    const count = Math.min(4 - this.headerUsed, chunk.length - offset);
                    chunk.copy(this.header, this.headerUsed, offset, offset + count);
                    offset += count;
                    this.headerUsed += count;
                    if (this.headerUsed !== 4) continue;
                    const length = this.header.readUInt32LE();
                    if (length === 0 || length > MAX_FRAME) throw new Error("frame.size");
                    this.body = Buffer.alloc(length);
                }
                const count = Math.min(this.body.length - this.bodyUsed, chunk.length - offset);
                chunk.copy(this.body, this.bodyUsed, offset, offset + count);
                offset += count;
                this.bodyUsed += count;
                if (this.bodyUsed !== this.body.length) continue;
                const now = Date.now();
                this.received = this.received.filter(time => now - time < 1000);
                if (this.received.length >= this.maxPerSecond) throw new Error("frame.rate");
                this.received.push(now);
                const message = this.parse(decoder.decode(this.body));
                this.headerUsed = 0;
                this.bodyUsed = 0;
                this.body = null;
                clearTimeout(this.timer);
                this.timer = null;
                this.emit("message", message);
            }
        } catch { this.destroy(); }
    }

    send(record) {
        if (this.closed || this.socket.destroyed) return false;
        // The host bounds requests; still refuse an unbounded blocked writer.
        if (this.socket.writableLength > MAX_FRAME * 2) { this.destroy(); return false; }
        this.socket.write(encodeFrame(record));
        return true;
    }

    destroy() {
        if (this.closed) return;
        this.closed = true;
        clearTimeout(this.timer);
        this.socket.destroy();
    }
}

module.exports = { MAX_FRAME, bytes32, equalProof, encodeFrame, FramedSocket };
