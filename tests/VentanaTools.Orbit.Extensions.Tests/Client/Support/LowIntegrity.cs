// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VentanaTools.Orbit.Extensions.Tests.Client;

/// <summary>
/// Starts a process as the current user at Low integrity (S-1-16-4096), the way a sandboxed browser
/// or document viewer runs: a duplicate of this process's token with a lowered label, started
/// without a window. Nothing here needs a privilege.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class LowIntegrity
{
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenAdjustDefault = 0x0080;
    private const uint MaximumAllowed = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const int TokenIntegrityLevelClass = 25;
    private const uint SeGroupIntegrity = 0x20;
    private const uint CreateNoWindow = 0x08000000;
    private const uint Infinite = 0xFFFFFFFF;
    private const uint WaitTimeout = 0x102;
    private const uint DaclSecurityInformation = 0x4;

    /// <summary>Windows PowerShell, which every supported Windows has.</summary>
    public static string WindowsPowerShell { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    /// <summary>Starts <paramref name="commandLine"/> at Low integrity.</summary>
    public static unsafe LowProcess Start(string commandLine)
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenDuplicate | TokenQuery | TokenAssignPrimary | TokenAdjustDefault, out var own))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        nint low = 0;
        nint sid = 0;
        try
        {
            if (!DuplicateTokenEx(own, MaximumAllowed, 0, SecurityImpersonation, TokenPrimary, out low)
                || !ConvertStringSidToSid("S-1-16-4096", out sid))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var label = new TokenMandatoryLabel { Sid = sid, Attributes = SeGroupIntegrity };
            if (!SetTokenInformation(low, TokenIntegrityLevelClass, &label, sizeof(TokenMandatoryLabel) + GetLengthSid(sid)))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            var startup = new StartupInfo { Size = sizeof(StartupInfo) };
            var command = (commandLine + "\0").ToCharArray();
            ProcessInformation information;
            fixed (char* text = command)
            {
                if (!CreateProcessAsUser(low, null, text, 0, 0, false, CreateNoWindow, 0, null, &startup, &information))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }

            CloseHandle(information.Thread);
            return new LowProcess(information.Process, information.ProcessId);
        }
        finally
        {
            if (sid != 0)
            {
                LocalFree(sid);
            }

            if (low != 0)
            {
                CloseHandle(low);
            }

            CloseHandle(own);
        }
    }

    /// <summary>A Low-integrity process; disposing it ends the process if it still runs.</summary>
    internal sealed class LowProcess : IDisposable
    {
        private nint _handle;

        public LowProcess(nint handle, uint id)
        {
            _handle = handle;
            Id = id;
        }

        public uint Id { get; }

        /// <summary>
        /// Gives the process an empty, protected access-control list, as the process itself can do to its own
        /// object (its owner keeps only the right to read and change that list), so that no other process of the
        /// user can open it any more. This handle was opened before and keeps its access.
        /// </summary>
        public void DenyEveryone()
        {
            if (!ConvertStringSecurityDescriptorToSecurityDescriptor("D:P", 1, out var descriptor, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            try
            {
                if (!SetKernelObjectSecurity(_handle, DaclSecurityInformation, descriptor))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }
            finally
            {
                LocalFree(descriptor);
            }
        }

        /// <summary>The exit code, or null when the process still runs after <paramref name="timeout"/>.</summary>
        public async Task<int?> WaitForExitAsync(TimeSpan timeout)
        {
            var handle = _handle;
            return await Task.Run(() =>
            {
                if (WaitForSingleObject(handle, (uint)timeout.TotalMilliseconds) == WaitTimeout)
                {
                    return (int?)null;
                }

                return GetExitCodeProcess(handle, out var code) ? (int)code : null;
            }).ConfigureAwait(false);
        }

        public void Dispose()
        {
            if (_handle == 0)
            {
                return;
            }

            if (WaitForSingleObject(_handle, 0) == WaitTimeout && TerminateProcess(_handle, 1))
            {
                _ = WaitForSingleObject(_handle, Infinite);
            }

            CloseHandle(_handle);
            _handle = 0;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenMandatoryLabel
    {
        public nint Sid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2Size;
        public nint Reserved2;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public uint ProcessId;
        public uint ThreadId;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint desiredAccess, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateTokenEx(nint existing, uint desiredAccess, nint attributes, int impersonationLevel, int tokenType,
        out nint token);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSidToSidW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSidToSid(string sid, out nint result);

    [LibraryImport("advapi32.dll")]
    private static partial int GetLengthSid(nint sid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetTokenInformation(nint token, int informationClass, void* information, int length);

    [LibraryImport("advapi32.dll", EntryPoint = "CreateProcessAsUserW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool CreateProcessAsUser(nint token, string? applicationName, char* commandLine, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment,
        string? currentDirectory, StartupInfo* startupInfo, ProcessInformation* processInformation);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);

    [LibraryImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptor(string descriptor, uint revision, out nint result,
        nint length);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetKernelObjectSecurity(nint handle, uint information, nint descriptor);
}
