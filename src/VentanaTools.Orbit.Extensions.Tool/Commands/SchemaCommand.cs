// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Text;

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>The JSON Schemas of contract §11.5, embedded in the tool from <c>schemas/extensions/</c>.</summary>
internal static class Schemas
{
    private const string Prefix = "schemas/extensions/";

    /// <summary>The schema kinds <c>schema --kind</c> accepts.</summary>
    public static IReadOnlyList<string> Kinds { get; } = ["manifest", "strings", "package", "pairing", "pack", "simulation"];

    private static readonly Lazy<Dictionary<string, string>> Names = new(() =>
        typeof(Schemas).Assembly.GetManifestResourceNames()
            .Select(name => (Name: name, Normalized: name.Replace('\\', '/')))
            .Where(entry => entry.Normalized.StartsWith(Prefix, StringComparison.Ordinal))
            .ToDictionary(entry => entry.Normalized[Prefix.Length..], entry => entry.Name, StringComparer.Ordinal));

    /// <summary>The schema's path under <c>schemas/extensions/</c>: host-keyed for manifests and strings, host-neutral otherwise.</summary>
    public static string RelativePath(string kind, string hostId) => kind switch
    {
        "manifest" => hostId + "/manifest.v3.json",
        "strings" => hostId + "/strings.v3.json",
        "package" => "package.v2.json",
        "pairing" => "pairing.v3.json",
        "pack" => "pack.v1.json",
        "simulation" => "simulation.v1.json",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Whether <paramref name="kind"/>'s schema depends on the host.</summary>
    public static bool IsHostKeyed(string kind) => kind is "manifest" or "strings";

    /// <summary>The bytes of an embedded schema, or null when the tool carries none at that path.</summary>
    public static byte[]? Read(string relativePath)
    {
        if (!Names.Value.TryGetValue(relativePath, out var name))
        {
            return null;
        }

        using var stream = typeof(Schemas).Assembly.GetManifestResourceStream(name)!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}

/// <summary>
/// <c>schema [--kind &lt;manifest|strings|package|pairing|pack|simulation&gt;] [--host &lt;id&gt;] [-o &lt;file&gt;]</c>:
/// writes a bundled JSON Schema, for editors that work offline.
/// </summary>
internal static class SchemaCommand
{
    public static async Task<int> RunAsync(ToolConsole console, string kind, string? hostId, string? output, CancellationToken cancellationToken)
    {
        var host = hostId ?? HostRegistry.Known.FirstOrDefault(entry => entry.Status == HostStatus.Active)?.Id;
        if (host is null && Schemas.IsHostKeyed(kind))
        {
            console.Fail("No active host is registered; pass --host.");
            return ExitCodes.Usage;
        }

        var bytes = Schemas.Read(Schemas.RelativePath(kind, host ?? string.Empty));
        if (bytes is null)
        {
            console.Fail("The tool carries no " + kind + " schema for host " + host + ".");
            return ExitCodes.Usage;
        }

        if (output is null)
        {
            await console.Out.WriteAsync(Encoding.UTF8.GetString(bytes).AsMemory(), cancellationToken).ConfigureAwait(false);
            return ExitCodes.Success;
        }

        var path = console.FullPath(output);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            console.Fail("Cannot write " + console.Display(path) + ".");
            return ExitCodes.InputOutput;
        }

        console.Note("wrote " + console.Display(path));
        return ExitCodes.Success;
    }
}
