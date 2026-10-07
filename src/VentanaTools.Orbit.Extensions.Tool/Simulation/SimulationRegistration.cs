// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Buffers;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using VentanaTools.Orbit.Extensions.Wire;

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>
/// The temporary registration of a simulation (contract §11.2): a random registration id and
/// secret, the pipe name for edition <c>sim</c>, and a pairing file in a fresh folder under
/// <c>%TEMP%</c>, both created with an access-control list that grants only the current user and a
/// Medium mandatory label with no-read-up, so a Low-integrity process of the same user can neither
/// list the folder nor read the secret. Disposing it zeroes the secret and deletes the file and its
/// folder.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class SimulationRegistration : IDisposable
{
    private const uint GenericWrite = 0x40000000;
    private const uint CreateNew = 1;
    private const uint FileAttributeNormal = 0x80;

    /// <summary>The edition a simulated host uses in its pipe name (contract §2.5).</summary>
    public const string Edition = "sim";

    private SimulationRegistration(string hostId, string registrationId, string pipeName, byte[] secret, string folder, string pairingPath)
    {
        HostId = hostId;
        RegistrationId = registrationId;
        PipeName = pipeName;
        Secret = secret;
        Folder = folder;
        PairingPath = pairingPath;
    }

    public string HostId { get; }

    public string RegistrationId { get; }

    public string PipeName { get; }

    /// <summary>The 32 secret bytes; zeroed on dispose.</summary>
    public byte[] Secret { get; }

    public string Folder { get; }

    public string PairingPath { get; }

    public static SimulationRegistration Create(string hostId, string extensionId)
    {
        string registrationId;
        do
        {
            registrationId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        }
        while (registrationId.All(c => c == '0'));

        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new UnauthorizedAccessException("The current user has no SID.");
        var pipeName = PipeNames.Create(hostId, Edition, PipeNames.UserHash(user.Value), registrationId);
        var secret = RandomNumberGenerator.GetBytes(32);
        var folder = Path.Combine(Path.GetTempPath(), "ventana-simulate-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant());
        CreateDirectory(folder, FolderDescriptor(user));
        var path = Path.Combine(folder, extensionId + ".pairing.json");
        var registration = new SimulationRegistration(hostId, registrationId, pipeName, secret, folder, path);
        try
        {
            registration.WritePairing(extensionId, user);
        }
        catch
        {
            registration.Dispose();
            throw;
        }

        return registration;
    }

    public void Dispose()
    {
        CryptographicOperations.ZeroMemory(Secret);
        try
        {
            if (File.Exists(PairingPath))
            {
                File.Delete(PairingPath);
            }

            if (Directory.Exists(Folder))
            {
                Directory.Delete(Folder, recursive: true);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The folder is in %TEMP% and readable only by this user; Windows cleans it up eventually.
        }
    }

    /// <summary>Writes the pairing file of contract §6.3: UTF-8 without a byte order mark, LF line endings, two-space indentation.</summary>
    private void WritePairing(string extensionId, SecurityIdentifier user)
    {
        var buffer = new ArrayBufferWriter<byte>(1_024);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("pairingVersion", 3);
            writer.WriteString("mode", "Persistent");
            writer.WriteString("hostId", HostId);
            writer.WriteString("pipeName", PipeName);
            writer.WriteString("registrationId", RegistrationId);
            writer.WriteString("extensionId", extensionId);
            writer.WriteBase64String("secret", Secret);
            writer.WriteEndObject();
        }

        try
        {
            using var stream = new FileStream(CreateNewFile(PairingPath, FileDescriptor(user)), FileAccess.Write);
            stream.Write(buffer.WrittenSpan);
            stream.WriteByte((byte)'\n');
        }
        finally
        {
            buffer.Clear();
        }
    }

    /// <summary>
    /// The folder: full control for the current user only, inherited by what it holds, and a Medium
    /// mandatory label that refuses reads, writes and execution from a lower integrity level.
    /// </summary>
    internal static string FolderDescriptor(SecurityIdentifier user) => "D:P(A;OICI;FA;;;" + user.Value + ")S:(ML;OICI;NRNWNX;;;ME)";

    /// <summary>The pairing file: as <see cref="FolderDescriptor"/>, without inheritance.</summary>
    internal static string FileDescriptor(SecurityIdentifier user) => "D:P(A;;FA;;;" + user.Value + ")S:(ML;;NRNWNX;;;ME)";

    /// <summary>
    /// Creates the folder with its security descriptor in the same call, so it never exists without
    /// it. .NET's access-control types cannot express a mandatory label.
    /// </summary>
    private static void CreateDirectory(string path, string sddl)
    {
        var descriptor = Descriptor(sddl);
        try
        {
            var attributes = new SecurityAttributes { Length = Unsafe.SizeOf<SecurityAttributes>(), SecurityDescriptor = descriptor };
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

    /// <summary>Creates a new file with its security descriptor in the same call; it fails when the file exists.</summary>
    private static SafeFileHandle CreateNewFile(string path, string sddl)
    {
        var descriptor = Descriptor(sddl);
        try
        {
            var attributes = new SecurityAttributes { Length = Unsafe.SizeOf<SecurityAttributes>(), SecurityDescriptor = descriptor };
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

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint memory);
}
