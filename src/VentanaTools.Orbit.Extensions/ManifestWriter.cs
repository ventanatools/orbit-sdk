// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VentanaTools.Orbit.Extensions;

/// <summary>Writes manifests and computes their manifest hash (contract §B.2).</summary>
public static class ManifestWriter
{
    /// <summary>
    /// Writes <paramref name="manifest"/> as UTF-8 JSON without a byte order mark, with members in
    /// canonical order (the order of contract §3.2 and §3.3) and null optional members left out.
    /// Indented output uses two spaces, LF line endings and a final line feed.
    /// </summary>
    /// <param name="manifest">The manifest; validate it first with <see cref="ManifestReader.Validate"/>.</param>
    /// <param name="indented">Whether to indent.</param>
    /// <returns>The JSON bytes.</returns>
    /// <exception cref="ArgumentException">A required member is null, or an enumeration holds an undefined value.</exception>
    public static byte[] Write(ExtensionManifest manifest, bool indented = true)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        RequireComplete(manifest);
        var tree = ManifestTree.FromModel(manifest);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = indented,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            WriteNode(writer, tree);
        }

        if (indented)
        {
            stream.WriteByte((byte)'\n');
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Computes the manifest hash (contract §B.2): SHA-256, in lowercase hexadecimal, of the RFC 8785
    /// canonical form of the manifest's contract projection, which covers <c>schemaVersion</c>,
    /// <c>id</c> and, for each contribution, its id, <c>provides</c> and settings with their kinds and
    /// choice values. Names, descriptions, glyphs, <c>hosts</c> and <c>$schema</c> do not change it.
    /// </summary>
    /// <param name="manifest">A valid manifest.</param>
    /// <returns>64 lowercase hexadecimal digits.</returns>
    /// <exception cref="ArgumentException">A member the projection needs is null or undefined.</exception>
    public static string ComputeHash(ExtensionManifest manifest) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalProjection(manifest))));

    /// <summary>The RFC 8785 canonical form of the contract projection (contract §B.2).</summary>
    internal static string CanonicalProjection(ExtensionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.Id is null || manifest.Contributions is null)
        {
            throw new ArgumentException("The manifest is not valid.", nameof(manifest));
        }

        var text = new StringBuilder();
        text.Append("{\"contributions\":[");
        var contributions = manifest.Contributions.Select(contribution =>
            contribution?.Id is null ? throw new ArgumentException("The manifest is not valid.", nameof(manifest)) : contribution);
        var first = true;
        foreach (var contribution in contributions.OrderBy(contribution => contribution.Id, StringComparer.Ordinal))
        {
            text.Append(first ? "{" : ",{");
            first = false;
            text.Append("\"id\":");
            Canonical(text, contribution.Id);
            text.Append(",\"provides\":[");
            var provides = new List<string>();
            if (contribution.Provides.HasFlag(Provides.Invoke))
            {
                provides.Add("invoke");
            }

            if (contribution.Provides.HasFlag(Provides.Face))
            {
                provides.Add("face");
            }

            AppendList(text, provides);
            text.Append(']');
            var settings = contribution.Settings ?? throw new ArgumentException("The manifest is not valid.", nameof(manifest));
            if (settings.Count > 0)
            {
                text.Append(",\"settings\":[");
                var firstSetting = true;
                foreach (var setting in settings.OrderBy(setting => setting?.Id, StringComparer.Ordinal))
                {
                    if (setting?.Id is null || setting.Choices is null || WireTokens.SettingKindToken(setting.Kind) is not { } kind)
                    {
                        throw new ArgumentException("The manifest is not valid.", nameof(manifest));
                    }

                    text.Append(firstSetting ? "{" : ",{");
                    firstSetting = false;
                    text.Append("\"choices\":[");
                    AppendList(text, setting.Choices.Select(choice =>
                        choice?.Value ?? throw new ArgumentException("The manifest is not valid.", nameof(manifest))));
                    text.Append("],\"id\":");
                    Canonical(text, setting.Id);
                    text.Append(",\"kind\":");
                    Canonical(text, kind);
                    text.Append('}');
                }

                text.Append(']');
            }

            text.Append('}');
        }

        text.Append("],\"id\":");
        Canonical(text, manifest.Id);
        text.Append(",\"schemaVersion\":");
        text.Append(manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        text.Append('}');
        return text.ToString();
    }

    private static void RequireComplete(ExtensionManifest manifest)
    {
        var complete = manifest.Id is not null && manifest.Name is not null && manifest.Description is not null
            && manifest.Version is not null && manifest.Hosts is not null && manifest.Hosts.All(host => host is not null)
            && (manifest.Publisher is null || manifest.Publisher.Name is not null)
            && (manifest.Requires is null || (manifest.Requires.Capabilities?.All(id => id is not null) ?? false))
            && manifest.Contributions is not null
            && manifest.Contributions.All(contribution => contribution is not null && contribution.Id is not null
                && contribution.Name is not null && contribution.Description is not null && contribution.Glyph is not null
                && contribution.Settings is not null
                && contribution.Settings.All(setting => setting is not null && setting.Id is not null && setting.Name is not null
                    && setting.Default is not null && setting.Choices is not null
                    && setting.Choices.All(choice => choice is not null && choice.Value is not null && choice.Name is not null)));
        if (!complete)
        {
            throw new ArgumentException("A required member of the manifest is null.", nameof(manifest));
        }
    }

    private static void AppendList(StringBuilder text, IEnumerable<string> values)
    {
        var first = true;
        foreach (var value in values.Order(StringComparer.Ordinal))
        {
            if (!first)
            {
                text.Append(',');
            }

            first = false;
            Canonical(text, value);
        }
    }

    /// <summary>An RFC 8785 string: the shortest JSON escapes, lowercase hexadecimal for other controls.</summary>
    private static void Canonical(StringBuilder text, string value)
    {
        text.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"':
                    text.Append("\\\"");
                    break;
                case '\\':
                    text.Append("\\\\");
                    break;
                case '\b':
                    text.Append("\\b");
                    break;
                case '\f':
                    text.Append("\\f");
                    break;
                case '\n':
                    text.Append("\\n");
                    break;
                case '\r':
                    text.Append("\\r");
                    break;
                case '\t':
                    text.Append("\\t");
                    break;
                default:
                    if (c < ' ')
                    {
                        text.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        text.Append(c);
                    }

                    break;
            }
        }

        text.Append('"');
    }

    private static void WriteNode(Utf8JsonWriter writer, JsonNode node)
    {
        switch (node.Kind)
        {
            case JsonKind.Object:
                writer.WriteStartObject();
                foreach (var member in node.Members!)
                {
                    if (member.Name == "settings" && member.Value.Kind == JsonKind.Array && member.Value.Items!.Count == 0)
                    {
                        continue;
                    }

                    writer.WritePropertyName(member.Name);
                    WriteNode(writer, member.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonKind.Array:
                writer.WriteStartArray();
                foreach (var item in node.Items!)
                {
                    WriteNode(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonKind.String:
                writer.WriteStringValue(node.Text);
                break;
            case JsonKind.Number:
                writer.WriteRawValue(node.Text!, skipInputValidation: true);
                break;
            default:
                throw new ArgumentException("The manifest has a null member or an undefined enumeration value.", nameof(node));
        }
    }
}
