// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";
const { ACTION_ID, STATUS_ID, isStart, isInvoke, sameSettings } = require("./wire-v2.js");

// Adobe documents these action events and modal exit. Descriptors stay here.
// Reconciliation handles notifications suppressed during another modal scope.
const EVENTS = Object.freeze(["select", "show", "hide", "open", "close", "make", "delete", "set", "undoEvent", "modalJavaScriptScopeExit"]);

function selection(app) {
    if (!app.documents.length) return { count: 0, documentId: null, layerId: null, layer: null };
    const document = app.activeDocument, layers = document.activeLayers;
    return { count: layers.length, documentId: document.id, layerId: layers.length === 1 ? layers[0].id : null,
        layer: layers.length === 1 ? layers[0] : null };
}

function createPlatform(photoshop, options = {}) {
    const refreshMs = options.refreshMs || 2000, minPushMs = options.minPushMs || 500, sessions = new Map();
    let closed = false, busy = false, heartbeat = null, pending = null, lastRefresh = 0;
    let subscribed = false, listenerWork = null;
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
    function face(value, state, detail) {
        return { $type: "setFace", face: { picture: { $type: "none" }, line1: { $type: "text", value },
            state, detail, goodForSeconds: 5 } };
    }
    function faceFor(session, current) {
        if (session.actionId === STATUS_ID && session.settings.display === "selection") {
            return face(current.count === 0 ? "None" : current.count === 1 ? "One" : "Many", "None",
                current.count === 0 ? "No layer is selected." : current.count === 1 ? "One layer is selected." : "More than one layer is selected.");
        }
        if (!current.layer) return { $type: "fail", failure: "NeedsSetup" };
        const visible = !!current.layer.visible;
        return face(visible ? "Shown" : "Hidden", visible ? "On" : "Off",
            visible ? "The selected layer is visible." : "The selected layer is hidden.");
    }
    function refresh() {
        pending = null;
        if (closed || !sessions.size || busy) return;
        lastRefresh = Date.now();
        let current;
        try { current = selection(photoshop.app); } catch (_) { current = null; }
        for (const session of sessions.values()) {
            let command;
            try { command = current ? faceFor(session, current) : { $type: "fail", failure: "NoData" }; }
            catch (_) { command = { $type: "fail", failure: "NoData" }; }
            session.publish({ type: "face", sessionId: session.sessionId, command });
        }
    }
    function requestRefresh() {
        if (closed || !sessions.size || pending) return;
        // Reserve frame-rate headroom for invocation results at the 64-session cap.
        const interval = Math.max(minPushMs, Math.ceil(sessions.size * 1000 / 64));
        pending = setTimeout(refresh, Math.max(0, interval - (Date.now() - lastRefresh)));
    }
    function start(message, publish) {
        if (closed || !isStart(message) || sessions.has(message.sessionId) || sessions.size >= 64) throw new Error("session.start");
        sessions.set(message.sessionId, { ...message, settings: { ...message.settings }, publish });
        if (!heartbeat) heartbeat = setInterval(() => { void syncListeners(); requestRefresh(); }, refreshMs);
        void syncListeners();
        requestRefresh();
    }
    function stop(id) {
        sessions.delete(id);
        if (!sessions.size) {
            clearInterval(heartbeat); heartbeat = null;
            clearTimeout(pending); pending = null;
            void syncListeners();
        }
    }
    async function invoke(message, canceled) {
        const session = sessions.get(message.sessionId);
        if (!isInvoke(message) || !session || session.actionId !== message.actionId || !sameSettings(session.settings, message.settings)) return "refused";
        if (message.actionId !== ACTION_ID) return "unsupported";
        if (closed || busy || canceled()) return "refused";
        busy = true;
        let modalContext = null;
        try {
            const target = selection(photoshop.app);
            if (!target.layer) return "refused";
            let completed = false;
            await photoshop.core.executeAsModal(async context => {
                modalContext = context;
                if (closed || canceled() || context.isCancelled || sessions.get(message.sessionId) !== session) return;
                const current = selection(photoshop.app);
                if (!current.layer || current.documentId !== target.documentId || current.layerId !== target.layerId) return;
                const desired = message.settings.mode === "toggle" ? !current.layer.visible : message.settings.mode === "show";
                if (current.layer.visible === desired) { completed = true; return; }
                const history = await context.hostControl.suspendHistory({
                    documentID: target.documentId, name: "Orbit: Change selected layer visibility"
                });
                // Suspending history awaits the host. Recheck revocation and the
                // target before the one mutation; an exception rolls it back.
                const next = selection(photoshop.app);
                if (closed || canceled() || context.isCancelled || sessions.get(message.sessionId) !== session ||
                    !next.layer || next.documentId !== target.documentId || next.layerId !== target.layerId) {
                    await context.hostControl.resumeHistory(history, false);
                    return;
                }
                next.layer.visible = desired;
                await context.hostControl.resumeHistory(history, true);
                completed = true;
            }, { commandName: "Orbit: Change selected layer visibility" });
            return completed ? "done" : "refused";
        } catch (error) {
            return canceled() || (modalContext && modalContext.isCancelled) || (error && error.number === 9) ? "refused" : "failed";
        } finally { busy = false; requestRefresh(); }
    }
    async function close() {
        closed = true; sessions.clear();
        clearInterval(heartbeat); heartbeat = null;
        clearTimeout(pending); pending = null;
        // An unfinished add sees closed on its next loop and removes its callback.
        await syncListeners();
    }
    return { start, stop, invoke, close };
}
module.exports = { createPlatform, EVENTS };
