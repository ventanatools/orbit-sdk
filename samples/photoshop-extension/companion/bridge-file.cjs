// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

// The bridge file the panel reads: one fixed per-user path, written atomically, deleted on a
// clean exit. It holds the per-launch bridge key and the loopback URL, never the pairing secret.

const fs = require("node:fs");
const os = require("node:os");
const path = require("node:path");
const { randomBytes } = require("node:crypto");

/** %LOCALAPPDATA%\VentanaTools\Samples\Photoshop\bridge.json */
function bridgeFilePath(env = process.env) {
    const local = env.LOCALAPPDATA || path.join(os.homedir(), "AppData", "Local");
    return path.join(local, "VentanaTools", "Samples", "Photoshop", "bridge.json");
}

/** Writes the file atomically: a private temporary file in the same folder, then a rename over the old one. */
async function writeBridgeFile(file, config) {
    const folder = path.dirname(file);
    await fs.promises.mkdir(folder, { recursive: true });
    const temporary = path.join(folder, "bridge." + randomBytes(8).toString("hex") + ".tmp");
    try {
        await fs.promises.writeFile(temporary, JSON.stringify(config, null, 2) + "\n", { flag: "wx", mode: 0o600 });
        await fs.promises.rename(temporary, file);
    } catch (error) {
        await fs.promises.rm(temporary, { force: true });
        throw error;
    }
}

/** Deletes the file; a file that is already gone is fine. */
async function removeBridgeFile(file) {
    await fs.promises.rm(file, { force: true });
}

module.exports = { bridgeFilePath, writeBridgeFile, removeBridgeFile };
