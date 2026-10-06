// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

// The Photoshop adapter: it reads the selection, publishes short faces when they change, and
// changes the one selected layer's visibility inside a modal scope with a named history step.
"use strict";

const constants = require("./constants.js");
const { isStart, isInvoke, sameSettings } = require("./wire.js");

// Adobe documents these action events and modal exit. Descriptors stay here. Reconciliation
// catches notifications suppressed during another modal scope.
const EVENTS = Object.freeze(["select", "show", "hide", "open", "close", "make", "delete", "set", "undoEvent", "modalJavaScriptScopeExit"]);
const HISTORY_NAME = "Change selected layer visibility";

function selection(app) {
    if (!app.documents.length) return { count: 0, documentId: null, layerId: null, layer: null };
    const document = app.activeDocument;
    const layers = document.activeLayers;
    return { count: layers.length, documentId: document.id, layerId: layers.length === 1 ? layers[0].id : null, layer: layers.length === 1 ? layers[0] : null };
}

function createPlatform(photoshop, options = {}) {
    const refreshMs = options.refreshMs || 2000;
    const minPushMs = options.minPushMs || 500;
    const sessions = new Map();
    let closed = false;
    let busy = false;
    let heartbeat = null;
    let pending = null;
    let lastRefresh = 0;
    let subscribed = false;
    let listenerWork = null;

    function syncListeners() {
        if (listenerWork || !photoshop.action) return listenerWork || Promise.resolve();
        listenerWork = (async () => {
            while (true) {
                const wanted = !closed && sessions.size > 0;
                if (wanted === subscribed) return;
                if (wanted) await photoshop.action.addNotificationListener(EVENTS, requestRefresh);
                else await photoshop.action.removeNotificationListener(EVENTS, requestRefresh);
                subscribed = wanted;
            }
        })().catch(() => { /* Reconciliation remains if a host rejects an event. */ })
            .finally(() => { listenerWork = null; });
        return listenerWork;
    }

    function face(sessionId, line1, state, detail) {
        return { type: "face", sessionId, face: { line1, state, detail, goodForSeconds: constants.FACE_SECONDS } };
    }

    function faceFor(session, current) {
        if (session.contributionId === constants.STATUS_ID && session.settings.display === "selection") {
            return face(session.sessionId, current.count === 0 ? "None" : current.count === 1 ? "One" : "Many", "None",
                current.count === 0 ? "No layer is selected." : current.count === 1 ? "One layer is selected." : "More than one layer is selected.");
        }
        if (!current.layer) return { type: "fail", sessionId: session.sessionId, failure: "NeedsSetup" };
        const visible = !!current.layer.visible;
        return face(session.sessionId, visible ? "Shown" : "Hidden", visible ? "On" : "Off",
            visible ? "The selected layer is visible." : "The selected layer is hidden.");
    }

    /** Publishes each session's face when it differs from the last one it published. */
    function refresh() {
        pending = null;
        if (closed || !sessions.size || busy) return;
        lastRefresh = Date.now();
        let current;
        try {
            current = selection(photoshop.app);
        } catch (_) {
            current = null;
        }
        for (const session of sessions.values()) {
            let command;
            try {
                command = current ? faceFor(session, current) : { type: "fail", sessionId: session.sessionId, failure: "NoResult" };
            } catch (_) {
                command = { type: "fail", sessionId: session.sessionId, failure: "NoResult" };
            }
            const text = JSON.stringify(command);
            if (text === session.last) continue;
            session.last = text;
            session.publish(command);
        }
    }

    function requestRefresh() {
        if (closed || !sessions.size || pending) return;
        // At most two refreshes per second, slower at the session cap.
        const interval = Math.max(minPushMs, Math.ceil(sessions.size * 1000 / 64));
        pending = setTimeout(refresh, Math.max(0, interval - (Date.now() - lastRefresh)));
    }

    function start(message, publish) {
        if (closed || !isStart(message) || sessions.has(message.sessionId) || sessions.size >= 64) throw new Error("session.start");
        sessions.set(message.sessionId, { sessionId: message.sessionId, contributionId: message.contributionId, settings: { ...message.settings }, publish, last: null });
        if (!heartbeat) {
            heartbeat = setInterval(() => {
                void syncListeners();
                requestRefresh();
            }, refreshMs);
        }
        void syncListeners();
        requestRefresh();
    }

    function stop(id) {
        sessions.delete(id);
        if (!sessions.size) {
            clearInterval(heartbeat);
            heartbeat = null;
            clearTimeout(pending);
            pending = null;
            void syncListeners();
        }
    }

    /** Runs one pick; resolves with { outcome, failure? }. */
    async function invoke(message, canceled) {
        const session = sessions.get(message.sessionId);
        if (!isInvoke(message) || !session || session.contributionId !== message.contributionId || !sameSettings(session.settings, message.settings)) {
            return { outcome: "Refused" };
        }
        if (message.contributionId !== constants.TOGGLE_ID) return { outcome: "Unsupported" };
        if (closed || busy || canceled()) return { outcome: "Refused" };
        busy = true;
        let modalContext = null;
        try {
            const target = selection(photoshop.app);
            if (!target.layer) return { outcome: "Refused" };
            let completed = false;
            await photoshop.core.executeAsModal(async (context) => {
                modalContext = context;
                if (closed || canceled() || context.isCancelled || sessions.get(message.sessionId) !== session) return;
                const current = selection(photoshop.app);
                if (!current.layer || current.documentId !== target.documentId || current.layerId !== target.layerId) return;
                const desired = message.settings.mode === "toggle" ? !current.layer.visible : message.settings.mode === "show";
                if (current.layer.visible === desired) {
                    completed = true;
                    return;
                }
                const history = await context.hostControl.suspendHistory({ documentID: target.documentId, name: HISTORY_NAME });
                // Suspending history awaits the host. Recheck cancellation and the target before the
                // one change; an exception rolls it back.
                const next = selection(photoshop.app);
                if (closed || canceled() || context.isCancelled || sessions.get(message.sessionId) !== session
                    || !next.layer || next.documentId !== target.documentId || next.layerId !== target.layerId) {
                    await context.hostControl.resumeHistory(history, false);
                    return;
                }
                next.layer.visible = desired;
                await context.hostControl.resumeHistory(history, true);
                completed = true;
            }, { commandName: HISTORY_NAME });
            return { outcome: completed ? "Done" : "Refused" };
        } catch (error) {
            return { outcome: canceled() || (modalContext && modalContext.isCancelled) || (error && error.number === 9) ? "Refused" : "Failed" };
        } finally {
            busy = false;
            requestRefresh();
        }
    }

    async function close() {
        closed = true;
        sessions.clear();
        clearInterval(heartbeat);
        heartbeat = null;
        clearTimeout(pending);
        pending = null;
        // An unfinished add sees closed on its next loop and removes its callback.
        await syncListeners();
    }

    return { start, stop, invoke, close };
}

module.exports = { createPlatform, EVENTS, HISTORY_NAME };
