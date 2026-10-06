// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

/// <reference types="node" />

import { EventEmitter } from "node:events";

/** result.outcome tokens (contract §7.8). */
export declare const Outcome: Readonly<{ Done: "Done"; Refused: "Refused"; Failed: "Failed"; Unsupported: "Unsupported" }>;
export type Outcome = (typeof Outcome)[keyof typeof Outcome];

/** face.state tokens (contract §7.7.1). */
export declare const FaceState: Readonly<{ None: "None"; Playing: "Playing"; Paused: "Paused"; On: "On"; Off: "Off" }>;
export type FaceState = (typeof FaceState)[keyof typeof FaceState];

/** The failure tokens of protocol 3 (contract §7.8). */
export declare const Failure: Readonly<{
    UnsupportedInput: "UnsupportedInput";
    NeedsSetup: "NeedsSetup";
    Network: "Network";
    NoResult: "NoResult";
    AppUnavailable: "AppUnavailable";
}>;
export type Failure = (typeof Failure)[keyof typeof Failure];

/** What Session.setFace, clearFace and fail return. */
export declare const PublishResult: Readonly<{ Accepted: "Accepted"; SessionEnded: "SessionEnded" }>;
export type PublishResult = (typeof PublishResult)[keyof typeof PublishResult];

/** Where a CompanionClient is. */
export declare const ConnectionState: Readonly<{ NotStarted: "NotStarted"; Connecting: "Connecting"; Connected: "Connected"; Waiting: "Waiting"; Stopped: "Stopped" }>;
export type ConnectionState = (typeof ConnectionState)[keyof typeof ConnectionState];

/** How an author's handler misbehaved. */
export declare const HandlerFault: Readonly<{ Exception: "Exception"; IgnoredCancellation: "IgnoredCancellation"; SessionCapacity: "SessionCapacity"; InvalidResult: "InvalidResult" }>;
export type HandlerFault = (typeof HandlerFault)[keyof typeof HandlerFault];

export declare const NetworkUse: Readonly<{ None: "None"; LocalNetwork: "LocalNetwork"; Internet: "Internet" }>;
export declare const SettingKind: Readonly<{ Choice: "Choice" }>;
export declare const Provides: Readonly<{ Invoke: "invoke"; Face: "face" }>;

// ---------------------------------------------------------------- declarations (contract §3)

export interface SettingChoice {
    readonly value: string;
    readonly name: string;
}

export interface Setting {
    readonly id: string;
    readonly kind: "Choice";
    readonly name: string;
    readonly description?: string;
    readonly default: string;
    readonly choices: readonly SettingChoice[];
}

export interface Contribution {
    readonly id: string;
    readonly name: string;
    readonly description: string;
    readonly glyph: string;
    readonly provides: readonly ("invoke" | "face")[];
    readonly settings?: readonly Setting[];
}

export interface Manifest {
    readonly $schema?: string;
    readonly schemaVersion: 3;
    readonly id: string;
    readonly name: string;
    readonly description: string;
    readonly version: string;
    readonly hosts: readonly string[];
    readonly glyph?: string;
    readonly publisher?: { readonly name: string; readonly url?: string };
    readonly supportUrl?: string;
    readonly defaultLanguage?: string;
    readonly disclosures?: { readonly network: "None" | "LocalNetwork" | "Internet"; readonly privacyUrl?: string };
    readonly requires?: { readonly capabilities: readonly string[] };
    readonly contributions: readonly Contribution[];
}

/** A finding about a static file (contract §4.1). The message never contains a value from the file. */
export declare class Diagnostic {
    readonly code: string;
    readonly path: string;
    readonly message: string;
    readonly severity: "Error" | "Warning";
    readonly file?: string;
    readonly line?: number;
    readonly column?: number;
    /** "<code> <path>: <message>" */
    toString(): string;
}

export interface HostInfo {
    readonly id: string;
    readonly displayName: string;
    readonly packageExtension?: string;
    readonly aliases: readonly string[];
    readonly status: "Active" | "Reserved";
}

export interface ManifestReadOptions {
    /** The reading host's id: enables manifest.host-not-listed. */
    hostId?: string;
    /** The capabilities the reading host implements: enables requires.capability-unsupported. */
    supportedCapabilities?: readonly string[];
    allowExperimentalCapabilities?: boolean;
    origin?: "FirstParty" | "ThirdParty";
    reservedPublishers?: readonly string[];
    /** The known hosts, for manifest.host-unknown and schema.uri-mismatch (default: the registry). */
    knownHosts?: readonly HostInfo[];
}

export interface ManifestReadResult {
    /** The manifest, frozen; undefined whenever any diagnostic is an error. */
    manifest?: Manifest;
    diagnostics: readonly Diagnostic[];
}

/** Reads a manifest from its UTF-8 bytes. */
export declare function readManifest(bytes: Uint8Array, options?: ManifestReadOptions): ManifestReadResult;
/** Reads extension.json, never more than 65,536 + 1 bytes; file-system failures reject. */
export declare function readManifestFile(path: string, options?: ManifestReadOptions): Promise<ManifestReadResult>;
/** Validates a manifest built in code; diagnostics carry no line or column. */
export declare function validateManifest(manifest: unknown, options?: ManifestReadOptions): ManifestReadResult;
/** The manifest hash (contract §B.2): 64 lowercase hexadecimal digits. */
export declare function computeManifestHash(manifest: Manifest): string;

// ---------------------------------------------------------------- pairing (contract §6)

export interface HandshakeTranscript {
    hostId: string;
    hostVersion: string;
    registrationId: string;
    clientNonce: string;
    serverNonce: string;
    version: number;
    minVersion: number;
    maxVersion: number;
    clientCapabilities: readonly string[];
    hostCapabilities: readonly string[];
    manifestHash: string;
}

/** A pairing: the credential for one registration. Whoever reads it owns it and disposes it. */
export declare class Pairing {
    readonly pairingVersion: 3;
    readonly mode: "Persistent";
    readonly hostId: string;
    readonly pipeName: string;
    readonly registrationId: string;
    readonly extensionId: string;
    readonly disposed: boolean;
    computeProof(transcript: HandshakeTranscript, role: "server" | "client"): string;
    verifyProof(proof: string | undefined, transcript: HandshakeTranscript, role: "server" | "client"): boolean;
    /** Zeroes the secret. */
    dispose(): void;
    /** Never includes the secret. */
    toString(): string;
}

export interface PairingReadOptions {
    expectedExtensionId: string;
    manifestHosts?: readonly string[];
}

export interface PairingReadResult {
    pairing?: Pairing;
    diagnostics: readonly Diagnostic[];
}

export declare function readPairing(bytes: Uint8Array, options: PairingReadOptions): PairingReadResult;
/** Reads a pairing file, never more than 4,096 + 1 bytes; file-system failures reject. */
export declare function readPairingFile(path: string, options: PairingReadOptions): Promise<PairingReadResult>;
/** %USERPROFILE%\.ventana\pairings\<host-id>\<extension-id>.pairing.json (contract §6.6). */
export declare function defaultPairingPath(hostId: string, extensionId: string): string;

// ---------------------------------------------------------------- the host registry (contract §2.3)

export declare const hosts: readonly HostInfo[];
export declare const reservedHostIds: readonly string[];
export declare function findHost(id: string, knownHosts?: readonly HostInfo[]): HostInfo | undefined;
export declare function firstActiveHost(hostIds: readonly string[], knownHosts?: readonly HostInfo[]): HostInfo | undefined;
export declare const reservedPublishers: readonly string[];
/** null when valid, else id.required, id.grammar, id.root-not-dotted, id.root-dotted, id.root-reserved or id.too-long. */
export declare function classifyId(id: string, origin?: "FirstParty" | "ThirdParty", reservedPublishers?: readonly string[]): string | null;

// ---------------------------------------------------------------- reason codes (contract §8)

export interface ReasonCodeInfo {
    readonly code?: string;
    readonly disposition: "Close" | "Advisory" | "Refuse" | "Ignore" | "Local";
    readonly seenIn: readonly string[];
    readonly pre: boolean;
    readonly violation: boolean;
    readonly meaning?: string;
    readonly fix?: string;
    readonly anchor?: string;
}

export declare const ReasonCodes: Readonly<Record<string, ReasonCodeInfo>>;
/** The catalog's information, or the rules for an unknown code (close; never terminal, a violation or advisory). */
export declare function reasonInfo(code: string): ReasonCodeInfo;
/** https://dev.ventana.tools/go/<host-id>/codes#<anchor> */
export declare function helpUri(code: string, hostId: string): string | undefined;

/** null when valid, else text.empty, string.too-long, text.whitespace or text.invalid-character (contract §3.6). */
export declare function checkDeclarationText(value: string, maxUnits: number): string | null;
/** Face text cleaning (contract §7.7.3); null when nothing remains. */
export declare function cleanText(text: string, maxElements: number, maxUnits: number): string | null;
export declare function isGlyph(value: unknown): boolean;

// ---------------------------------------------------------------- the companion (contract §9.2, §10)

/** A face, as setFace takes it. Text is cleaned with the display limits before sending; it never throws because of what the text says. */
export interface FaceInit {
    line1?: string;
    line2?: string;
    /** The sentence assistive technology reads; defaults to line1's text in the host. */
    detail?: string;
    /** One private-use glyph, written as "\uXXXX". */
    glyph?: string;
    state?: FaceState;
    /** 1 to 86,400; rounded up to whole seconds. */
    goodForSeconds: number;
    /** Republish at 80% of the lifetime while the session handler runs, for at most a day. */
    renew?: boolean;
}

export interface Session {
    readonly id: string;
    readonly contributionId: string;
    readonly settings: Readonly<Record<string, string>>;
    readonly uiLanguage: string;
    readonly provides: readonly ("invoke" | "face")[];
    readonly isActive: boolean;
    /** The connection's effective capabilities. */
    readonly hostCapabilities: readonly string[];
    supports(capabilityId: string): boolean;
    setFace(face: FaceInit): PublishResult;
    clearFace(): PublishResult;
    fail(failure: Failure): PublishResult;
}

export interface Invocation {
    readonly requestId: string;
    readonly session: Session;
}

export type InvokeResult = Outcome | { outcome: Outcome; failure?: Failure };

export interface Handler {
    runSession?(session: Session, signal: AbortSignal): Promise<void> | void;
    invoke?(invocation: Invocation, signal: AbortSignal): Promise<InvokeResult> | InvokeResult;
}

export interface RetryOptions {
    initialMs?: number;
    maxMs?: number;
    hostAbsentMaxMs?: number;
    jitter?: number;
    stableMs?: number;
}

export interface StatusEvent {
    readonly state: ConnectionState;
    readonly reason?: string;
    readonly retryInMs?: number;
    readonly attempt: number;
    readonly serverVerified: boolean;
    /** The host's id and version from its challenge; present only once the challenge proof verified. */
    readonly host?: { readonly id: string; readonly version: string };
    /** The protocol version negotiated with the host (contract §7.3); present only with `host`. */
    readonly negotiatedVersion?: number;
}

export interface HandlerFaultedEvent {
    readonly kind: HandlerFault;
    readonly contributionId: string;
    readonly sessionId?: string;
    readonly requestId?: string;
    readonly error?: unknown;
}

export declare class CompanionClient extends EventEmitter {
    constructor(options: { pairing: Pairing; manifest: Manifest; handler: Handler; retry?: RetryOptions });
    readonly state: ConnectionState;
    /** Connects and serves the host until the signal is aborted or the client is Stopped. */
    run(signal: AbortSignal): Promise<void>;
    on(event: "status", listener: (status: StatusEvent) => void): this;
    on(event: "handlerFaulted", listener: (fault: HandlerFaultedEvent) => void): this;
    on(event: string | symbol, listener: (...args: any[]) => void): this;
}

export declare class ContributionRouter implements Handler {
    mapSession(contributionId: string, run: (session: Session, signal: AbortSignal) => Promise<void> | void): this;
    mapInvoke(contributionId: string, invoke: (invocation: Invocation, signal: AbortSignal) => Promise<InvokeResult> | InvokeResult): this;
    map(contributionId: string, handler: Handler): this;
    findUnmapped(manifest: Manifest): string[];
    runSession(session: Session, signal: AbortSignal): Promise<void> | void;
    invoke(invocation: Invocation, signal: AbortSignal): Promise<InvokeResult> | InvokeResult;
}

export interface RunOptions {
    /** Overrides --manifest. */
    manifestPath?: string;
    /** Overrides --pairing. */
    pairingPath?: string;
    /** Watch the pairing file and extension.json (default true). */
    watchFiles?: boolean;
    /** Where status lines go (default process.stderr). */
    output?: { write(text: string): unknown };
    onStatus?: (status: StatusEvent) => void;
    onHandlerFaulted?: (fault: HandlerFaultedEvent) => void;
    retry?: RetryOptions;
    /** Stops the companion; it then resolves with 0. */
    signal?: AbortSignal;
}

export interface CompanionArguments {
    readonly manifestPath?: string;
    readonly pairingPath?: string;
    readonly verbose: boolean;
    readonly remaining: readonly string[];
}

/** Same arguments, discovery and exit codes as CompanionApp: 0 stopped, 1 unexpected, 2 usage, 3 a file is missing or invalid with watchFiles off, 4 Stopped with watchFiles off. */
export declare function runCompanion(args: string[], handler: Handler, options?: RunOptions): Promise<number>;
export declare function parseArguments(args: string[]): CompanionArguments;
