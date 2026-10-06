// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Buffers;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using VentanaTools.Orbit.Extensions.Wire;

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>
/// The temporary registration of a simulation (contract §11.2): a random registration id and
/// secret, the pipe name for edition <c>sim</c>, and a pairing file in a fresh folder under
/// <c>%TEMP%</c>, both created with an access-control list that grants only the current user.
/// Disposing it zeroes the secret and deletes the file and its folder.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class SimulationRegistration : IDisposable
{
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
        new DirectoryInfo(folder).Create(FolderSecurity(user));
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
            using var stream = new FileInfo(PairingPath).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4_096,
                FileOptions.None, FileSecurity(user));
            stream.Write(buffer.WrittenSpan);
            stream.WriteByte((byte)'\n');
        }
        finally
        {
            buffer.Clear();
        }
    }

    private static FileSecurity FileSecurity(SecurityIdentifier user)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }

    private static DirectorySecurity FolderSecurity(SecurityIdentifier user)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
}
