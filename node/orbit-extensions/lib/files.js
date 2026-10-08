// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * Bounded file reads: never more than the limit plus one byte, so a reader can tell an oversized
 * file from one at its limit without loading it. File-system failures surface as Node's own errors.
 */

const fs = require("node:fs");

async function readBounded(path, maxBytes) {
    if (typeof path !== "string" || path.length === 0) throw new TypeError("The path must be a non-empty string.");
    const handle = await fs.promises.open(path, "r");
    try {
        const buffer = Buffer.alloc(maxBytes + 1);
        let length = 0;
        while (length < buffer.length) {
            const { bytesRead } = await handle.read(buffer, length, buffer.length - length, null);
            if (bytesRead === 0) break;
            length += bytesRead;
        }
        const copy = Buffer.from(buffer.subarray(0, length));
        buffer.fill(0);
        return copy;
    } finally {
        await handle.close();
    }
}

function readBoundedSync(path, maxBytes) {
    if (typeof path !== "string" || path.length === 0) throw new TypeError("The path must be a non-empty string.");
    const fd = fs.openSync(path, "r");
    try {
        const buffer = Buffer.alloc(maxBytes + 1);
        let length = 0;
        while (length < buffer.length) {
            const bytesRead = fs.readSync(fd, buffer, length, buffer.length - length, null);
            if (bytesRead === 0) break;
            length += bytesRead;
        }
        const copy = Buffer.from(buffer.subarray(0, length));
        buffer.fill(0);
        return copy;
    } finally {
        fs.closeSync(fd);
    }
}

/** A comparable stamp of a file: whether it exists, its last write time and its length. */
function stampOf(path) {
    try {
        const stat = fs.statSync(path);
        return stat.isFile() ? stat.mtimeMs + ":" + stat.size : "missing";
    } catch {
        return "missing";
    }
}

module.exports = { readBounded, readBoundedSync, stampOf };
