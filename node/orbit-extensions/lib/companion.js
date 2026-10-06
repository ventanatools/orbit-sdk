// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

"use strict";

/**
 * runCompanion (contract §9.2 CompanionApp, §10): finds extension.json and the pairing file,
 * prints status lines, watches both files, handles Ctrl+C, and returns the exit code: 0 stopped
 * by Ctrl+C or the signal; 1 unexpected; 2 usage; 3 a file is missing or invalid with watchFiles
 * off; 4 Stopped with watchFiles off.
 */

const fs = require("node:fs");
const path = require("node:path");
const { readManifestFile } = require("./manifest.js");
const { readPairingFile, defaultPairingPath } = require("./pairing.js");
const { computeManifestHash } = require("./hash.js");
const { findHost, firstActiveHost } = require("./hosts.js");
const { reasonInfo, isKnownReason, helpUri } = require("./codes.js");
const { isHostId } = require("./text.js");
const { stampOf } = require("./files.js");
const { systemClock } = require("./wire/limits.js");
const { ContributionRouter } = require("./router.js");
const { CompanionClient, kInternals, kPeerMessage, kRetryNow, kSubscriberFaulted } = require("./client/client.js");

const MANIFEST_FILE = "extension.json";
const PAIRING_SUFFIX = ".pairing.json";
const POLL_MS = 1000;

/** The internal seams of runCompanion (transport and clock); used by tests. */
const kAppInternals = Symbol("appInternals");

class UsageError extends Error {}

/** Parses --manifest <path>, --pairing <path> and --verbose; every other argument is left for the author. */
function parseArguments(args) {
    if (!Array.isArray(args)) throw new TypeError("args must be an array of strings.");
    let manifestPath;
    let pairingPath;
    let verbose = false;
    const remaining = [];
    for (let i = 0; i < args.length; i++) {
        const arg = args[i];
        if (arg === "--manifest" || arg === "--pairing") {
            if (i + 1 >= args.length) throw new UsageError(arg + " needs a value.");
            i++;
            if (arg === "--manifest") manifestPath = args[i];
            else pairingPath = args[i];
        } else if (arg === "--verbose") {
            verbose = true;
        } else {
            remaining.push(arg);
        }
    }
    return Object.freeze({ manifestPath, pairingPath, verbose, remaining: Object.freeze(remaining) });
}

function stateWord(state) {
    switch (state) {
        case "Connecting": return "connecting";
        case "Connected": return "connected";
        case "Waiting": return "waiting";
        case "Stopped": return "stopped";
        default: return "not started";
    }
}

function appendFix(line, code, hostId, hostName) {
    const info = reasonInfo(code);
    let text = line;
    if (info.fix) text += " " + (hostName ? info.fix.split("the host").join(hostName) : info.fix);
    if (hostId && isHostId(hostId)) {
        const help = helpUri(code, hostId);
        if (help) text += " " + help;
    }
    return text;
}

/** "ventana: <state> (<code>) <fix> <help link>" */
function statusLine(state, reason, hostId, hostName) {
    const line = "ventana: " + stateWord(state);
    return reason === undefined ? line : appendFix(line + " (" + reason + ")", reason, hostId, hostName);
}

/** "ventana: fault <Kind> in <contribution-id> (<code>) <fix> <help link>" */
function faultLine(fault, hostId, hostName) {
    const code = fault.kind === "IgnoredCancellation" ? "session.handler-stalled" : fault.kind === "SessionCapacity" ? "session.capacity" : "session.handler-faulted";
    return appendFix("ventana: fault " + fault.kind + " in " + fault.contributionId + " (" + code + ")", code, hostId, hostName);
}

function printableAscii(text) {
    return [...String(text)].filter((c) => c >= " " && c <= "~").slice(0, 256).join("");
}

function abortError() {
    const error = new Error("The operation was aborted.");
    error.name = "AbortError";
    return error;
}

class AppRunner {
    constructor(args, handler, options, output, internals) {
        this.args = args;
        this.handler = handler;
        this.options = options;
        this.output = output;
        this.internals = internals;
        this.clock = internals.clock || systemClock;
        this.watchFiles = options.watchFiles !== false;
        this.entryDirectory = internals.entryDirectory || entryDirectory();
        this.subscriberReported = false;
        this.unmappedPrinted = false;
    }

    line(text) {
        try {
            this.output.write(text + "\n");
        } catch {
            // An output that fails must not stop the companion.
        }
    }

    delay(ms, signal) {
        return new Promise((resolve, reject) => {
            if (signal.aborted) {
                reject(abortError());
                return;
            }
            const onAbort = () => {
                this.clock.clearTimeout(timer);
                reject(abortError());
            };
            const timer = this.clock.setTimeout(() => {
                signal.removeEventListener("abort", onAbort);
                resolve();
            }, ms);
            signal.addEventListener("abort", onAbort, { once: true });
        });
    }

    async waitForChange(paths, initial, signal) {
        while (true) {
            await this.delay(POLL_MS, signal);
            if (paths.some((file, i) => stampOf(file) !== initial[i])) return;
        }
    }

    async run(signal) {
        const manifestPath = this.options.manifestPath || this.args.manifestPath || this.findManifest();
        while (true) {
            const loaded = await this.loadManifest(manifestPath, signal);
            if (!loaded) return 3;
            this.printUnmapped(loaded.manifest);
            const found = await this.loadPairing(loaded.manifest, manifestPath, signal);
            if (!found) return 3;
            let end;
            try {
                end = await this.runClient(loaded.manifest, manifestPath, loaded.stamp, found, signal);
            } finally {
                found.pairing.dispose();
            }
            if (end === "Cancelled") return 0;
            if (end === "Stopped" && !this.watchFiles) return 4;
            if (end === "Stopped") await this.waitForChange([found.path, manifestPath], [found.stamp, loaded.stamp], signal);
        }
    }

    findManifest() {
        const candidates = [path.join(this.entryDirectory, MANIFEST_FILE), path.join(process.cwd(), MANIFEST_FILE)];
        return candidates.find((file) => fs.existsSync(file)) || candidates[0];
    }

    async readManifest(file, report) {
        let read;
        try {
            read = await readManifestFile(file);
        } catch (error) {
            if (report) this.line("ventana: cannot read the manifest " + file + " (" + (error && error.code ? error.code : "error") + ")");
            return undefined;
        }
        if (read.manifest) return read.manifest;
        if (report) {
            for (const diagnostic of read.diagnostics.filter((d) => d.severity === "Error")) this.line("ventana: " + file + ": " + diagnostic.toString());
        }
        return undefined;
    }

    async loadManifest(file, signal) {
        while (true) {
            // Stamped before reading, so a write that lands during the read is still a change.
            const stamp = stampOf(file);
            const manifest = await this.readManifest(file, true);
            if (manifest) return { manifest, stamp };
            if (!this.watchFiles) return undefined;
            this.line("ventana: watching " + file);
            await this.waitForChange([file], [stamp], signal);
        }
    }

    printUnmapped(manifest) {
        if (this.unmappedPrinted || !(this.handler instanceof ContributionRouter)) return;
        this.unmappedPrinted = true;
        for (const id of this.handler.findUnmapped(manifest)) this.line("ventana: no handler is mapped for " + id);
    }

    pairingCandidates(manifest) {
        const candidates = [];
        for (const id of manifest.hosts) {
            const host = findHost(id);
            if (host && host.status === "Active") candidates.push(defaultPairingPath(id, manifest.id));
        }
        candidates.push(path.join(this.entryDirectory, manifest.id + PAIRING_SUFFIX));
        candidates.push(path.join(process.cwd(), manifest.id + PAIRING_SUFFIX));
        const seen = new Set();
        return candidates.filter((file) => {
            const key = file.toLowerCase();
            if (seen.has(key)) return false;
            seen.add(key);
            return true;
        });
    }

    async loadPairing(manifest, manifestPath, signal) {
        const explicit = this.options.pairingPath || this.args.pairingPath;
        const candidates = explicit ? [explicit] : this.pairingCandidates(manifest);
        const watchedPaths = [...candidates, manifestPath];
        let watched = candidates[0];
        const hostId = hostIdFor(manifest, undefined);
        while (true) {
            // Stamped before looking, so a pairing saved while it is being read is still a change.
            const stamps = watchedPaths.map(stampOf);
            const index = candidates.findIndex((file) => {
                try {
                    return fs.statSync(file).isFile();
                } catch {
                    return false;
                }
            });
            const file = index >= 0 ? candidates[index] : undefined;
            let code = "pairing.missing";
            let detail;
            if (file !== undefined) {
                try {
                    const read = await readPairingFile(file, { expectedExtensionId: manifest.id, manifestHosts: manifest.hosts });
                    if (read.pairing) return { pairing: read.pairing, path: file, stamp: stamps[index] };
                    const first = read.diagnostics.find((d) => d.severity === "Error");
                    if (first && isKnownReason(first.code)) code = first.code;
                    else if (first) detail = first.toString();
                } catch {
                    // An unreadable file waits for a change like an invalid one.
                }
                watched = file;
            }
            const state = this.watchFiles ? "Waiting" : "Stopped";
            this.line(statusLine(state, code, hostId, hostNameFor(manifest, undefined)) + (detail ? " " + detail : ""));
            if (!this.watchFiles) return undefined;
            this.line("ventana: watching " + watched);
            await this.waitForChange(watchedPaths, stamps, signal);
        }
    }

    async runClient(manifest, manifestPath, manifestStamp, found, signal) {
        const run = new AbortController();
        const onOuterAbort = () => run.abort();
        signal.addEventListener("abort", onOuterAbort, { once: true });
        let current = manifest;
        const hostId = hostIdFor(manifest, found.pairing);
        const hostName = hostNameFor(manifest, found.pairing);
        let waitingOnPerson = false;
        let restart = false;
        const client = new CompanionClient({ pairing: found.pairing, manifest, handler: this.handler, retry: this.options.retry });
        const internals = client[kInternals];
        if (this.internals.transportFor) internals.transport = this.internals.transportFor(found.pairing);
        if (this.internals.clock) internals.clock = this.internals.clock;
        internals.reloadManifest = async () => {
            const reread = await this.readManifest(manifestPath, false);
            if (!reread || computeManifestHash(reread) === computeManifestHash(current)) return undefined;
            current = reread;
            return reread;
        };
        client.on("status", (status) => {
            waitingOnPerson = status.state === "Waiting" && (status.reason === "manifest.mismatch" || status.reason === "auth.identity-changed");
            this.line(statusLine(status.state, status.reason, hostId, hostName));
            if (this.args.verbose && status[kPeerMessage] !== undefined) this.line("ventana: host message: " + printableAscii(status[kPeerMessage]));
            if (typeof this.options.onStatus === "function") this.options.onStatus(status);
        });
        client.on("handlerFaulted", (fault) => {
            this.line(faultLine(fault, hostId, hostName));
            if (fault.error !== undefined) {
                const error = fault.error;
                this.line(error && error.stack ? String(error.stack) : String(error));
            }
            if (typeof this.options.onHandlerFaulted === "function") this.options.onHandlerFaulted(fault);
        });
        client[kSubscriberFaulted] = (error) => {
            if (this.subscriberReported) return;
            this.subscriberReported = true;
            const name = error && error.name ? error.name : "Error";
            const message = error && error.message ? error.message : String(error);
            this.line("ventana: a status or fault callback threw " + name + ": " + message);
        };

        const watchStop = new AbortController();
        const watch = this.watchFiles
            ? this.watch(found, manifestPath, manifestStamp, () => waitingOnPerson, client, () => {
                restart = true;
                run.abort();
            }, watchStop.signal).catch(() => {})
            : Promise.resolve();
        try {
            await client.run(run.signal);
        } finally {
            watchStop.abort();
            await watch;
            signal.removeEventListener("abort", onOuterAbort);
        }
        if (signal.aborted) return "Cancelled";
        return restart ? "Restart" : "Stopped";
    }

    async watch(found, manifestPath, manifestStamp, waitingOnPerson, client, restart, signal) {
        let manifest = manifestStamp;
        while (!signal.aborted) {
            await this.delay(POLL_MS, signal);
            if (!waitingOnPerson()) continue;
            if (stampOf(found.path) !== found.stamp) {
                restart();
                return;
            }
            const next = stampOf(manifestPath);
            if (next !== manifest) {
                manifest = next;
                client[kRetryNow]();
            }
        }
    }
}

function hostIdFor(manifest, pairing) {
    if (pairing) return pairing.hostId;
    const active = firstActiveHost(manifest.hosts);
    return active ? active.id : manifest.hosts[0];
}

function hostNameFor(manifest, pairing) {
    if (pairing) {
        const host = findHost(pairing.hostId);
        return host ? host.displayName : pairing.hostId;
    }
    const active = firstActiveHost(manifest.hosts);
    return active ? active.displayName : manifest.hosts[0];
}

/** The directory of the entry module (require.main), else of the script, else the current directory. */
function entryDirectory() {
    if (require.main && typeof require.main.filename === "string") return path.dirname(require.main.filename);
    if (process.argv[1]) return path.dirname(path.resolve(process.argv[1]));
    return process.cwd();
}

/**
 * Runs a companion: `args` are the process arguments (usually process.argv.slice(2)), `handler`
 * the contribution handler. Resolves with the exit code.
 */
async function runCompanion(args, handler, options = {}) {
    const opts = options || {};
    const output = opts.output && typeof opts.output.write === "function" ? opts.output : process.stderr;
    const internals = opts[kAppInternals] || {};
    let parsed;
    try {
        parsed = parseArguments(args);
    } catch (error) {
        if (!(error instanceof UsageError)) throw error;
        output.write("ventana: " + error.message + "\n");
        return 2;
    }
    if (!handler || typeof handler !== "object") throw new TypeError("handler must be an object with runSession and/or invoke.");
    const stop = new AbortController();
    const onSignal = () => stop.abort();
    if (opts.signal) {
        if (opts.signal.aborted) stop.abort();
        else opts.signal.addEventListener("abort", onSignal, { once: true });
    }
    const hookSigint = internals.hookSigint !== false;
    if (hookSigint) process.on("SIGINT", onSignal);
    try {
        return await new AppRunner(parsed, handler, opts, output, internals).run(stop.signal);
    } catch (error) {
        if (stop.signal.aborted) return 0;
        output.write("ventana: unexpected " + (error && error.name ? error.name : "Error") + ": " + (error && error.message ? error.message : String(error)) + "\n");
        if (error && error.stack) output.write(String(error.stack) + "\n");
        return 1;
    } finally {
        if (hookSigint) process.removeListener("SIGINT", onSignal);
        if (opts.signal) opts.signal.removeEventListener("abort", onSignal);
    }
}

module.exports = { runCompanion, parseArguments, statusLine, faultLine, kAppInternals };
