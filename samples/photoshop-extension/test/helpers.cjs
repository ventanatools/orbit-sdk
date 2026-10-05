"use strict";
const net = require("node:net");
const { EventEmitter, once } = require("node:events");
const { randomBytes, randomUUID, createHmac } = require("node:crypto");
const { setTimeout: delay } = require("node:timers/promises");

class Inbox extends EventEmitter {
    constructor() { super(); this.messages = []; }
    push(message) { this.messages.push(message); this.emit("changed"); }
    async next(type, timeoutMs = 3000) {
        const deadline = Date.now() + timeoutMs;
        while (true) {
            const index = this.messages.findIndex(message => message.type === type);
            if (index >= 0) return this.messages.splice(index, 1)[0];
            if (Date.now() >= deadline) throw new Error(`Timed out waiting for ${type}`);
            await delay(5);
        }
    }
}

function independentFrame(message) {
    const body = Buffer.from(JSON.stringify(message));
    const result = Buffer.alloc(body.length + 4);
    result.writeUInt32LE(body.length);
    body.copy(result, 4);
    return result;
}

function independentProof(pairing, role, cn, sn) {
    return createHmac("sha256", Buffer.from(pairing.secret, "base64"))
        .update(["Orbit.Extensions.v2", role, pairing.registrationId, cn, sn].join("\n")).digest("base64");
}

async function fakeOrbit({ badProof = false, fragment = false } = {}) {
    const id = randomUUID().replaceAll("-", "");
    const pairing = { protocolVersion: 2, pipeName: `Orbit.Extensions.v2.test.0123456789abcdef.${id}`,
        registrationId: id, extensionId: "example.photoshop", secret: randomBytes(32).toString("base64") };
    const inbox = new Inbox();
    const sockets = new Set();
    let active;
    let connections = 0;
    const server = net.createServer(socket => {
        connections++;
        sockets.add(socket);
        socket.on("error", () => {});
        socket.on("close", () => { sockets.delete(socket); inbox.push({ type: "disconnected" }); });
        let bytes = Buffer.alloc(0);
        let clientNonce;
        const serverNonce = randomBytes(32).toString("base64");
        function send(message) {
            const frame = independentFrame(message);
            if (fragment && message.type === "challenge") {
                socket.write(frame.subarray(0, 2));
                setImmediate(() => { socket.write(frame.subarray(2, 7)); socket.write(frame.subarray(7)); });
            } else socket.write(frame);
        }
        socket.on("data", chunk => {
            bytes = Buffer.concat([bytes, chunk]);
            while (bytes.length >= 4 && bytes.length >= 4 + bytes.readUInt32LE()) {
                const length = bytes.readUInt32LE();
                const message = JSON.parse(bytes.subarray(4, 4 + length).toString("utf8"));
                bytes = bytes.subarray(4 + length);
                if (message.type === "hello") {
                    clientNonce = message.clientNonce;
                    send({ type: "challenge", serverNonce, proof: badProof ? Buffer.alloc(32).toString("base64") :
                        independentProof(pairing, "server", clientNonce, serverNonce) });
                } else if (message.type === "authenticate") {
                    if (message.proof !== independentProof(pairing, "client", clientNonce, serverNonce)) socket.destroy();
                    else { active = socket; send({ type: "ready" }); inbox.push({ type: "authenticated" }); }
                } else inbox.push(message);
            }
        });
    });
    server.listen(`\\\\.\\pipe\\${pairing.pipeName}`);
    await once(server, "listening");
    return { pairing, inbox, get connections() { return connections; },
        send: message => active.write(independentFrame(message)),
        disconnect: () => active.destroy(),
        close: async () => { for (const socket of sockets) socket.destroy(); await new Promise(resolve => server.close(resolve)); } };
}

function fakePhotoshop(options = {}) {
    let mutations = 0;
    let visible = true;
    let suspension = null;
    const history = [], historyEvents = [];
    const layer = { id: 2 };
    Object.defineProperty(layer, "visible", {
        get: () => visible, set: value => {
            mutations++;
            historyEvents.push({ type: "mutation", value, suspended: suspension !== null });
            visible = value;
            if (options.failAfterMutation) throw new Error("simulated setter failure");
        }
    });
    const document = { id: 1, activeLayers: [layer] };
    const app = { documents: [document], activeDocument: document };
    function finishHistory(commit, automatic) {
        const state = suspension;
        if (!state) throw new Error("no active history suspension");
        historyEvents.push({ type: automatic ? "autoResume" : "resume", commit });
        if (commit && visible !== state.before)
            history.push({ documentID: state.documentID, name: state.name, before: state.before, after: visible });
        if (!commit) visible = state.before;
        suspension = null;
    }
    const context = { isCancelled: false, hostControl: {
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
        }
    } };
    const photoshop = { app, core: { executeAsModal: async callback => {
        try {
            const result = await callback(context);
            if (suspension) finishHistory(true, true);
            return result;
        } catch (error) {
            if (suspension) finishHistory(false, true);
            throw error;
        }
    } } };
    return { photoshop, layer, document, context, history, historyEvents, get mutations() { return mutations; } };
}

module.exports = { Inbox, independentFrame, independentProof, fakeOrbit, fakePhotoshop, delay };
