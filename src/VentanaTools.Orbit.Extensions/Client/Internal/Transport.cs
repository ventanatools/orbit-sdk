// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace VentanaTools.Orbit.Extensions;

/// <summary>
/// How a <see cref="CompanionClient"/> reaches its host. The named-pipe transport is the only
/// public one; the in-memory transport seam is internal and serves the Testing package and the
/// SDK's own tests.
/// </summary>
internal interface ICompanionTransport
{
    /// <summary>Connects once.</summary>
    /// <param name="cancellationToken">Cancels the attempt.</param>
    /// <returns>The connected stream and whether the server is verified before any byte is written, or the failure.</returns>
    ValueTask<TransportConnection> ConnectAsync(CancellationToken cancellationToken);
}

/// <summary>The outcome of one connection attempt: a connected stream, or the local reason code that explains a failure.</summary>
internal sealed class TransportConnection
{
    private TransportConnection(Stream? stream, bool serverVerified, ReasonCode? failure)
    {
        Stream = stream;
        ServerVerified = serverVerified;
        Failure = failure;
    }

    /// <summary>The byte stream when connected; the client disposes it.</summary>
    public Stream? Stream { get; }

    /// <summary>Whether the operating system confirmed the server's user and integrity level (contract §7.1).</summary>
    public bool ServerVerified { get; }

    /// <summary>Why the attempt failed; nothing was written to the server.</summary>
    public ReasonCode? Failure { get; }

    public static TransportConnection Connected(Stream stream, bool serverVerified) => new(stream, serverVerified, null);

    public static TransportConnection Failed(ReasonCode code) => new(null, false, code);
}

/// <summary>The in-memory transport seam: each attempt asks <c>accept</c> to take the host end of a new duplex pair.</summary>
internal sealed class InMemoryTransport : ICompanionTransport
{
    private readonly Func<Stream, CancellationToken, ValueTask<bool>> _accept;
    private readonly bool _serverVerified;

    /// <param name="accept">Receives the host end. Return false to refuse the attempt (<c>host.not-running</c>).</param>
    /// <param name="serverVerified">What the attempt reports as operating-system verification.</param>
    public InMemoryTransport(Func<Stream, CancellationToken, ValueTask<bool>> accept, bool serverVerified)
    {
        _accept = accept;
        _serverVerified = serverVerified;
    }

    public async ValueTask<TransportConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        var (client, host) = DuplexMemoryStream.CreatePair();
        bool accepted;
        try
        {
            accepted = await _accept(host, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await client.DisposeAsync().ConfigureAwait(false);
            await host.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        if (!accepted)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            await host.DisposeAsync().ConfigureAwait(false);
            return TransportConnection.Failed(ReasonCode.HostNotRunning);
        }

        return TransportConnection.Connected(client, _serverVerified);
    }
}

/// <summary>
/// The named-pipe transport (contract §7.1): the pipe must be owned by the current user
/// (<see cref="PipeOptions.CurrentUserOnly"/>) and is opened with identification-level
/// impersonation. Before any byte is written, the server process's user and integrity level are
/// checked: a server that passes is verified, a server whose process runs as another user or at a
/// lower integrity level is refused (<c>auth.server-unverified</c>), and a server whose process
/// cannot be opened or read stays unverified until its challenge proof verifies.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class PipeTransport : ICompanionTransport
{
    private const int ConnectTimeoutMilliseconds = 5_000;
    private readonly string _pipeName;

    public PipeTransport(string pipeName)
    {
        _pipeName = pipeName;
    }

    public async ValueTask<TransportConnection> ConnectAsync(CancellationToken cancellationToken)
    {
        // A missing pipe answers at once, so a companion started before its host does not wait.
        if (PipeNatives.PipeIsMissing(_pipeName))
        {
            return TransportConnection.Failed(ReasonCode.HostNotRunning);
        }

        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            // The pipe's owner is not the current user: nothing was written to it.
            await pipe.DisposeAsync().ConfigureAwait(false);
            return TransportConnection.Failed(ReasonCode.AuthServerUnverified);
        }
        catch (TimeoutException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return TransportConnection.Failed(PipeNatives.PipeIsMissing(_pipeName) ? ReasonCode.HostNotRunning : ReasonCode.HostPipeBusy);
        }
        catch (IOException)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return TransportConnection.Failed(ReasonCode.HostNotRunning);
        }
        catch
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var check = PipeNatives.CheckServer(pipe.SafePipeHandle);
        if (check == ServerCheck.Refused)
        {
            // Another user's or a lower-integrity process holds the pipe name: nothing was written to it.
            await pipe.DisposeAsync().ConfigureAwait(false);
            return TransportConnection.Failed(ReasonCode.AuthServerUnverified);
        }

        return TransportConnection.Connected(pipe, check == ServerCheck.Verified);
    }
}

/// <summary>What the operating system says about a pipe server's process (contract §7.1).</summary>
internal enum ServerCheck
{
    /// <summary>It runs as the current user at an integrity level no lower than this process's.</summary>
    Verified = 1,

    /// <summary>Its process or token could not be opened or read; it is unverified until its challenge proof verifies.</summary>
    Unchecked = 2,

    /// <summary>It runs as another user or at a lower integrity level; the client writes nothing to it.</summary>
    Refused = 3,
}

/// <summary>The Win32 calls the pipe transport needs.</summary>
[SupportedOSPlatform("windows")]
internal static partial class PipeNatives
{
    private const int ErrorFileNotFound = 2;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenUserClass = 1;
    private const int TokenIntegrityLevelClass = 25;

    /// <summary>Whether no instance of the pipe exists; never connects to it.</summary>
    public static bool PipeIsMissing(string pipeName)
    {
        if (WaitNamedPipe(@"\\.\pipe\" + pipeName, 1))
        {
            return false;
        }

        return Marshal.GetLastPInvokeError() == ErrorFileNotFound;
    }

    /// <summary>Checks the pipe's server process (contract §7.1). Any failure to check means <see cref="ServerCheck.Unchecked"/>.</summary>
    public static ServerCheck CheckServer(SafePipeHandle pipe)
    {
        try
        {
            return GetNamedPipeServerProcessId(pipe, out var processId) ? CheckProcess(processId) : ServerCheck.Unchecked;
        }
        catch (SystemException)
        {
            return ServerCheck.Unchecked;
        }
    }

    /// <summary>
    /// Whether a process runs as the current user at an integrity level no lower than this
    /// process's: <see cref="ServerCheck.Refused"/> when its token was read and either test fails,
    /// <see cref="ServerCheck.Unchecked"/> when the process or its token cannot be opened or read.
    /// </summary>
    public static ServerCheck CheckProcess(uint processId)
    {
        try
        {
            using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process.IsInvalid || !OpenProcessToken(process, TokenQuery, out var serverToken))
            {
                return ServerCheck.Unchecked;
            }

            using var server = new SafeAccessTokenHandle(serverToken);
            using var self = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            var serverUser = TokenSid(server.DangerousGetHandle(), TokenUserClass);
            if (serverUser is null || self.User is null)
            {
                return ServerCheck.Unchecked;
            }

            if (serverUser != self.User)
            {
                return ServerCheck.Refused;
            }

            var serverLevel = IntegrityRid(server.DangerousGetHandle());
            var ownLevel = IntegrityRid(self.Token);
            if (serverLevel is null || ownLevel is null)
            {
                return ServerCheck.Unchecked;
            }

            return serverLevel >= ownLevel ? ServerCheck.Verified : ServerCheck.Refused;
        }
        catch (SystemException)
        {
            // Access denied, a handle that closed meanwhile, or a token this check cannot read: not checked.
            return ServerCheck.Unchecked;
        }
    }

    private static uint? IntegrityRid(nint token)
    {
        var sid = TokenSid(token, TokenIntegrityLevelClass);
        if (sid is null)
        {
            return null;
        }

        var value = sid.Value;
        var last = value.LastIndexOf('-');
        return last >= 0 && uint.TryParse(value.AsSpan(last + 1), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var rid) ? rid : null;
    }

    /// <summary>The SID at the start of a TOKEN_USER or TOKEN_MANDATORY_LABEL (both begin with SID_AND_ATTRIBUTES).</summary>
    private static unsafe SecurityIdentifier? TokenSid(nint token, int informationClass)
    {
        GetTokenInformation(token, informationClass, null, 0, out var length);
        if (length <= 0 || length > 4096)
        {
            return null;
        }

        var buffer = new byte[length];
        fixed (byte* data = buffer)
        {
            if (!GetTokenInformation(token, informationClass, data, length, out _))
            {
                return null;
            }

            var sid = *(nint*)data;
            return sid == 0 ? null : new SecurityIdentifier(sid);
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "WaitNamedPipeW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WaitNamedPipe(string name, uint timeout);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint desiredAccess, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetTokenInformation(nint token, int informationClass, byte* information, int length, out int returnLength);
}
