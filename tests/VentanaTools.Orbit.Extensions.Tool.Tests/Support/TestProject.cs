// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions.Tool.Tests;

/// <summary>Small extension projects the tool tests pack, validate and simulate.</summary>
internal static class TestProject
{
    /// <summary>A manifest with a widget and an action, for <paramref name="hostId"/>.</summary>
    public static string Manifest(string hostId, string id = "example.tool-test") => $$"""
        {
          "$schema": ".schemas/manifest.v3.json",
          "schemaVersion": 3,
          "id": "{{id}}",
          "name": "Tool test",
          "description": "An extension the tool tests pack and simulate.",
          "version": "1.2.3",
          "hosts": ["{{hostId}}"],
          "disclosures": { "network": "None" },
          "contributions": [
            {
              "id": "{{id}}/widget",
              "name": "Widget",
              "description": "Shows how often it was invoked.",
              "glyph": "\uE710",
              "provides": ["invoke", "face"]
            },
            {
              "id": "{{id}}/action",
              "name": "Action",
              "description": "Passes or fails, as its setting says.",
              "glyph": "\uE8BD",
              "provides": ["invoke"],
              "settings": [
                {
                  "id": "mode",
                  "kind": "Choice",
                  "name": "Mode",
                  "default": "pass",
                  "choices": [
                    { "value": "pass", "name": "Pass" },
                    { "value": "fail", "name": "Fail" }
                  ]
                }
              ]
            }
          ]
        }
        """;

    /// <summary>German strings for <see cref="Manifest"/>.</summary>
    public static string Strings(string id = "example.tool-test") => $$"""
        {
          "schemaVersion": 3,
          "language": "de-DE",
          "name": "Werkzeugtest",
          "contributions": {
            "{{id}}/widget": { "name": "Anzeige" }
          }
        }
        """;

    /// <summary>
    /// A project with <c>extension.json</c>, <c>strings/</c>, a readme, a <c>companion/</c> folder of
    /// payload files and an <c>extension.pack.json</c> that copies them.
    /// </summary>
    public static TempFolder Create(string? hostId = null, string? packConfig = null)
    {
        var folder = new TempFolder();
        folder.Write("extension.json", Manifest(hostId ?? ActiveHost.Id));
        folder.Write("strings/de-DE.json", Strings());
        folder.Write("PACKAGE-README.md", "# Tool test\n\nA package the tool tests build.\n");
        folder.Write("companion/run.cmd", "@echo off\r\necho companion\r\n");
        folder.Write("companion/settings.json", "{ \"greeting\": \"hello\" }\n");
        folder.Write("companion/obj/cache.bin", "never packed");
        folder.Write("companion/notes.user", "never packed");
        folder.Write("companion/example.tool-test.pairing.json", "never packed: an always-excluded name");
        folder.Write("extension.pack.json", packConfig ?? """
            {
              "packVersion": 1,
              "readme": "PACKAGE-README.md",
              "strings": "strings",
              "payload": [
                { "from": "companion", "to": "companion" },
                { "from": "PACKAGE-README.md", "to": "docs" }
              ]
            }
            """);
        return folder;
    }

    /// <summary>A pairing file for <paramref name="extensionId"/> and <paramref name="hostId"/>; the secret is a fixed test value.</summary>
    public static string Pairing(string hostId, string extensionId) => $$"""
        {
          "pairingVersion": 3,
          "mode": "Persistent",
          "hostId": "{{hostId}}",
          "pipeName": "Ventana.Extensions.v3.{{hostId}}.dev.0123456789abcdef.00112233445566778899aabbccddeeff",
          "registrationId": "00112233445566778899aabbccddeeff",
          "extensionId": "{{extensionId}}",
          "secret": "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="
        }
        """;
}
