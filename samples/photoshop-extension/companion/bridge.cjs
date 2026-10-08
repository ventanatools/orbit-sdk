// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

// The bridge between the SDK's sessions and invocations and the Photoshop UXP panel: a loopback
// WebSocket server with its own mutual HMAC handshake. The SDK owns the host connection; this
// module only forwards sessions, invocations and faces.

const http = require("node:http");
const { randomBytes, createHmac, timingSafeEqual } = require("node:crypto");
const { WebSocket, WebSocketServer } = require("ws");
const { Outcome, Failure } = require("@ventanatools/orbit-extensions");
const constants = require("../uxp/constants.js");
const { parseRecord, fields, isHex64, isResult, isFace } = require("../uxp/wire.js");

const MAX_MESSAGE_BYTES = 65536;
const MAX_MESSAGES_PER_SECOND = 128;
const MAX_PENDING_INVOKES = 32;

/** HMAC-SHA256 over the ASCII transcript label, role and both nonces, in hexadecimal. */
function bridgeProof(secret, role, clientNonce, serverNonce) {
    return createHmac("sha256", Buffer.from(secret, "hex"))
        .update(`${constants.HANDSHAKE_LABEL}\n${role}\n${clientNonce}\n${serverNonce}`, "utf8").digest("hex");
}

function sameHex(left, right) {
    return isHex64(left) && isHex64(right) && timingSafeEqual(Buffer.from(left, "hex"), Buffer.from(right, "hex"));
}

/** A fresh per-launch key: unrelated to the pairing secret, so the bridge file never carries a host credential. */
function newBridgeKey() {
    const random = randomBytes(32);
    try {
        return createHmac("sha256", random).update(constants.KEY_LABEL + "\n", "utf8").digest("hex");
    } finally {
        random.fill(0);
    }
}

function abortError() {
    const error = new Error("The operation was aborted.");
    error.name = "AbortError";
    return error;
}

/**
 * Starts the bridge. Rejects with an error whose code is "EADDRINUSE" when the port is taken.
 * Options: port (default 38475; 0 picks a free one, for tests), helloMs (1 s), handshakeMs (5 s),
 * maxPending (4 sockets that have not authenticated), onStatus(text).
 */
async function startBridge({ port = constants.BRIDGE_PORT, helloMs = 1000, handshakeMs = 5000, maxPending = 4, onStatus = () => {} } = {}) {
    const secret = newBridgeKey();
    const server = http.createServer((_, response) => {
        response.writeHead(404);
        response.end();
    });
    server.headersTimeout = 5000;
    server.requestTimeout = 5000;
    const wss = new WebSocketServer({ noServer: true, maxPayload: MAX_MESSAGE_BYTES, perMessageDeflate: false });
    const pendingSockets = new Set();
    const sessions = new Map();
    const invocations = new Map();
    let photoshop = null;
    let stopping = false;

    function send(socket, message) {
        if (!socket || socket.readyState !== WebSocket.OPEN) return false;
        if (socket.bufferedAmount > MAX_MESSAGE_BYTES) {
            socket.terminate();
            return false;
        }
        socket.send(JSON.stringify(message));
        return true;
    }

    function startMessage(session) {
        return { type: "startSession", sessionId: session.id, contributionId: session.contributionId, settings: { ...session.settings } };
    }

    function photoshopLost() {
        photoshop = null;
        for (const [requestId, call] of invocations) {
            invocations.delete(requestId);
            call.resolve({ outcome: Outcome.Failed, failure: Failure.AppUnavailable });
        }
        for (const session of sessions.values()) session.fail(Failure.AppUnavailable);
        if (!stopping) onStatus("Photoshop disconnected; its live state is unavailable.");
    }

    function onPhotoshopMessage(message) {
        if (isResult(message)) {
            const call = invocations.get(message.requestId);
            if (!call) return;
            invocations.delete(message.requestId);
            call.resolve(message.failure ? { outcome: message.outcome, failure: message.failure } : message.outcome);
            return;
        }
        if (!isFace(message)) throw new Error("bridge.message");
        const session = sessions.get(message.sessionId);
        if (!session) return;
        if (message.type === "face") {
            session.setFace({ line1: message.face.line1, state: message.face.state, detail: message.face.detail, goodForSeconds: message.face.goodForSeconds, renew: true });
        } else if (message.type === "clearFace") {
            session.clearFace();
        } else {
            session.fail(message.failure);
        }
    }

    server.on("upgrade", (request, socket, head) => {
        // UXP sends the exact Origin file://; other local clients send none. Every other Origin is
        // refused. Origin is not a credential: every peer must still prove the bridge key.
        const origin = request.headers.origin;
        const loopback = socket.remoteAddress === "127.0.0.1" || socket.remoteAddress === "::ffff:127.0.0.1";
        if (request.url !== constants.BRIDGE_PATH || (origin !== undefined && origin !== "file://") || !loopback
            || pendingSockets.size >= maxPending || stopping) {
            socket.end("HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n");
            return;
        }
        wss.handleUpgrade(request, socket, head, (ws) => wss.emit("connection", ws));
    });

    wss.on("connection", (socket) => {
        let stage = "hello";
        let clientNonce;
        let serverNonce;
        let received = [];
        pendingSockets.add(socket);
        const helloTimer = setTimeout(() => {
            if (stage === "hello") socket.terminate();
        }, helloMs);
        const handshakeTimer = setTimeout(() => {
            if (stage !== "ready") socket.terminate();
        }, handshakeMs);
        socket.on("error", () => socket.terminate());
        socket.on("close", () => {
            clearTimeout(helloTimer);
            clearTimeout(handshakeTimer);
            pendingSockets.delete(socket);
            if (photoshop === socket) photoshopLost();
        });
        socket.on("message", (data, binary) => {
            try {
                if (binary) throw new Error("bridge.binary");
                const now = Date.now();
                received = received.filter((time) => now - time < 1000);
                if (received.length >= MAX_MESSAGES_PER_SECOND) throw new Error("bridge.rate");
                received.push(now);
                const message = parseRecord(new TextDecoder("utf-8", { fatal: true }).decode(data), MAX_MESSAGE_BYTES);
                if (stage === "hello") {
                    if (!fields(message, ["type", "bridgeVersion", "clientNonce"]) || message.type !== "hello"
                        || message.bridgeVersion !== constants.BRIDGE_VERSION || !isHex64(message.clientNonce)) throw new Error("bridge.hello");
                    clientNonce = message.clientNonce;
                    serverNonce = randomBytes(32).toString("hex");
                    stage = "authenticate";
                    clearTimeout(helloTimer);
                    send(socket, { type: "challenge", serverNonce, proof: bridgeProof(secret, "server", clientNonce, serverNonce) });
                } else if (stage === "authenticate") {
                    if (!fields(message, ["type", "proof"]) || message.type !== "authenticate"
                        || !sameHex(message.proof, bridgeProof(secret, "client", clientNonce, serverNonce)) || photoshop) throw new Error("bridge.authenticate");
                    stage = "ready";
                    clearTimeout(handshakeTimer);
                    pendingSockets.delete(socket);
                    photoshop = socket;
                    send(socket, { type: "ready" });
                    for (const session of sessions.values()) send(socket, startMessage(session));
                    onStatus("Photoshop connected; live sessions restored without replaying commands.");
                } else {
                    onPhotoshopMessage(message);
                }
            } catch {
                socket.terminate();
            }
        });
    });

    await new Promise((resolve, reject) => {
        server.once("error", reject);
        server.listen(port, constants.LISTEN_HOST, () => {
            server.off("error", reject);
            resolve();
        });
    });
    server.on("error", () => onStatus("The bridge listener failed."));

    const handler = {
        /** One session: Photoshop shows it while connected; without Photoshop its face says the app is unavailable. */
        async runSession(session, signal) {
            sessions.set(session.id, session);
            if (!send(photoshop, startMessage(session))) session.fail(Failure.AppUnavailable);
            await new Promise((resolve) => {
                if (signal.aborted) resolve();
                else signal.addEventListener("abort", resolve, { once: true });
            });
            sessions.delete(session.id);
            send(photoshop, { type: "stopSession", sessionId: session.id });
        },

        /** One pick: forwarded to the panel, which runs it in Photoshop. */
        invoke(invocation, signal) {
            const { requestId, session } = invocation;
            if (!photoshop) return { outcome: Outcome.Failed, failure: Failure.AppUnavailable };
            if (invocations.size >= MAX_PENDING_INVOKES) return Outcome.Refused;
            return new Promise((resolve, reject) => {
                invocations.set(requestId, { resolve });
                signal.addEventListener("abort", () => {
                    if (!invocations.delete(requestId)) return;
                    send(photoshop, { type: "cancel", requestId });
                    reject(abortError());
                }, { once: true });
                const sent = send(photoshop, {
                    type: "invoke", requestId, sessionId: session.id, contributionId: session.contributionId, settings: { ...session.settings },
                });
                if (!sent) {
                    invocations.delete(requestId);
                    resolve({ outcome: Outcome.Failed, failure: Failure.AppUnavailable });
                }
            });
        },
    };

    async function close() {
        if (stopping) return;
        stopping = true;
        for (const client of wss.clients) client.terminate();
        await new Promise((resolve) => wss.close(resolve));
        await new Promise((resolve) => server.close(resolve));
    }

    return {
        config: Object.freeze({ bridgeVersion: constants.BRIDGE_VERSION, url: `ws://${constants.LISTEN_HOST}:${server.address().port}${constants.BRIDGE_PATH}`, secret }),
        handler,
        close,
        get photoshopConnected() {
            return photoshop !== null;
        },
    };
}

module.exports = { startBridge, bridgeProof };
