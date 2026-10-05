"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const vm = require("node:vm");
const { delay } = require("./helpers.cjs");

test("explicit disconnect invalidates an unfinished bridge-file read", async () => {
    const handlers = {}, elements = {};
    for (const id of ["connect", "disconnect", "status"]) elements[id] = {
        textContent: "", addEventListener: (_, handler) => { handlers[id] = handler; }
    };
    let releaseRead, sockets = 0, lifecycle;
    const file = { getMetadata: async () => ({ size: 100 }),
        read: () => new Promise(resolve => { releaseRead = resolve; }) };
    const modules = {
        uxp: { entrypoints: { setup: value => {
            assert.equal(typeof value.plugin.create, "function", "Adobe requires create when plugin lifecycle is defined");
            lifecycle = value;
        } },
            storage: { localFileSystem: { getFileForOpening: async () => file }, formats: { utf8: "utf8" } } },
        "./client-v2.js": { parseBridgeConfig: JSON.parse, connectSessionV2: () => ({ close() {} }) },
        "./platform.js": { createPlatform: () => ({}) },
        photoshop: {}
    };
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, "../uxp/index.js"), "utf8"), {
        require: name => modules[name], document: { getElementById: id => elements[id] },
        WebSocket: function () { sockets++; }
    });
    const choosing = handlers.connect();
    const deadline = Date.now() + 1000;
    while (!releaseRead) { if (Date.now() > deadline) throw new Error("test.timeout"); await delay(1); }
    handlers.disconnect();
    releaseRead(JSON.stringify({ protocolVersion: 2, url: "ws://127.0.0.1:38475/orbit-photoshop", secret: "00".repeat(32) }));
    await choosing;
    assert.equal(sockets, 0);
    assert.equal(elements.status.textContent, "Disconnected.");
    assert.equal(typeof lifecycle.plugin.destroy, "function");
});
