"use strict";

const fs = require("node:fs/promises");
const path = require("node:path");
const { parsePairing } = require("./protocol-v2.cjs");
const { startBridgeV2 } = require("./bridge-v2.cjs");

async function main() {
    const args = process.argv.slice(2);
    if (args.length !== 4 || args[0] !== "--pairing" || args[2] !== "--bridge-file") {
        console.error("Usage: npm start -- --pairing <Orbit.pairing.json> --bridge-file <Photoshop.bridge.json>");
        process.exitCode = 1;
        return;
    }
    if (process.platform !== "win32") throw new Error("platform");
    const pairingPath = path.resolve(args[1]);
    const bridgePath = path.resolve(args[3]);
    if (pairingPath.toLowerCase() === bridgePath.toLowerCase()) throw new Error("path");
    // Read only the user-selected file, and cap it before parsing. Neither path
    // nor any secret is echoed. Pairing is never accepted on a command line.
    const handle = await fs.open(pairingPath, "r");
    let text;
    try {
        const stat = await handle.stat();
        if (!stat.isFile() || stat.size > 4096) throw new Error("size");
        const data = Buffer.alloc(4097);
        const { bytesRead } = await handle.read(data, 0, data.length, 0);
        if (bytesRead > 4096) throw new Error("size");
        text = data.subarray(0, bytesRead).toString("utf8");
    } finally { await handle.close(); }
    const pairing = parsePairing(text);
    const bridge = await startBridgeV2({ pairing, onStatus: message => console.log(message) });
    try {
        // Refuse to overwrite an existing file. On Windows the containing
        // directory's ACL is authoritative; mode alone is not an ACL boundary.
        await fs.writeFile(bridgePath, JSON.stringify(bridge.bridgeConfig, null, 2), { flag: "wx", mode: 0o600 });
    } catch (error) {
        await bridge.close();
        throw error;
    }
    console.log("Bridge ready on local port 38475. In the Photoshop sample panel, choose the bridge file you requested.");
    console.log("Keep both connection files private. Ctrl+C stops the companion; no commands are replayed.");
    let closing = false;
    async function stop() {
        if (closing) return;
        closing = true;
        await bridge.close();
        // Leave the explicitly chosen file for the user to remove. Its derived
        // secret expires with this process and cannot authenticate to Orbit.
        process.exitCode = 0;
    }
    process.once("SIGINT", () => void stop());
    process.once("SIGTERM", () => void stop());
}

main().catch(() => {
    console.error("Could not start. Check the pairing file, Windows, port 38475, and a new bridge-file path in a private folder.");
    process.exitCode = 1;
});
