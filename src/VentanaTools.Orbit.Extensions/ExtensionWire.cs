// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VentanaTools.Orbit.Extensions;

/// <summary>The bounded versioned wire format. No reflection or foreign types are read.</summary>
public static class ExtensionWire
{
    public const int ProtocolVersion = 2;
    public const int MaxFrameBytes = 65_536;
    public static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(5);
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string Proof(ReadOnlySpan<byte> secret, string role, string registrationId, string clientNonce, string serverNonce,
        int protocolVersion = ProtocolVersion)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(protocolVersion, ProtocolVersion);
        return Convert.ToBase64String(HMACSHA256.HashData(secret,
            Utf8.GetBytes($"Orbit.Extensions.v2\n{role}\n{registrationId}\n{clientNonce}\n{serverNonce}")));
    }

    public static bool MatchesProof(string? actual, string expected) =>
        TryBytes(actual, out var a) && TryBytes(expected, out var b) && CryptographicOperations.FixedTimeEquals(a, b);

    public static bool TryBytes(string? value, out byte[] bytes)
    {
        bytes = new byte[32];
        return value is { Length: 44 } && Convert.TryFromBase64String(value, bytes, out var count)
            && count == 32 && Convert.ToBase64String(bytes) == value;
    }

    /// <summary>Wait indefinitely for an idle peer, but bound every started frame.</summary>
    public static async Task<byte[]?> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) == 0) return null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(FrameTimeout);
        await stream.ReadExactlyAsync(header.AsMemory(1), deadline.Token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > MaxFrameBytes) throw new InvalidDataException("Invalid extension frame size.");
        var data = new byte[(int)length];
        await stream.ReadExactlyAsync(data, deadline.Token).ConfigureAwait(false);
        _ = Utf8.GetCharCount(data); // Reject malformed UTF-8 before JSON can replace anything.
        return data;
    }

    public static async Task WriteAsync(Stream stream, ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        if (data.Length is 0 or > MaxFrameBytes) throw new InvalidDataException("Invalid extension frame size.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(data, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static byte[] Message(string type, params (string Name, string Value)[] values)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteString("type", type);
            foreach (var (name, value) in values) json.WriteString(name, value);
            json.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Only flat protocol objects; duplicates, unknown members and missing members fail closed.</summary>
    public static JsonDocument ReadMessage(byte[] data, string type, params string[] members)
    {
        var document = ExtensionJson.ReadObject(data);
        try
        {
            var root = document.RootElement;
            ExtensionJson.RequireFields(root, ["type", .. members]);
            if (Text(root, "type") != type)
                throw new InvalidDataException("Invalid extension message.");
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    public static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: <= MaxFrameBytes } text
            ? text : throw new InvalidDataException("Invalid extension message.");
}
