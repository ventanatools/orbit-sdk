// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>
/// Creates the simulation's folder, pairing file and pipe with their security descriptor in the
/// creating call, so none of them ever exists without it. The descriptors carry a mandatory label,
/// which .NET's access-control types cannot express (<see cref="System.IO.Pipes.PipeSecurity"/>
/// drops label entries). No privilege is needed for a label at or below the caller's own level.
/// </summary>
[SupportedOSPlatform("windows")]
internal static partial class LabelledObjects
{
    private const int ErrorAccessDenied = 5;
    private const uint GenericWrite = 0x40000000;
    private const uint CreateNew = 1;
    private const uint FileAttributeNormal = 0x80;
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagFirstPipeInstance = 0x00080000;
    private const uint FileFlagOverlapped = 0x40000000;
    private const uint PipeTypeByte = 0x00000000;
    private const uint PipeReadModeByte = 0x00000000;
    private const uint PipeWait = 0x00000000;
    private const uint PipeRejectRemoteClients = 0x00000008;
    private const uint PipeBufferBytes = 4_096;

    /// <summary>Creates a folder with the security descriptor <paramref name="sddl"/>.</summary>
    /// <exception cref="IOException">The folder could not be created.</exception>
    public static void CreateDirectory(string path, string sddl)
    {
        var descriptor = Descriptor(sddl);
        try
        {
            var attributes = Attributes(descriptor);
            if (!CreateDirectoryW(path, in attributes))
            {
                throw new IOException("The simulation folder could not be created.", new Win32Exception(Marshal.GetLastPInvokeError()));
            }
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    /// <summary>Creates a new file for writing with the security descriptor <paramref name="sddl"/>; it fails when the file exists.</summary>
    /// <exception cref="IOException">The file could not be created.</exception>
    public static SafeFileHandle CreateNewFile(string path, string sddl)
    {
        var descriptor = Descriptor(sddl);
        try
        {
            var attributes = Attributes(descriptor);
            var handle = CreateFileW(path, GenericWrite, 0, in attributes, CreateNew, FileAttributeNormal, 0);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new IOException("The simulation pairing file could not be created.", new Win32Exception(error));
            }

            return handle;
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    /// <summary>
    /// Creates the first and only instance of <c>\\.\pipe\</c><paramref name="pipeName"/>: duplex, byte mode,
    /// overlapped, rejecting remote clients, with the security descriptor <paramref name="sddl"/>.
    /// </summary>
    /// <exception cref="IOException">
    /// The name is in use by a server that allows one instance (<c>ERROR_PIPE_BUSY</c>), or the pipe could
    /// not be created for another reason.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">
    /// Access was denied (<c>ERROR_ACCESS_DENIED</c>), which includes a name in use by a server that allows
    /// more than one instance, since a first instance cannot be created then.
    /// </exception>
    public static SafePipeHandle CreateServerPipe(string pipeName, string sddl)
    {
        var descriptor = Descriptor(sddl);
        try
        {
            var attributes = Attributes(descriptor);
            var handle = CreateNamedPipeW(@"\\.\pipe\" + pipeName,
                PipeAccessDuplex | FileFlagFirstPipeInstance | FileFlagOverlapped,
                PipeTypeByte | PipeReadModeByte | PipeWait | PipeRejectRemoteClients,
                1, PipeBufferBytes, PipeBufferBytes, 0, in attributes);
            if (handle.IsInvalid)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw error == ErrorAccessDenied
                    ? new UnauthorizedAccessException("The simulation pipe could not be created.", new Win32Exception(error))
                    : new IOException("The simulation pipe could not be created.", new Win32Exception(error));
            }

            return handle;
        }
        finally
        {
            LocalFree(descriptor);
        }
    }

    private static SecurityAttributes Attributes(nint descriptor) =>
        new() { Length = Unsafe.SizeOf<SecurityAttributes>(), SecurityDescriptor = descriptor };

    /// <summary>A self-relative security descriptor from SDDL, which the caller frees with <c>LocalFree</c>.</summary>
    private static nint Descriptor(string sddl) =>
        ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, out _)
            ? descriptor
            : throw new Win32Exception(Marshal.GetLastPInvokeError());

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public nint SecurityDescriptor;
        public int InheritHandle;
    }

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, uint revision, out nint descriptor,
        out uint length);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateDirectoryW(string path, in SecurityAttributes securityAttributes);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string path, uint desiredAccess, uint shareMode, in SecurityAttributes securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafePipeHandle CreateNamedPipeW(string name, uint openMode, uint pipeMode, uint maxInstances, uint outBufferSize,
        uint inBufferSize, uint defaultTimeout, in SecurityAttributes securityAttributes);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
