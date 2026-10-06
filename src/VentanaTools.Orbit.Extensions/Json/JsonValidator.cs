// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions;

/// <summary>
/// Validation primitives over a <see cref="JsonNode"/> tree that report diagnostics with the
/// paths, positions and precedence of contract §4.
/// </summary>
internal sealed class JsonValidator(DiagnosticBag bag, byte[]? document, int documentStart, int documentLength, string? file)
{
    public DiagnosticBag Bag { get; } = bag;

    public string? File { get; } = file;

    /// <summary>A validator for a tree built in code: no positions.</summary>
    public static JsonValidator ForCode(DiagnosticBag bag) => new(bag, null, 0, 0, null);

    /// <summary>Reports <paramref name="code"/> at <paramref name="path"/>, located at <paramref name="at"/>'s token.</summary>
    public void Report(string code, string path, JsonNode? at)
    {
        long? line = null, column = null;
        if (at is { Offset: >= 0 } && document is not null)
        {
            (var l, var c) = JsonTree.Position(document.AsSpan(documentStart, documentLength), at.Offset);
            line = l;
            column = c;
        }

        Bag.Report(code, path, File, line, column);
    }

    /// <summary>
    /// The members of <paramref name="node"/> by name. Reports an unknown member as
    /// <c>json.unknown-member</c>, or <c>json.member-renamed</c> for a name in <paramref name="renamed"/>.
    /// </summary>
    public Dictionary<string, JsonNode> Members(JsonNode node, string path, IReadOnlyCollection<string> known,
        IReadOnlyCollection<string>? renamed = null)
    {
        var members = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var member in node.Members ?? [])
        {
            if (known.Contains(member.Name))
            {
                members.TryAdd(member.Name, member.Value);
                continue;
            }

            var memberPath = JsonPointer.ForUnknown(path, member.Name, out var truncated);
            var code = renamed?.Contains(member.Name) == true ? DiagnosticCodes.JsonMemberRenamed : DiagnosticCodes.JsonUnknownMember;
            Report(code, memberPath, truncated ? node : member.Value);
        }

        return members;
    }

    /// <summary>
    /// Checks that <paramref name="node"/> has <paramref name="kind"/>. Reports
    /// <c>json.null-not-allowed</c>, <c>null.member</c>, <c>null.element</c> or <c>json.type-mismatch</c>.
    /// An <see cref="JsonKind.Undefined"/> node passes as a string.
    /// </summary>
    public bool Expect(JsonNode node, string path, JsonKind kind, bool element = false)
    {
        if (node.Kind == JsonKind.Null)
        {
            Report(node.FromCode ? element ? DiagnosticCodes.NullElement : DiagnosticCodes.NullMember : DiagnosticCodes.JsonNullNotAllowed,
                path, node);
            return false;
        }

        if (node.Kind == kind || (kind == JsonKind.String && node.Kind == JsonKind.Undefined))
        {
            return true;
        }

        Report(DiagnosticCodes.JsonTypeMismatch, path, node);
        return false;
    }

    /// <summary>A member that must be present: reports <paramref name="missingCode"/> at the member's path when it is not.</summary>
    public JsonNode? Required(Dictionary<string, JsonNode> members, string name, string path, JsonNode parent,
        string missingCode = DiagnosticCodes.JsonRequiredMissing)
    {
        if (members.TryGetValue(name, out var node))
        {
            return node;
        }

        Report(missingCode, JsonPointer.Append(path, name), parent);
        return null;
    }

    /// <summary>A required string member; null when it is missing or not a string.</summary>
    public string? RequiredString(Dictionary<string, JsonNode> members, string name, string path, JsonNode parent,
        string missingCode = DiagnosticCodes.JsonRequiredMissing) =>
        Required(members, name, path, parent, missingCode) is { } node ? AsString(node, JsonPointer.Append(path, name)) : null;

    /// <summary>An optional string member; null when it is absent or not a string.</summary>
    public string? OptionalString(Dictionary<string, JsonNode> members, string name, string path) =>
        members.TryGetValue(name, out var node) ? AsString(node, JsonPointer.Append(path, name)) : null;

    /// <summary>The string value of <paramref name="node"/>, or null after reporting why it is not one.</summary>
    public string? AsString(JsonNode node, string path, bool element = false) =>
        Expect(node, path, JsonKind.String, element) && node.Kind == JsonKind.String ? node.Text : null;

    /// <summary>The integer value of <paramref name="node"/>, or null after reporting why it is not one.</summary>
    public long? AsInteger(JsonNode node, string path)
    {
        if (!Expect(node, path, JsonKind.Number))
        {
            return null;
        }

        if (node.TryGetInteger(out var value))
        {
            return value;
        }

        Report(DiagnosticCodes.JsonTypeMismatch, path, node);
        return null;
    }

    /// <summary>
    /// Checks a declaration string (contract §3.6). <paramref name="labelCode"/> replaces
    /// <c>text.empty</c> for a <c>name</c>, and a name longer than 32 text elements raises <c>text.long</c>.
    /// </summary>
    public bool DeclarationText(string? value, JsonNode at, string path, int maxUnits, string? labelCode = null)
    {
        if (value is null)
        {
            return false;
        }

        var code = TextRules.CheckDeclarationText(value, maxUnits);
        if (code is not null)
        {
            Report(code == DiagnosticCodes.TextEmpty && labelCode is not null ? labelCode : code, path, at);
            return false;
        }

        if (labelCode is not null && TextRules.TextElements(value) > 32)
        {
            Report(DiagnosticCodes.TextLong, path, at);
        }

        return true;
    }

    /// <summary>The items of an array member with its length checked, or null after reporting why there are none.</summary>
    public List<JsonNode>? Array(JsonNode node, string path, int min, int max)
    {
        if (!Expect(node, path, JsonKind.Array))
        {
            return null;
        }

        var items = node.Items!;
        if (items.Count < min)
        {
            Report(DiagnosticCodes.ListTooShort, path, node);
        }
        else if (items.Count > max)
        {
            Report(DiagnosticCodes.ListTooLong, path, node);
        }

        return items;
    }

    /// <summary>Reports the duplicate members a parse found.</summary>
    public void Duplicates(JsonParse parse)
    {
        foreach (var (path, value) in parse.Duplicates)
        {
            Report(DiagnosticCodes.JsonDuplicateMember, path, value);
        }
    }

    /// <summary>
    /// Reports a <c>$schema</c> member: at most 512 UTF-16 code units, and the warning
    /// <c>schema.uri-mismatch</c> for an absolute http or https URL not in <paramref name="published"/>.
    /// </summary>
    public void SchemaMember(Dictionary<string, JsonNode> members, string path, IEnumerable<string>? published)
    {
        if (!members.TryGetValue("$schema", out var node))
        {
            return;
        }

        var memberPath = JsonPointer.Append(path, "$schema");
        if (AsString(node, memberPath) is not { } value)
        {
            return;
        }

        if (value.Length > 512)
        {
            Report(DiagnosticCodes.StringTooLong, memberPath, node);
            return;
        }

        if (published is not null && Uri.TryCreate(value, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp)
            && !published.Contains(value, StringComparer.Ordinal))
        {
            Report(DiagnosticCodes.SchemaUriMismatch, memberPath, node);
        }
    }

    /// <summary>Reports a parse failure as one diagnostic.</summary>
    public void ParseFailure(JsonParse parse)
    {
        var code = parse.Failure == JsonFailure.Depth ? DiagnosticCodes.JsonDepth : DiagnosticCodes.JsonSyntax;
        Bag.Report(code, parse.FailurePath, File, parse.FailureLine, parse.FailureColumn);
    }
}

/// <summary>The published JSON Schema URLs (contract §2.2, §11.5).</summary>
internal static class SchemaUrls
{
    private const string Root = "https://dev.ventana.tools/schemas/extensions/";

    public static string HostKeyed(string hostId, string name) => Root + hostId + "/" + name + ".v3.json";

    public static string HostNeutral(string name, int version) => Root + name + ".v" + version.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json";

    /// <summary>The host-keyed URLs of <paramref name="name"/> for every listed host, by id and by known aliases.</summary>
    public static IEnumerable<string> ForHosts(IEnumerable<string> hosts, string name, IReadOnlyList<HostInfo> knownHosts)
    {
        foreach (var id in hosts)
        {
            if (!TextRules.IsHostId(id))
            {
                continue;
            }

            yield return HostKeyed(id, name);
            if (HostRegistry.Find(id, knownHosts) is { } host)
            {
                if (TextRules.IsHostId(host.Id))
                {
                    yield return HostKeyed(host.Id, name);
                }

                foreach (var alias in host.Aliases ?? [])
                {
                    if (TextRules.IsHostId(alias))
                    {
                        yield return HostKeyed(alias, name);
                    }
                }
            }
        }
    }
}
