// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Text.Json;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tests;

/// <summary>Writes and compares <c>*.expected.json</c> diagnostic lists.</summary>
internal static class Expected
{
    /// <summary>The diagnostics as fixture JSON: code, path, severity, then file, line and column when present.</summary>
    public static byte[] Serialize(IEnumerable<Diagnostic> diagnostics) => Fixtures.WriteJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteStartArray("diagnostics");
        foreach (var diagnostic in diagnostics)
        {
            writer.WriteStartObject();
            writer.WriteString("code", diagnostic.Code);
            writer.WriteString("path", diagnostic.Path);
            writer.WriteString("severity", diagnostic.Severity.ToString());
            if (diagnostic.File is not null)
            {
                writer.WriteString("file", diagnostic.File);
            }

            if (diagnostic.Line is { } line)
            {
                writer.WriteNumber("line", line);
            }

            if (diagnostic.Column is { } column)
            {
                writer.WriteNumber("column", column);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    });

    /// <summary>Compares diagnostics with the expected file, or writes it when fixtures are being updated.</summary>
    public static void Match(string relative, IReadOnlyList<Diagnostic> diagnostics)
    {
        var actual = Serialize(diagnostics);
        if (Fixtures.Update)
        {
            File.WriteAllBytes(Fixtures.PathOf(relative), actual);
            return;
        }

        Assert.True(File.Exists(Fixtures.PathOf(relative)), "Missing " + relative);
        Assert.Equal(Fixtures.Utf8(Fixtures.Text(relative)), Fixtures.Utf8(actual));
    }

    /// <summary>The (code, path) pairs of an inline expected list.</summary>
    public static List<(string Code, string Path)> Pairs(JsonElement list) =>
        list.EnumerateArray().Select(item => (item.GetProperty("code").GetString()!, item.GetProperty("path").GetString()!)).ToList();

    public static List<(string Code, string Path)> Pairs(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Path)).ToList();
}
