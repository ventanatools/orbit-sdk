// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VentanaTools.Orbit.Extensions.Tool.Tests;

/// <summary>What Windows reports about a pipe through one of its handles.</summary>
[SupportedOSPlatform("windows")]
internal static partial class PipeFacts
{
    /// <summary><c>PIPE_SERVER_END</c> in <see cref="Flags"/>.</summary>
    public const uint ServerEnd = 0x00000001;

    /// <summary>
    /// <c>PIPE_REJECT_REMOTE_CLIENTS</c>: <c>GetNamedPipeInfo</c> reports the pipe mode it was created
    /// with in its flags (checked: a pipe created without the mode reports <c>PIPE_SERVER_END</c> alone).
    /// </summary>
    public const uint RejectRemoteClients = 0x00000008;

    private const int KernelObject = 6;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint LabelSecurityInformation = 0x00000010;

    /// <summary>The flags <c>GetNamedPipeInfo</c> returns for <paramref name="pipe"/>.</summary>
    public static uint Flags(SafeHandle pipe) =>
        GetNamedPipeInfo(pipe, out var flags, out _, out _, out _) ? flags : throw new Win32Exception(Marshal.GetLastPInvokeError());

    /// <summary>The pipe's owner, access-control list and mandatory label as SDDL.</summary>
    public static string Sddl(SafeHandle pipe)
    {
        const uint what = OwnerSecurityInformation | DaclSecurityInformation | LabelSecurityInformation;
        var error = GetSecurityInfo(pipe, KernelObject, what, 0, 0, 0, 0, out var descriptor);
        if (error != 0)
        {
            throw new Win32Exception(error);
        }

        try
        {
            if (!ConvertSecurityDescriptorToStringSecurityDescriptorW(descriptor, 1, what, out var text, out _))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            try
            {
                return Marshal.PtrToStringUni(text)!;
            }
            finally
            {
                LocalFree(text);
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetNamedPipeInfo(SafeHandle pipe, out uint flags, out uint outBufferSize, out uint inBufferSize,
        out uint maxInstances);

    [LibraryImport("advapi32.dll")]
    private static partial int GetSecurityInfo(SafeHandle handle, int objectType, uint securityInfo, nint owner, nint group, nint dacl,
        nint sacl, out nint securityDescriptor);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertSecurityDescriptorToStringSecurityDescriptorW(nint securityDescriptor, uint revision, uint securityInfo,
        out nint text, out uint length);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
