// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

// The companion: the SDK connects to the host app with the pairing file the host saved, and the
// bridge forwards sessions and picks to the Photoshop panel. Started with `npm start`; Ctrl+C stops it.

const path = require("node:path");
const { runCompanion, parseArguments, findHost } = require("@ventanatools/orbit-extensions");
const constants = require("../uxp/constants.js");
const { startBridge } = require("./bridge.cjs");
const { bridgeFilePath, writeBridgeFile, removeBridgeFile } = require("./bridge-file.cjs");

/** The host's display name, from the registry entry of the id the host proved in its challenge. */
function hostName(status) {
    const host = status.host ? findHost(status.host.id) : undefined;
    return host ? host.displayName : "The host app";
}

async function main(args) {
    if (process.platform !== "win32") {
        console.error("This sample needs Windows: the host app's companion transport is a named pipe.");
        return 1;
    }
    let bridge;
    try {
        bridge = await startBridge({ onStatus: (text) => console.log(text) });
    } catch (error) {
        if (error && error.code === "EADDRINUSE") {
            console.error(`Port ${constants.BRIDGE_PORT} is in use: another copy of this companion, or another program, is listening on it. Stop it, then start the companion again.`);
        } else {
            console.error(`The bridge could not start (${error && error.code ? error.code : "error"}).`);
        }
        return 1;
    }
    const file = bridgeFilePath();
    try {
        await writeBridgeFile(file, bridge.config);
        console.log(`Bridge ready. In Photoshop, open the Photoshop bridge sample panel; the first time, choose ${file}. The panel reconnects by itself after that.`);
        const options = {
            onStatus: (status) => {
                if (status.state === "Connected") console.log(`${hostName(status)} connected; waiting for sessions.`);
            },
        };
        // The manifest sits next to package.json, one folder above this file.
        if (!parseArguments(args).manifestPath) options.manifestPath = path.join(__dirname, "..", "extension.json");
        return await runCompanion(args, bridge.handler, options);
    } finally {
        await bridge.close();
        await removeBridgeFile(file);
    }
}

main(process.argv.slice(2)).then((code) => {
    process.exitCode = code;
}, (error) => {
    console.error(`The companion stopped unexpectedly (${error && error.name ? error.name : "Error"}).`);
    process.exitCode = 1;
});
