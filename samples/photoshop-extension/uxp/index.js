// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

const { entrypoints, storage } = require("uxp");
const { parseBridgeConfig, connectSessionV2 } = require("./client-v2.js");
const { createPlatform } = require("./platform.js");
let session = null;
let choosing = false;
let destroyed = false;
let choiceGeneration = 0;

function status(message) { const label = document.getElementById("status"); if (label && !destroyed) label.textContent = message; }
function disconnect() {
    choiceGeneration++;
    closeSession();
}
function closeSession() {
    if (session) session.close();
    session = null;
    status("Disconnected.");
}

function connectionFailure(stage, error) {
    // Fixed local stages/categories only: never show exception prose, paths,
    // bridge JSON, credentials, nonces or remote messages.
    const step = ["picker", "metadata", "read", "config", "previous", "socket", "platform", "session"].includes(stage)
        ? stage : "setup";
    const kind = error && ["TypeError", "ReferenceError", "RangeError", "SyntaxError"].includes(error.name)
        ? error.name : "Error";
    return "Could not connect (" + step + "/" + kind + ").";
}

document.getElementById("connect").addEventListener("click", async function () {
    if (choosing) return;
    choosing = true;
    const generation = choiceGeneration;
    let stage = "picker", socket = null, platform = null;
    try {
        const file = await storage.localFileSystem.getFileForOpening({ types: ["json"], allowMultiple: false });
        if (!file) return;
        stage = "metadata";
        const metadata = await file.getMetadata();
        if (metadata.size > 2048) throw new Error("bridge.size");
        stage = "read";
        const text = await file.read({ format: storage.formats.utf8 });
        stage = "config";
        const config = parseBridgeConfig(text);
        if (destroyed || generation !== choiceGeneration) return;
        stage = "previous";
        closeSession();
        status("Authenticating the local companion…");
        stage = "socket";
        // The parser pins the bridge to this port/path. Installed Photoshop
        // 27.9 accepted localhost but rejected the IPv4 literal permission.
        socket = new WebSocket("ws://localhost:38475/orbit-photoshop");
        stage = "platform";
        platform = createPlatform(require("photoshop"));
        stage = "session";
        session = connectSessionV2(socket, config, platform, status);
        // The session now owns both resources.
        socket = null; platform = null;
    } catch (error) {
        // Initialization can fail after constructing a native socket (for
        // example, unavailable secure randomness). Do not leave it unowned.
        if (socket) {
            try {
                socket.onopen = socket.onmessage = socket.onerror = socket.onclose = null;
                socket.close();
            } catch (_) { /* best effort; diagnostics stay fixed */ }
        }
        if (platform) {
            try { await platform.close(); } catch (_) { /* no exception prose */ }
        }
        if (!destroyed && generation === choiceGeneration)
            status(connectionFailure(stage, error));
    } finally { choosing = false; }
});
document.getElementById("disconnect").addEventListener("click", disconnect);

entrypoints.setup({
    plugin: { create: function () {}, destroy: function () { destroyed = true; disconnect(); } },
    // Hiding/destroying a panel is not consent revocation. Plugin destruction is.
    panels: { orbitBridge: { show: function () {}, hide: function () {}, destroy: function () {} } }
});
