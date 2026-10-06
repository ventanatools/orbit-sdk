// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using Microsoft.Extensions.FileSystemGlobbing;
using VentanaTools.Orbit.Extensions.Packaging;

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>The optional <c>dotnet publish</c> step of <c>extension.pack.json</c> (contract §11.3).</summary>
internal sealed class PackBuild
{
    public required string Project { get; init; }

    public string Runtime { get; init; } = "win-x64";

    public string Configuration { get; init; } = "Release";

    /// <summary>The folder under <c>payload/</c> the publish output is copied to.</summary>
    public string To { get; init; } = "companion";

    public required JsonNode Node { get; init; }
}

/// <summary>One copy rule of <c>extension.pack.json</c> (contract §11.3).</summary>
internal sealed class PackCopyRule
{
    public required int Index { get; init; }

    public required string From { get; init; }

    /// <summary>The folder under <c>payload/</c> the files go to; empty for <c>payload/</c> itself.</summary>
    public required string To { get; init; }

    public IReadOnlyList<string> Include { get; init; } = ["**/*"];

    public IReadOnlyList<string> Exclude { get; init; } = [];

    public required JsonNode FromNode { get; init; }

    /// <summary>The globs as a matcher over paths relative to <see cref="From"/>.</summary>
    public Matcher Matcher()
    {
        var matcher = new Matcher(StringComparison.OrdinalIgnoreCase);
        matcher.AddIncludePatterns(Include);
        matcher.AddExcludePatterns(Exclude);
        return matcher;
    }
}

/// <summary>A valid <c>extension.pack.json</c>, pack configuration version 1 (contract §11.3).</summary>
internal sealed class PackConfig
{
    public const int CurrentVersion = 1;

    public required string Readme { get; init; }

    public required JsonNode ReadmeNode { get; init; }

    public string? Strings { get; init; }

    public JsonNode? StringsNode { get; init; }

    public PackBuild? Build { get; init; }

    public IReadOnlyList<PackCopyRule> Payload { get; init; } = [];

    /// <summary>The document, for the line and column of a node.</summary>
    public required byte[] Document { get; init; }

    /// <summary>The 1-based line and UTF-8 column of <paramref name="node"/>.</summary>
    public (long Line, long Column) Position(JsonNode node) => JsonTree.Position(Document, node.Offset);
}

/// <summary>
/// Reads <c>extension.pack.json</c> with the strict JSON rules of contract §3.1 and the members
/// of §11.3, reporting with the codes and precedence of §4.
/// </summary>
internal static class PackConfigReader
{
    public const int MaxBytes = 65_536;

    private static readonly string[] RootMembers = ["$schema", "packVersion", "readme", "strings", "build", "payload"];
    private static readonly string[] BuildMembers = ["project", "runtime", "configuration", "to"];
    private static readonly string[] RuleMembers = ["from", "to", "include", "exclude"];

    /// <summary>The published URL of the pack configuration schema (contract §11.5).</summary>
    public static string SchemaUrl => SchemaUrls.HostNeutral("pack", PackConfig.CurrentVersion);

    public static ReadResult<PackConfig> Read(ReadOnlySpan<byte> utf8)
    {
        var bag = new DiagnosticBag();
        var config = ReadCore(utf8, bag);
        return new ReadResult<PackConfig>(config, bag.ToList());
    }

    private static PackConfig? ReadCore(ReadOnlySpan<byte> utf8, DiagnosticBag bag)
    {
        if (!JsonFile.TryOpen(utf8, MaxBytes, null, bag, out var document))
        {
            return null;
        }

        var bytes = document.ToArray();
        var parse = JsonTree.Parse(bytes);
        var v = new JsonValidator(bag, bytes, 0, bytes.Length, null);
        if (parse.Root is not { } root)
        {
            v.ParseFailure(parse);
            return null;
        }

        v.Duplicates(parse);
        if (!v.Expect(root, string.Empty, JsonKind.Object))
        {
            return null;
        }

        // A configuration of another version is validated against nothing else (as manifests are).
        if (root.Members!.FirstOrDefault(member => member.Name == "packVersion") is { } versionMember
            && versionMember.Value.TryGetInteger(out var version) && version != PackConfig.CurrentVersion)
        {
            v.Report(DiagnosticCodes.SchemaVersionUnsupported, "/packVersion", versionMember.Value);
            return null;
        }

        var m = v.Members(root, string.Empty, RootMembers);
        v.SchemaMember(m, string.Empty, [SchemaUrl]);
        if (v.Required(m, "packVersion", string.Empty, root) is { } versionNode)
        {
            v.AsInteger(versionNode, "/packVersion");
        }

        var readme = v.RequiredString(m, "readme", string.Empty, root);
        var strings = v.OptionalString(m, "strings", string.Empty);
        PackBuild? build = null;
        if (m.TryGetValue("build", out var buildNode) && v.Expect(buildNode, "/build", JsonKind.Object))
        {
            build = Build(buildNode, v);
        }

        var rules = new List<PackCopyRule>();
        if (m.TryGetValue("payload", out var payloadNode) && v.Expect(payloadNode, "/payload", JsonKind.Array))
        {
            for (var i = 0; i < payloadNode.Items!.Count; i++)
            {
                if (Rule(payloadNode.Items[i], i, v) is { } rule)
                {
                    rules.Add(rule);
                }
            }
        }

        if (bag.HasErrors || readme is null)
        {
            return null;
        }

        return new PackConfig
        {
            Readme = readme,
            ReadmeNode = m["readme"],
            Strings = strings,
            StringsNode = m.GetValueOrDefault("strings"),
            Build = build,
            Payload = rules,
            Document = bytes,
        };
    }

    private static PackBuild? Build(JsonNode node, JsonValidator v)
    {
        var m = v.Members(node, "/build", BuildMembers);
        var project = v.RequiredString(m, "project", "/build", node);
        var runtime = v.OptionalString(m, "runtime", "/build");
        var configuration = v.OptionalString(m, "configuration", "/build");
        var to = v.OptionalString(m, "to", "/build");
        if (to is not null && !IsPayloadFolder(to))
        {
            v.Report(DiagnosticCodes.PackagePath, "/build/to", m["to"]);
            return null;
        }

        return project is null ? null : new PackBuild
        {
            Project = project,
            Runtime = runtime ?? "win-x64",
            Configuration = configuration ?? "Release",
            To = Normalize(to ?? "companion"),
            Node = node,
        };
    }

    private static PackCopyRule? Rule(JsonNode node, int index, JsonValidator v)
    {
        var path = JsonPointer.Append("/payload", index);
        if (!v.Expect(node, path, JsonKind.Object, element: true))
        {
            return null;
        }

        var m = v.Members(node, path, RuleMembers);
        var from = v.RequiredString(m, "from", path, node);
        var to = v.RequiredString(m, "to", path, node);
        if (to is not null && !IsPayloadFolder(to))
        {
            v.Report(DiagnosticCodes.PackagePath, JsonPointer.Append(path, "to"), m["to"]);
            to = null;
        }

        var include = Globs(m, "include", path, v);
        var exclude = Globs(m, "exclude", path, v);
        if (from is null || to is null || include is null || exclude is null)
        {
            return null;
        }

        return new PackCopyRule
        {
            Index = index,
            From = from,
            To = Normalize(to),
            Include = m.ContainsKey("include") ? include : ["**/*"],
            Exclude = exclude,
            FromNode = m["from"],
        };
    }

    private static List<string>? Globs(Dictionary<string, JsonNode> m, string name, string path, JsonValidator v)
    {
        if (!m.TryGetValue(name, out var node))
        {
            return [];
        }

        var memberPath = JsonPointer.Append(path, name);
        if (!v.Expect(node, memberPath, JsonKind.Array))
        {
            return null;
        }

        var globs = new List<string>();
        var valid = true;
        for (var i = 0; i < node.Items!.Count; i++)
        {
            if (v.AsString(node.Items[i], JsonPointer.Append(memberPath, i), element: true) is { } glob)
            {
                globs.Add(glob);
            }
            else
            {
                valid = false;
            }
        }

        return valid ? globs : null;
    }

    /// <summary>A folder under <c>payload/</c>: empty (or <c>.</c>) for the payload root, else entry-name segments.</summary>
    public static bool IsPayloadFolder(string to)
    {
        var normalized = Normalize(to);
        return normalized.Length == 0 || PackageReader.IsEntryName("payload/" + normalized);
    }

    private static string Normalize(string to)
    {
        var trimmed = to.Replace('\\', '/').Trim('/');
        return trimmed == "." ? string.Empty : trimmed;
    }
}
