// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

const path = require("node:path");
const { EventEmitter, once } = require("node:events");
const { setTimeout: delay } = require("node:timers/promises");
const { WebSocket } = require("ws");
const { startTestHost, assertManifestValid } = require("@ventanatools/orbit-extensions/testing");
const { startBridge } = require("../companion/bridge.cjs");
const { connectSession } = require("../uxp/client.js");
const { createPlatform } = require("../uxp/platform.js");

const MANIFEST = path.join(__dirname, "..", "extension.json");

class Inbox extends EventEmitter {
    constructor() {
        super();
        this.messages = [];
    }

    push(message) {
        this.messages.push(message);
        this.emit("changed");
    }

    async next(type, timeoutMs = 3000) {
        const deadline = Date.now() + timeoutMs;
        while (true) {
            const index = this.messages.findIndex((message) => message.type === type);
            if (index >= 0) return this.messages.splice(index, 1)[0];
            if (Date.now() >= deadline) throw new Error(`Timed out waiting for ${type}`);
            await delay(5);
        }
    }
}

async function until(predicate, what = "condition", timeoutMs = 3000) {
    const deadline = Date.now() + timeoutMs;
    while (!predicate()) {
        if (Date.now() > deadline) throw new Error("Timed out waiting for " + what + ".");
        await delay(5);
    }
}

/** An in-memory Photoshop document with one selected layer and history. */
function fakePhotoshop(options = {}) {
    let mutations = 0;
    let visible = true;
    let suspension = null;
    const history = [];
    const historyEvents = [];
    const layer = { id: 2 };
    Object.defineProperty(layer, "visible", {
        get: () => visible,
        set: (value) => {
            mutations++;
            historyEvents.push({ type: "mutation", value, suspended: suspension !== null });
            visible = value;
            if (options.failAfterMutation) throw new Error("simulated setter failure");
        },
    });
    const document = { id: 1, activeLayers: [layer] };
    const app = { documents: [document], activeDocument: document };
    function finishHistory(commit, automatic) {
        const state = suspension;
        if (!state) throw new Error("no active history suspension");
        historyEvents.push({ type: automatic ? "autoResume" : "resume", commit });
        if (commit && visible !== state.before) history.push({ documentID: state.documentID, name: state.name, before: state.before, after: visible });
        if (!commit) visible = state.before;
        suspension = null;
    }
    const context = {
        isCancelled: false,
        hostControl: {
            suspendHistory: async ({ documentID, name }) => {
                if (suspension || documentID !== document.id) throw new Error("invalid history suspension");
                const token = {};
                suspension = { token, documentID, name, before: visible };
                historyEvents.push({ type: "suspend", documentID, name });
                return token;
            },
            resumeHistory: async (token, commit = true) => {
                if (!suspension || suspension.token !== token) throw new Error("invalid history token");
                finishHistory(commit, false);
            },
        },
    };
    const photoshop = {
        app,
        core: {
            executeAsModal: async (callback) => {
                try {
                    const result = await callback(context);
                    if (suspension) finishHistory(true, true);
                    return result;
                } catch (error) {
                    if (suspension) finishHistory(false, true);
                    throw error;
                }
            },
        },
    };
    return { photoshop, layer, document, context, history, historyEvents, get mutations() { return mutations; } };
}

/** A bridge on a free port and a test host running the real SDK client with the bridge's handler. */
async function bridgeAndHost(t, bridgeOptions = {}) {
    const statuses = [];
    const bridge = await startBridge({ port: 0, onStatus: (text) => statuses.push(text), ...bridgeOptions });
    const host = await startTestHost({ manifest: assertManifestValid(MANIFEST), handler: bridge.handler });
    t.after(async () => {
        await host.close();
        await bridge.close();
    });
    return { bridge, host, statuses };
}

/** Connects the UXP client, as the panel does, to the bridge with a fake Photoshop. */
function attachPanel(t, bridge, model, config = bridge.config) {
    const socket = new WebSocket(bridge.config.url, { origin: "file://" });
    const statuses = new Inbox();
    const inbound = new Inbox();
    let lost;
    const session = connectSession(socket, config, createPlatform(model.photoshop, { refreshMs: 50, minPushMs: 10 }), (status) => {
        statuses.push({ type: status === "Connected." ? "connected" : "disconnected" });
    }, (wasLost) => {
        lost = wasLost;
    });
    socket.on("message", (data) => inbound.push(JSON.parse(data.toString("utf8"))));
    t.after(() => session.close());
    return { socket, session, statuses, inbound, get lost() { return lost; } };
}

/** Opens a raw WebSocket to the bridge and collects what it receives. */
async function openPeer(bridge, origin = "file://") {
    const socket = new WebSocket(bridge.config.url, origin === undefined ? {} : { origin });
    socket.on("error", () => {});
    const inbox = new Inbox();
    const closed = new Promise((resolve) => socket.once("close", resolve));
    socket.on("message", (data) => inbox.push(JSON.parse(data.toString("utf8"))));
    await once(socket, "open");
    return { socket, inbox, closed };
}

module.exports = { Inbox, until, delay, fakePhotoshop, bridgeAndHost, attachPanel, openPeer, MANIFEST };
