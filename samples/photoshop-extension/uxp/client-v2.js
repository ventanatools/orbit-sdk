// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";
const { parseRecordV2, fields, isRequestId, isOutcome, isStart, isInvoke } = require("./wire-v2.js");
const { bridgeProof, randomNonce, matchesProof } = require("./vendor/bridge-crypto.js");

function parseBridgeConfig(text) {
    const value = parseRecordV2(text, 2048);
    if (!fields(value, ["protocolVersion", "url", "secret"]) || value.protocolVersion !== 2 ||
        value.url !== "ws://127.0.0.1:38475/orbit-photoshop" || typeof value.secret !== "string" || !/^[0-9a-f]{64}$/.test(value.secret)) throw new Error("bridge.config");
    return value;
}
function connectSessionV2(socket, config, platform, onStatus) {
    let stage = "challenge", stopped = false, received = [];
    const clientNonce = randomNonce(), jobs = new Map(), seen = new Set(), sessions = new Set();
    const timer = setTimeout(close, 5000);
    function send(message) {
        if (stopped || socket.readyState !== 1) return;
        if (socket.bufferedAmount > 65536) { close(); return; }
        socket.send(JSON.stringify(message));
    }
    function close() {
        if (stopped) return;
        stopped = true; clearTimeout(timer);
        for (const job of jobs.values()) { job.canceled = true; clearTimeout(job.deadline); }
        jobs.clear(); sessions.clear();
        void platform.close();
        try { socket.close(); } catch (_) { /* already gone */ }
        onStatus("Disconnected. Choose the bridge file again.");
    }
    socket.onopen = () => send({ type: "hello", protocolVersion: 2, clientNonce });
    socket.onerror = close; socket.onclose = close;
    socket.onmessage = event => {
        if (stopped) return;
        try {
            const now = Date.now();
            received = received.filter(time => now - time < 1000);
            if (received.length >= 128) throw new Error("bridge.rate");
            received.push(now);
            const message = parseRecordV2(event.data, 65536);
            if (stage === "challenge") {
                if (!fields(message, ["type", "serverNonce", "proof"]) || message.type !== "challenge" ||
                    !matchesProof(message.proof, bridgeProof(config.secret, "server", clientNonce, message.serverNonce))) throw new Error("bridge.challenge");
                stage = "ready";
                send({ type: "authenticate", proof: bridgeProof(config.secret, "client", clientNonce, message.serverNonce) });
            } else if (stage === "ready") {
                if (!fields(message, ["type"]) || message.type !== "ready") throw new Error("bridge.ready");
                stage = "connected"; clearTimeout(timer);
                onStatus("Connected.");
            } else if (message.type === "startSession") {
                if (!isStart(message) || sessions.has(message.sessionId) || sessions.size >= 64) throw new Error("bridge.session");
                sessions.add(message.sessionId);
                platform.start(message, face => { if (sessions.has(face.sessionId)) send(face); });
            } else if (message.type === "stopSession") {
                if (!fields(message, ["type", "sessionId"]) || !isRequestId(message.sessionId)) throw new Error("bridge.stop");
                sessions.delete(message.sessionId);
                for (const job of jobs.values()) if (job.sessionId === message.sessionId) job.canceled = true;
                platform.stop(message.sessionId);
            } else if (message.type === "invoke") {
                if (!isInvoke(message) || seen.has(message.requestId) || seen.size >= 4096 || jobs.size >= 32) throw new Error("bridge.invoke");
                seen.add(message.requestId);
                const job = { canceled: false, sessionId: message.sessionId };
                jobs.set(message.requestId, job);
                job.deadline = setTimeout(() => { job.canceled = true; }, 12000);
                Promise.resolve().then(() => platform.invoke(message, () => stopped || job.canceled)).then(outcome => {
                    if (!job.canceled && !stopped) send({ type: "result", requestId: message.requestId, outcome: isOutcome(outcome) ? outcome : "failed" });
                }, () => {
                    if (!job.canceled && !stopped) send({ type: "result", requestId: message.requestId, outcome: "failed" });
                }).finally(() => { clearTimeout(job.deadline); jobs.delete(message.requestId); });
            } else if (message.type === "cancel") {
                if (!fields(message, ["type", "requestId"]) || !isRequestId(message.requestId)) throw new Error("bridge.cancel");
                const job = jobs.get(message.requestId);
                if (job) job.canceled = true;
            } else throw new Error("bridge.type");
        } catch (_) { close(); }
    };
    return { close };
}
module.exports = { parseBridgeConfig, connectSessionV2 };
