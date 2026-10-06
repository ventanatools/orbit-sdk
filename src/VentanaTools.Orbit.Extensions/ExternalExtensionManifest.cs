// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Collections.ObjectModel;
using System.Text.Json;

namespace VentanaTools.Orbit.Extensions;

/// <summary>A portable declaration of commands and face contributions. It contains no executable, credentials or host powers.</summary>
public sealed record ExternalExtensionManifest(
    string Id, string Name, string Description, IReadOnlyList<ExternalExtensionAction> Actions)
{
    public const int SchemaVersion = 2;
    public int ManifestVersion { get; init; } = SchemaVersion;
    public string PackageVersion { get; init; } = "1.0.0";
    public IReadOnlyList<string> Hosts { get; init; } = ["orbit"];
    public override string ToString() => nameof(ExternalExtensionManifest);
}

public sealed record ExternalExtensionAction(string Id, string Name, string Description, string Glyph)
{
    public bool CanInvoke { get; init; } = true;
    public bool HasFace { get; init; }
    public IReadOnlyList<ExternalExtensionSetting> Settings { get; init; } = [];
    public override string ToString() => nameof(ExternalExtensionAction);
}

/// <summary>Only fixed diagnostic codes escape the reader, never file content.</summary>
public sealed record ExternalExtensionManifestRead(ExternalExtensionManifest? Manifest, string? Error);

public static class ExternalExtensionManifestReader
{
    public const int MaxBytes = 65536;
    public const int MaxActions = 32;
    private static readonly string[] ManifestFields = ["schemaVersion", "id", "name", "description", "version", "hosts", "contributions"];
    private static readonly string[] ActionFields = ["id", "name", "description", "glyph", "capabilities", "settings"];

    public static ExternalExtensionManifestRead Read(ReadOnlySpan<byte> bytes, IReadOnlyCollection<string>? reservedPublishers = null)
    {
        if (bytes.Length is 0 or > MaxBytes) return new(null, "manifest.size");
        var policy = reservedPublishers ?? ExtensionIds.ReservedPublishers;
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schemaVersion", out var schema))
                return new(null, "manifest.fields");
            if (schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != ExternalExtensionManifest.SchemaVersion)
                return new(null, "manifest.version");
            if (!Fields(root, ManifestFields)) return new(null, "manifest.fields");
            if (!Text(root, "id", ExtensionIds.MaxLength, out var id) || !ExtensionIds.IsValid(id, ExtensionOrigin.ThirdParty, policy)
                || id.Contains('/', StringComparison.Ordinal)
                || !Text(root, "name", 80, out var name) || !Text(root, "description", 512, out var description))
                return new(null, "manifest.identity");
            if (!Text(root, "version", 64, out var packageVersion) || !PackageVersion(packageVersion)
                || ReadHosts(root.GetProperty("hosts")) is not { } hosts)
                return new(null, "manifest.version");
            var array = root.GetProperty("contributions");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > MaxActions)
                return new(null, "manifest.actions");
            var actions = new List<ExternalExtensionAction>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in array.EnumerateArray())
            {
                if (!Fields(row, ActionFields)
                    || !Text(row, "id", ExtensionIds.MaxLength, out var actionId)
                    || !IsActionId(actionId, policy) || !ExtensionIds.IsInNamespace(actionId, id) || !ids.Add(actionId)
                    || !Text(row, "name", 80, out var actionName)
                    || !Text(row, "description", 512, out var actionDescription)
                    || !Text(row, "glyph", 1, out var glyph) || !ExtensionText.IsGlyph(glyph))
                    return new(null, "manifest.action");
                if (!Capabilities(row.GetProperty("capabilities"), out var invoke, out var face)
                    || ReadSettings(row.GetProperty("settings")) is not { } settings)
                    return new(null, "manifest.action");
                actions.Add(new(actionId, actionName, actionDescription, glyph)
                    { CanInvoke = invoke, HasFace = face, Settings = settings });
            }
            return new(new(id, name, description, actions.AsReadOnly())
                { ManifestVersion = version, PackageVersion = packageVersion, Hosts = hosts }, null);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException)
        {
            return new(null, "manifest.json");
        }
    }

    /// <summary>Writes the declared schema in its canonical field order. It never writes credentials or executable paths.</summary>
    public static byte[] Serialize(ExternalExtensionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        CheckWriteBounds(manifest);
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", manifest.ManifestVersion);
            json.WriteString("id", manifest.Id);
            json.WriteString("name", manifest.Name);
            json.WriteString("description", manifest.Description);
            json.WriteString("version", manifest.PackageVersion);
            json.WriteStartArray("hosts");
            foreach (var host in manifest.Hosts) json.WriteStringValue(host);
            json.WriteEndArray();
            json.WriteStartArray("contributions");
            foreach (var action in manifest.Actions)
            {
                json.WriteStartObject();
                json.WriteString("id", action.Id);
                json.WriteString("name", action.Name);
                json.WriteString("description", action.Description);
                json.WriteString("glyph", action.Glyph);
                json.WriteStartArray("capabilities");
                if (action.CanInvoke) json.WriteStringValue("invoke");
                if (action.HasFace) json.WriteStringValue("face");
                json.WriteEndArray();
                json.WriteStartArray("settings");
                foreach (var setting in action.Settings)
                {
                    json.WriteStartObject();
                    json.WriteString("id", setting.Id);
                    json.WriteString("name", setting.Name);
                    json.WriteString("default", setting.DefaultValue);
                    json.WriteStartArray("choices");
                    foreach (var choice in setting.Choices)
                    {
                        json.WriteStartObject();
                        json.WriteString("value", choice.Value);
                        json.WriteString("name", choice.Name);
                        json.WriteEndObject();
                    }
                    json.WriteEndArray();
                    json.WriteEndObject();
                }
                json.WriteEndArray();
                json.WriteEndObject();
            }
            json.WriteEndArray();
            json.WriteEndObject();
        }
        if (stream.Length > MaxBytes) throw new InvalidDataException("Invalid extension manifest.");
        return stream.ToArray();
    }

    /// <summary>Strictly validates and copies a programmatic declaration into read-only nested collections.</summary>
    public static ExternalExtensionManifest Snapshot(ExternalExtensionManifest manifest,
        IReadOnlyCollection<string>? reservedPublishers = null) =>
        Read(Serialize(manifest), reservedPublishers).Manifest
            ?? throw new InvalidDataException("Invalid extension manifest.");

    private static void CheckWriteBounds(ExternalExtensionManifest manifest)
    {
        if (manifest.ManifestVersion != ExternalExtensionManifest.SchemaVersion || manifest.Id is not { Length: <= ExtensionIds.MaxLength }
            || manifest.Name is not { Length: <= 80 } || manifest.Description is not { Length: <= 512 }
            || manifest.Actions is not { Count: > 0 and <= MaxActions })
            throw new InvalidDataException("Invalid extension manifest.");
        if (manifest.PackageVersion is not { Length: <= 64 } || manifest.Hosts is not { Count: > 0 and <= 8 }
            || manifest.Hosts.Any(host => host is not { Length: <= 32 }))
            throw new InvalidDataException("Invalid extension manifest.");
        foreach (var action in manifest.Actions)
        {
            if (action is null || action.Id is not { Length: <= ExtensionIds.MaxLength } || action.Name is not { Length: <= 80 }
                || action.Description is not { Length: <= 512 } || action.Glyph is not { Length: 1 }
                || action.Settings is not { Count: <= ExternalExtensionSettings.MaxSettings })
                throw new InvalidDataException("Invalid extension manifest.");
            if (!action.CanInvoke && !action.HasFace)
                throw new InvalidDataException("Invalid extension manifest.");
            foreach (var setting in action.Settings)
            {
                if (setting is null || setting.Id is not { Length: <= ExtensionText.MaxSettingKeyLength }
                    || setting.Name is not { Length: <= 80 } || setting.DefaultValue is not { Length: <= ExternalExtensionSettings.MaxChoiceLength }
                    || setting.Choices is not { Count: >= 2 and <= 32 })
                    throw new InvalidDataException("Invalid extension manifest.");
                foreach (var choice in setting.Choices)
                    if (choice is null || choice.Value is not { Length: <= ExternalExtensionSettings.MaxChoiceLength }
                        || choice.Name is not { Length: <= 80 })
                        throw new InvalidDataException("Invalid extension manifest.");
            }
        }
    }

    public static bool IsActionId(string? id) => IsActionId(id, ExtensionIds.ReservedPublishers);

    public static bool IsActionId(string? id, IReadOnlyCollection<string> reservedPublishers) =>
        id is not null && ExtensionIds.IsValid(id, ExtensionOrigin.ThirdParty, reservedPublishers)
        && id.Contains('/', StringComparison.Ordinal);

    private static bool PackageVersion(string value)
    {
        var parts = value.Split('.');
        return parts.Length == 3 && parts.All(part => part.Length is > 0 and <= 9
            && (part.Length == 1 || part[0] != '0') && part.All(char.IsAsciiDigit));
    }

    private static ReadOnlyCollection<string>? ReadHosts(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 8) return null;
        var hosts = new List<string>();
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String || entry.GetString() is not { Length: > 0 and <= 32 } host
                || !char.IsAsciiLetterLower(host[0])
                || host.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
                || hosts.Contains(host, StringComparer.Ordinal)) return null;
            hosts.Add(host);
        }
        return hosts.AsReadOnly();
    }

    private static bool Capabilities(JsonElement array, out bool invoke, out bool face)
    {
        invoke = false;
        face = false;
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 2) return false;
        foreach (var entry in array.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.String) return false;
            switch (entry.GetString())
            {
                case "invoke" when !invoke: invoke = true; break;
                case "face" when !face: face = true; break;
                default: return false;
            }
        }
        return true;
    }

    private static ReadOnlyCollection<ExternalExtensionSetting>? ReadSettings(JsonElement array)
    {
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > ExternalExtensionSettings.MaxSettings) return null;
        var settings = new List<ExternalExtensionSetting>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in array.EnumerateArray())
        {
            if (!Fields(row, ["id", "name", "default", "choices"])
                || !Text(row, "id", ExtensionText.MaxSettingKeyLength, out var id) || !ExtensionText.IsSettingKey(id) || !ids.Add(id)
                || !Text(row, "name", 80, out var name)
                || !Text(row, "default", ExternalExtensionSettings.MaxChoiceLength, out var defaultValue)) return null;
            var choices = row.GetProperty("choices");
            if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() is < 2 or > 32) return null;
            var values = new HashSet<string>(StringComparer.Ordinal);
            var declared = new List<ExternalExtensionChoice>();
            foreach (var choice in choices.EnumerateArray())
            {
                if (!Fields(choice, ["value", "name"])
                    || !Text(choice, "value", ExternalExtensionSettings.MaxChoiceLength, out var value)
                    || !ExtensionIds.IsSegment(value) || !values.Add(value)
                    || !Text(choice, "name", 80, out var label)) return null;
                declared.Add(new(value, label));
            }
            if (!values.Contains(defaultValue)) return null;
            settings.Add(new(id, name, defaultValue, declared.AsReadOnly()));
        }
        return settings.AsReadOnly();
    }

    private static bool Fields(JsonElement element, string[] allowed) => ExtensionJson.HasFields(element, allowed);

    private static bool Text(JsonElement owner, string key, int maximum, out string value)
    {
        var element = owner.GetProperty(key);
        value = element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : string.Empty;
        return ExtensionText.IsDeclarationText(value, maximum);
    }
}
