// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");

const source = fs.readFileSync(path.join(__dirname, "../uxp/index.js"), "utf8");
const privateValue = "PRIVATE_FILE_PATH_AND_CREDENTIAL_MUST_NEVER_APPEAR";

function panel(failAt, failure) {
    const handlers = {}, elements = {}, counts = { sockets: 0, socketClosed: 0, platformClosed: 0 };
    for (const id of ["connect", "disconnect", "status"]) elements[id] = {
        textContent: "", addEventListener: (_, handler) => { handlers[id] = handler; }
    };
    const hit = step => { if (step === failAt) throw failure; };
    const file = {
        getMetadata: async () => { hit("metadata"); return { size: 100 }; },
        read: async () => { hit("read"); return privateValue; }
    };
    const modules = {
        uxp: {
            entrypoints: { setup() {} },
            storage: {
                localFileSystem: { getFileForOpening: async () => { hit("picker"); return file; } },
                formats: { utf8: "utf8" }
            }
        },
        "./client-v2.js": {
            parseBridgeConfig: () => {
                hit("config");
                return { protocolVersion: 2, url: "ws://127.0.0.1:38475/orbit-photoshop", secret: privateValue };
            },
            connectSessionV2: (_socket, _config, _platform, onStatus) => {
                hit("session"); onStatus("Connected.");
                return { close() { onStatus("Disconnected."); } };
            }
        },
        "./platform.js": { createPlatform: () => {
            hit("platform");
            return { close: async () => { counts.platformClosed++; } };
        } },
        photoshop: {}
    };
    vm.runInNewContext(source, {
        require: name => modules[name],
        document: { getElementById: id => elements[id] },
        WebSocket: function (url) {
            assert.equal(url, "ws://localhost:38475/orbit-photoshop");
            hit("socket"); counts.sockets++;
            this.close = () => { counts.socketClosed++; };
        }
    });
    return { handlers, elements, counts };
}

for (const stage of ["picker", "metadata", "read", "config", "socket", "platform", "session"]) {
    test("connection reports only fixed stage/category for " + stage, async () => {
        const failure = new TypeError(privateValue);
        failure.stack = privateValue;
        const current = panel(stage, failure);
        await current.handlers.connect();
        assert.equal(current.elements.status.textContent, "Could not connect (" + stage + "/TypeError).");
        assert.equal(current.elements.status.textContent.includes(privateValue), false);
        assert.equal(current.counts.socketClosed, stage === "platform" || stage === "session" ? 1 : 0);
        assert.equal(current.counts.platformClosed, stage === "session" ? 1 : 0);
    });
}

test("unrecognized error names never enter connection diagnostics", async () => {
    const failure = new Error(privateValue);
    failure.name = privateValue;
    const current = panel("session", failure);
    await current.handlers.connect();
    assert.equal(current.elements.status.textContent, "Could not connect (session/Error).");
});

test("successful initialization transfers resource ownership to the session", async () => {
    const current = panel(null, null);
    await current.handlers.connect();
    assert.equal(current.elements.status.textContent, "Connected.");
    assert.equal(current.counts.sockets, 1);
    assert.equal(current.counts.socketClosed, 0);
    assert.equal(current.counts.platformClosed, 0);
});
