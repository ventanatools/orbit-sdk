// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

// The panel: the person chooses the companion's bridge file once; the panel keeps it through a
// persistent token and reconnects with backoff whenever the companion comes back.
"use strict";

const { entrypoints, storage } = require("uxp");
const constants = require("./constants.js");
const { parseBridgeConfig } = require("./wire.js");
const { connectSession } = require("./client.js");
const { createPlatform } = require("./platform.js");

const TOKEN_KEY = "photoshopBridge.bridgeFile";
const RECONNECT_KEY = "photoshopBridge.reconnect";
const FIRST_RETRY_MS = 1000;
const MAX_RETRY_MS = 30000;
const STAGES = ["picker", "token", "metadata", "read", "config", "previous", "socket", "platform", "session"];

let session = null;
let choosing = false;
let destroyed = false;
let generation = 0;
let retryTimer = null;
let retryDelay = FIRST_RETRY_MS;

function status(message) {
    const label = document.getElementById("status");
    if (label && !destroyed) label.textContent = message;
}

function remember(key, value) {
    try {
        if (value === null) localStorage.removeItem(key);
        else localStorage.setItem(key, value);
    } catch (_) {
        // Without storage the panel still works; it just cannot reconnect by itself.
    }
}

function recall(key) {
    try {
        return localStorage.getItem(key);
    } catch (_) {
        return null;
    }
}

/** Fixed stages and error categories only: never exception prose, paths, bridge contents or keys. */
function connectionFailure(stage, error) {
    const step = STAGES.includes(stage) ? stage : "setup";
    const kind = error && ["TypeError", "ReferenceError", "RangeError", "SyntaxError"].includes(error.name) ? error.name : "Error";
    return "Could not connect (" + step + "/" + kind + ").";
}

function stopRetrying() {
    clearTimeout(retryTimer);
    retryTimer = null;
}

/** Tries again after the current backoff delay, keeping `failure` (fixed text) visible. Returns whether it will. */
function scheduleReconnect(failure) {
    if (destroyed || recall(RECONNECT_KEY) !== "on" || retryTimer) return false;
    const delay = retryDelay;
    retryDelay = Math.min(retryDelay * 2, MAX_RETRY_MS);
    status((failure ? failure + " " : "") + "Waiting for the companion; trying again in " + Math.round(delay / 1000) + " s.");
    retryTimer = setTimeout(() => {
        retryTimer = null;
        void reconnect();
    }, delay);
    return true;
}

function closeSession() {
    const current = session;
    session = null;
    if (current) current.close();
}

/** Connects with the bridge file `entry`; on failure, reports the stage and, when `retry`, tries again later. */
async function connectFrom(entry, current, retry) {
    let stage = "metadata";
    let socket = null;
    let platform = null;
    try {
        const metadata = await entry.getMetadata();
        if (metadata.size > 2048) throw new Error("bridge.size");
        stage = "read";
        const text = await entry.read({ format: storage.formats.utf8 });
        stage = "config";
        const config = parseBridgeConfig(text);
        if (destroyed || current !== generation) return;
        stage = "previous";
        closeSession();
        status("Authenticating the local companion...");
        stage = "socket";
        // The parser pins the bridge file to this port and path. Photoshop's permission matcher
        // accepts "localhost" but not the IPv4 literal the companion listens on.
        socket = new WebSocket(constants.PANEL_URL);
        stage = "platform";
        platform = createPlatform(require("photoshop"));
        stage = "session";
        session = connectSession(socket, config, platform, (text) => {
            if (text === "Connected.") retryDelay = FIRST_RETRY_MS;
            status(text);
        }, (lost) => {
            session = null;
            if (lost && current === generation) scheduleReconnect();
        });
        // The session now owns both resources.
        socket = null;
        platform = null;
    } catch (error) {
        // Initialization can fail after a native socket exists. Do not leave it unowned.
        if (socket) {
            try {
                socket.onopen = socket.onmessage = socket.onerror = socket.onclose = null;
                socket.close();
            } catch (_) { /* best effort; diagnostics stay fixed */ }
        }
        if (platform) {
            try {
                await platform.close();
            } catch (_) { /* no exception prose */ }
        }
        if (!destroyed && current === generation) {
            const failure = connectionFailure(stage, error);
            if (!retry || !scheduleReconnect(failure)) status(failure);
        }
    }
}

/** Reopens the remembered bridge file through its persistent token and connects. */
async function reconnect() {
    const token = recall(TOKEN_KEY);
    if (!token || destroyed || session) return;
    const current = generation;
    let entry;
    try {
        entry = await storage.localFileSystem.getEntryForPersistentToken(token);
    } catch (error) {
        if (!destroyed && current === generation) {
            status(connectionFailure("token", error) + " Choose the bridge file again.");
            remember(TOKEN_KEY, null);
        }
        return;
    }
    if (current !== generation) return;
    await connectFrom(entry, current, true);
}

document.getElementById("connect").addEventListener("click", async function () {
    if (choosing) return;
    choosing = true;
    const current = ++generation;
    stopRetrying();
    retryDelay = FIRST_RETRY_MS;
    try {
        let file;
        try {
            file = await storage.localFileSystem.getFileForOpening({ types: ["json"], allowMultiple: false });
        } catch (error) {
            if (!destroyed && current === generation) status(connectionFailure("picker", error));
            return;
        }
        if (!file || current !== generation) return;
        try {
            remember(TOKEN_KEY, await storage.localFileSystem.createPersistentToken(file));
            remember(RECONNECT_KEY, "on");
        } catch (_) {
            // Without a persistent token the panel connects now but cannot reconnect by itself.
        }
        await connectFrom(file, current, true);
    } finally {
        choosing = false;
    }
});

document.getElementById("disconnect").addEventListener("click", function () {
    generation++;
    remember(RECONNECT_KEY, null);
    stopRetrying();
    closeSession();
    status("Disconnected.");
});

entrypoints.setup({
    plugin: {
        create: function () {
            if (recall(RECONNECT_KEY) === "on" && recall(TOKEN_KEY)) void reconnect();
        },
        destroy: function () {
            destroyed = true;
            generation++;
            stopRetrying();
            closeSession();
        },
    },
    // Hiding or destroying a panel keeps the connection. Plugin destruction ends it.
    panels: { photoshopBridge: { show: function () {}, hide: function () {}, destroy: function () {} } },
});
