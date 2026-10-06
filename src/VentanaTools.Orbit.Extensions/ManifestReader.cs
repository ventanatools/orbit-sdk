// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Collections.ObjectModel;

namespace VentanaTools.Orbit.Extensions;

/// <summary>Options for <see cref="ManifestReader"/>.</summary>
public sealed class ManifestReadOptions
{
    /// <summary>The reading host's id; when set, a manifest that does not list it (or one of its aliases) raises <c>manifest.host-not-listed</c>.</summary>
    public string? HostId { get; init; }

    /// <summary>The capabilities the reading host implements; when set, any other required capability raises <c>requires.capability-unsupported</c>.</summary>
    public IReadOnlyCollection<string>? SupportedCapabilities { get; init; }

    /// <summary>Whether experimental (<c>x.</c>) capabilities may be required: true only in a host's developer mode.</summary>
    public bool AllowExperimentalCapabilities { get; init; }

    /// <summary>Whose ids the manifest declares; extension authors' by default.</summary>
    public IdOrigin Origin { get; init; } = IdOrigin.ThirdParty;

    /// <summary>The reserved publishers (contract §3.5).</summary>
    public IReadOnlyCollection<string> ReservedPublishers { get; init; } = ExtensionIds.ReservedPublishers;

    /// <summary>The known hosts, for <c>manifest.host-unknown</c> and <c>schema.uri-mismatch</c>. Tests pass a list holding <c>example-host</c>.</summary>
    public IReadOnlyList<HostInfo> KnownHosts { get; init; } = HostRegistry.Known;
}

/// <summary>
/// Reads and validates manifests (contract §3) with every rule and code of contract §4.4.
/// Content problems are always diagnostics, never exceptions.
/// </summary>
/// <remarks>
/// The reader is strict: member names compare ordinally after unescaping, unknown members are
/// errors, <c>null</c> is never valid, and the file is at most 65,536 bytes of UTF-8 (a UTF-8
/// byte order mark is ignored). The members are pure and safe to call from any thread.
/// </remarks>
public static class ManifestReader
{
    /// <summary>The largest manifest, in bytes.</summary>
    public const int MaxBytes = 65_536;

    /// <summary>Reads a manifest from UTF-8 bytes.</summary>
    /// <param name="utf8">The file's bytes.</param>
    /// <param name="options">Validation options; null for the defaults.</param>
    /// <returns>The manifest when it is valid, and every diagnostic.</returns>
    public static ReadResult<ExtensionManifest> Read(ReadOnlySpan<byte> utf8, ManifestReadOptions? options = null) =>
        Read(utf8, options ?? new ManifestReadOptions(), null);

    /// <summary>Reads a manifest file, never reading more than <see cref="MaxBytes"/> + 1 bytes.</summary>
    /// <param name="path">The file's path.</param>
    /// <param name="options">Validation options; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The manifest when it is valid, and every diagnostic.</returns>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    public static async Task<ReadResult<ExtensionManifest>> ReadFileAsync(string path, ManifestReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var bytes = await BoundedFile.ReadAsync(path, MaxBytes, cancellationToken).ConfigureAwait(false);
        return Read(bytes, options);
    }

    /// <summary>Validates a manifest built in code. Diagnostics use JSON paths and carry no line or column.</summary>
    /// <param name="manifest">The manifest; null raises <c>manifest.null</c>.</param>
    /// <param name="options">Validation options; null for the defaults.</param>
    /// <returns>A read-only copy of the manifest when it is valid, and every diagnostic.</returns>
    public static ReadResult<ExtensionManifest> Validate(ExtensionManifest manifest, ManifestReadOptions? options = null)
    {
        var bag = new DiagnosticBag();
        var tree = ManifestTree.FromModel(manifest);
        var validator = JsonValidator.ForCode(bag);
        var valid = ManifestValidation.Validate(tree, validator, options ?? new ManifestReadOptions());
        return new ReadResult<ExtensionManifest>(valid && !bag.HasErrors ? ManifestTree.ToModel(tree) : null, bag.ToList());
    }

    internal static ReadResult<ExtensionManifest> Read(ReadOnlySpan<byte> utf8, ManifestReadOptions options, string? file)
    {
        var bag = new DiagnosticBag();
        var manifest = ReadInto(utf8, options, file, bag);
        return new ReadResult<ExtensionManifest>(manifest, bag.ToList());
    }

    internal static ExtensionManifest? ReadInto(ReadOnlySpan<byte> utf8, ManifestReadOptions options, string? file, DiagnosticBag target)
    {
        var bag = new DiagnosticBag();
        var manifest = ReadCore(utf8, options, file, bag);
        target.AddRange(bag.Items);
        return manifest;
    }

    private static ExtensionManifest? ReadCore(ReadOnlySpan<byte> utf8, ManifestReadOptions options, string? file, DiagnosticBag bag)
    {
        if (!JsonFile.TryOpen(utf8, MaxBytes, file, bag, out var document))
        {
            return null;
        }

        var parse = JsonTree.Parse(document);
        var validator = new JsonValidator(bag, document.ToArray(), 0, document.Length, file);
        if (parse.Root is null)
        {
            validator.ParseFailure(parse);
            return null;
        }

        validator.Duplicates(parse);
        var valid = ManifestValidation.Validate(parse.Root, validator, options);
        return valid && !bag.HasErrors ? ManifestTree.ToModel(parse.Root) : null;
    }
}

/// <summary>The checks every JSON file reader shares before parsing: size and encoding.</summary>
internal static class JsonFile
{
    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];

    /// <summary>Checks size and byte order marks and strips a UTF-8 byte order mark.</summary>
    public static bool TryOpen(ReadOnlySpan<byte> utf8, int maxBytes, string? file, DiagnosticBag bag, out ReadOnlySpan<byte> document)
    {
        document = default;
        if (utf8.Length > maxBytes)
        {
            bag.Report(DiagnosticCodes.JsonTooLarge, string.Empty, file);
            return false;
        }

        if (HasWideByteOrderMark(utf8))
        {
            bag.Report(DiagnosticCodes.JsonEncoding, string.Empty, file);
            return false;
        }

        document = utf8.StartsWith(Utf8Bom) ? utf8[Utf8Bom.Length..] : utf8;
        return true;
    }

    /// <summary>Whether the bytes start with a UTF-16 or UTF-32 byte order mark.</summary>
    public static bool HasWideByteOrderMark(ReadOnlySpan<byte> utf8) =>
        utf8.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]) || utf8.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])
        || utf8.StartsWith((ReadOnlySpan<byte>)[0x00, 0x00, 0xFE, 0xFF]);

    public static ReadOnlySpan<byte> StripUtf8Bom(ReadOnlySpan<byte> utf8) => utf8.StartsWith(Utf8Bom) ? utf8[Utf8Bom.Length..] : utf8;
}

/// <summary>Bounded file reads: never more than the limit plus one byte.</summary>
internal static class BoundedFile
{
    public static async Task<byte[]> ReadAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0,
        });
        var limit = checked((int)Math.Min(maxBytes + 1, int.MaxValue));
        var buffer = new byte[Math.Min(limit, 81_920)];
        using var copy = new MemoryStream();
        while (copy.Length < limit)
        {
            var want = (int)Math.Min(buffer.Length, limit - copy.Length);
            var read = await stream.ReadAsync(buffer.AsMemory(0, want), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            copy.Write(buffer, 0, read);
        }

        return copy.ToArray();
    }
}

/// <summary>The manifest rules of contract §3 over a JSON tree.</summary>
internal static class ManifestValidation
{
    private static readonly string[] RootMembers =
    [
        "$schema", "schemaVersion", "id", "name", "description", "version", "hosts", "glyph", "publisher",
        "supportUrl", "defaultLanguage", "disclosures", "requires", "contributions",
    ];

    private static readonly string[] ContributionMembers = ["id", "name", "description", "glyph", "provides", "settings"];
    private static readonly string[] SettingMembers = ["id", "kind", "name", "description", "default", "choices"];
    private static readonly string[] ChoiceMembers = ["value", "name"];
    private static readonly string[] PublisherMembers = ["name", "url"];
    private static readonly string[] DisclosureMembers = ["network", "privacyUrl"];
    private static readonly string[] RequiresMembers = ["capabilities"];
    private static readonly string[] RootRenamed = ["manifestVersion"];
    private static readonly string[] ContributionRenamed = ["capabilities"];

    /// <summary>Validates the tree; returns false when it cannot be a manifest at all.</summary>
    public static bool Validate(JsonNode root, JsonValidator v, ManifestReadOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (root.Kind == JsonKind.Null && root.FromCode)
        {
            v.Report(DiagnosticCodes.ManifestNull, string.Empty, root);
            return false;
        }

        if (!v.Expect(root, string.Empty, JsonKind.Object))
        {
            return false;
        }

        // A different schema version makes every other rule meaningless; report only that.
        if (root.Members!.FirstOrDefault(member => member.Name == "schemaVersion") is { } version
            && version.Value.TryGetInteger(out var schema) && schema != ExtensionManifest.CurrentSchemaVersion)
        {
            v.Report(DiagnosticCodes.SchemaVersionUnsupported, "/schemaVersion", version.Value);
            return false;
        }

        var m = v.Members(root, string.Empty, RootMembers, RootRenamed);
        if (m.TryGetValue("schemaVersion", out var schemaNode))
        {
            v.AsInteger(schemaNode, "/schemaVersion");
        }
        else if (!root.Members!.Any(member => member.Name == "manifestVersion"))
        {
            v.Report(DiagnosticCodes.JsonRequiredMissing, "/schemaVersion", root);
        }

        var id = ExtensionId(m, root, v, options);
        if (v.Required(m, "name", string.Empty, root) is { } name)
        {
            v.DeclarationText(v.AsString(name, "/name"), name, "/name", 80, DiagnosticCodes.ChromeLabelRequired);
        }

        if (v.Required(m, "description", string.Empty, root) is { } description)
        {
            v.DeclarationText(v.AsString(description, "/description"), description, "/description", 512);
        }

        if (v.Required(m, "version", string.Empty, root) is { } versionNode
            && v.AsString(versionNode, "/version") is { } packageVersion && !Grammars.IsPackageVersion(packageVersion))
        {
            v.Report(DiagnosticCodes.ManifestVersionInvalid, "/version", versionNode);
        }

        var hosts = Hosts(m, root, v, options);
        if (m.TryGetValue("glyph", out var glyph))
        {
            Glyph(glyph, "/glyph", v);
        }

        if (m.TryGetValue("publisher", out var publisher) && v.Expect(publisher, "/publisher", JsonKind.Object))
        {
            var pm = v.Members(publisher, "/publisher", PublisherMembers);
            if (v.Required(pm, "name", "/publisher", publisher) is { } publisherName)
            {
                v.DeclarationText(v.AsString(publisherName, "/publisher/name"), publisherName, "/publisher/name", 80,
                    DiagnosticCodes.ChromeLabelRequired);
            }

            Url(pm, "url", "/publisher", v);
        }

        Url(m, "supportUrl", string.Empty, v);
        if (m.TryGetValue("defaultLanguage", out var language)
            && v.AsString(language, "/defaultLanguage") is { } tag && !TextRules.IsLanguageTag(tag))
        {
            v.Report(DiagnosticCodes.LanguageInvalid, "/defaultLanguage", language);
        }

        if (m.TryGetValue("disclosures", out var disclosures) && v.Expect(disclosures, "/disclosures", JsonKind.Object))
        {
            var dm = v.Members(disclosures, "/disclosures", DisclosureMembers);
            if (v.Required(dm, "network", "/disclosures", disclosures) is { } network
                && v.Expect(network, "/disclosures/network", JsonKind.String)
                && WireTokens.ParseNetworkUse(network.Text) is null)
            {
                v.Report(DiagnosticCodes.EnumUndefined, "/disclosures/network", network);
            }

            Url(dm, "privacyUrl", "/disclosures", v);
        }

        if (m.TryGetValue("requires", out var requires) && v.Expect(requires, "/requires", JsonKind.Object))
        {
            Requirements(requires, v, options);
        }

        Contributions(m, root, v, options, id);
        if (hosts is not null)
        {
            v.SchemaMember(m, string.Empty, SchemaUrls.ForHosts(hosts, "manifest", options.KnownHosts).ToList());
        }
        else
        {
            v.SchemaMember(m, string.Empty, null);
        }

        return true;
    }

    private static string? ExtensionId(Dictionary<string, JsonNode> m, JsonNode root, JsonValidator v, ManifestReadOptions options)
    {
        if (v.Required(m, "id", string.Empty, root, DiagnosticCodes.IdRequired) is not { } node
            || v.AsString(node, "/id") is not { } id)
        {
            return null;
        }

        var code = ExtensionIds.Classify(id, options.Origin, options.ReservedPublishers);
        if (code is null && id.Contains('/', StringComparison.Ordinal))
        {
            code = DiagnosticCodes.IdGrammar;
        }

        if (code is not null)
        {
            v.Report(code, "/id", node);
            return null;
        }

        return id;
    }

    private static List<string>? Hosts(Dictionary<string, JsonNode> m, JsonNode root, JsonValidator v, ManifestReadOptions options)
    {
        if (v.Required(m, "hosts", string.Empty, root) is not { } node || v.Array(node, "/hosts", 1, 8) is not { } items)
        {
            return null;
        }

        var hosts = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var path = JsonPointer.Append("/hosts", i);
            if (v.AsString(items[i], path, element: true) is not { } host)
            {
                continue;
            }

            if (!TextRules.IsHostId(host))
            {
                v.Report(DiagnosticCodes.ManifestHostInvalid, path, items[i]);
                continue;
            }

            if (!seen.Add(host))
            {
                v.Report(DiagnosticCodes.ManifestHostDuplicate, path, items[i]);
                continue;
            }

            hosts.Add(host);
            var current = options.HostId is not null && string.Equals(host, options.HostId, StringComparison.Ordinal);
            if (!current && HostRegistry.Find(host, options.KnownHosts) is not { Status: HostStatus.Active })
            {
                v.Report(DiagnosticCodes.ManifestHostUnknown, path, items[i]);
            }
        }

        if (options.HostId is { } hostId && items.Count is >= 1 and <= 8)
        {
            var accepted = new HashSet<string>(StringComparer.Ordinal) { hostId };
            if (HostRegistry.Find(hostId, options.KnownHosts) is { } self)
            {
                accepted.Add(self.Id);
                accepted.UnionWith(self.Aliases ?? []);
            }

            if (!hosts.Any(accepted.Contains))
            {
                v.Report(DiagnosticCodes.ManifestHostNotListed, "/hosts", node);
            }
        }

        return hosts;
    }

    private static void Requirements(JsonNode requires, JsonValidator v, ManifestReadOptions options)
    {
        var rm = v.Members(requires, "/requires", RequiresMembers);
        if (v.Required(rm, "capabilities", "/requires", requires) is not { } node
            || v.Array(node, "/requires/capabilities", 1, 16) is not { } items)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var path = JsonPointer.Append("/requires/capabilities", i);
            if (v.AsString(items[i], path, element: true) is not { } id)
            {
                continue;
            }

            if (id.Length > Grammars.MaxDottedCode)
            {
                v.Report(DiagnosticCodes.StringTooLong, path, items[i]);
                continue;
            }

            if (!seen.Add(id))
            {
                v.Report(DiagnosticCodes.RequiresCapabilityDuplicate, path, items[i]);
                continue;
            }

            if (options.SupportedCapabilities is { } supported)
            {
                var allowed = supported.Contains(id, StringComparer.Ordinal)
                    && (!Wire.Capabilities.IsExperimental(id) || options.AllowExperimentalCapabilities);
                if (!allowed)
                {
                    v.Report(DiagnosticCodes.RequiresCapabilityUnsupported, path, items[i]);
                }
            }
            else if (!Wire.CapabilityRegistry.IsKnown(id))
            {
                v.Report(DiagnosticCodes.RequiresCapabilityUnknown, path, items[i]);
            }
        }
    }

    private static void Contributions(Dictionary<string, JsonNode> m, JsonNode root, JsonValidator v, ManifestReadOptions options,
        string? extensionId)
    {
        if (v.Required(m, "contributions", string.Empty, root) is not { } node
            || v.Array(node, "/contributions", 1, 32) is not { } items)
        {
            return;
        }

        var ids = new List<(string Id, string Path, JsonNode At)>();
        for (var i = 0; i < items.Count; i++)
        {
            var path = JsonPointer.Append("/contributions", i);
            var item = items[i];
            if (!v.Expect(item, path, JsonKind.Object, element: true))
            {
                continue;
            }

            if (Contribution(item, path, v, options) is { } contributionId)
            {
                ids.Add(contributionId);
            }
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (id, path, at) in ids)
        {
            if (!seen.Add(id))
            {
                v.Report(DiagnosticCodes.IdDuplicate, path, at);
            }

            if (extensionId is null)
            {
                continue;
            }

            var leaf = string.Equals(id, extensionId, StringComparison.Ordinal);
            if ((leaf && items.Count != 1) || (!leaf && !ExtensionIds.IsInNamespace(id, extensionId)))
            {
                v.Report(DiagnosticCodes.IdOutsideNamespace, path, at);
            }
        }
    }

    private static (string, string, JsonNode)? Contribution(JsonNode item, string path, JsonValidator v, ManifestReadOptions options)
    {
        var cm = v.Members(item, path, ContributionMembers, ContributionRenamed);
        (string, string, JsonNode)? result = null;
        var idPath = JsonPointer.Append(path, "id");
        if (v.Required(cm, "id", path, item, DiagnosticCodes.IdRequired) is { } idNode && v.AsString(idNode, idPath) is { } id)
        {
            var code = ExtensionIds.Classify(id, options.Origin, options.ReservedPublishers);
            if (code is not null)
            {
                v.Report(code, idPath, idNode);
            }
            else
            {
                result = (id, idPath, idNode);
            }
        }

        if (v.Required(cm, "name", path, item) is { } name)
        {
            var namePath = JsonPointer.Append(path, "name");
            v.DeclarationText(v.AsString(name, namePath), name, namePath, 80, DiagnosticCodes.ChromeLabelRequired);
        }

        if (v.Required(cm, "description", path, item) is { } description)
        {
            var descriptionPath = JsonPointer.Append(path, "description");
            v.DeclarationText(v.AsString(description, descriptionPath), description, descriptionPath, 512);
        }

        if (v.Required(cm, "glyph", path, item, DiagnosticCodes.ChromeGlyphRequired) is { } glyph)
        {
            Glyph(glyph, JsonPointer.Append(path, "glyph"), v);
        }

        var providesPath = JsonPointer.Append(path, "provides");
        if (cm.TryGetValue("provides", out var provides))
        {
            Provides(provides, providesPath, v);
        }
        else if (!(item.Members ?? []).Any(member => member.Name == "capabilities"))
        {
            v.Report(DiagnosticCodes.JsonRequiredMissing, providesPath, item);
        }

        if (cm.TryGetValue("settings", out var settings))
        {
            Settings(settings, JsonPointer.Append(path, "settings"), v);
        }

        return result;
    }

    private static void Provides(JsonNode node, string path, JsonValidator v)
    {
        if (v.Array(node, path, 1, 2) is not { } items)
        {
            return;
        }

        var seen = new HashSet<Provides>();
        for (var i = 0; i < items.Count; i++)
        {
            var itemPath = JsonPointer.Append(path, i);
            if (!v.Expect(items[i], itemPath, JsonKind.String, element: true))
            {
                continue;
            }

            if (WireTokens.ParseProvides(items[i].Text) is not { } value)
            {
                v.Report(DiagnosticCodes.ContributionProvidesUnknown, itemPath, items[i]);
            }
            else if (!seen.Add(value))
            {
                v.Report(DiagnosticCodes.ContributionProvidesDuplicate, itemPath, items[i]);
            }
        }
    }

    private static void Settings(JsonNode node, string path, JsonValidator v)
    {
        if (v.Array(node, path, 0, 16) is not { } items)
        {
            return;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var settingPath = JsonPointer.Append(path, i);
            var item = items[i];
            if (!v.Expect(item, settingPath, JsonKind.Object, element: true))
            {
                continue;
            }

            var sm = v.Members(item, settingPath, SettingMembers);
            var idPath = JsonPointer.Append(settingPath, "id");
            if (v.Required(sm, "id", settingPath, item, DiagnosticCodes.SettingIdRequired) is { } idNode
                && v.AsString(idNode, idPath) is { } id)
            {
                if (string.IsNullOrWhiteSpace(id))
                {
                    v.Report(DiagnosticCodes.SettingIdRequired, idPath, idNode);
                }
                else if (!char.IsAsciiLetterLower(id[0]) || !id.All(char.IsAsciiLetterOrDigit))
                {
                    v.Report(DiagnosticCodes.SettingIdGrammar, idPath, idNode);
                }
                else if (id.Length > 32)
                {
                    v.Report(DiagnosticCodes.StringTooLong, idPath, idNode);
                }
                else if (!ids.Add(id))
                {
                    v.Report(DiagnosticCodes.SettingIdDuplicate, idPath, idNode);
                }
            }

            var choice = true;
            var kindPath = JsonPointer.Append(settingPath, "kind");
            if (v.Required(sm, "kind", settingPath, item) is { } kind && v.Expect(kind, kindPath, JsonKind.String))
            {
                if (WireTokens.ParseSettingKind(kind.Text) is null)
                {
                    v.Report(DiagnosticCodes.SettingKindUnsupported, kindPath, kind);
                    choice = false;
                }
            }

            if (v.Required(sm, "name", settingPath, item) is { } name)
            {
                var namePath = JsonPointer.Append(settingPath, "name");
                v.DeclarationText(v.AsString(name, namePath), name, namePath, 80, DiagnosticCodes.SettingLabelRequired);
            }

            if (sm.TryGetValue("description", out var description))
            {
                var descriptionPath = JsonPointer.Append(settingPath, "description");
                v.DeclarationText(v.AsString(description, descriptionPath), description, descriptionPath, 512);
            }

            string? defaultValue = null;
            JsonNode? defaultNode = null;
            var defaultPath = JsonPointer.Append(settingPath, "default");
            if (sm.TryGetValue("default", out defaultNode))
            {
                defaultValue = v.AsString(defaultNode, defaultPath);
            }
            else if (choice)
            {
                v.Report(DiagnosticCodes.SettingDefaultRequired, defaultPath, item);
            }

            HashSet<string>? values = null;
            var choicesPath = JsonPointer.Append(settingPath, "choices");
            if (sm.TryGetValue("choices", out var choices))
            {
                values = Choices(choices, choicesPath, v);
            }
            else if (choice)
            {
                v.Report(DiagnosticCodes.SettingChoicesRequired, choicesPath, item);
            }

            if (choice && defaultValue is not null && defaultNode is not null && values is not null
                && !values.Contains(defaultValue))
            {
                v.Report(DiagnosticCodes.SettingDefaultUnknown, defaultPath, defaultNode);
            }
        }
    }

    private static HashSet<string>? Choices(JsonNode node, string path, JsonValidator v)
    {
        if (v.Array(node, path, 2, 32) is not { } items)
        {
            return null;
        }

        var values = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < items.Count; i++)
        {
            var choicePath = JsonPointer.Append(path, i);
            var item = items[i];
            if (!v.Expect(item, choicePath, JsonKind.Object, element: true))
            {
                continue;
            }

            var cm = v.Members(item, choicePath, ChoiceMembers);
            var valuePath = JsonPointer.Append(choicePath, "value");
            if (v.Required(cm, "value", choicePath, item, DiagnosticCodes.ChoiceValueRequired) is { } valueNode
                && v.AsString(valueNode, valuePath) is { } value)
            {
                if (value.Length == 0)
                {
                    v.Report(DiagnosticCodes.ChoiceValueRequired, valuePath, valueNode);
                }
                else if (!ExtensionIds.IsSegment(value))
                {
                    v.Report(DiagnosticCodes.ChoiceValueGrammar, valuePath, valueNode);
                }
                else if (value.Length > 64)
                {
                    v.Report(DiagnosticCodes.StringTooLong, valuePath, valueNode);
                }
                else if (!values.Add(value))
                {
                    v.Report(DiagnosticCodes.ChoiceValueDuplicate, valuePath, valueNode);
                }
            }

            if (v.Required(cm, "name", choicePath, item) is { } name)
            {
                var namePath = JsonPointer.Append(choicePath, "name");
                v.DeclarationText(v.AsString(name, namePath), name, namePath, 80, DiagnosticCodes.ChoiceLabelRequired);
            }
        }

        return values;
    }

    private static void Glyph(JsonNode node, string path, JsonValidator v)
    {
        if (v.AsString(node, path) is not { } glyph)
        {
            return;
        }

        if (glyph.Length == 0)
        {
            v.Report(DiagnosticCodes.ChromeGlyphRequired, path, node);
        }
        else if (!TextRules.IsGlyph(glyph))
        {
            v.Report(DiagnosticCodes.ChromeGlyphInvalid, path, node);
        }
    }

    private static void Url(Dictionary<string, JsonNode> members, string name, string parent, JsonValidator v)
    {
        if (!members.TryGetValue(name, out var node))
        {
            return;
        }

        var path = JsonPointer.Append(parent, name);
        if (v.AsString(node, path) is { } url && !TextRules.IsHttpsUrl(url))
        {
            v.Report(DiagnosticCodes.UrlInvalid, path, node);
        }
    }
}

/// <summary>Converts manifests between the object model and JSON trees.</summary>
internal static class ManifestTree
{
    /// <summary>A tree for a manifest built in code, in canonical member order.</summary>
    public static JsonNode FromModel(ExtensionManifest? manifest)
    {
        if (manifest is null)
        {
            return JsonNode.CodeNull();
        }

        var root = JsonNode.NewObject();
        AddString(root, "$schema", manifest.Schema);
        root.Add("schemaVersion", JsonNode.Integer(manifest.SchemaVersion));
        AddString(root, "id", manifest.Id);
        AddString(root, "name", manifest.Name);
        AddString(root, "description", manifest.Description);
        AddString(root, "version", manifest.Version);
        root.Add("hosts", Strings(manifest.Hosts));
        AddString(root, "glyph", manifest.Glyph);
        if (manifest.Publisher is { } publisher)
        {
            var node = JsonNode.NewObject();
            AddString(node, "name", publisher.Name);
            AddString(node, "url", publisher.Url);
            root.Add("publisher", node);
        }

        AddString(root, "supportUrl", manifest.SupportUrl);
        AddString(root, "defaultLanguage", manifest.DefaultLanguage);
        if (manifest.Disclosures is { } disclosures)
        {
            var node = JsonNode.NewObject();
            node.Add("network", Token(WireTokens.NetworkUseToken(disclosures.Network)));
            AddString(node, "privacyUrl", disclosures.PrivacyUrl);
            root.Add("disclosures", node);
        }

        if (manifest.Requires is { } requires)
        {
            root.Add("requires", JsonNode.NewObject().Add("capabilities", Strings(requires.Capabilities)));
        }

        if (manifest.Contributions is null)
        {
            root.Add("contributions", JsonNode.CodeNull());
        }
        else
        {
            var array = JsonNode.NewArray();
            foreach (var contribution in manifest.Contributions)
            {
                array.Items!.Add(contribution is null ? JsonNode.CodeNull() : Contribution(contribution));
            }

            root.Add("contributions", array);
        }

        return root;
    }

    /// <summary>The model of a valid tree, with read-only collections.</summary>
    public static ExtensionManifest ToModel(JsonNode root)
    {
        var m = Map(root);
        return new ExtensionManifest
        {
            Schema = Text(m, "$schema"),
            SchemaVersion = ExtensionManifest.CurrentSchemaVersion,
            Id = Text(m, "id")!,
            Name = Text(m, "name")!,
            Description = Text(m, "description")!,
            Version = Text(m, "version")!,
            Hosts = StringList(m["hosts"]),
            Glyph = Text(m, "glyph"),
            Publisher = m.TryGetValue("publisher", out var publisher)
                ? new Publisher { Name = Text(Map(publisher), "name")!, Url = Text(Map(publisher), "url") }
                : null,
            SupportUrl = Text(m, "supportUrl"),
            DefaultLanguage = Text(m, "defaultLanguage"),
            Disclosures = m.TryGetValue("disclosures", out var disclosures)
                ? new Disclosures
                {
                    Network = WireTokens.ParseNetworkUse(Text(Map(disclosures), "network"))!.Value,
                    PrivacyUrl = Text(Map(disclosures), "privacyUrl"),
                }
                : null,
            Requires = m.TryGetValue("requires", out var requires)
                ? new ManifestRequirements { Capabilities = StringList(Map(requires)["capabilities"]) }
                : null,
            Contributions = new ReadOnlyCollection<Contribution>(m["contributions"].Items!.Select(ToContribution).ToArray()),
        };
    }

    private static Contribution ToContribution(JsonNode node)
    {
        var m = Map(node);
        var provides = Provides.None;
        foreach (var item in m["provides"].Items!)
        {
            provides |= WireTokens.ParseProvides(item.Text)!.Value;
        }

        return new Contribution
        {
            Id = Text(m, "id")!,
            Name = Text(m, "name")!,
            Description = Text(m, "description")!,
            Glyph = Text(m, "glyph")!,
            Provides = provides,
            Settings = m.TryGetValue("settings", out var settings)
                ? new ReadOnlyCollection<Setting>(settings.Items!.Select(ToSetting).ToArray())
                : new ReadOnlyCollection<Setting>([]),
        };
    }

    private static Setting ToSetting(JsonNode node)
    {
        var m = Map(node);
        return new Setting
        {
            Id = Text(m, "id")!,
            Kind = WireTokens.ParseSettingKind(Text(m, "kind"))!.Value,
            Name = Text(m, "name")!,
            Description = Text(m, "description"),
            Default = Text(m, "default")!,
            Choices = new ReadOnlyCollection<SettingChoice>(m["choices"].Items!.Select(choice =>
            {
                var cm = Map(choice);
                return new SettingChoice { Value = Text(cm, "value")!, Name = Text(cm, "name")! };
            }).ToArray()),
        };
    }

    private static JsonNode Contribution(Contribution contribution)
    {
        var node = JsonNode.NewObject();
        AddString(node, "id", contribution.Id);
        AddString(node, "name", contribution.Name);
        AddString(node, "description", contribution.Description);
        AddString(node, "glyph", contribution.Glyph);
        var provides = JsonNode.NewArray();
        if (contribution.Provides.HasFlag(Provides.Invoke))
        {
            provides.Items!.Add(JsonNode.String("invoke"));
        }

        if (contribution.Provides.HasFlag(Provides.Face))
        {
            provides.Items!.Add(JsonNode.String("face"));
        }

        if ((contribution.Provides & ~(Provides.Invoke | Provides.Face)) != 0)
        {
            provides.Items!.Add(JsonNode.Undefined());
        }

        node.Add("provides", provides);
        if (contribution.Settings is null)
        {
            node.Add("settings", JsonNode.CodeNull());
        }
        else
        {
            var settings = JsonNode.NewArray();
            foreach (var setting in contribution.Settings)
            {
                settings.Items!.Add(setting is null ? JsonNode.CodeNull() : Setting(setting));
            }

            node.Add("settings", settings);
        }

        return node;
    }

    private static JsonNode Setting(Setting setting)
    {
        var node = JsonNode.NewObject();
        AddString(node, "id", setting.Id);
        node.Add("kind", Token(WireTokens.SettingKindToken(setting.Kind)));
        AddString(node, "name", setting.Name);
        AddString(node, "description", setting.Description);
        AddString(node, "default", setting.Default);
        if (setting.Choices is not null)
        {
            var choices = JsonNode.NewArray();
            foreach (var choice in setting.Choices)
            {
                if (choice is null)
                {
                    choices.Items!.Add(JsonNode.CodeNull());
                    continue;
                }

                var item = JsonNode.NewObject();
                AddString(item, "value", choice.Value);
                AddString(item, "name", choice.Name);
                choices.Items!.Add(item);
            }

            node.Add("choices", choices);
        }

        return node;
    }

    private static JsonNode Strings(IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return JsonNode.CodeNull();
        }

        var array = JsonNode.NewArray();
        foreach (var value in values)
        {
            array.Items!.Add(value is null ? JsonNode.CodeNull() : JsonNode.String(value));
        }

        return array;
    }

    private static JsonNode Token(string? token) => token is null ? JsonNode.Undefined() : JsonNode.String(token);

    private static void AddString(JsonNode node, string name, string? value)
    {
        if (value is not null)
        {
            node.Add(name, JsonNode.String(value));
        }
    }

    private static Dictionary<string, JsonNode> Map(JsonNode node)
    {
        var map = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var member in node.Members!)
        {
            map.TryAdd(member.Name, member.Value);
        }

        return map;
    }

    private static string? Text(Dictionary<string, JsonNode> m, string name) => m.TryGetValue(name, out var node) ? node.Text : null;

    private static ReadOnlyCollection<string> StringList(JsonNode node) =>
        new(node.Items!.Select(item => item.Text!).ToArray());
}
