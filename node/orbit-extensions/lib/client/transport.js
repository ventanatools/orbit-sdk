// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * Transports. The companion transport is a Windows named pipe (contract §7.1). Node's
 * net.connect can neither verify who owns a pipe nor request identification-level impersonation,
 * so every server is unverified until its challenge proof verifies (contract §10).
 */

const net = require("node:net");
const { Duplex } = require("node:stream");

const CONNECT_TIMEOUT_MS = 5000;

function failureFor(error) {
    switch (error && error.code) {
        case "EACCES":
        case "EPERM":
            // The pipe's access-control list does not admit the current user: not the host's pipe.
            return "auth.server-unverified";
        case "EBUSY":
            return "host.pipe-busy";
        default:
            return "host.not-running";
    }
}

/** The named-pipe transport for `pipeName` (without the \\.\pipe\ prefix). */
function pipeTransport(pipeName) {
    return {
        connect(signal) {
            return new Promise((resolve) => {
                if (signal && signal.aborted) {
                    resolve({ failure: undefined });
                    return;
                }
                const socket = net.connect({ path: "\\\\.\\pipe\\" + pipeName });
                let done = false;
                const finish = (result, destroy) => {
                    if (done) return;
                    done = true;
                    clearTimeout(timer);
                    socket.removeListener("connect", onConnect);
                    socket.removeListener("error", onError);
                    if (signal) signal.removeEventListener("abort", onAbort);
                    if (destroy) {
                        socket.on("error", () => {});
                        socket.destroy();
                    }
                    resolve(result);
                };
                const onConnect = () => finish({ socket }, false);
                const onError = (error) => finish({ failure: failureFor(error) }, true);
                const onAbort = () => finish({ failure: undefined }, true);
                const timer = setTimeout(() => finish({ failure: "host.pipe-busy" }, true), CONNECT_TIMEOUT_MS);
                socket.once("connect", onConnect);
                socket.once("error", onError);
                if (signal) signal.addEventListener("abort", onAbort, { once: true });
            });
        },
    };
}

/**
 * Two connected in-memory duplex streams: what one writes, the other reads. Ending or destroying
 * one ends the other after it has read everything written before.
 */
function duplexPair() {
    const sides = [];
    const ended = [false, false];
    const endOf = (index) => {
        const side = sides[index];
        if (!ended[index] && !side.destroyed) {
            ended[index] = true;
            side.push(null);
        }
    };
    const make = (index) => {
        const other = 1 - index;
        const side = new Duplex({
            allowHalfOpen: false,
            read() {},
            write(chunk, encoding, callback) {
                if (!ended[other] && !sides[other].destroyed) sides[other].push(Buffer.from(chunk));
                callback();
            },
            final(callback) {
                endOf(other);
                callback();
            },
        });
        side.once("close", () => endOf(other));
        return side;
    };
    sides.push(make(0), make(1));
    return sides;
}

module.exports = { pipeTransport, duplexPair, CONNECT_TIMEOUT_MS };
