// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace VentanaTools.Orbit.Extensions.Wire;

/// <summary>
/// Pipe names (contract §2.5):
/// <c>Ventana.Extensions.v3.&lt;host-id&gt;.&lt;edition&gt;.&lt;user-hash&gt;.&lt;registration-id&gt;</c>.
/// </summary>
public static class PipeNames
{
    /// <summary>The product-neutral prefix of every pipe name; <c>v3</c> names the transport generation.</summary>
    public const string Prefix = "Ventana.Extensions.v3.";

    /// <summary>Builds a pipe name from its parts.</summary>
    /// <param name="hostId">The host id (contract §2.4).</param>
    /// <param name="edition">The host's edition: 1 to 32 of <c>[a-z0-9-]</c>; opaque to clients.</param>
    /// <param name="userHash">16 lowercase hexadecimal digits (see <see cref="UserHash"/>).</param>
    /// <param name="registrationId">The registration id, a GUID in wire form.</param>
    /// <returns>The pipe name, without the <c>\\.\pipe\</c> prefix.</returns>
    /// <exception cref="ArgumentException">A part does not match its grammar.</exception>
    public static string Create(string hostId, string edition, string userHash, string registrationId)
    {
        if (!TextRules.IsHostId(hostId))
        {
            throw new ArgumentException("The host id does not match the host-id grammar.", nameof(hostId));
        }

        if (!Grammars.IsEdition(edition))
        {
            throw new ArgumentException("The edition does not match the edition grammar.", nameof(edition));
        }

        if (!Grammars.IsUserHash(userHash))
        {
            throw new ArgumentException("The user hash is not 16 lowercase hexadecimal digits.", nameof(userHash));
        }

        if (!Grammars.IsGuid(registrationId))
        {
            throw new ArgumentException("The registration id is not a GUID in wire form.", nameof(registrationId));
        }

        return Prefix + hostId + "." + edition + "." + userHash + "." + registrationId;
    }

    /// <summary>Splits a pipe name into its parts when it matches the grammar exactly.</summary>
    /// <param name="value">The pipe name.</param>
    /// <param name="parts">The parts, when it matches.</param>
    /// <returns>Whether <paramref name="value"/> is a valid pipe name.</returns>
    public static bool TryParse(string? value, [NotNullWhen(true)] out PipeNameParts? parts)
    {
        parts = null;
        if (value is null || !value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var segments = value[Prefix.Length..].Split('.');
        if (segments.Length != 4 || !TextRules.IsHostId(segments[0]) || !Grammars.IsEdition(segments[1])
            || !Grammars.IsUserHash(segments[2]) || !Grammars.IsGuid(segments[3]))
        {
            return false;
        }

        parts = new PipeNameParts
        {
            HostId = segments[0],
            Edition = segments[1],
            UserHash = segments[2],
            RegistrationId = segments[3],
        };
        return true;
    }

    /// <summary>The user hash: the first 16 lowercase hexadecimal digits of SHA-256 over the UTF-8 SID string.</summary>
    /// <param name="userSid">The user's SID string, for example <c>S-1-5-21-…</c>.</param>
    /// <returns>16 lowercase hexadecimal digits.</returns>
    public static string UserHash(string userSid)
    {
        ArgumentException.ThrowIfNullOrEmpty(userSid);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(userSid)))[..16];
    }
}

/// <summary>The parts of a pipe name (contract §2.5).</summary>
public sealed class PipeNameParts
{
    /// <summary>The host id.</summary>
    public required string HostId { get; init; }

    /// <summary>The edition, opaque to clients.</summary>
    public required string Edition { get; init; }

    /// <summary>The user hash.</summary>
    public required string UserHash { get; init; }

    /// <summary>The registration id.</summary>
    public required string RegistrationId { get; init; }
}

/// <summary>Which proof a transcript is for (contract §7.3.4).</summary>
public enum ProofRole
{
    /// <summary><c>server</c>: the host's proof in <c>challenge</c>.</summary>
    Server = 1,

    /// <summary><c>client</c>: the companion's proof in <c>authenticate</c>.</summary>
    Client = 2,
}

/// <summary>
/// The values both proofs cover (contract §7.3.4): the host's identity, the registration, both
/// nonces, the negotiated version, both offers and the manifest hash.
/// </summary>
public sealed class HandshakeTranscript
{
    /// <summary>The challenge's <c>host.id</c>.</summary>
    public required string HostId { get; init; }

    /// <summary>The challenge's <c>host.version</c>.</summary>
    public required string HostVersion { get; init; }

    /// <summary>The hello's <c>registrationId</c>.</summary>
    public required string RegistrationId { get; init; }

    /// <summary>The hello's <c>clientNonce</c>.</summary>
    public required string ClientNonce { get; init; }

    /// <summary>The challenge's <c>serverNonce</c>.</summary>
    public required string ServerNonce { get; init; }

    /// <summary>The negotiated version.</summary>
    public required int Version { get; init; }

    /// <summary>The hello's <c>minVersion</c>.</summary>
    public required int MinVersion { get; init; }

    /// <summary>The hello's <c>maxVersion</c>.</summary>
    public required int MaxVersion { get; init; }

    /// <summary>The hello's capability list.</summary>
    public required IReadOnlyCollection<string> ClientCapabilities { get; init; }

    /// <summary>The challenge's capability list.</summary>
    public required IReadOnlyCollection<string> HostCapabilities { get; init; }

    /// <summary>The hello's <c>manifestHash</c>.</summary>
    public required string ManifestHash { get; init; }

    /// <summary>
    /// The UTF-8 transcript: label, role, host id, host version, registration id, both nonces,
    /// the three versions, both capability lists (each sorted ordinally and joined with commas)
    /// and the manifest hash, joined with LF and with no final LF.
    /// </summary>
    /// <param name="role">The proof the transcript is for.</param>
    /// <returns>The transcript bytes.</returns>
    /// <exception cref="ArgumentException">A value is null or contains a line feed or a comma.</exception>
    public byte[] ToBytes(ProofRole role)
    {
        var roleToken = WireTokens.ProofRoleToken(role) ?? throw new ArgumentException("The role is not defined.", nameof(role));
        string[] fields =
        [
            ProtocolVersions.Label,
            roleToken,
            Field(HostId),
            Field(HostVersion),
            Field(RegistrationId),
            Field(ClientNonce),
            Field(ServerNonce),
            Version.ToString(CultureInfo.InvariantCulture),
            MinVersion.ToString(CultureInfo.InvariantCulture),
            MaxVersion.ToString(CultureInfo.InvariantCulture),
            Capabilities(ClientCapabilities),
            Capabilities(HostCapabilities),
            Field(ManifestHash),
        ];
        return Encoding.UTF8.GetBytes(string.Join('\n', fields));
    }

    private static string Capabilities(IReadOnlyCollection<string>? values)
    {
        if (values is null)
        {
            throw new ArgumentException("A capability list is null.");
        }

        return string.Join(',', values.Select(Field).Order(StringComparer.Ordinal));
    }

    private static string Field(string? value)
    {
        if (value is null || value.Contains('\n', StringComparison.Ordinal) || value.Contains(',', StringComparison.Ordinal))
        {
            throw new ArgumentException("A transcript value is null or contains a line feed or a comma.");
        }

        return value;
    }
}

/// <summary>The handshake's nonces, version negotiation and proofs (contract §7.3).</summary>
public static class Handshake
{
    /// <summary>32 fresh random bytes in canonical Base64.</summary>
    /// <returns>A nonce.</returns>
    public static string NewNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>The highest version in both ranges, or null when they do not overlap.</summary>
    /// <param name="clientMin">The hello's <c>minVersion</c>.</param>
    /// <param name="clientMax">The hello's <c>maxVersion</c>.</param>
    /// <param name="hostMin">The host's lowest version.</param>
    /// <param name="hostMax">The host's highest version.</param>
    /// <returns>The negotiated version, or null.</returns>
    public static int? Negotiate(int clientMin, int clientMax, int hostMin, int hostMax)
    {
        var high = Math.Min(clientMax, hostMax);
        var low = Math.Max(clientMin, hostMin);
        return clientMin <= clientMax && hostMin <= hostMax && high >= low ? high : null;
    }

    /// <summary>A proof: canonical Base64 of HMAC-SHA256 keyed with the 32-byte secret over the transcript.</summary>
    /// <param name="secret">The 32 decoded secret bytes.</param>
    /// <param name="transcript">The transcript.</param>
    /// <param name="role">The proof's role.</param>
    /// <returns>The proof.</returns>
    /// <exception cref="ArgumentException">The secret is not 32 bytes, or the transcript is invalid.</exception>
    public static string ComputeProof(ReadOnlySpan<byte> secret, HandshakeTranscript transcript, ProofRole role)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        if (secret.Length != 32)
        {
            throw new ArgumentException("The secret is not 32 bytes.", nameof(secret));
        }

        Span<byte> mac = stackalloc byte[32];
        HMACSHA256.HashData(secret, transcript.ToBytes(role), mac);
        return Convert.ToBase64String(mac);
    }

    /// <summary>Verifies a peer's proof in constant time over the decoded bytes.</summary>
    /// <param name="secret">The 32 decoded secret bytes.</param>
    /// <param name="proof">The received proof.</param>
    /// <param name="transcript">The transcript.</param>
    /// <param name="role">The proof's role.</param>
    /// <returns>Whether the proof is canonical Base64 of 32 bytes and equals the expected proof.</returns>
    public static bool VerifyProof(ReadOnlySpan<byte> secret, string? proof, HandshakeTranscript transcript, ProofRole role)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        if (secret.Length != 32 || proof is null)
        {
            return false;
        }

        Span<byte> received = stackalloc byte[32];
        Span<byte> expected = stackalloc byte[32];
        if (!Grammars.TryDecodeKey32(proof, received))
        {
            return false;
        }

        HMACSHA256.HashData(secret, transcript.ToBytes(role), expected);
        return CryptographicOperations.FixedTimeEquals(received, expected);
    }
}
