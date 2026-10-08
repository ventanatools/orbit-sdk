// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Collections.Frozen;
using System.Security.Cryptography;
using VentanaTools.Orbit.Extensions.Wire;

namespace VentanaTools.Orbit.Extensions;

/// <summary>A pairing file's mode (contract §6.3).</summary>
public enum PairingMode
{
    /// <summary><c>Persistent</c>: valid until the person revokes access or removes the extension.</summary>
    Persistent = 1,

    /// <summary><c>PerLaunch</c>: reserved for host-managed activation (contract §6.5); this version refuses it.</summary>
    PerLaunch = 2,
}

/// <summary>Options for <see cref="PairingReader"/>.</summary>
public sealed class PairingReadOptions
{
    /// <summary>The companion manifest's extension id, which the file's <c>extensionId</c> must equal.</summary>
    public required string ExpectedExtensionId { get; init; }

    /// <summary>The companion manifest's <c>hosts</c>; when set, the file's <c>hostId</c> must be one of them.</summary>
    public IReadOnlyCollection<string>? ManifestHosts { get; init; }
}

/// <summary>
/// Reads pairing files (contract §6): the credential a host writes, on request, so a companion
/// can authenticate to one registration. Problems are reported as diagnostics whose codes are the
/// <c>pairing.*</c> reason codes and the <c>json.*</c> codes of contract §4.4.
/// </summary>
/// <remarks>
/// The secret is never materialized as a string, and every buffer that held it is zeroed. A
/// diagnostic never contains any value from the file.
/// </remarks>
public static class PairingReader
{
    /// <summary>The largest pairing file, in bytes.</summary>
    public const int MaxBytes = 4096;

    private static readonly FrozenSet<string> Raw = new[] { "secret" }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly string[] Known =
        ["$schema", "pairingVersion", "mode", "hostId", "pipeName", "registrationId", "extensionId", "secret"];

    private static readonly string[] KnownPerLaunch = [.. Known, "credentialId", "expires", "packageFamilyName"];

    /// <summary>Reads a pairing file from its bytes.</summary>
    /// <param name="utf8">The file's bytes.</param>
    /// <param name="options">The companion's expectations.</param>
    /// <returns>The pairing when the file is valid (the caller owns and disposes it), and every diagnostic.</returns>
    /// <exception cref="ArgumentException"><see cref="PairingReadOptions.ExpectedExtensionId"/> is not an extension id.</exception>
    public static ReadResult<Pairing> Read(ReadOnlySpan<byte> utf8, PairingReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!ExtensionIds.IsValid(options.ExpectedExtensionId, IdOrigin.ThirdParty, [])
            || options.ExpectedExtensionId.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException("The expected extension id is not an extension id.", nameof(options));
        }

        var bag = new DiagnosticBag();
        var pairing = ReadCore(utf8, options, bag);
        if (bag.HasErrors)
        {
            pairing?.Dispose();
            pairing = null;
        }

        return new ReadResult<Pairing>(pairing, bag.ToList());
    }

    /// <summary>Reads a pairing file, never reading more than <see cref="MaxBytes"/> + 1 bytes.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="options">The companion's expectations.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The pairing when the file is valid (the caller owns and disposes it), and every diagnostic.</returns>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    public static async Task<ReadResult<Pairing>> ReadFileAsync(string path, PairingReadOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var bytes = await BoundedFile.ReadAsync(path, MaxBytes, cancellationToken).ConfigureAwait(false);
        try
        {
            return Read(bytes, options);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    /// <summary>
    /// The default location of a pairing file (contract §6.6):
    /// <c>%USERPROFILE%\.ventana\pairings\&lt;host-id&gt;\&lt;extension-id&gt;.pairing.json</c>.
    /// </summary>
    /// <param name="hostId">The host id.</param>
    /// <param name="extensionId">The extension id.</param>
    /// <returns>The full path.</returns>
    /// <exception cref="ArgumentException">An argument does not match its grammar.</exception>
    public static string DefaultPath(string hostId, string extensionId)
    {
        if (!TextRules.IsHostId(hostId))
        {
            throw new ArgumentException("The host id does not match the host-id grammar.", nameof(hostId));
        }

        if (!ExtensionIds.IsValid(extensionId, IdOrigin.ThirdParty, []) || extensionId.Contains('/', StringComparison.Ordinal))
        {
            throw new ArgumentException("The extension id is not an extension id.", nameof(extensionId));
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ventana", "pairings", hostId,
            extensionId + ".pairing.json");
    }

    private static Pairing? ReadCore(ReadOnlySpan<byte> utf8, PairingReadOptions options, DiagnosticBag bag)
    {
        if (utf8.Length > MaxBytes)
        {
            bag.Report(ReasonCode.PairingTooLarge.Value, string.Empty);
            return null;
        }

        if (JsonFile.HasWideByteOrderMark(utf8))
        {
            bag.Report(ReasonCode.PairingEncoding.Value, string.Empty);
            return null;
        }

        var document = JsonFile.StripUtf8Bom(utf8);
        var buffer = document.ToArray();
        try
        {
            var parse = JsonTree.Parse(buffer, Raw);
            if (parse.Failure == JsonFailure.Utf8)
            {
                bag.Report(ReasonCode.PairingEncoding.Value, string.Empty);
                return null;
            }

            var v = new JsonValidator(bag, buffer, 0, buffer.Length, null);
            if (parse.Root is null)
            {
                v.ParseFailure(parse);
                return null;
            }

            v.Duplicates(parse);
            return Validate(parse.Root, buffer, v, options);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
        }
    }

    private static Pairing? Validate(JsonNode root, byte[] document, JsonValidator v, PairingReadOptions options)
    {
        if (!v.Expect(root, string.Empty, JsonKind.Object))
        {
            return null;
        }

        if (root.Members!.FirstOrDefault(member => member.Name == "pairingVersion") is { } versionMember
            && versionMember.Value.TryGetInteger(out var version) && version != 3)
        {
            v.Report(ReasonCode.PairingVersionUnsupported.Value, "/pairingVersion", versionMember.Value);
            return null;
        }

        var perLaunch = root.Members!.Any(member => member.Name == "mode" && member.Value.Kind == JsonKind.String
            && member.Value.Text == "PerLaunch");
        var m = v.Members(root, string.Empty, perLaunch ? KnownPerLaunch : Known);
        v.SchemaMember(m, string.Empty, null);
        if (v.Required(m, "pairingVersion", string.Empty, root) is { } versionNode)
        {
            v.AsInteger(versionNode, "/pairingVersion");
        }

        if (v.Required(m, "mode", string.Empty, root) is { } modeNode && v.AsString(modeNode, "/mode") is { } mode
            && WireTokens.ParsePairingMode(mode) is not PairingMode.Persistent)
        {
            v.Report(ReasonCode.PairingModeUnsupported.Value, "/mode", modeNode);
        }

        string? hostId = null;
        if (v.Required(m, "hostId", string.Empty, root) is { } hostNode && v.AsString(hostNode, "/hostId") is { } host)
        {
            if (!TextRules.IsHostId(host)
                || (options.ManifestHosts is { } hosts && !hosts.Contains(host, StringComparer.Ordinal)))
            {
                v.Report(ReasonCode.PairingHostNotListed.Value, "/hostId", hostNode);
            }
            else
            {
                hostId = host;
            }
        }

        string? registrationId = null;
        if (v.Required(m, "registrationId", string.Empty, root) is { } registrationNode
            && v.AsString(registrationNode, "/registrationId") is { } registration)
        {
            if (Grammars.IsGuid(registration))
            {
                registrationId = registration;
            }
            else
            {
                v.Report(ReasonCode.PairingRegistrationInvalid.Value, "/registrationId", registrationNode);
            }
        }

        string? pipeName = null;
        if (v.Required(m, "pipeName", string.Empty, root) is { } pipeNode && v.AsString(pipeNode, "/pipeName") is { } pipe)
        {
            if (!PipeNames.TryParse(pipe, out var parts)
                || (hostId is not null && !string.Equals(parts.HostId, hostId, StringComparison.Ordinal))
                || (registrationId is not null && !string.Equals(parts.RegistrationId, registrationId, StringComparison.Ordinal)))
            {
                v.Report(ReasonCode.PairingPipeNameInvalid.Value, "/pipeName", pipeNode);
            }
            else
            {
                pipeName = pipe;
            }
        }

        string? extensionId = null;
        if (v.Required(m, "extensionId", string.Empty, root) is { } extensionNode
            && v.AsString(extensionNode, "/extensionId") is { } extension)
        {
            if (string.Equals(extension, options.ExpectedExtensionId, StringComparison.Ordinal))
            {
                extensionId = extension;
            }
            else
            {
                v.Report(ReasonCode.PairingExtensionMismatch.Value, "/extensionId", extensionNode);
            }
        }

        byte[]? secret = null;
        if (v.Required(m, "secret", string.Empty, root) is { } secretNode && v.Expect(secretNode, "/secret", JsonKind.String))
        {
            secret = DecodeSecret(document, secretNode);
            if (secret is null)
            {
                v.Report(ReasonCode.PairingSecretInvalid.Value, "/secret", secretNode);
            }
        }

        if (v.Bag.HasErrors || hostId is null || registrationId is null || pipeName is null || extensionId is null || secret is null)
        {
            if (secret is not null)
            {
                CryptographicOperations.ZeroMemory(secret);
            }

            return null;
        }

        return new Pairing(hostId, pipeName, registrationId, extensionId, secret);
    }

    private static byte[]? DecodeSecret(byte[] document, JsonNode node)
    {
        Span<byte> text = stackalloc byte[512];
        var secret = new byte[32];
        try
        {
            var length = node.IsRaw ? JsonTree.CopyRawUtf8(document, node, text) : -1;
            if (length != 44 || !Grammars.TryDecodeKey32(text[..length], secret))
            {
                CryptographicOperations.ZeroMemory(secret);
                return null;
            }

            return secret;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(text);
        }
    }
}

/// <summary>
/// A pairing: the credential for one registration (contract §6). Whoever reads it owns it and
/// disposes it, which zeroes the secret. The secret is never exposed: proofs are computed here.
/// </summary>
/// <remarks>Instances are safe to use from several threads.</remarks>
public sealed class Pairing : IDisposable
{
    private readonly object _gate = new();
    private readonly byte[] _secret;
    private bool _disposed;

    internal Pairing(string hostId, string pipeName, string registrationId, string extensionId, byte[] secret)
    {
        HostId = hostId;
        PipeName = pipeName;
        RegistrationId = registrationId;
        ExtensionId = extensionId;
        _secret = secret;
    }

    /// <summary>The pairing file version: 3.</summary>
    public int PairingVersion { get; } = 3;

    /// <summary>The mode: <see cref="PairingMode.Persistent"/> in this version.</summary>
    public PairingMode Mode { get; } = PairingMode.Persistent;

    /// <summary>The host id the credential was issued under.</summary>
    public string HostId { get; }

    /// <summary>The pipe name (contract §2.5), without the <c>\\.\pipe\</c> prefix.</summary>
    public string PipeName { get; }

    /// <summary>The registration id.</summary>
    public string RegistrationId { get; }

    /// <summary>The extension id.</summary>
    public string ExtensionId { get; }

    /// <summary>Computes a proof over <paramref name="transcript"/> with this pairing's secret.</summary>
    /// <param name="transcript">The transcript.</param>
    /// <param name="role">The proof's role.</param>
    /// <returns>The proof.</returns>
    /// <exception cref="ObjectDisposedException">The pairing was disposed.</exception>
    public string ComputeProof(HandshakeTranscript transcript, ProofRole role)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Handshake.ComputeProof(_secret, transcript, role);
        }
    }

    /// <summary>Verifies a peer's proof over <paramref name="transcript"/> in constant time.</summary>
    /// <param name="proof">The received proof.</param>
    /// <param name="transcript">The transcript.</param>
    /// <param name="role">The proof's role.</param>
    /// <returns>Whether the proof is valid.</returns>
    /// <exception cref="ObjectDisposedException">The pairing was disposed.</exception>
    public bool VerifyProof(string? proof, HandshakeTranscript transcript, ProofRole role)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return Handshake.VerifyProof(_secret, proof, transcript, role);
        }
    }

    /// <summary>Zeroes the secret. Further proofs throw <see cref="ObjectDisposedException"/>.</summary>
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
    }

    /// <summary>A description without the secret.</summary>
    /// <returns>The type name and the registration id.</returns>
    public override string ToString() => nameof(Pairing) + " " + RegistrationId;
}
