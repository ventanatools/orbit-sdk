// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

/// <reference types="node" />

import type {
    Failure, FaceState, Handler, HandlerFaultedEvent, Invocation, Manifest, ManifestReadOptions, Session, StatusEvent,
} from "@ventanatools/orbit-extensions";
import type { Clock, HostLimits } from "@ventanatools/orbit-extensions/wire";

/** A face as a host holds it after cleaning. */
export interface RecordedFace {
    readonly line1?: string;
    readonly line2?: string;
    readonly detail?: string;
    readonly glyph?: string;
    readonly state: FaceState;
    readonly goodForSeconds: number;
    readonly renew?: boolean;
}

export interface RecordedPublication {
    readonly kind: "SetFace" | "ClearFace" | "Fail" | "AfterStop";
    readonly face?: RecordedFace;
    readonly failure?: Failure;
    readonly atMs: number;
}

/** A session with no connection that records what a handler publishes. */
export interface RecordingSession {
    readonly session: Session;
    readonly publications: readonly RecordedPublication[];
    readonly lastFace: RecordedFace | undefined;
    readonly signal: AbortSignal;
    /** Ends the session as stopSession would. */
    stop(): void;
    /**
     * Runs handler.runSession, which runs up to its first await before run returns; an AbortError
     * after stop is a normal completion.
     */
    run(handler: Handler, options?: { timeoutMs?: number }): Promise<void>;
    /** The next face the handler shows (SetFace), in publication order; the timeout (default 2000) is real time. */
    waitForFace(timeoutMs?: number): Promise<RecordedFace>;
    /** Resolves once at least `count` publications of any kind are recorded; the timeout (default 2000) is real time. */
    waitForPublications(count: number, timeoutMs?: number): Promise<void>;
}

/** Fills defaults and checks the settings against the manifest (TypeError when they do not match). */
export declare function createTestSession(options: {
    manifest: Manifest;
    contributionId: string;
    settings?: Record<string, string>;
    uiLanguage?: string;
    clock?: Clock;
}): RecordingSession;

/** An invocation of a recording session's session, for calling an invoke handler directly. */
export declare function createTestInvocation(recording: RecordingSession, requestId?: string): Invocation;

export interface TestHostSession {
    readonly sessionId: string;
    readonly contributionId: string;
    /** The code the companion refused the session with, or undefined when it accepted it. */
    readonly refusedCode?: string;
}

export interface TestInvokeOutcome {
    readonly kind: "Done" | "Refused" | "Failed" | "Unsupported" | "Cancelled" | "TimedOut";
    readonly failure?: Failure;
}

export interface TestInvocation {
    readonly requestId: string;
    readonly result: Promise<TestInvokeOutcome>;
}

export interface TranscriptEntry {
    readonly atMs: number;
    readonly from: "Host" | "Companion";
    readonly type: string;
    readonly sessionId?: string;
    readonly requestId?: string;
}

/** A scripted protocol-3 host driving a real CompanionClient over an in-memory duplex stream. */
export interface TestHost {
    readonly client: import("@ventanatools/orbit-extensions").CompanionClient;
    readonly statuses: readonly StatusEvent[];
    readonly faults: readonly HandlerFaultedEvent[];
    readonly transcript: readonly TranscriptEntry[];
    transcriptJsonLines(): string;
    /** Resolves once the companion accepted or refused the session; missing settings get their defaults. */
    startSession(contributionId: string, settings?: Record<string, string>): Promise<TestHostSession>;
    startInvoke(session: TestHostSession): TestInvocation;
    cancel(invocation: TestInvocation): void;
    invoke(session: TestHostSession, options?: { signal?: AbortSignal }): Promise<TestInvokeOutcome>;
    stopSession(session: TestHostSession): void;
    /** Ends the connection, sending error with `code` first when given; the client reconnects. */
    disconnect(code?: string): void;
    /** The next face the session received, in arrival order. */
    waitForFace(session: TestHostSession, timeoutMs?: number): Promise<{ readonly face: RecordedFace; readonly arrivedAtMs: number }>;
    close(): Promise<void>;
}

export declare function startTestHost(options: {
    manifest: Manifest;
    handler: Handler;
    /** Default: the first active registry host in the manifest's hosts, else its first host. */
    hostId?: string;
    limits?: HostLimits;
    capabilities?: readonly string[];
    uiLanguage?: string;
    clock?: Clock;
    retry?: import("@ventanatools/orbit-extensions").RetryOptions;
    connectTimeoutMs?: number;
}): Promise<TestHost>;

/** Throws an Error (name "ConformanceError", with `failures`) listing every error a host would refuse the manifest for. */
export declare function assertManifestValid(pathOrManifest: string | Manifest, options?: ManifestReadOptions): Manifest;

/** A manual clock: time moves only when advanced, and due timers run in time order. */
export declare class ManualClock implements Clock {
    now(): number;
    setTimeout(callback: () => void, ms: number): number;
    clearTimeout(handle: unknown): void;
    readonly pending: number;
    advance(ms: number): void;
    advanceAsync(ms: number, stepMs?: number): Promise<void>;
}
