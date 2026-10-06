// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Node = System.Text.Json.Nodes.JsonNode;

namespace VentanaTools.Orbit.Extensions.Tests;

/// <summary>
/// The small edit language fixtures use to describe a variant of a base document: <c>set</c> maps
/// JSON Pointers to new values (an object member is added when missing), and <c>remove</c> lists
/// pointers to delete.
/// </summary>
internal static class Patch
{
    public static byte[] Apply(byte[] document, JsonElement? set, JsonElement? remove)
    {
        var root = Node.Parse(document)!;
        if (remove is { ValueKind: JsonValueKind.Array } removals)
        {
            foreach (var pointer in removals.EnumerateArray())
            {
                var (parent, token) = Locate(root, pointer.GetString()!);
                if (parent is JsonObject obj)
                {
                    obj.Remove(token);
                }
                else if (parent is JsonArray array)
                {
                    array.RemoveAt(int.Parse(token, System.Globalization.CultureInfo.InvariantCulture));
                }
            }
        }

        if (set is { ValueKind: JsonValueKind.Object } sets)
        {
            foreach (var entry in sets.EnumerateObject())
            {
                var value = entry.Value.ValueKind == JsonValueKind.Null ? null : Node.Parse(entry.Value.GetRawText());
                if (entry.Name.Length == 0)
                {
                    root = value!;
                    continue;
                }

                var (parent, token) = Locate(root, entry.Name);
                if (parent is JsonObject obj)
                {
                    obj[token] = value;
                }
                else if (parent is JsonArray array)
                {
                    var index = int.Parse(token, System.Globalization.CultureInfo.InvariantCulture);
                    if (index == array.Count)
                    {
                        array.Add(value);
                    }
                    else
                    {
                        array[index] = value;
                    }
                }
            }
        }

        return Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    private static (Node Parent, string Token) Locate(Node root, string pointer)
    {
        var tokens = pointer.Split('/')[1..].Select(token => token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
        var node = root;
        foreach (var token in tokens[..^1])
        {
            node = node is JsonArray array
                ? array[int.Parse(token, System.Globalization.CultureInfo.InvariantCulture)]!
                : node[token]!;
        }

        return (node, tokens[^1]);
    }
}
