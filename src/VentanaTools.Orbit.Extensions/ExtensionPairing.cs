// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Security.Cryptography;
using System.Text.Json;

namespace VentanaTools.Orbit.Extensions;

/// <summary>
/// An explicit pairing export. The key stays private, proofs bind both nonces and the
/// role, and diagnostics never contain credentials. Dispose after the client stops.
/// </summary>
public sealed class ExtensionPairing : IDisposable
{
    public const int MaxBytes = 4096;
    private readonly object _gate = new();
    private readonly byte[] _secret;
    private bool _disposed;

    private ExtensionPairing(int protocolVersion, string pipeName, string registrationId, string extensionId, byte[] secret)
    {
        ProtocolVersion = protocolVersion;
        PipeName = pipeName;
        RegistrationId = registrationId;
        ExtensionId = extensionId;
        _secret = secret;
    }

    public int ProtocolVersion { get; }
    public string PipeName { get; }
    public string RegistrationId { get; }
    public string ExtensionId { get; }

    /// <summary>
    /// Reads exactly the five exported fields, a canonical per-user pipe name and a
    /// 32-byte key. The declaration's expected identity and version cannot be changed
    /// by a pairing file. Invalid content produces a fixed diagnostic.
    /// </summary>
    public static ExtensionPairing Parse(ReadOnlySpan<byte> utf8, string expectedExtensionId, int? expectedProtocolVersion = null)
    {
        if (utf8.Length is 0 or > MaxBytes) throw InvalidPairing();
        byte[]? secret = null;
        try
        {
            using var document = ExtensionJson.ReadObject(utf8.ToArray(), MaxBytes);
            var root = document.RootElement;
            ExtensionJson.RequireFields(root, ["protocolVersion", "pipeName", "registrationId", "extensionId", "secret"]);
            if (!ExtensionIds.IsValid(expectedExtensionId, ExtensionOrigin.ThirdParty)
                || expectedExtensionId.Contains('/', StringComparison.Ordinal)
                || root.GetProperty("protocolVersion") is not { ValueKind: JsonValueKind.Number } version
                || !version.TryGetInt32(out var protocolVersion) || protocolVersion != ExtensionWire.ProtocolVersion
                || (expectedProtocolVersion is { } expected && protocolVersion != expected))
                throw InvalidPairing();

            var pipeName = ExtensionWire.Text(root, "pipeName");
            var registrationId = ExtensionWire.Text(root, "registrationId");
            var extensionId = ExtensionWire.Text(root, "extensionId");
            if (!string.Equals(extensionId, expectedExtensionId, StringComparison.Ordinal)
                || !Guid.TryParseExact(registrationId, "N", out var registration) || registration == Guid.Empty
                || registration.ToString("N") != registrationId
                || !IsPipeName(pipeName, registrationId)
                || !ExtensionWire.TryBytes(ExtensionWire.Text(root, "secret"), out secret))
                throw InvalidPairing();

            var pairing = new ExtensionPairing(protocolVersion, pipeName, registrationId, extensionId, secret);
            secret = null; // Ownership passes to the disposable value only after every check.
            return pairing;
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or ArgumentException)
        {
            throw InvalidPairing();
        }
        finally
        {
            if (secret is not null) CryptographicOperations.ZeroMemory(secret);
        }
    }

    /// <summary>Creates only the client or server proof for canonical 32-byte nonces.</summary>
    public string CreateProof(string role, string clientNonce, string serverNonce)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (role is not ("client" or "server") || !IsNonce(clientNonce) || !IsNonce(serverNonce))
                throw new ArgumentException("Invalid extension proof parameters.");
            return ExtensionWire.Proof(_secret, role, RegistrationId, clientNonce, serverNonce, ProtocolVersion);
        }
    }

    /// <summary>Compares a peer's canonical proof in constant time.</summary>
    public bool VerifyProof(string? proof, string role, string clientNonce, string serverNonce) =>
        ExtensionWire.MatchesProof(proof, CreateProof(role, clientNonce, serverNonce));

    public void Dispose()
    {
        lock (_gate)
        {
            if (!_disposed)
            {
                CryptographicOperations.ZeroMemory(_secret);
                _disposed = true;
            }
        }
        GC.SuppressFinalize(this);
    }

    public override string ToString() => nameof(ExtensionPairing);

    private static bool IsPipeName(string value, string registrationId)
    {
        var parts = value.Split('.');
        return parts.Length == 6 && parts[0] == "Orbit" && parts[1] == "Extensions"
            && parts[2] == "v2"
            && parts[3] is { Length: > 0 and <= 32 }
            && parts[3].All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-')
            && parts[4].Length == 16 && parts[4].All(IsLowerHex)
            && string.Equals(parts[5], registrationId, StringComparison.Ordinal);
    }

    private static bool IsLowerHex(char value) => char.IsAsciiDigit(value) || value is >= 'a' and <= 'f';

    private static bool IsNonce(string? value)
    {
        var valid = ExtensionWire.TryBytes(value, out var bytes);
        CryptographicOperations.ZeroMemory(bytes);
        return valid;
    }

    private static InvalidDataException InvalidPairing() => new("Invalid extension pairing.");
}
