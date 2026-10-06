// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using Json.Schema;
using VentanaTools.Orbit.Extensions.Packaging;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Schemas.Tests;

/// <summary>The repository, the schemas in <c>schemas/extensions/</c> and the shared fixtures.</summary>
internal static class Repository
{
    private static readonly Lazy<string> RootPath = new(() =>
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "fixtures", "hosts.json")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test binaries.");
    });

    private static readonly ConcurrentDictionary<string, JsonSchema> Loaded = new(StringComparer.Ordinal);

    public static string Root => RootPath.Value;

    public static string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    public static string SchemaFolder => PathOf("schemas/extensions");

    /// <summary>A schema under <c>schemas/extensions/</c>, loaded once into a registry of its own.</summary>
    public static JsonSchema Schema(string relative) => Loaded.GetOrAdd(relative, path =>
        JsonSchema.FromText(File.ReadAllText(Path.Combine(SchemaFolder, path)), new BuildOptions { SchemaRegistry = new SchemaRegistry() }));

    /// <summary>Every schema file, relative to <c>schemas/extensions/</c> with <c>/</c> separators.</summary>
    public static IEnumerable<string> SchemaFiles() =>
        Directory.EnumerateFiles(SchemaFolder, "*.json", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(SchemaFolder, path).Replace(Path.DirectorySeparatorChar, '/'))
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// Whether <paramref name="schema"/> accepts <paramref name="bytes"/>. A file that is not JSON at
    /// all is refused: an editor reports it as a syntax error before any schema applies.
    /// </summary>
    public static bool Accepts(JsonSchema schema, byte[] bytes)
    {
        try
        {
            // Editors ignore a UTF-8 byte order mark, as readers do (contract §3.1).
            var start = bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? 3 : 0;
            using var document = JsonDocument.Parse(bytes.AsMemory(start));
            return schema.Evaluate(document.RootElement).IsValid;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }
}

/// <summary>The schema files themselves (contract §11.5).</summary>
public sealed class SchemaFileTests
{
    private const string Root = "https://dev.ventana.tools/schemas/extensions/";

    public static TheoryData<string> Files() => [.. Repository.SchemaFiles()];

    [Theory]
    [MemberData(nameof(Files))]
    public void EverySchemaIsDraft202012AndItsIdIsItsUrl(string relative)
    {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Repository.SchemaFolder, relative)));
        Assert.Equal("https://json-schema.org/draft/2020-12/schema", document.RootElement.GetProperty("$schema").GetString());
        Assert.Equal(Root + relative, document.RootElement.GetProperty("$id").GetString());
        Assert.True(MetaSchemas.Draft202012.Evaluate(document.RootElement).IsValid, relative + " is not a valid 2020-12 schema.");
        Assert.NotNull(Repository.Schema(relative));
    }

    [Fact]
    public void EachActiveHostHasItsManifestAndStringsSchemasAndNothingElseIsHostKeyed()
    {
        var active = HostRegistry.Known.Where(host => host.Status == HostStatus.Active).Select(host => host.Id).Order(StringComparer.Ordinal);
        var folders = Directory.EnumerateDirectories(Repository.SchemaFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal);
        Assert.Equal(active, folders);
        foreach (var host in active)
        {
            Assert.Equal(["manifest.v3.json", "strings.v3.json"],
                Directory.EnumerateFiles(Path.Combine(Repository.SchemaFolder, host)).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        }

        Assert.Equal(["pack.v1.json", "package.v2.json", "pairing.v3.json", "simulation.v1.json"],
            Directory.EnumerateFiles(Repository.SchemaFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void TheSchemasNameNoProductOutsideTheirHostKeyedIds()
    {
        // Product names (contract §2.8) may appear only as the host-id segment of a host-keyed $id.
        var products = HostRegistry.Known.Select(host => host.Id).Concat(HostRegistry.Known.Select(host => host.DisplayName))
            .Concat(HostRegistry.ReservedIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var relative in Repository.SchemaFiles())
        {
            var text = File.ReadAllText(Path.Combine(Repository.SchemaFolder, relative));
            if (relative.Contains('/', StringComparison.Ordinal))
            {
                text = text.Replace("\"$id\": \"" + Root + relative + "\"", string.Empty, StringComparison.Ordinal);
            }

            Assert.DoesNotContain(products, product => text.Contains(product, StringComparison.OrdinalIgnoreCase));
        }
    }
}

/// <summary>
/// Every fixture through the schema and the reader (contract §11.5): the schema is never stricter
/// than the reader, and where the reader refuses a file the schema accepts, every code it reports
/// is a reader-only rule listed here.
/// </summary>
public sealed class FixtureAgreementTests
{
    /// <summary>The rules only the reader checks, and why a schema cannot.</summary>
    private static readonly Dictionary<string, string> ReaderOnly = new(StringComparer.Ordinal)
    {
        ["json.too-large"] = "a file size",
        ["json.depth"] = "a nesting limit applied before the schema",
        ["json.duplicate-member"] = "JSON parsers keep one of two duplicate members",
        ["json.type-mismatch"] = "a number with a fraction or exponent that JSON Schema counts as an integer (3.0)",
        ["id.root-reserved"] = "the reserved publishers, kept out of the schemas so they name no product",
        ["id.outside-namespace"] = "a contribution id under the extension id: a cross-member check",
        ["id.duplicate"] = "unique contribution ids: uniqueItems compares whole objects",
        ["setting.id-duplicate"] = "unique setting ids: uniqueItems compares whole objects",
        ["choice.value-duplicate"] = "unique choice values: uniqueItems compares whole objects",
        ["setting.default-unknown"] = "default among the choice values: a cross-member check",
        ["manifest.host-not-listed"] = "the reading host's id, an option",
        ["requires.capability-unsupported"] = "the host's capabilities, an option",
        ["text.empty"] = "text of format characters only (the Cf class)",
        ["chrome.label-required"] = "a name of format characters only (the Cf class)",
        ["setting.label-required"] = "a name of format characters only (the Cf class)",
        ["choice.label-required"] = "a name of format characters only (the Cf class)",
        ["text.invalid-character"] = "invisible format characters (the Cf class) and unpaired surrogates",
        ["string.too-long"] = "lengths in UTF-16 code units, where JSON Schema counts code points",
        ["url.invalid"] = "the full URL rule: an authority, no user information",
        ["strings.target-unknown"] = "keys checked against the manifest",
        ["strings.language-mismatch"] = "language checked against the file name",
        ["strings.language-invalid"] = "the file name's tag",
        ["pairing.too-large"] = "a file size",
        ["pairing.host-not-listed"] = "hostId checked against the manifest",
        ["pairing.extension-mismatch"] = "extensionId checked against the manifest",
        ["pairing.pipe-name-invalid"] = "the pipe name's host id and registration id checked against hostId and registrationId",
        ["package.descriptor"] = "inventory rows sorted, unique and not listing the descriptor: checks across rows",
        ["package.manifest-hash"] = "manifestHash checked against the archive's manifest",
        ["package.inventory-missing"] = "the inventory checked against the archive",
        ["package.hash-mismatch"] = "sizes and hashes checked against the archive",
    };

    private static readonly ExtensionManifest Countdown =
        ManifestReader.Read(File.ReadAllBytes(Repository.PathOf("fixtures/manifests/valid/countdown.json"))).Value!;

    public static TheoryData<string> Manifests() => Fixtures("fixtures/manifests", SearchOption.AllDirectories);

    public static TheoryData<string> StringsFiles() => Fixtures("fixtures/strings", SearchOption.AllDirectories);

    public static TheoryData<string> PairingFiles() => Fixtures("fixtures/pairing", SearchOption.TopDirectoryOnly);

    [Theory]
    [MemberData(nameof(Manifests))]
    public void ManifestSchemaAndReaderAgree(string relative)
    {
        var bytes = File.ReadAllBytes(Repository.PathOf(relative));
        var options = ManifestOptions(relative);
        var read = ManifestReader.Read(bytes, options);
        Agree(relative, ActiveManifestSchema, bytes, read.Diagnostics);
    }

    [Theory]
    [MemberData(nameof(StringsFiles))]
    public void StringsSchemaAndReaderAgree(string relative)
    {
        var bytes = File.ReadAllBytes(Repository.PathOf(relative));
        var read = StringsReader.Read(bytes, Path.GetFileNameWithoutExtension(relative), new StringsReadOptions { Manifest = Countdown });
        Agree(relative, Repository.Schema(HostKeyed("strings.v3.json")), bytes, read.Diagnostics);
    }

    [Theory]
    [MemberData(nameof(PairingFiles))]
    public void PairingSchemaAndReaderAgree(string relative)
    {
        var bytes = File.ReadAllBytes(Repository.PathOf(relative));
        var read = PairingReader.Read(bytes, new PairingReadOptions { ExpectedExtensionId = "example.countdown", ManifestHosts = ["example-host"] });
        using (read.Value)
        {
            Agree(relative, Repository.Schema("pairing.v3.json"), bytes, read.Diagnostics);
        }
    }

    public static TheoryData<string> Archives() =>
        [.. Directory.EnumerateFiles(Repository.PathOf("fixtures/packages"), "*.zip").Select(path => Path.GetFileName(path)).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Archives))]
    public void DescriptorSchemaAndReaderAgree(string name)
    {
        var bytes = File.ReadAllBytes(Repository.PathOf("fixtures/packages/" + name));
        var read = PackageReader.Read(bytes, new PackageReadOptions { Manifest = new ManifestReadOptions { KnownHosts = [] } });
        byte[] descriptor;
        try
        {
            using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (archive.GetEntry("extension.package.json") is not { } entry)
            {
                return;
            }

            using var stream = entry.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            descriptor = copy.ToArray();
        }
        catch (Exception error) when (error is InvalidDataException or NotSupportedException or IOException)
        {
            return; // The archive itself is malformed; there is no descriptor to compare.
        }

        var descriptorFindings = read.Diagnostics.Where(diagnostic => diagnostic.File == "extension.package.json").ToList();
        var accepted = Repository.Accepts(Repository.Schema("package.v2.json"), descriptor);
        if (descriptorFindings.Count == 0)
        {
            Assert.True(accepted, name + ": the reader accepts the descriptor but the schema refuses it.");
        }
        else if (accepted)
        {
            AssertReaderOnly(name, descriptorFindings);
        }
    }

    [Theory]
    [InlineData("templates/content/action-csharp/extension.json")]
    [InlineData("templates/content/widget-csharp/extension.json")]
    [InlineData("templates/content/node/extension.json")]
    public void TheTemplateManifestsAreValid(string relative)
    {
        var bytes = File.ReadAllBytes(Repository.PathOf(relative));
        var read = ManifestReader.Read(bytes, new ManifestReadOptions { KnownHosts = [] });
        Assert.True(read.Succeeded, string.Join("\n", read.Diagnostics));
        Assert.True(Repository.Accepts(ActiveManifestSchema, bytes));
    }

    private static JsonSchema ActiveManifestSchema => Repository.Schema(HostKeyed("manifest.v3.json"));

    private static string HostKeyed(string name) => HostRegistry.Known.First(host => host.Status == HostStatus.Active).Id + "/" + name;

    private static void Agree(string relative, JsonSchema schema, byte[] bytes, IReadOnlyList<Diagnostic> diagnostics)
    {
        var errors = diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).ToList();
        var accepted = Repository.Accepts(schema, bytes);
        if (errors.Count == 0)
        {
            Assert.True(accepted, relative + ": the reader accepts the file but the schema refuses it (a schema must never be stricter).");
            return;
        }

        if (accepted)
        {
            AssertReaderOnly(relative, errors);
        }
    }

    private static void AssertReaderOnly(string what, IEnumerable<Diagnostic> errors)
    {
        var unexplained = errors.Select(error => error.Code).Where(code => !ReaderOnly.ContainsKey(code)).Distinct().ToList();
        Assert.True(unexplained.Count == 0,
            what + ": the schema accepts a file the reader refuses with " + string.Join(", ", unexplained) + ", which is not a reader-only rule.");
    }

    private static ManifestReadOptions ManifestOptions(string relative)
    {
        var path = Repository.PathOf(Path.ChangeExtension(relative, ".options.json"));
        if (!File.Exists(path))
        {
            return new ManifestReadOptions();
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = document.RootElement;
        return new ManifestReadOptions
        {
            HostId = root.TryGetProperty("hostId", out var host) ? host.GetString() : null,
            SupportedCapabilities = root.TryGetProperty("supportedCapabilities", out var capabilities)
                ? capabilities.EnumerateArray().Select(item => item.GetString()!).ToList()
                : null,
            AllowExperimentalCapabilities = root.TryGetProperty("allowExperimentalCapabilities", out var experimental) && experimental.GetBoolean(),
        };
    }

    private static TheoryData<string> Fixtures(string folder, SearchOption search)
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(Repository.PathOf(folder), "*.json", search)
                     .Select(path => Path.GetRelativePath(Repository.Root, path).Replace(Path.DirectorySeparatorChar, '/'))
                     .Where(path => !path.EndsWith(".expected.json", StringComparison.Ordinal) && !path.EndsWith(".options.json", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            data.Add(path);
        }

        return data;
    }

    [Fact]
    public void TheReaderOnlyListNamesOnlyCatalogCodes()
    {
        foreach (var code in ReaderOnly.Keys)
        {
            Assert.True(typeof(DiagnosticCodes).GetFields().Any(field => (string?)field.GetValue(null) == code) || ReasonCode.TryParse(code, out var reason) && reason.IsKnown,
                code + " is not a known code.");
        }
    }
}
