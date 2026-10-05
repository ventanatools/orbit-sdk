"use strict";
const http = require("node:http");
const net = require("node:net");
const { randomBytes, createHmac, timingSafeEqual } = require("node:crypto");
const { WebSocket, WebSocketServer } = require("ws");
const { parseRecordV2, fields, isRequestId, isOutcome, isStart, isInvoke, isFace, sameSettings, ACTION_ID } = require("../uxp/wire-v2.js");
const { bytes32, equalProof, FramedSocket } = require("./protocol.cjs");
const { proofV2, bridgeProofV2 } = require("./protocol-v2.cjs");
const HEX32 = /^[0-9a-f]{64}$/;

function sameHex(left, right) {
    return typeof left === "string" && HEX32.test(left) && timingSafeEqual(Buffer.from(left, "hex"), Buffer.from(right, "hex"));
}

async function startBridgeV2({ pairing, port = 38475, onStatus = () => {}, handshakeMs = 5000, invokeMs = 12000, reconnectMs = 1000 }) {
    if (pairing.protocolVersion !== 2) throw new Error("pairing.version");
    const secret = createHmac("sha256", bytes32(pairing.secret))
        .update("Orbit.Photoshop.Bridge.Key.v2\n").update(randomBytes(32)).digest("hex");
    const server = http.createServer((_, response) => { response.writeHead(404); response.end(); });
    server.headersTimeout = 5000; server.requestTimeout = 5000; server.maxConnections = 8;
    const wss = new WebSocketServer({ noServer: true, maxPayload: 65536, perMessageDeflate: false });
    const pending = new Map(), sessions = new Map();
    let photoshop = null, pipe = null, retry = null, stopping = false, orbitReady = false;

    function sendWebSocket(socket, message) {
        if (!socket || socket.readyState !== WebSocket.OPEN) return false;
        if (socket.bufferedAmount > 65536) { socket.terminate(); return false; }
        socket.send(JSON.stringify(message));
        return true;
    }

    function finish(requestId, outcome) {
        const job = pending.get(requestId);
        if (!job) return;
        pending.delete(requestId);
        clearTimeout(job.timer);
        job.complete(outcome);
    }
    function cancel(requestId) {
        if (!pending.has(requestId)) return;
        sendWebSocket(photoshop, { type: "cancel", requestId });
        finish(requestId, "refused");
    }
    function stopSession(id) {
        sessions.delete(id);
        sendWebSocket(photoshop, { type: "stopSession", sessionId: id });
        for (const [requestId, job] of pending) if (job.sessionId === id) cancel(requestId);
    }
    function dispatch(message) {
        const session = sessions.get(message.sessionId);
        if (!session || session.actionId !== message.actionId || !sameSettings(session.settings, message.settings)) return Promise.resolve("refused");
        if (message.actionId !== ACTION_ID) return Promise.resolve("unsupported");
        if (!photoshop || pending.size >= 32) return Promise.resolve("refused");
        return new Promise(complete => {
            const timer = setTimeout(() => cancel(message.requestId), invokeMs);
            pending.set(message.requestId, { complete, timer, sessionId: message.sessionId });
            if (!sendWebSocket(photoshop, message)) finish(message.requestId, "refused");
        });
    }

    server.on("upgrade", (request, socket, head) => {
        // UXP sends file://; other non-browser clients can omit Origin or use
        // null. These exceptions still require the per-launch HMAC proof.
        const origin = request.headers.origin;
        if (request.url !== "/orbit-photoshop" || (origin && origin !== "null" && origin !== "file://") ||
            wss.clients.size >= 4 || socket.remoteAddress !== "127.0.0.1") {
            socket.end("HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n"); return;
        }
        wss.handleUpgrade(request, socket, head, ws => wss.emit("connection", ws));
    });

    wss.on("connection", socket => {
        let stage = "hello", clientNonce, serverNonce, received = [];
        const timer = setTimeout(() => socket.terminate(), handshakeMs);
        socket.on("error", () => socket.terminate());
        socket.on("close", () => {
            clearTimeout(timer);
            if (photoshop !== socket) return;
            photoshop = null;
            for (const id of pending.keys()) finish(id, "refused");
            if (orbitReady) for (const sessionId of sessions.keys())
                pipe?.send({ type: "face", sessionId, command: { $type: "fail", failure: "Network" } });
            onStatus("Photoshop disconnected; live state is unavailable.");
        });
        socket.on("message", (data, binary) => {
            try {
                if (binary) throw new Error("bridge.binary");
                const now = Date.now();
                received = received.filter(time => now - time < 1000);
                if (received.length >= 128) throw new Error("bridge.rate");
                received.push(now);
                const message = parseRecordV2(new TextDecoder("utf-8", { fatal: true }).decode(data), 65536);
                if (stage === "hello") {
                    if (!fields(message, ["type", "protocolVersion", "clientNonce"]) || message.type !== "hello" ||
                        message.protocolVersion !== 2 || typeof message.clientNonce !== "string" || !HEX32.test(message.clientNonce)) throw new Error("bridge.hello");
                    clientNonce = message.clientNonce; serverNonce = randomBytes(32).toString("hex"); stage = "authenticate";
                    sendWebSocket(socket, { type: "challenge", serverNonce, proof: bridgeProofV2(secret, "server", clientNonce, serverNonce) });
                } else if (stage === "authenticate") {
                    if (!fields(message, ["type", "proof"]) || message.type !== "authenticate" ||
                        !sameHex(message.proof, bridgeProofV2(secret, "client", clientNonce, serverNonce)) || photoshop) throw new Error("bridge.authenticate");
                    stage = "ready"; clearTimeout(timer); photoshop = socket;
                    sendWebSocket(socket, { type: "ready" });
                    for (const session of sessions.values()) sendWebSocket(socket, session);
                    onStatus("Photoshop authenticated; active sessions restored without replaying commands.");
                } else if (message.type === "result") {
                    if (!fields(message, ["type", "requestId", "outcome"]) || !isRequestId(message.requestId) || !isOutcome(message.outcome)) throw new Error("bridge.result");
                    finish(message.requestId, message.outcome);
                } else if (message.type === "face") {
                    if (!isFace(message)) throw new Error("bridge.face");
                    if (orbitReady && sessions.has(message.sessionId)) pipe?.send(message);
                } else throw new Error("bridge.type");
            } catch { socket.terminate(); }
        });
    });

    await new Promise((resolve, reject) => {
        server.once("error", reject);
        server.listen(port, "127.0.0.1", () => { server.off("error", reject); resolve(); });
    });
    server.on("error", () => { onStatus("Bridge listener failed."); void close(); });
    const bridgeConfig = { protocolVersion: 2, url: `ws://127.0.0.1:${server.address().port}/orbit-photoshop`, secret };

    function connectOrbit() {
        if (stopping) return;
        const socket = net.createConnection(`\\\\.\\pipe\\${pairing.pipeName}`);
        const connection = new FramedSocket(socket, { parse: parseRecordV2 });
        pipe = connection;
        const clientNonce = randomBytes(32).toString("base64");
        let stage = "challenge", serverNonce;
        const outstanding = new Set(), seen = new Set();
        const timer = setTimeout(() => connection.destroy(), handshakeMs);
        socket.once("connect", () => connection.send({ type: "hello", protocolVersion: 2, registrationId: pairing.registrationId, clientNonce }));
        connection.on("close", () => {
            clearTimeout(timer); orbitReady = false;
            for (const id of outstanding) cancel(id);
            outstanding.clear();
            for (const id of [...sessions.keys()]) stopSession(id);
            if (!stopping) {
                onStatus("Orbit unavailable; sessions ended. Commands are never replayed.");
                retry = setTimeout(connectOrbit, reconnectMs);
            }
        });
        connection.on("message", message => {
            try {
                if (stage === "challenge") {
                    if (!fields(message, ["type", "serverNonce", "proof"]) || message.type !== "challenge") throw new Error("pipe.challenge");
                    bytes32(message.serverNonce); serverNonce = message.serverNonce;
                    if (!equalProof(message.proof, proofV2(pairing.secret, "server", pairing.registrationId, clientNonce, serverNonce))) throw new Error("pipe.proof");
                    stage = "ready";
                    connection.send({ type: "authenticate", proof: proofV2(pairing.secret, "client", pairing.registrationId, clientNonce, serverNonce) });
                } else if (stage === "ready") {
                    if (!fields(message, ["type"]) || message.type !== "ready") throw new Error("pipe.ready");
                    stage = "connected"; clearTimeout(timer); orbitReady = true;
                    onStatus("Orbit authenticated; waiting for configured instances.");
                } else if (message.type === "startSession") {
                    if (!isStart(message) || sessions.has(message.sessionId) || sessions.size >= 64) throw new Error("pipe.session");
                    sessions.set(message.sessionId, message);
                    if (!sendWebSocket(photoshop, message)) connection.send({ type: "face", sessionId: message.sessionId,
                        command: { $type: "fail", failure: "Network" } });
                } else if (message.type === "stopSession") {
                    if (!fields(message, ["type", "sessionId"]) || !isRequestId(message.sessionId)) throw new Error("pipe.stop");
                    stopSession(message.sessionId);
                } else if (message.type === "invoke") {
                    if (!isInvoke(message) || seen.has(message.requestId) || seen.size >= 4096 || outstanding.size >= 32) throw new Error("pipe.invoke");
                    seen.add(message.requestId); outstanding.add(message.requestId);
                    void dispatch(message).then(outcome => {
                        if (outstanding.delete(message.requestId) && !connection.closed)
                            connection.send({ type: "result", requestId: message.requestId, outcome });
                    });
                } else if (message.type === "cancel") {
                    if (!fields(message, ["type", "requestId"]) || !isRequestId(message.requestId)) throw new Error("pipe.cancel");
                    outstanding.delete(message.requestId); cancel(message.requestId);
                } else throw new Error("pipe.type");
            } catch { connection.destroy(); }
        });
    }

    async function close() {
        if (stopping) return;
        stopping = true; clearTimeout(retry);
        pipe?.destroy();
        for (const id of pending.keys()) cancel(id);
        for (const client of wss.clients) client.terminate();
        await new Promise(resolve => wss.close(resolve));
        await new Promise(resolve => server.close(resolve));
    }
    connectOrbit();
    return { bridgeConfig, close, get orbitReady() { return orbitReady; } };
}
module.exports = { startBridgeV2 };
