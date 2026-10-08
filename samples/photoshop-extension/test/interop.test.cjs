// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

// The interop peer against a scripted host on a real named pipe, as a host team's test drives it.

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const os = require("node:os");
const net = require("node:net");
const path = require("node:path");
const readline = require("node:readline");
const { spawn } = require("node:child_process");
const { once } = require("node:events");
const { randomBytes } = require("node:crypto");
const wire = require("@ventanatools/orbit-extensions/wire");
const { readManifestFile } = require("@ventanatools/orbit-extensions");
const constants = require("../uxp/constants.js");
const { until } = require("./helpers.cjs");

const windows = { skip: process.platform !== "win32" ? "The host transport is a Windows named pipe." : false, timeout: 30000 };

test("the interop peer connects through the bridge and the panel, and reports each pick", windows, async () => {
    const { manifest } = await readManifestFile(path.join(__dirname, "..", "extension.json"));
    const hostId = manifest.hosts[0];
    const secret = randomBytes(32);
    const registrationId = randomBytes(16).toString("hex");
    const pipeName = wire.createPipeName(hostId, "test", randomBytes(8).toString("hex"), registrationId);
    const folder = fs.mkdtempSync(path.join(os.tmpdir(), "photoshop-interop-"));
    const pairingPath = path.join(folder, "example.photoshop.pairing.json");
    fs.writeFileSync(pairingPath, JSON.stringify({ pairingVersion: 3, mode: "Persistent", hostId, pipeName, registrationId, extensionId: manifest.id, secret: secret.toString("base64") }, null, 2));
    const results = [];
    let hostSocket;
    const server = net.createServer((socket) => {
        hostSocket = socket;
        socket.on("error", () => {});
        let phase = "Handshake";
        let transcript;
        const send = (message) => socket.write(wire.encodeFrame(message));
        const reader = new wire.FrameReader({
            maxFrameBytes: 65536,
            onError: () => socket.destroy(),
            onFrame: (body) => {
                const { message } = wire.readMessage(body, "Companion", phase);
                if (!message) return socket.destroy();
                if (message.type === "hello") {
                    transcript = {
                        hostId, hostVersion: "1.0.0", registrationId, clientNonce: message.clientNonce, serverNonce: wire.newNonce(), version: 3,
                        minVersion: message.minVersion, maxVersion: message.maxVersion, clientCapabilities: message.capabilities, hostCapabilities: [],
                        manifestHash: message.manifestHash,
                    };
                    send({ type: "challenge", serverNonce: transcript.serverNonce, version: 3, capabilities: [], host: { id: hostId, version: "1.0.0" }, proof: wire.computeProof(secret, transcript, "server") });
                } else if (message.type === "authenticate") {
                    assert.ok(wire.verifyProof(secret, message.proof, transcript, "client"));
                    phase = "Authenticated";
                    send({ type: "ready", version: 3, capabilities: [], host: { id: hostId, version: "1.0.0" }, uiLanguage: "en-US", limits: wire.protocol3Limits });
                } else if (message.type === "result") {
                    results.push(message.outcome);
                } else if (message.type === "ping") {
                    send({ type: "pong", id: message.id });
                }
            },
        });
        socket.on("data", (chunk) => reader.push(chunk));
    });
    server.listen("\\\\.\\pipe\\" + pipeName);
    await once(server, "listening");
    const peer = spawn(process.execPath, [path.join(__dirname, "interop-peer.cjs"), pairingPath], { stdio: ["pipe", "pipe", "pipe"] });
    const lines = [];
    readline.createInterface({ input: peer.stdout }).on("line", (line) => lines.push(line));
    try {
        await until(() => lines.includes("READY"), "READY", 20000);
        const sessionId = randomBytes(16).toString("hex");
        hostSocket.write(wire.encodeFrame({ type: "startSession", sessionId, contributionId: constants.TOGGLE_ID, settings: { mode: "hide" } }));
        hostSocket.write(wire.encodeFrame({ type: "invoke", requestId: randomBytes(16).toString("hex"), sessionId }));
        await until(() => results.length === 1, "the result", 10000);
        assert.deepEqual(results, ["Done"]);
        await until(() => lines.includes("INVOKED 1"), "INVOKED 1");
        peer.stdin.write("STOP\n");
        const [code] = await once(peer, "exit");
        assert.equal(code, 0);
    } finally {
        if (peer.exitCode === null) peer.kill();
        if (hostSocket) hostSocket.destroy();
        await new Promise((resolve) => server.close(resolve));
        fs.rmSync(folder, { recursive: true, force: true });
    }
});
