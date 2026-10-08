// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";
const path = require("node:path");
const { mkdir, copyFile } = require("node:fs/promises");
const { build } = require("esbuild");

async function main() {
    const root = path.resolve(__dirname, "..");
    const vendor = path.join(root, "uxp/vendor");
    await mkdir(vendor, { recursive: true });
    await build({
        absWorkingDir: root,
        entryPoints: ["tools/crypto-entry.mjs"],
        outfile: "uxp/vendor/bridge-crypto.js",
        bundle: true, platform: "browser", format: "cjs", target: "es2020",
        // Keep readable code and bundled copyright notices in the developer sample.
        minify: false, legalComments: "inline"
    });
    await copyFile(path.join(root, "node_modules/@noble/hashes/LICENSE"), path.join(vendor, "noble-hashes.LICENSE.txt"));
    console.log("Prepared UXP crypto bundle and its MIT license.");
}

main().catch(() => { console.error("UXP bundle failed; run npm ci --ignore-scripts first."); process.exitCode = 1; });
