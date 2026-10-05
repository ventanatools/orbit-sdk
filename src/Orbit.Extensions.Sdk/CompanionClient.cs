// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Seth Cottle

using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Orbit.Extensions.Protocol;

namespace Orbit.Extensions.Sdk;

/// <summary>A v2 companion connection. This class never starts Orbit or loads extension code into it.</summary>
public sealed class CompanionClient
{
    private int _running;
    private int _sessionHandlers;
    private int _invokeHandlers;

    /// <summary>
    /// Connects until cancelled, waiting one second between attempts. Each connection has fresh
    /// sessions. Pending calls and faces are discarded on loss, never retried on the new connection.
    /// Keep pairing alive until this task ends; no credentials are logged or exposed to handlers.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public async Task RunAsync(ExtensionPairing pairing, ExternalExtensionManifest manifest,
        IContributionHandler handler, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(handler);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var snapshot = ExternalExtensionManifestReader.Snapshot(manifest)
            ?? throw new ArgumentException("Invalid extension manifest.", nameof(manifest));
        if (snapshot.ManifestVersion != 2 || pairing.ProtocolVersion != 2 || pairing.ExtensionId != snapshot.Id
            || !snapshot.Hosts.Contains("orbit", StringComparer.Ordinal))
            throw new ArgumentException("The pairing and manifest do not describe an Orbit v2 extension.", nameof(manifest));
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
            throw new InvalidOperationException("This companion client is already running.");
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeClientStream(".", pairing.PipeName, PipeDirection.InOut,
                        PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    using (var auth = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
                    {
                        auth.CancelAfter(ExtensionWire.FrameTimeout);
                        await pipe.ConnectAsync(auth.Token).ConfigureAwait(false);
                        await AuthenticateAsync(pipe, pairing, auth.Token).ConfigureAwait(false);
                    }
                    var connection = new CompanionConnection(pipe, snapshot, handler, this, cancellationToken);
                    await connection.RunAsync().ConfigureAwait(false);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException
                    or UnauthorizedAccessException or OperationCanceledException or System.Text.DecoderFallbackException)
                {
                    // Peer text, paths, credentials and exception messages never become diagnostics.
                }
                if (!cancellationToken.IsCancellationRequested)
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        finally { Volatile.Write(ref _running, 0); }
    }

    private static async Task AuthenticateAsync(Stream stream, ExtensionPairing pairing, CancellationToken cancellationToken)
    {
        var clientNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        await ExtensionWire.WriteAsync(stream, CompanionMessages.Hello(pairing, clientNonce), cancellationToken).ConfigureAwait(false);
        var challengeBytes = await ExtensionWire.ReadAsync(stream, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Extension authentication failed.");
        using var challenge = ExtensionWire.ReadMessage(challengeBytes, "challenge", "serverNonce", "proof");
        var serverNonce = ExtensionWire.Text(challenge.RootElement, "serverNonce");
        if (!ExtensionWire.TryBytes(serverNonce, out _)
            || !pairing.VerifyProof(ExtensionWire.Text(challenge.RootElement, "proof"), "server", clientNonce, serverNonce))
            throw new InvalidDataException("Extension authentication failed.");
        await ExtensionWire.WriteAsync(stream, ExtensionWire.Message("authenticate",
            ("proof", pairing.CreateProof("client", clientNonce, serverNonce))), cancellationToken).ConfigureAwait(false);
        var readyBytes = await ExtensionWire.ReadAsync(stream, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Extension authentication failed.");
        using var ready = ExtensionWire.ReadMessage(readyBytes, "ready");
    }

    // These count actual running author tasks across reconnects. A handler that ignores its
    // cancellation cannot make repeated reconnects create an unbounded collection of tasks.
    internal bool TryEnterSession()
    {
        if (Interlocked.Increment(ref _sessionHandlers) <= CompanionConnection.SessionLimit) return true;
        Interlocked.Decrement(ref _sessionHandlers);
        return false;
    }

    internal bool TryEnterInvoke()
    {
        if (Interlocked.Increment(ref _invokeHandlers) <= CompanionConnection.InvokeLimit) return true;
        Interlocked.Decrement(ref _invokeHandlers);
        return false;
    }

    internal void ExitSession() => Interlocked.Decrement(ref _sessionHandlers);
    internal void ExitInvoke() => Interlocked.Decrement(ref _invokeHandlers);
}
