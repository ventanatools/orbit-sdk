// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Collections.ObjectModel;

namespace VentanaTools.Orbit.Extensions;

/// <summary>Options for <see cref="StringsReader"/>.</summary>
public sealed class StringsReadOptions
{
    /// <summary>The manifest the strings translate; keys must name its contributions, settings and choices.</summary>
    public required ExtensionManifest Manifest { get; init; }

    /// <summary>The known hosts, for <c>schema.uri-mismatch</c>.</summary>
    public IReadOnlyList<HostInfo> KnownHosts { get; init; } = HostRegistry.Known;
}

/// <summary>
/// A localized strings file, <c>strings/&lt;language-tag&gt;.json</c> (contract §3.10). Each member
/// replaces the manifest text it names; keys address manifest items by id.
/// </summary>
public sealed class ExtensionStrings
{
    /// <summary>The <c>$schema</c> member.</summary>
    public string? Schema { get; init; }

    /// <summary>The <c>schemaVersion</c> member: 3.</summary>
    public int SchemaVersion { get; init; } = ExtensionManifest.CurrentSchemaVersion;

    /// <summary>The language tag, equal (case-insensitively) to the file name's tag.</summary>
    public required string Language { get; init; }

    /// <summary>Replaces the manifest's <c>name</c>.</summary>
    public string? Name { get; init; }

    /// <summary>Replaces the manifest's <c>description</c>.</summary>
    public string? Description { get; init; }

    /// <summary>Replaces the publisher's name.</summary>
    public PublisherStrings? Publisher { get; init; }

    /// <summary>Contribution strings by contribution id.</summary>
    public IReadOnlyDictionary<string, ContributionStrings> Contributions { get; init; } =
        new ReadOnlyDictionary<string, ContributionStrings>(new Dictionary<string, ContributionStrings>(StringComparer.Ordinal));
}

/// <summary>The <c>publisher</c> object of a strings file.</summary>
public sealed class PublisherStrings
{
    /// <summary>Replaces the publisher's name.</summary>
    public required string Name { get; init; }
}

/// <summary>A contribution string object (contract §3.10).</summary>
public sealed class ContributionStrings
{
    /// <summary>Replaces the contribution's name.</summary>
    public string? Name { get; init; }

    /// <summary>Replaces the contribution's description.</summary>
    public string? Description { get; init; }

    /// <summary>Setting strings by setting id.</summary>
    public IReadOnlyDictionary<string, SettingStrings> Settings { get; init; } =
        new ReadOnlyDictionary<string, SettingStrings>(new Dictionary<string, SettingStrings>(StringComparer.Ordinal));
}

/// <summary>A setting string object (contract §3.10).</summary>
public sealed class SettingStrings
{
    /// <summary>Replaces the setting's name.</summary>
    public string? Name { get; init; }

    /// <summary>Replaces the setting's description.</summary>
    public string? Description { get; init; }

    /// <summary>Choice names by choice value.</summary>
    public IReadOnlyDictionary<string, string> Choices { get; init; } =
        new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));
}

/// <summary>
/// Reads and validates localized strings files (contract §3.10) against a manifest, with the
/// JSON rules of contract §3.1 and the declaration text rule. Readers validate strings files even
/// when a host does not apply them.
/// </summary>
public static class StringsReader
{
    /// <summary>The largest strings file, in bytes.</summary>
    public const int MaxBytes = 65_536;

    private static readonly string[] RootMembers = ["$schema", "schemaVersion", "language", "name", "description", "publisher", "contributions"];
    private static readonly string[] ContributionMembers = ["name", "description", "settings"];
    private static readonly string[] SettingMembers = ["name", "description", "choices"];
    private static readonly string[] PublisherMembers = ["name"];

    /// <summary>Reads a strings file from its bytes.</summary>
    /// <param name="utf8">The file's bytes.</param>
    /// <param name="fileTag">The language tag of the file name (<c>de-DE</c> for <c>de-DE.json</c>).</param>
    /// <param name="options">The manifest the strings translate.</param>
    /// <returns>The strings when the file is valid, and every diagnostic.</returns>
    public static ReadResult<ExtensionStrings> Read(ReadOnlySpan<byte> utf8, string fileTag, StringsReadOptions options) =>
        Read(utf8, fileTag, options, null);

    /// <summary>Reads a strings file, never reading more than <see cref="MaxBytes"/> + 1 bytes. The tag is the file name's.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="options">The manifest the strings translate.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The strings when the file is valid, and every diagnostic.</returns>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    public static async Task<ReadResult<ExtensionStrings>> ReadFileAsync(string path, StringsReadOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var bytes = await BoundedFile.ReadAsync(path, MaxBytes, cancellationToken).ConfigureAwait(false);
        var name = Path.GetFileName(path);
        var tag = name.EndsWith(".json", StringComparison.Ordinal) ? name[..^5] : string.Empty;
        return Read(bytes, tag, options);
    }

    internal static ReadResult<ExtensionStrings> Read(ReadOnlySpan<byte> utf8, string fileTag, StringsReadOptions options, string? file)
    {
        var bag = new DiagnosticBag();
        var strings = ReadInto(utf8, fileTag, options, file, bag);
        return new ReadResult<ExtensionStrings>(strings, bag.ToList());
    }

    internal static ExtensionStrings? ReadInto(ReadOnlySpan<byte> utf8, string fileTag, StringsReadOptions options, string? file,
        DiagnosticBag target)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Manifest);
        var bag = new DiagnosticBag();
        var strings = ReadCore(utf8, fileTag, options, file, bag);
        target.AddRange(bag.Items);
        return bag.HasErrors ? null : strings;
    }

    private static ExtensionStrings? ReadCore(ReadOnlySpan<byte> utf8, string fileTag, StringsReadOptions options, string? file,
        DiagnosticBag bag)
    {
        if (!TextRules.IsLanguageTag(fileTag))
        {
            bag.Report(DiagnosticCodes.StringsLanguageInvalid, string.Empty, file);
        }

        if (!JsonFile.TryOpen(utf8, MaxBytes, file, bag, out var document))
        {
            return null;
        }

        var parse = JsonTree.Parse(document);
        var v = new JsonValidator(bag, document.ToArray(), 0, document.Length, file);
        if (parse.Root is null)
        {
            v.ParseFailure(parse);
            return null;
        }

        v.Duplicates(parse);
        return Validate(parse.Root, fileTag, options, v);
    }

    private static ExtensionStrings? Validate(JsonNode root, string fileTag, StringsReadOptions options, JsonValidator v)
    {
        if (!v.Expect(root, string.Empty, JsonKind.Object))
        {
            return null;
        }

        if (root.Members!.FirstOrDefault(member => member.Name == "schemaVersion") is { } version
            && version.Value.TryGetInteger(out var schema) && schema != ExtensionManifest.CurrentSchemaVersion)
        {
            v.Report(DiagnosticCodes.SchemaVersionUnsupported, "/schemaVersion", version.Value);
            return null;
        }

        var manifest = options.Manifest;
        var m = v.Members(root, string.Empty, RootMembers);
        if (v.Required(m, "schemaVersion", string.Empty, root) is { } schemaNode)
        {
            v.AsInteger(schemaNode, "/schemaVersion");
        }

        var language = v.RequiredString(m, "language", string.Empty, root);
        if (language is not null)
        {
            if (!TextRules.IsLanguageTag(language))
            {
                v.Report(DiagnosticCodes.LanguageInvalid, "/language", m["language"]);
            }
            else if (TextRules.IsLanguageTag(fileTag) && !string.Equals(language, fileTag, StringComparison.OrdinalIgnoreCase))
            {
                v.Report(DiagnosticCodes.StringsLanguageMismatch, "/language", m["language"]);
            }
        }

        var name = Text(m, "name", string.Empty, 80, DiagnosticCodes.ChromeLabelRequired, v);
        var description = Text(m, "description", string.Empty, 512, null, v);
        PublisherStrings? publisher = null;
        if (m.TryGetValue("publisher", out var publisherNode) && v.Expect(publisherNode, "/publisher", JsonKind.Object))
        {
            var pm = v.Members(publisherNode, "/publisher", PublisherMembers);
            if (v.Required(pm, "name", "/publisher", publisherNode) is not null)
            {
                var publisherName = Text(pm, "name", "/publisher", 80, DiagnosticCodes.ChromeLabelRequired, v);
                if (manifest.Publisher is null)
                {
                    v.Report(DiagnosticCodes.StringsTargetUnknown, "/publisher", publisherNode);
                }

                publisher = publisherName is null ? null : new PublisherStrings { Name = publisherName };
            }
        }

        var contributions = new Dictionary<string, ContributionStrings>(StringComparer.Ordinal);
        if (m.TryGetValue("contributions", out var contributionsNode) && v.Expect(contributionsNode, "/contributions", JsonKind.Object))
        {
            foreach (var member in contributionsNode.Members!)
            {
                var path = JsonPointer.ForUnknown("/contributions", member.Name, out var truncated);
                var contribution = manifest.Contributions?.FirstOrDefault(item => item?.Id == member.Name);
                if (contribution is null)
                {
                    v.Report(DiagnosticCodes.StringsTargetUnknown, path, truncated ? contributionsNode : member.Value);
                    continue;
                }

                if (Contribution(member.Value, JsonPointer.Append("/contributions", member.Name), contribution, v) is { } strings)
                {
                    contributions[member.Name] = strings;
                }
            }
        }

        if (m.TryGetValue("$schema", out _))
        {
            v.SchemaMember(m, string.Empty, SchemaUrls.ForHosts(manifest.Hosts ?? [], "strings", options.KnownHosts).ToList());
        }

        return language is null
            ? null
            : new ExtensionStrings
            {
                Schema = m.TryGetValue("$schema", out var schemaValue) ? schemaValue.Text : null,
                Language = language,
                Name = name,
                Description = description,
                Publisher = publisher,
                Contributions = new ReadOnlyDictionary<string, ContributionStrings>(contributions),
            };
    }

    private static ContributionStrings? Contribution(JsonNode node, string path, Contribution contribution, JsonValidator v)
    {
        if (!v.Expect(node, path, JsonKind.Object))
        {
            return null;
        }

        var m = v.Members(node, path, ContributionMembers);
        var name = Text(m, "name", path, 80, DiagnosticCodes.ChromeLabelRequired, v);
        var description = Text(m, "description", path, 512, null, v);
        var settings = new Dictionary<string, SettingStrings>(StringComparer.Ordinal);
        var settingsPath = JsonPointer.Append(path, "settings");
        if (m.TryGetValue("settings", out var settingsNode) && v.Expect(settingsNode, settingsPath, JsonKind.Object))
        {
            foreach (var member in settingsNode.Members!)
            {
                var memberPath = JsonPointer.ForUnknown(settingsPath, member.Name, out var truncated);
                var setting = contribution.Settings?.FirstOrDefault(item => item?.Id == member.Name);
                if (setting is null)
                {
                    v.Report(DiagnosticCodes.StringsTargetUnknown, memberPath, truncated ? settingsNode : member.Value);
                    continue;
                }

                if (Setting(member.Value, JsonPointer.Append(settingsPath, member.Name), setting, v) is { } strings)
                {
                    settings[member.Name] = strings;
                }
            }
        }

        return new ContributionStrings
        {
            Name = name,
            Description = description,
            Settings = new ReadOnlyDictionary<string, SettingStrings>(settings),
        };
    }

    private static SettingStrings? Setting(JsonNode node, string path, Setting setting, JsonValidator v)
    {
        if (!v.Expect(node, path, JsonKind.Object))
        {
            return null;
        }

        var m = v.Members(node, path, SettingMembers);
        var name = Text(m, "name", path, 80, DiagnosticCodes.SettingLabelRequired, v);
        var description = Text(m, "description", path, 512, null, v);
        var choices = new Dictionary<string, string>(StringComparer.Ordinal);
        var choicesPath = JsonPointer.Append(path, "choices");
        if (m.TryGetValue("choices", out var choicesNode) && v.Expect(choicesNode, choicesPath, JsonKind.Object))
        {
            foreach (var member in choicesNode.Members!)
            {
                var memberPath = JsonPointer.ForUnknown(choicesPath, member.Name, out var truncated);
                if (setting.Choices?.Any(choice => choice?.Value == member.Name) != true)
                {
                    v.Report(DiagnosticCodes.StringsTargetUnknown, memberPath, truncated ? choicesNode : member.Value);
                    continue;
                }

                var valuePath = JsonPointer.Append(choicesPath, member.Name);
                var text = v.AsString(member.Value, valuePath);
                if (v.DeclarationText(text, member.Value, valuePath, 80, DiagnosticCodes.ChoiceLabelRequired))
                {
                    choices[member.Name] = text!;
                }
            }
        }

        return new SettingStrings
        {
            Name = name,
            Description = description,
            Choices = new ReadOnlyDictionary<string, string>(choices),
        };
    }

    private static string? Text(Dictionary<string, JsonNode> m, string member, string parent, int maxUnits, string? labelCode,
        JsonValidator v)
    {
        if (!m.TryGetValue(member, out var node))
        {
            return null;
        }

        var path = JsonPointer.Append(parent, member);
        var text = v.AsString(node, path);
        return v.DeclarationText(text, node, path, maxUnits, labelCode) ? text : null;
    }
}
