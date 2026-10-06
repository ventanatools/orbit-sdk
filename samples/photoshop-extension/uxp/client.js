// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

// The panel's side of the bridge: it proves the bridge key over a fresh challenge, then runs the
// sessions and picks the companion forwards. Commands are never queued or replayed.
"use strict";

const constants = require("./constants.js");
const { parseRecord, fields, isStart, isStop, isInvoke, isCancel, OUTCOMES, FAILURES } = require("./wire.js");
const { bridgeProof, randomNonce, matchesProof } = require("./vendor/bridge-crypto.js");

const INVOKE_LIMIT_MS = 12000;

/**
 * Runs one authenticated connection on `socket` with the bridge `config`. `onStatus(text)` gets
 * fixed status text; `onClosed(lost)` runs once when the connection ends (lost is false after
 * close()). Returns { close }.
 */
function connectSession(socket, config, platform, onStatus, onClosed) {
    let stage = "challenge";
    let stopped = false;
    let explicit = false;
    let received = [];
    const clientNonce = randomNonce();
    const jobs = new Map();
    const seen = new Set();
    const sessions = new Set();
    const timer = setTimeout(close, 5000);

    function send(message) {
        if (stopped || socket.readyState !== 1) return;
        if (socket.bufferedAmount > 65536) {
            close();
            return;
        }
        socket.send(JSON.stringify(message));
    }

    function finish() {
        if (stopped) return;
        stopped = true;
        clearTimeout(timer);
        for (const job of jobs.values()) {
            job.canceled = true;
            clearTimeout(job.deadline);
        }
        jobs.clear();
        sessions.clear();
        void platform.close();
        try {
            socket.close();
        } catch (_) {
            // Already gone.
        }
        onStatus(explicit ? "Disconnected." : "Disconnected. Reconnecting when the companion is back.");
        if (onClosed) onClosed(!explicit);
    }

    function close() {
        explicit = true;
        finish();
    }

    socket.onopen = () => send({ type: "hello", bridgeVersion: constants.BRIDGE_VERSION, clientNonce });
    socket.onerror = finish;
    socket.onclose = finish;
    socket.onmessage = (event) => {
        if (stopped) return;
        try {
            const now = Date.now();
            received = received.filter((time) => now - time < 1000);
            if (received.length >= 128) throw new Error("bridge.rate");
            received.push(now);
            const message = parseRecord(event.data, 65536);
            if (stage === "challenge") {
                if (!fields(message, ["type", "serverNonce", "proof"]) || message.type !== "challenge" || !/^[0-9a-f]{64}$/.test(message.serverNonce)
                    || !matchesProof(message.proof, bridgeProof(config.secret, "server", clientNonce, message.serverNonce))) throw new Error("bridge.challenge");
                stage = "ready";
                send({ type: "authenticate", proof: bridgeProof(config.secret, "client", clientNonce, message.serverNonce) });
            } else if (stage === "ready") {
                if (!fields(message, ["type"]) || message.type !== "ready") throw new Error("bridge.ready");
                stage = "connected";
                clearTimeout(timer);
                onStatus("Connected.");
            } else if (message.type === "startSession") {
                if (!isStart(message) || sessions.has(message.sessionId) || sessions.size >= 64) throw new Error("bridge.session");
                sessions.add(message.sessionId);
                platform.start(message, (published) => {
                    if (sessions.has(published.sessionId)) send(published);
                });
            } else if (message.type === "stopSession") {
                if (!isStop(message)) throw new Error("bridge.stop");
                sessions.delete(message.sessionId);
                for (const job of jobs.values()) if (job.sessionId === message.sessionId) job.canceled = true;
                platform.stop(message.sessionId);
            } else if (message.type === "invoke") {
                if (!isInvoke(message) || seen.has(message.requestId) || seen.size >= 4096 || jobs.size >= 32) throw new Error("bridge.invoke");
                seen.add(message.requestId);
                const job = { canceled: false, sessionId: message.sessionId, deadline: null };
                jobs.set(message.requestId, job);
                job.deadline = setTimeout(() => {
                    job.canceled = true;
                }, INVOKE_LIMIT_MS);
                Promise.resolve()
                    .then(() => platform.invoke(message, () => stopped || job.canceled))
                    .then((result) => {
                        if (job.canceled || stopped) return;
                        const answer = { type: "result", requestId: message.requestId, outcome: OUTCOMES.indexOf(result && result.outcome) >= 0 ? result.outcome : "Failed" };
                        if (answer.outcome === "Failed" && result && FAILURES.indexOf(result.failure) >= 0) answer.failure = result.failure;
                        send(answer);
                    }, () => {
                        if (!job.canceled && !stopped) send({ type: "result", requestId: message.requestId, outcome: "Failed" });
                    })
                    .finally(() => {
                        clearTimeout(job.deadline);
                        jobs.delete(message.requestId);
                    });
            } else if (message.type === "cancel") {
                if (!isCancel(message)) throw new Error("bridge.cancel");
                const job = jobs.get(message.requestId);
                if (job) job.canceled = true;
            } else {
                throw new Error("bridge.type");
            }
        } catch (_) {
            finish();
        }
    };
    return { close };
}

module.exports = { connectSession };
