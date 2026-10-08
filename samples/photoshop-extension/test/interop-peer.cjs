// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

// A peer for a host team's interoperability tests: the real SDK client, the companion's bridge
// and the UXP client, with only Photoshop's document replaced. Usage:
//   node test/interop-peer.cjs <pairing file path>
// It prints READY once the host connection and the panel are both up, then INVOKED <count> after
// each pick that changed the document. STOP on standard input shuts it down with exit code 0.

const path = require("node:path");
const { WebSocket } = require("ws");
const { CompanionClient, readManifestFile, readPairingFile } = require("@ventanatools/orbit-extensions");
const { startBridge } = require("../companion/bridge.cjs");
const { connectSession } = require("../uxp/client.js");
const { createPlatform } = require("../uxp/platform.js");
const { fakePhotoshop, delay } = require("./helpers.cjs");

async function main() {
    if (process.argv.length !== 3) throw new Error("arguments");
    const { manifest } = await readManifestFile(path.join(__dirname, "..", "extension.json"));
    if (!manifest) throw new Error("manifest");
    const { pairing } = await readPairingFile(process.argv[2], { expectedExtensionId: manifest.id, manifestHosts: manifest.hosts });
    if (!pairing) throw new Error("pairing");
    const bridge = await startBridge({ port: 0 });
    const stop = new AbortController();
    let session;
    let running;
    try {
        const model = fakePhotoshop();
        const platform = createPlatform(model.photoshop);
        const invoke = platform.invoke;
        platform.invoke = async (message, canceled) => {
            const result = await invoke(message, canceled);
            if (result.outcome === "Done") console.log(`INVOKED ${model.mutations}`);
            return result;
        };
        let panelConnected = false;
        session = connectSession(new WebSocket(bridge.config.url, { origin: "file://" }), bridge.config, platform,
            (status) => { if (status === "Connected.") panelConnected = true; }, () => {});
        const client = new CompanionClient({ pairing, manifest, handler: bridge.handler });
        running = client.run(stop.signal);
        const deadline = Date.now() + 10000;
        while (client.state !== "Connected" || !panelConnected) {
            if (Date.now() >= deadline) throw new Error("timeout");
            await delay(10);
        }
        console.log("READY");
        await new Promise((resolve) => {
            process.stdin.setEncoding("utf8");
            process.stdin.on("data", (text) => { if (text.includes("STOP")) resolve(); });
            process.stdin.on("end", resolve);
            process.once("SIGINT", resolve);
            process.once("SIGTERM", resolve);
        });
    } finally {
        stop.abort();
        if (running) await running;
        if (session) session.close();
        await bridge.close();
        pairing.dispose();
        process.stdin.pause();
    }
}

main().catch(() => {
    console.error("INTEROP_FAILED");
    process.exitCode = 1;
});
