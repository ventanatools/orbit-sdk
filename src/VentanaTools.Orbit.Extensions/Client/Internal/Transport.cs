// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
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
/// The named-pipe transport (contract §7.1), opened with identification-level impersonation. Before
/// any byte is written, it checks that the pipe is owned by the current user's account, its token's
/// user SID (not <see cref="PipeOptions.CurrentUserOnly"/>, which compares the token's default owner:
/// the Administrators group in an elevated process), then the server process's user and integrity
/// level and the pipe's mandatory label. A server that passes is verified; a pipe that another
/// principal owns, a server whose process runs as another user or at a lower integrity level, or
/// whose pipe was created at a lower integrity level, is refused (<c>auth.server-unverified</c>); and
/// a server whose process cannot be opened or read, but whose pipe label is no lower than this
/// process's level, stays unverified until its challenge proof verifies.
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

        // Not PipeOptions.CurrentUserOnly: it compares the pipe's owner with the token's default owner, which is the
        // Administrators group in an elevated process, so an elevated companion would refuse every host's pipe and accept
        // one that group owns. CheckServer compares the owner with the token's user before anything is written.
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous,
            TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(ConnectTimeoutMilliseconds, cancellationToken).ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException)
        {
            // The pipe's access-control list or label does not admit this process: nothing was written to it.
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
            // The pipe is not owned by the current user's account, another user's or a lower-integrity process holds the
            // pipe name, or its pipe was created at a lower integrity level: nothing was written to it.
            await pipe.DisposeAsync().ConfigureAwait(false);
            return TransportConnection.Failed(ReasonCode.AuthServerUnverified);
        }

        return TransportConnection.Connected(pipe, check == ServerCheck.Verified);
    }
}

/// <summary>What the operating system says about a pipe server's process and its pipe (contract §7.1).</summary>
internal enum ServerCheck
{
    /// <summary>
    /// Its pipe is owned by the current user's account, it runs as the current user at an integrity
    /// level no lower than this process's, and its pipe's label is no lower than Medium or this
    /// process's level, whichever is lower.
    /// </summary>
    Verified = 1,

    /// <summary>
    /// Its pipe is owned by the current user's account, and its process or token could not be opened or
    /// read, but its pipe's label is no lower than this process's integrity level; it is unverified
    /// until its challenge proof verifies.
    /// </summary>
    Unchecked = 2,

    /// <summary>
    /// Its pipe is owned by another principal or its owner cannot be read, it runs as another user or
    /// at a lower integrity level, or its pipe's label is lower than the check allows or cannot be
    /// read; the client writes nothing to it.
    /// </summary>
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
    private const int SeKernelObject = 6;
    private const uint OwnerSecurityInformation = 0x1;
    private const uint LabelSecurityInformation = 0x10;
    private const byte SystemMandatoryLabelAceType = 0x11;
    private const int MaximumSecurityDescriptorLength = 64 * 1024;
    private const string LabelSidPrefix = "S-1-16-";

    /// <summary>Medium integrity (S-1-16-8192), the level of an object that carries no mandatory label.</summary>
    internal const uint MediumIntegrity = 0x2000;

    /// <summary>Whether no instance of the pipe exists; never connects to it.</summary>
    public static bool PipeIsMissing(string pipeName)
    {
        if (WaitNamedPipe(@"\\.\pipe\" + pipeName, 1))
        {
            return false;
        }

        return Marshal.GetLastPInvokeError() == ErrorFileNotFound;
    }

    /// <summary>
    /// Checks the pipe's owner, its server process and its mandatory label, in that order (contract
    /// §7.1). A pipe that is not owned by the current user's account refuses the server; a process that
    /// cannot be checked counts as <see cref="ServerCheck.Unchecked"/>; a label that cannot be read
    /// refuses the server.
    /// </summary>
    public static ServerCheck CheckServer(SafePipeHandle pipe)
    {
        if (!IsOwnedByUser(PipeOwner(pipe), CurrentUser()))
        {
            return ServerCheck.Refused;
        }

        ServerCheck process;
        try
        {
            process = GetNamedPipeServerProcessId(pipe, out var processId) ? CheckProcess(processId) : ServerCheck.Unchecked;
        }
        catch (SystemException)
        {
            process = ServerCheck.Unchecked;
        }

        return process == ServerCheck.Refused ? ServerCheck.Refused : WithLabel(process, PipeLabel(pipe), OwnIntegrity());
    }

    /// <summary>
    /// Combines the process check with the pipe's label (contract §7.1). Windows labels an object with
    /// its creator's integrity level when that level is below Medium, and the creator cannot raise or
    /// remove the label, so the label still shows a lower-integrity creator after that process has
    /// denied everyone access to itself to defeat the process check. A pipe created at Medium or above
    /// carries no label and counts as Medium, so after a passed process check the label must reach
    /// Medium or this process's level, whichever is lower; otherwise it must reach this process's
    /// level. A label or own level that could not be read refuses the server.
    /// </summary>
    /// <param name="process">The process check: <see cref="ServerCheck.Verified"/> or <see cref="ServerCheck.Unchecked"/>.</param>
    /// <param name="pipeLabel">The pipe's integrity level (the label's relative identifier), or null when it could not be read.</param>
    /// <param name="ownLevel">This process's integrity level (relative identifier), or null when it could not be read.</param>
    internal static ServerCheck WithLabel(ServerCheck process, uint? pipeLabel, uint? ownLevel)
    {
        if (process == ServerCheck.Refused || pipeLabel is not { } label || ownLevel is not { } own)
        {
            return ServerCheck.Refused;
        }

        var floor = process == ServerCheck.Verified ? Math.Min(own, MediumIntegrity) : own;
        return label < floor ? ServerCheck.Refused : process;
    }

    /// <summary>
    /// The owner check (contract §7.1): whether a pipe is owned by the current user's account, the user
    /// SID of this process's token. No other owner passes, the token's default owner included: in an
    /// elevated process that is the Administrators group, which any administrator's elevated process
    /// can make a pipe's owner (it is what <see cref="PipeOptions.CurrentUserOnly"/> compares). An
    /// owner or user that could not be read fails the check.
    /// </summary>
    /// <param name="owner">The pipe's owner, or null when it could not be read.</param>
    /// <param name="user">The user SID of this process's token, or null when it could not be read.</param>
    internal static bool IsOwnedByUser(SecurityIdentifier? owner, SecurityIdentifier? user) =>
        owner is not null && user is not null && owner == user;

    /// <summary>
    /// The pipe's owner, read through the client's own handle, which has <c>READ_CONTROL</c> as part of
    /// <c>GENERIC_READ</c>, or null when it cannot be read.
    /// </summary>
    internal static SecurityIdentifier? PipeOwner(SafeHandle pipe) => SecurityDescriptor(pipe, OwnerSecurityInformation)?.Owner;

    /// <summary>
    /// The pipe's integrity level, read through the client's own handle, which has <c>READ_CONTROL</c>
    /// as part of <c>GENERIC_READ</c>: the lowest mandatory label that applies to the pipe itself,
    /// Medium when it has none, or null when it cannot be read.
    /// </summary>
    internal static uint? PipeLabel(SafeHandle pipe)
    {
        try
        {
            return SecurityDescriptor(pipe, LabelSecurityInformation) is { } descriptor ? LowestLabel(descriptor) : null;
        }
        catch (SystemException)
        {
            return null;
        }
    }

    /// <summary>
    /// The parts of the pipe's security descriptor that <paramref name="information"/> names, read
    /// through <paramref name="pipe"/>, or null when they cannot be read.
    /// </summary>
    private static RawSecurityDescriptor? SecurityDescriptor(SafeHandle pipe, uint information)
    {
        nint descriptor = 0;
        try
        {
            if (GetSecurityInfo(pipe, SeKernelObject, information, 0, 0, 0, 0, out descriptor) != 0 || descriptor == 0)
            {
                return null;
            }

            var length = GetSecurityDescriptorLength(descriptor);
            if (length <= 0 || length > MaximumSecurityDescriptorLength)
            {
                return null;
            }

            var bytes = new byte[length];
            Marshal.Copy(descriptor, bytes, 0, length);
            return new RawSecurityDescriptor(bytes, 0);
        }
        catch (SystemException)
        {
            return null;
        }
        finally
        {
            if (descriptor != 0)
            {
                _ = LocalFree(descriptor);
            }
        }
    }

    /// <summary>
    /// The lowest integrity level among the mandatory labels that apply to the object itself (an
    /// inherit-only label applies only to children), Medium when there is none, or null when a label
    /// is malformed.
    /// </summary>
    internal static uint? LowestLabel(RawSecurityDescriptor descriptor)
    {
        uint? lowest = null;
        foreach (var ace in descriptor.SystemAcl ?? new RawAcl(GenericAcl.AclRevision, 0))
        {
            if ((byte)ace.AceType != SystemMandatoryLabelAceType || (ace.AceFlags & AceFlags.InheritOnly) != 0)
            {
                continue;
            }

            // SYSTEM_MANDATORY_LABEL_ACE: the header, a 4-byte mask (the label's policy), then the label's SID.
            if (ace is not CustomAce custom || custom.GetOpaque() is not { Length: >= 4 + 8 } opaque)
            {
                return null;
            }

            if (Rid(new SecurityIdentifier(opaque, 4)) is not { } rid)
            {
                return null;
            }

            lowest = lowest is { } current ? Math.Min(current, rid) : rid;
        }

        return lowest ?? MediumIntegrity;
    }

    /// <summary>The user SID of this process's token, or null when it cannot be read.</summary>
    private static SecurityIdentifier? CurrentUser()
    {
        try
        {
            using var self = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            return self.User;
        }
        catch (SystemException)
        {
            return null;
        }
    }

    /// <summary>This process's integrity level (relative identifier), or null when it cannot be read.</summary>
    private static uint? OwnIntegrity()
    {
        try
        {
            using var self = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            return IntegrityRid(self.Token);
        }
        catch (SystemException)
        {
            return null;
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
        return sid is null ? null : Rid(sid);
    }

    /// <summary>The level of a mandatory-label SID (S-1-16-<i>rid</i>), or null for any other SID.</summary>
    private static uint? Rid(SecurityIdentifier sid)
    {
        var value = sid.Value;
        return value.StartsWith(LabelSidPrefix, StringComparison.Ordinal)
            && uint.TryParse(value.AsSpan(LabelSidPrefix.Length), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var rid)
            ? rid
            : null;
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

    [LibraryImport("advapi32.dll")]
    private static partial uint GetSecurityInfo(SafeHandle handle, int objectType, uint securityInformation, nint owner, nint group,
        nint dacl, nint sacl, out nint securityDescriptor);

    [LibraryImport("advapi32.dll")]
    private static partial int GetSecurityDescriptorLength(nint securityDescriptor);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
