// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace VentanaTools.Orbit.Extensions.Tool.Tests;

/// <summary>
/// Runs code on this thread as the current user at Low integrity (S-1-16-4096), the way a sandboxed
/// browser or document viewer runs: a duplicate of this process's token with a lowered label, used
/// only by impersonation. Nothing here needs a privilege, and nothing outside this thread changes.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class LowIntegrityToken
{
    public const int ErrorAccessDenied = 5;
    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;

    private const uint OpenExisting = 3;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenImpersonate = 0x0004;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenImpersonation = 2;
    private const int TokenIntegrityLevelClass = 25;
    private const uint SeGroupIntegrity = 0x20;

    /// <summary>Runs <paramref name="action"/> impersonating a Low-integrity copy of this process's token.</summary>
    public static T Run<T>(Func<T> action)
    {
        using var token = Create();
        return WindowsIdentity.RunImpersonated(token, action);
    }

    /// <summary>
    /// Opens <c>\\.\pipe\</c><paramref name="pipeName"/> with <paramref name="access"/> at Low integrity, as a
    /// sandboxed process would; returns the open handle, which the caller disposes, or the Win32 error.
    /// </summary>
    public static (SafeFileHandle? Handle, int Error) OpenPipe(string pipeName, uint access) => Run(() =>
    {
        var handle = CreateFileW(@"\\.\pipe\" + pipeName, access, 0, 0, OpenExisting, 0, 0);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            return ((SafeFileHandle?)null, error);
        }

        return (handle, 0);
    });

    private static SafeAccessTokenHandle Create()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        if (!OpenProcessToken(process.Handle, TokenDuplicate | TokenQuery | TokenImpersonate | TokenAdjustDefault, out var own))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        using var ownToken = new SafeAccessTokenHandle(own);
        if (!DuplicateTokenEx(own, MaximumAllowed, 0, SecurityImpersonation, TokenImpersonation, out var duplicate))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var low = new SafeAccessTokenHandle(duplicate);
        var sid = new SecurityIdentifier("S-1-16-4096");
        var sidBytes = new byte[sid.BinaryLength];
        sid.GetBinaryForm(sidBytes, 0);
        var sidMemory = Marshal.AllocHGlobal(sidBytes.Length);
        try
        {
            Marshal.Copy(sidBytes, 0, sidMemory, sidBytes.Length);
            var label = new TokenMandatoryLabel { Sid = sidMemory, Attributes = SeGroupIntegrity };
            if (!SetTokenInformation(duplicate, TokenIntegrityLevelClass, in label, Marshal.SizeOf<TokenMandatoryLabel>() + sidBytes.Length))
            {
                var error = Marshal.GetLastPInvokeError();
                low.Dispose();
                throw new Win32Exception(error);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(sidMemory);
        }

        return low;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public nint Sid;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateTokenEx(nint existing, uint desiredAccess, nint attributes, int impersonationLevel, int tokenType,
        out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetTokenInformation(nint token, int informationClass, in TokenMandatoryLabel information, int length);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint attributes, uint disposition, uint flags,
        nint template);
}
