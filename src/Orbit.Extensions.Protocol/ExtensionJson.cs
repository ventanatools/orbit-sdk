// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Seth Cottle

using System.Text;
using System.Text.Json;

namespace Orbit.Extensions.Protocol;

/// <summary>Strict JSON primitives shared by host and companion readers.</summary>
public static class ExtensionJson
{
    private static readonly UTF8Encoding Utf8 = new(false, true);

    /// <summary>Reads one bounded UTF-8 object. The caller owns the returned document and checks every object's fields.</summary>
    public static JsonDocument ReadObject(ReadOnlyMemory<byte> bytes, int maxBytes = ExtensionWire.MaxFrameBytes, int maxDepth = 8)
    {
        if (bytes.Length is 0 || bytes.Length > maxBytes) throw Invalid();
        try
        {
            _ = Utf8.GetCharCount(bytes.Span);
            var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = maxDepth });
            if (document.RootElement.ValueKind == JsonValueKind.Object) return document;
            document.Dispose();
            throw Invalid();
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        {
            throw Invalid();
        }
    }

    /// <summary>Checks required, optional and duplicate names at this object level, with ordinal matching.</summary>
    public static bool HasFields(JsonElement element, IReadOnlyCollection<string> required,
        IReadOnlyCollection<string>? optional = null)
    {
        ArgumentNullException.ThrowIfNull(required);
        if (element.ValueKind != JsonValueKind.Object) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!seen.Add(property.Name) || (!required.Contains(property.Name, StringComparer.Ordinal)
                && optional?.Contains(property.Name, StringComparer.Ordinal) != true)) return false;
        return required.All(seen.Contains);
    }

    /// <summary>Refuses unknown, repeated or missing members without including peer content in the error.</summary>
    public static void RequireFields(JsonElement element, IReadOnlyCollection<string> required,
        IReadOnlyCollection<string>? optional = null)
    {
        if (!HasFields(element, required, optional)) throw Invalid();
    }

    private static InvalidDataException Invalid() => new("Invalid extension message.");
}
