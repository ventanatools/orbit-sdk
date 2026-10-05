// SPDX-License-Identifier: Apache-2.0
// Copyright 2026 Seth Cottle

using System.Text.Json;
using Orbit.Extensions.Protocol;

namespace Orbit.Extensions.Sdk;

internal sealed record HostMessage(string Type, string? SessionId = null, string? RequestId = null,
    ExternalExtensionAction? Action = null, IReadOnlyDictionary<string, string>? Settings = null);

/// <summary>Typed host messages and the companion's narrow output vocabulary; shared framing and JSON rules.</summary>
internal static class CompanionMessages
{
    internal static HostMessage Read(byte[] bytes, IReadOnlyDictionary<string, ExternalExtensionAction> actions)
    {
        using var document = ExtensionJson.ReadObject(bytes);
        var root = document.RootElement;
        var type = ExtensionWire.Text(root, "type");
        switch (type)
        {
            case "stopSession":
                ExtensionJson.RequireFields(root, ["type", "sessionId"]);
                return new(type, Id(root, "sessionId"));
            case "cancel":
                ExtensionJson.RequireFields(root, ["type", "requestId"]);
                return new(type, RequestId: Id(root, "requestId"));
            case "startSession":
            case "invoke":
                ExtensionJson.RequireFields(root, type == "invoke"
                    ? ["type", "sessionId", "requestId", "actionId", "settings"]
                    : ["type", "sessionId", "actionId", "settings"]);
                var actionId = ExtensionWire.Text(root, "actionId");
                if (!actions.TryGetValue(actionId, out var action)) throw Invalid();
                var element = root.GetProperty("settings");
                if (element.ValueKind != JsonValueKind.Object) throw Invalid();
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    if (values.Count >= ExternalExtensionSettings.MaxSettings
                        || property.Value.ValueKind != JsonValueKind.String
                        || property.Value.GetString() is not { Length: <= ExternalExtensionSettings.MaxChoiceLength } value
                        || !values.TryAdd(property.Name, value)) throw Invalid();
                }
                var resolved = ExternalExtensionSettings.Resolve(action, values);
                // Host sessions always carry the complete effective configuration, not a patch.
                if (resolved is null || !SameSettings(values, resolved)) throw Invalid();
                return new(type, Id(root, "sessionId"), type == "invoke" ? Id(root, "requestId") : null, action, resolved);
            default: throw Invalid();
        }
    }

    internal static bool SameSettings(IReadOnlyDictionary<string, string> first,
        IReadOnlyDictionary<string, string> second) =>
        first.Count == second.Count && first.All(pair => second.TryGetValue(pair.Key, out var value)
            && string.Equals(pair.Value, value, StringComparison.Ordinal));

    private static string Id(JsonElement root, string name)
    {
        var value = ExtensionWire.Text(root, name);
        return Guid.TryParseExact(value, "N", out var id) && id != Guid.Empty && id.ToString("N") == value
            ? value : throw Invalid();
    }

    internal static byte[] Hello(ExtensionPairing pairing, string nonce) => Encode(json =>
    {
        json.WriteStartObject();
        json.WriteString("type", "hello");
        json.WriteNumber("protocolVersion", pairing.ProtocolVersion);
        json.WriteString("registrationId", pairing.RegistrationId);
        json.WriteString("clientNonce", nonce);
        json.WriteEndObject();
    });

    internal static byte[] Result(string requestId, CompanionOutcome outcome) =>
        ExtensionWire.Message("result", ("requestId", requestId), ("outcome", outcome switch
        {
            CompanionOutcome.Done => "done",
            CompanionOutcome.Refused => "refused",
            CompanionOutcome.Unsupported => "unsupported",
            _ => "failed",
        }));

    internal static byte[] Face(string sessionId, CompanionFace face)
    {
        ArgumentNullException.ThrowIfNull(face);
        if (face.GoodForSeconds is < 1 or > 86400 || !Enum.IsDefined(face.State)
            || face.Glyph is not null && !ExtensionText.IsGlyph(face.Glyph))
            throw new ArgumentException("Invalid companion face.", nameof(face));
        return Command(sessionId, json =>
        {
            json.WriteString("$type", "setFace");
            json.WriteStartObject("face");
            json.WriteStartObject("picture");
            json.WriteString("$type", face.Glyph is null ? "none" : "glyph");
            if (face.Glyph is not null) json.WriteString("codePoint", face.Glyph);
            json.WriteEndObject();
            Line(json, "line1", ExtensionText.Clean(face.Line1, 40, 160));
            Line(json, "line2", ExtensionText.Clean(face.Line2, 60, 240));
            var detail = ExtensionText.Clean(face.Detail, 160, 640);
            if (detail is not null) json.WriteString("detail", detail);
            json.WriteString("state", face.State.ToString());
            json.WriteNumber("goodForSeconds", face.GoodForSeconds);
            json.WriteEndObject();
        });
    }

    internal static byte[] Clear(string sessionId) =>
        Command(sessionId, json => json.WriteString("$type", "clearFace"));

    internal static byte[] Fail(string sessionId, CompanionFailure failure)
    {
        if (!Enum.IsDefined(failure)) throw new ArgumentOutOfRangeException(nameof(failure));
        return Command(sessionId, json =>
        {
            json.WriteString("$type", "fail");
            json.WriteString("failure", failure.ToString());
        });
    }

    private static byte[] Command(string sessionId, Action<Utf8JsonWriter> write) => Encode(json =>
    {
        json.WriteStartObject();
        json.WriteString("type", "face");
        json.WriteString("sessionId", sessionId);
        json.WriteStartObject("command");
        write(json);
        json.WriteEndObject();
        json.WriteEndObject();
    });

    private static void Line(Utf8JsonWriter json, string name, string? text)
    {
        if (text is null) return;
        json.WriteStartObject(name);
        json.WriteString("$type", "text");
        json.WriteString("value", text);
        json.WriteEndObject();
    }

    private static byte[] Encode(Action<Utf8JsonWriter> write)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream)) write(json);
        return stream.ToArray();
    }

    private static InvalidDataException Invalid() => new("Invalid extension host message.");
}
