// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

// The panel's script in a sandbox with fake UXP modules: fixed diagnostics, resource ownership,
// the persistent token and reconnection with backoff.

const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const constants = require("../uxp/constants.js");
const wire = require("../uxp/wire.js");
const { delay, until } = require("./helpers.cjs");

const source = fs.readFileSync(path.join(__dirname, "../uxp/index.js"), "utf8");
const PRIVATE = "PRIVATE_FILE_PATH_AND_KEY_MUST_NEVER_APPEAR";
const CONFIG = JSON.stringify({ bridgeVersion: 3, url: constants.BRIDGE_URL, secret: "ab".repeat(32) });

function panel({ failAt, failure, storage: initial = {}, configText = CONFIG, readGate } = {}) {
    const handlers = {};
    const elements = {};
    const lifecycle = {};
    const counts = { sockets: 0, socketClosed: 0, platformClosed: 0, sessions: 0, tokensRead: 0 };
    const timers = [];
    const store = new Map(Object.entries(initial));
    const sessions = [];
    for (const id of ["connect", "disconnect", "status"]) {
        elements[id] = { textContent: "", addEventListener: (_, handler) => { handlers[id] = handler; } };
    }
    const hit = (step) => {
        if (step === failAt) throw failure;
    };
    const file = {
        getMetadata: async () => {
            hit("metadata");
            return { size: 100 };
        },
        read: async () => {
            hit("read");
            if (readGate) await readGate;
            return configText;
        },
    };
    const modules = {
        uxp: {
            entrypoints: { setup: (value) => Object.assign(lifecycle, value) },
            storage: {
                localFileSystem: {
                    getFileForOpening: async () => {
                        hit("picker");
                        return file;
                    },
                    createPersistentToken: async () => "token-1",
                    getEntryForPersistentToken: async (token) => {
                        counts.tokensRead++;
                        hit("token");
                        assert.equal(token, "token-1");
                        return file;
                    },
                },
                formats: { utf8: "utf8" },
            },
        },
        "./constants.js": constants,
        "./wire.js": { parseBridgeConfig: (text) => {
            hit("config");
            return wire.parseBridgeConfig(text);
        } },
        "./client.js": {
            connectSession: (_socket, config, _platform, onStatus, onClosed) => {
                hit("session");
                assert.equal(config.secret, "ab".repeat(32));
                counts.sessions++;
                const session = {
                    closed: false,
                    lose() {
                        onStatus("Disconnected. Reconnecting when the companion is back.");
                        onClosed(true);
                    },
                    close() {
                        if (session.closed) return;
                        session.closed = true;
                        onStatus("Disconnected.");
                        onClosed(false);
                    },
                };
                sessions.push(session);
                onStatus("Connected.");
                return session;
            },
        },
        "./platform.js": {
            createPlatform: () => {
                hit("platform");
                return { close: async () => { counts.platformClosed++; } };
            },
        },
        photoshop: {},
    };
    vm.runInNewContext(source, {
        require: (name) => modules[name],
        document: { getElementById: (id) => elements[id] },
        localStorage: {
            getItem: (key) => (store.has(key) ? store.get(key) : null),
            setItem: (key, value) => store.set(key, String(value)),
            removeItem: (key) => store.delete(key),
        },
        setTimeout: (callback, ms) => {
            const timer = { callback, ms, cleared: false };
            timers.push(timer);
            return timer;
        },
        clearTimeout: (timer) => {
            if (timer) timer.cleared = true;
        },
        WebSocket: function (url) {
            assert.equal(url, "ws://localhost:38475/photoshop-bridge");
            hit("socket");
            counts.sockets++;
            this.close = () => { counts.socketClosed++; };
        },
    });
    const fire = async () => {
        const timer = timers.find((entry) => !entry.cleared && !entry.fired);
        assert.ok(timer, "a retry is scheduled");
        timer.fired = true;
        timer.callback();
        await delay(5);
        return timer.ms;
    };
    return { handlers, elements, counts, lifecycle, store, sessions, timers, fire };
}

for (const stage of ["picker", "metadata", "read", "config", "socket", "platform", "session"]) {
    test("a failure at " + stage + " shows only its fixed stage and category", async () => {
        const failure = new TypeError(PRIVATE);
        failure.stack = PRIVATE;
        const current = panel({ failAt: stage, failure });
        await current.handlers.connect();
        assert.match(current.elements.status.textContent, new RegExp("^Could not connect \\(" + stage + "/TypeError\\)\\."));
        assert.equal(current.elements.status.textContent.includes(PRIVATE), false);
        assert.equal(current.counts.socketClosed, stage === "platform" || stage === "session" ? 1 : 0);
        assert.equal(current.counts.platformClosed, stage === "session" ? 1 : 0);
    });
}

test("unrecognised error names never enter the diagnostics", async () => {
    const failure = new Error(PRIVATE);
    failure.name = PRIVATE;
    const current = panel({ failAt: "session", failure });
    await current.handlers.connect();
    assert.ok(current.elements.status.textContent.startsWith("Could not connect (session/Error)."));
    assert.equal(current.elements.status.textContent.includes(PRIVATE), false);
});

test("choosing the bridge file connects, hands the resources to the session and remembers the file", async () => {
    const current = panel();
    await current.handlers.connect();
    assert.equal(current.elements.status.textContent, "Connected.");
    assert.equal(current.counts.sockets, 1);
    assert.equal(current.counts.socketClosed, 0);
    assert.equal(current.store.get("photoshopBridge.bridgeFile"), "token-1");
    assert.equal(current.store.get("photoshopBridge.reconnect"), "on");
    assert.equal(typeof current.lifecycle.plugin.create, "function", "Adobe requires create when the plugin lifecycle is defined");
});

test("a lost connection reconnects through the persistent token with doubling backoff", async () => {
    const current = panel();
    await current.handlers.connect();
    current.sessions[0].lose();
    assert.equal(await current.fire(), 1000);
    await until(() => current.counts.sessions === 2, "the reconnection");
    assert.equal(current.counts.tokensRead, 1);
    current.sessions[1].lose();
    assert.equal(await current.fire(), 1000, "a connection that succeeded resets the backoff");
    const failing = panel({ failAt: "read", failure: new Error("gone"), storage: { "photoshopBridge.bridgeFile": "token-1", "photoshopBridge.reconnect": "on" } });
    failing.lifecycle.plugin.create();
    await delay(5);
    assert.equal(failing.elements.status.textContent, "Could not connect (read/Error). Waiting for the companion; trying again in 1 s.");
    assert.equal(await failing.fire(), 1000);
    assert.equal(await failing.fire(), 2000);
    assert.equal(await failing.fire(), 4000);
});

test("Disconnect stops reconnecting, and plugin destruction ends the session", async () => {
    const current = panel();
    await current.handlers.connect();
    current.handlers.disconnect();
    assert.equal(current.elements.status.textContent, "Disconnected.");
    assert.equal(current.sessions[0].closed, true);
    assert.equal(current.store.get("photoshopBridge.reconnect"), undefined);
    assert.equal(current.timers.filter((timer) => !timer.cleared).length, 0);
    const destroyed = panel();
    await destroyed.handlers.connect();
    destroyed.lifecycle.plugin.destroy();
    assert.equal(destroyed.sessions[0].closed, true);
    assert.equal(destroyed.store.get("photoshopBridge.reconnect"), "on", "unloading the plugin keeps the person's choice");
});

test("a token the panel can no longer open asks for the file again", async () => {
    const current = panel({ failAt: "token", failure: new Error(PRIVATE), storage: { "photoshopBridge.bridgeFile": "token-1", "photoshopBridge.reconnect": "on" } });
    current.lifecycle.plugin.create();
    await delay(5);
    assert.equal(current.elements.status.textContent, "Could not connect (token/Error). Choose the bridge file again.");
    assert.equal(current.store.get("photoshopBridge.bridgeFile"), undefined);
});

test("Disconnect during a bridge-file read wins over the read", async () => {
    let release;
    const readGate = new Promise((resolve) => {
        release = resolve;
    });
    const current = panel({ readGate });
    const choosing = current.handlers.connect();
    await delay(5);
    current.handlers.disconnect();
    release();
    await choosing;
    assert.equal(current.counts.sockets, 0);
    assert.equal(current.elements.status.textContent, "Disconnected.");
});
