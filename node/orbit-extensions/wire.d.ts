// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

/// <reference types="node" />

import type { HandshakeTranscript, ReasonCodeInfo } from "@ventanatools/orbit-extensions";

export declare const PROTOCOL_MIN: 3;
export declare const PROTOCOL_MAX: 3;
/** The transcript label and transport generation: "Ventana.Extensions.v3". */
export declare const LABEL: string;
/** "Ventana.Extensions.v3." */
export declare const PIPE_PREFIX: string;
export declare const MAX_FRAME_BYTES: 65536;
export declare const MAX_HANDSHAKE_FRAME_BYTES: 8192;

export type Sender = "Host" | "Companion";
export type ConnectionPhase = "Handshake" | "PeerVerified" | "Authenticated";

/** A protocol-3 message as a plain object, as on the wire (contract §7.5). */
export interface WireMessage {
    readonly type: string;
    readonly [member: string]: unknown;
}

export interface MessageReadResult {
    /** The message, when the frame is valid. */
    message?: WireMessage;
    /** The code to close with, when the frame is refused. */
    violation?: string;
    /** Why the frame was discarded (protocol.type-unknown), when it is ignored. */
    ignored?: string;
}

/** Reads one frame body; never throws for any content. A string is encoded as UTF-8. */
export declare function readMessage(frame: Uint8Array | string, sender: Sender, phase: ConnectionPhase): MessageReadResult;
/** Writes one message canonically: compact, type first, every character outside printable ASCII escaped. */
export declare function writeMessage(message: WireMessage): Buffer;
/** The frame of a message object (written canonically) or of a frame body. */
export declare function encodeFrame(record: WireMessage | Uint8Array): Buffer;

export declare class FrameReader {
    constructor(options: {
        maxFrameBytes?: number;
        onFrame(body: Buffer): void;
        onError(code: "frame.too-large" | "frame.timeout"): void;
        clock?: Clock;
    });
    readonly inFrame: boolean;
    setMaxFrameBytes(value: number): void;
    push(chunk: Buffer): void;
    close(): void;
}

export interface Clock {
    now(): number;
    setTimeout(callback: () => void, ms: number): unknown;
    clearTimeout(handle: unknown): void;
}

export declare function newNonce(): string;
export declare function negotiate(clientMin: number, clientMax: number, hostMin: number, hostMax: number): number | null;
export declare function transcriptBytes(transcript: HandshakeTranscript, role: "server" | "client"): Buffer;
/** `secret` is 32 bytes, or their canonical Base64. */
export declare function computeProof(secret: Uint8Array | string, transcript: HandshakeTranscript, role: "server" | "client"): string;
export declare function verifyProof(secret: Uint8Array | string, proof: string | undefined, transcript: HandshakeTranscript, role: "server" | "client"): boolean;
export declare function createPipeName(hostId: string, edition: string, userHash: string, registrationId: string): string;
export declare function parsePipeName(value: string): { readonly hostId: string; readonly edition: string; readonly userHash: string; readonly registrationId: string } | null;
export declare function userHash(userSid: string): string;

export interface HostLimits {
    readonly maxFrameBytes: number;
    readonly maxSessions: number;
    readonly maxPendingInvokes: number;
    readonly invokeTimeoutMs: number;
    readonly faceChangesPerSecond: number;
    readonly faceBurst: number;
    readonly messageRate: number;
    readonly messageBurst: number;
    readonly hardMessageRate: number;
    readonly hardMessageBurst: number;
    readonly pingIntervalMs: number;
    readonly pongTimeoutMs: number;
}

export declare const protocol3Limits: HostLimits;
export declare function limitsWithinRanges(limits: HostLimits): boolean;
export declare function limitsForCompanion(limits: HostLimits): HostLimits;

/** A token bucket (contract §B.3). */
export declare class TokenBucket {
    constructor(ratePerSecond: number, burst: number, clock?: Clock);
    /** One token per started 1,024 bytes. */
    static costOf(frameBytes: number): number;
    readonly retryAfterMs: number;
    tryTake(tokens?: number): boolean;
}

export declare const ReasonCodes: Readonly<Record<string, ReasonCodeInfo>>;
export declare function reasonInfo(code: string): ReasonCodeInfo;
export declare function isKnownReason(code: string): boolean;
export declare function isReasonCode(code: string): boolean;
export declare function helpUri(code: string, hostId: string): string | undefined;
export declare const capabilities: readonly { readonly id: string; readonly status: string; readonly meaning: string }[];
export declare function isExperimentalCapability(id: string): boolean;
