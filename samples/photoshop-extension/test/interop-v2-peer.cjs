"use strict";
// Real C# host -> Node bridge -> UXP client, with only Photoshop's DOM replaced.
const fs = require("node:fs/promises");
const { WebSocket } = require("ws");
const { parsePairing } = require("../companion/protocol-v2.cjs");
const { startBridgeV2 } = require("../companion/bridge-v2.cjs");
const { connectSessionV2 } = require("../uxp/client-v2.js");
const { createPlatform } = require("../uxp/platform.js");
const { fakePhotoshop, delay } = require("./helpers.cjs");

async function main() {
    if (process.argv.length !== 3) throw new Error("arguments");
    const pairing = parsePairing(await fs.readFile(process.argv[2], "utf8"));
    const bridge = await startBridgeV2({ pairing, port: 0 });
    let session;
    try {
        const model = fakePhotoshop(), platform = createPlatform(model.photoshop);
        const original = platform.invoke;
        platform.invoke = async (message, canceled) => {
            const result = await original(message, canceled);
            if (result === "done") console.log(`INVOKED ${model.mutations}`);
            return result;
        };
        let connected = false;
        session = connectSessionV2(new WebSocket(bridge.bridgeConfig.url), bridge.bridgeConfig, platform,
            status => { if (status.startsWith("Connected.")) connected = true; });
        const deadline = Date.now() + 10000;
        while (!bridge.orbitReady || !connected) {
            if (Date.now() >= deadline) throw new Error("timeout");
            await delay(10);
        }
        console.log("READY");
        await new Promise(resolve => {
            process.stdin.setEncoding("utf8");
            process.stdin.on("data", text => { if (text.includes("STOP")) resolve(); });
            process.stdin.on("end", resolve);
            process.once("SIGINT", resolve); process.once("SIGTERM", resolve);
        });
    } finally { session?.close(); await bridge.close(); process.stdin.pause(); }
}
main().catch(() => { console.error("INTEROP_FAILED"); process.exitCode = 1; });
