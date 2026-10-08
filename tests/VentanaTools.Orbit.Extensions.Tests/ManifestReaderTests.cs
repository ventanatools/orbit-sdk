// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tests;

public sealed class ManifestReaderTests
{
    public static TheoryData<string> ValidManifests() => Names("manifests/valid");

    public static TheoryData<string> InvalidManifests()
    {
        var data = new TheoryData<string>();
        foreach (var name in Directory.EnumerateFiles(Fixtures.PathOf("manifests/invalid"), "*.json")
                     .Select(Path.GetFileName)
                     .Where(name => !name!.EndsWith(".expected.json", StringComparison.Ordinal) && !name.EndsWith(".options.json", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            data.Add(name![..^5]);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(ValidManifests))]
    public void EveryValidManifestFixtureReadsWithoutDiagnostics(string name)
    {
        var result = ManifestReader.Read(Fixtures.Bytes("manifests/valid/" + name + ".json"), TestHosts.Options);
        Assert.Empty(result.Diagnostics);
        Assert.True(result.Succeeded);
        var validated = ManifestReader.Validate(result.Value, TestHosts.Options);
        Assert.Empty(validated.Diagnostics);
        var written = ManifestWriter.Write(result.Value);
        var reread = ManifestReader.Read(written, TestHosts.Options);
        Assert.True(reread.Succeeded);
        Assert.Equal(written, ManifestWriter.Write(reread.Value));
        Assert.Equal(ManifestWriter.ComputeHash(result.Value), ManifestWriter.ComputeHash(reread.Value));
    }

    [Theory]
    [MemberData(nameof(InvalidManifests))]
    public void EveryInvalidManifestFixtureGivesExactlyItsExpectedDiagnostics(string name)
    {
        var options = TestHosts.Options;
        var optionsPath = Fixtures.PathOf("manifests/invalid/" + name + ".options.json");
        if (File.Exists(optionsPath))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(optionsPath));
            var root = document.RootElement;
            options = new ManifestReadOptions
            {
                KnownHosts = TestHosts.Known,
                HostId = root.TryGetProperty("hostId", out var host) ? host.GetString() : null,
                SupportedCapabilities = root.TryGetProperty("supportedCapabilities", out var supported)
                    ? supported.EnumerateArray().Select(item => item.GetString()!).ToList()
                    : null,
                AllowExperimentalCapabilities = root.TryGetProperty("allowExperimentalCapabilities", out var experimental) && experimental.GetBoolean(),
            };
        }

        var result = ManifestReader.Read(Fixtures.Bytes("manifests/invalid/" + name + ".json"), options);
        Assert.NotEmpty(result.Diagnostics);
        Assert.Single(result.Diagnostics);
        Assert.All(result.Diagnostics, diagnostic => Assert.Null(diagnostic.File));
        Expected.Match("manifests/invalid/" + name + ".expected.json", result.Diagnostics);
    }

    public static TheoryData<string> BuiltInCodeCases()
    {
        var data = new TheoryData<string>();
        using var document = Fixtures.Json("manifests/built-in-code.json");
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            data.Add(item.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(BuiltInCodeCases))]
    public void ManifestsBuiltInCodeReportNullsAndUndefinedValuesWithoutPositions(string name)
    {
        using var document = Fixtures.Json("manifests/built-in-code.json");
        var item = document.RootElement.GetProperty("cases").EnumerateArray().Single(entry => entry.GetProperty("name").GetString() == name);
        var manifest = ManifestReader.Read(Fixtures.Bytes(document.RootElement.GetProperty("base").GetString()!), TestHosts.Options).Value!;
        object? model = ModelEditor.Clone(manifest);
        foreach (var set in item.GetProperty("set").EnumerateObject())
        {
            model = ModelEditor.Set(model, set.Name, set.Value);
        }

        var result = ManifestReader.Validate((ExtensionManifest)model!, TestHosts.Options);
        Assert.Equal(Expected.Pairs(item.GetProperty("expected")), Expected.Pairs(result.Diagnostics));
        Assert.All(result.Diagnostics, diagnostic =>
        {
            Assert.Null(diagnostic.Line);
            Assert.Null(diagnostic.Column);
        });
        Assert.Null(result.Value);
    }

    [Fact]
    public void TheWorkedExampleHasThePublishedManifestHashAndCanonicalForm()
    {
        var manifest = ManifestReader.Read(Fixtures.Bytes("manifests/valid/countdown.json"), TestHosts.Options).Value!;
        Assert.Equal("1f7c38cf0bc0408b9b62e5d90fdc796c479fe791e7b54fdb701c82cedccb2e8f", ManifestWriter.ComputeHash(manifest));
        Assert.Equal(
            """{"contributions":[{"id":"example.countdown/status","provides":["face"]},{"id":"example.countdown/timer","provides":["face","invoke"],"settings":[{"choices":["five-minutes","one-minute","twenty-five-minutes"],"id":"duration","kind":"Choice"},{"choices":["pause","reset","start"],"id":"mode","kind":"Choice"}]}],"id":"example.countdown","schemaVersion":3}""",
            ManifestWriter.CanonicalProjection(manifest));
    }

    [Fact]
    public void TheContractExampleWithItsHostAndSchemaReadsAndHashesTheSame()
    {
        var text = Fixtures.Utf8(Fixtures.Text("manifests/valid/countdown.json"))
            .Replace("\"schemaVersion\": 3,", "\"$schema\": \"https://dev.ventana.tools/schemas/extensions/orbit/manifest.v3.json\",\n  \"schemaVersion\": 3,", StringComparison.Ordinal)
            .Replace("\"example-host\"", "\"orbit\"", StringComparison.Ordinal);
        var result = ManifestReader.Read(Encoding.UTF8.GetBytes(text));
        Assert.Empty(result.Diagnostics);
        Assert.Equal("1f7c38cf0bc0408b9b62e5d90fdc796c479fe791e7b54fdb701c82cedccb2e8f", ManifestWriter.ComputeHash(result.Value!));
    }

    [Fact]
    public void LeafCasesFollowTheLoneLeafRule()
    {
        using var document = Fixtures.Json("ids.json");
        foreach (var item in document.RootElement.GetProperty("leafCases").EnumerateArray())
        {
            var extension = item.GetProperty("extension").GetString()!;
            var contributions = item.GetProperty("contributions").EnumerateArray().Select(entry => entry.GetString()!).ToList();
            var manifest = new ExtensionManifest
            {
                Id = extension,
                Name = "Leaf",
                Description = "Leaf cases.",
                Version = "1.0.0",
                Hosts = [TestHosts.Id],
                Contributions = contributions.Select(id => new Contribution
                {
                    Id = id,
                    Name = "Item",
                    Description = "An item.",
                    Glyph = "\uE768",
                    Provides = Provides.Invoke,
                }).ToList(),
            };
            var result = ManifestReader.Validate(manifest, TestHosts.Options);
            if (item.GetProperty("code").ValueKind == JsonValueKind.Null)
            {
                Assert.Empty(result.Diagnostics);
            }
            else
            {
                var diagnostic = Assert.Single(result.Diagnostics);
                Assert.Equal(item.GetProperty("code").GetString(), diagnostic.Code);
                Assert.Equal("/contributions/" + item.GetProperty("index").GetInt32() + "/id", diagnostic.Path);
            }
        }
    }

    [Theory]
    [InlineData("\"schemaVersion\": 3", "\"schemaVersion\": 1", DiagnosticCodes.SchemaVersionUnsupported)]
    [InlineData("\"schemaVersion\": 3", "\"schemaVersion\": 4", DiagnosticCodes.SchemaVersionUnsupported)]
    [InlineData("\"schemaVersion\": 3", "\"schemaVersion\": 3, \"schemaVersion\": 3", DiagnosticCodes.JsonDuplicateMember)]
    [InlineData("\"schemaVersion\": 3", "\"schemaVersion\": 3, \"entrypoint\": \"run.exe\"", DiagnosticCodes.JsonUnknownMember)]
    [InlineData("\"schemaVersion\": 3", "\"SchemaVersion\": 3", DiagnosticCodes.JsonUnknownMember)]
    [InlineData("\"id\": \"example.countdown\"", "\"id\": \"orbit.countdown\"", DiagnosticCodes.IdRootReserved)]
    [InlineData("\"id\": \"example.countdown\"", "\"id\": \"countdown\"", DiagnosticCodes.IdRootNotDotted)]
    [InlineData("\"id\": \"example.countdown/status\"", "\"id\": \"other.extension/status\"", DiagnosticCodes.IdOutsideNamespace)]
    [InlineData("\"id\": \"example.countdown/status\"", "\"id\": \"now-playing/play-pause\"", DiagnosticCodes.IdRootNotDotted)]
    [InlineData("\"id\": \"example.countdown/status\"", "\"id\": \"example.countdown\"", DiagnosticCodes.IdOutsideNamespace)]
    [InlineData("\"name\": \"Countdown sample\"", "\"name\": \"Countdown\\u202Esample\"", DiagnosticCodes.TextInvalidCharacter)]
    [InlineData("\"name\": \"Countdown sample\"", "\"name\": \"Countdown\\nsample\"", DiagnosticCodes.TextInvalidCharacter)]
    [InlineData("\"glyph\": \"\\uE916\",\n      \"provides\": [\"face\"]", "\"glyph\": \"not a glyph\",\n      \"provides\": [\"face\"]", DiagnosticCodes.ChromeGlyphInvalid)]
    [InlineData("\"glyph\": \"\\uE916\",\n      \"provides\": [\"face\"]", "\"glyph\": \"A\",\n      \"provides\": [\"face\"]", DiagnosticCodes.ChromeGlyphInvalid)]
    [InlineData("\"provides\": [\"face\"]", "\"source\": \"file:///x\", \"provides\": [\"face\"]", DiagnosticCodes.JsonUnknownMember)]
    [InlineData("\"provides\": [\"invoke\", \"face\"]", "\"provides\": [\"invoke\", \"invoke\"]", DiagnosticCodes.ContributionProvidesDuplicate)]
    [InlineData("\"provides\": [\"invoke\", \"face\"]", "\"provides\": [\"images\"]", DiagnosticCodes.ContributionProvidesUnknown)]
    [InlineData("\"provides\": [\"invoke\", \"face\"]", "\"provides\": []", DiagnosticCodes.ListTooShort)]
    [InlineData("\"default\": \"start\"", "\"default\": \"unknown\"", DiagnosticCodes.SettingDefaultUnknown)]
    [InlineData("{ \"value\": \"pause\", \"name\": \"Pause\" }", "{ \"value\": \"start\", \"name\": \"Pause\" }", DiagnosticCodes.ChoiceValueDuplicate)]
    public void UntrustedOrAmbiguousDeclarationsAreRefusedWithAStableCode(string before, string after, string code)
    {
        var text = Fixtures.Utf8(Fixtures.Text("manifests/valid/countdown.json"));
        Assert.Contains(before, text, StringComparison.Ordinal);
        var result = ManifestReader.Read(Encoding.UTF8.GetBytes(text.Replace(before, after, StringComparison.Ordinal)), TestHosts.Options);
        Assert.Null(result.Value);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == code);
        Assert.All(result.Diagnostics, diagnostic => Assert.DoesNotContain("run.exe", diagnostic.Message, StringComparison.Ordinal));
    }

    /// <summary>
    /// An escape that forms an unpaired surrogate is valid JSON (contract §4.4 limits
    /// <c>json.syntax</c> to bad tokens, comments, trailing commas or content, and invalid UTF-8),
    /// so it never stops collect-all: declaration text reports <c>text.invalid-character</c> (§3.6),
    /// other values their grammar code, and a member name the parent's path (§4.1).
    /// </summary>
    [Fact]
    public void UnpairedSurrogatesAreValueFaultsAndEveryOtherProblemIsStillReported()
    {
        var edits = new (string Before, string After)[]
        {
            ("\"name\": \"Countdown sample\"", "\"name\": \"Countdown \\ud800 sample\""),
            ("\"version\": \"0.3.0\"", "\"version\": \"x\""),
            ("\"publisher\": { \"name\": \"Example Co\"", "\"publisher\": { \"name\": \"Example \\uDC00Co\""),
            ("\"disclosures\": { \"network\": \"None\" }", "\"disclosures\": { \"network\": \"None\", \"\\ud83d\": true }"),
            ("{ \"value\": \"pause\", \"name\": \"Pause\" }", "{ \"value\": \"pause\", \"name\": \"Pa\\udfffuse\" }"),
            ("\"id\": \"example.countdown/status\"", "\"id\": \"example.countdown/st\\ud800tus\""),
            ("\"glyph\": \"\\uE916\",\n      \"provides\": [\"face\"]", "\"glyph\": \"\\uDFFF\",\n      \"provides\": [\"face\"]"),
        };
        var text = Fixtures.Utf8(Fixtures.Text("manifests/valid/countdown.json"));
        foreach (var (before, after) in edits)
        {
            Assert.Contains(before, text, StringComparison.Ordinal);
            text = text.Replace(before, after, StringComparison.Ordinal);
        }

        var result = ManifestReader.Read(Encoding.UTF8.GetBytes(text), TestHosts.Options);
        Assert.Null(result.Value);
        (string Code, string Path)[] expected =
        [
            (DiagnosticCodes.TextInvalidCharacter, "/name"),
            (DiagnosticCodes.ManifestVersionInvalid, "/version"),
            (DiagnosticCodes.TextInvalidCharacter, "/publisher/name"),
            (DiagnosticCodes.JsonUnknownMember, "/disclosures"),
            (DiagnosticCodes.TextInvalidCharacter, "/contributions/0/settings/0/choices/1/name"),
            (DiagnosticCodes.IdGrammar, "/contributions/1/id"),
            (DiagnosticCodes.ChromeGlyphInvalid, "/contributions/1/glyph"),
        ];
        Assert.Equal(expected.Order(), Expected.Pairs(result.Diagnostics).Order());
    }

    [Fact]
    public void UnpairedSurrogatesInMemberNamesNeverReachAPath()
    {
        var result = ManifestReader.Read(Encoding.UTF8.GetBytes(
            "{\"schemaVersion\": 3, \"\\udc00\": 1, \"\\udc00\": 2, \"publisher\": {\"\\ud800x\": [tru]}}"));
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal((DiagnosticCodes.JsonSyntax, "/publisher"), (diagnostic.Code, diagnostic.Path));

        result = ManifestReader.Read(Encoding.UTF8.GetBytes(
            "{\"schemaVersion\": 3, \"\\udc00\": 1, \"\\udc00\": 2, \"publisher\": {\"\\ud800x\": [[[[[[[[1]]]]]]]]}}"));
        diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal((DiagnosticCodes.JsonDepth, "/publisher"), (diagnostic.Code, diagnostic.Path));

        result = ManifestReader.Read(Encoding.UTF8.GetBytes("{\"schemaVersion\": 3, \"\\udc00\": 1, \"\\udc00\": 2}"));
        Assert.Contains(result.Diagnostics, item => item is { Code: DiagnosticCodes.JsonDuplicateMember, Path: "" });
        Assert.All(result.Diagnostics, item => Assert.True(JsonPointer.IsPrintableAscii(item.Path), item.Code));
    }

    [Fact]
    public void ASchemaMemberHasNoCharacterRule()
    {
        var text = Fixtures.Utf8(Fixtures.Text("manifests/valid/countdown.json"))
            .Replace("\"schemaVersion\": 3,", "\"$schema\": \"./manifest\\ud800.v3.json\",\n  \"schemaVersion\": 3,", StringComparison.Ordinal);
        var result = ManifestReader.Read(Encoding.UTF8.GetBytes(text), TestHosts.Options);
        Assert.Empty(result.Diagnostics);
        Assert.Equal("./manifest\ud800.v3.json", result.Value!.Schema);
    }

    [Fact]
    public void ContributionCatalogsAreBoundedAndIdsUnique()
    {
        var manifest = Countdown();
        var action = manifest.Contributions[1];
        Assert.Equal(DiagnosticCodes.ListTooShort, Assert.Single(ManifestReader.Validate(With(manifest, contributions: []), TestHosts.Options).Diagnostics).Code);
        var many = Enumerable.Range(0, 33).Select(i => new Contribution
        {
            Id = "example.countdown/c" + i,
            Name = action.Name,
            Description = action.Description,
            Glyph = action.Glyph,
            Provides = action.Provides,
        }).ToList();
        Assert.Equal(DiagnosticCodes.ListTooLong, Assert.Single(ManifestReader.Validate(With(manifest, contributions: many), TestHosts.Options).Diagnostics).Code);
        Assert.Equal(DiagnosticCodes.JsonTooLarge, Assert.Single(ManifestReader.Read(new byte[ManifestReader.MaxBytes + 1]).Diagnostics).Code);
        Assert.Equal(DiagnosticCodes.JsonSyntax, Assert.Single(ManifestReader.Read([]).Diagnostics).Code);
    }

    [Fact]
    public void ReadModelsAreDetachedAndFrozen()
    {
        var bytes = Fixtures.Bytes("manifests/valid/countdown.json");
        var manifest = ManifestReader.Read(bytes, TestHosts.Options).Value!;
        Array.Clear(bytes);
        Assert.Equal("example.countdown", manifest.Id);
        Assert.Throws<NotSupportedException>(() => ((IList<Contribution>)manifest.Contributions).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<Setting>)manifest.Contributions[0].Settings).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<SettingChoice>)manifest.Contributions[0].Settings[0].Choices).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)manifest.Hosts).Clear());

        var hosts = new List<string> { TestHosts.Id };
        var validated = ManifestReader.Validate(With(manifest, hosts: hosts), TestHosts.Options).Value!;
        hosts.Clear();
        Assert.Single(validated.Hosts);
    }

    [Fact]
    public void TheModelMirrorsTheJson()
    {
        var manifest = Countdown();
        Assert.Equal(3, manifest.SchemaVersion);
        Assert.Equal("0.3.0", manifest.Version);
        Assert.Equal([TestHosts.Id], manifest.Hosts);
        Assert.Equal(NetworkUse.None, manifest.Disclosures!.Network);
        Assert.Equal("Example Co", manifest.Publisher!.Name);
        var timer = manifest.Contributions[0];
        Assert.Equal(Provides.Invoke | Provides.Face, timer.Provides);
        Assert.Equal("\uE916", timer.Glyph);
        Assert.Equal(SettingKind.Choice, timer.Settings[0].Kind);
        Assert.Equal("start", timer.Settings[0].Default);
        Assert.Equal(Provides.Face, manifest.Contributions[1].Provides);
        Assert.Empty(manifest.Contributions[1].Settings);
    }

    [Fact]
    public void ManifestsBuiltInCodeWithDefaultsValidate()
    {
        var manifest = new ExtensionManifest
        {
            Id = "example.code",
            Name = "Code",
            Description = "Built in code.",
            Version = "1.0.0",
            Hosts = [TestHosts.Id],
            Contributions =
            [
                new Contribution
                {
                    Id = "example.code/run",
                    Name = "Run",
                    Description = "Runs.",
                    Glyph = "\uE768",
                    Provides = Provides.Invoke,
                    Settings = [new Setting { Id = "mode", Name = "Mode", Default = "a", Choices = [new SettingChoice { Value = "a", Name = "A" }, new SettingChoice { Value = "b", Name = "B" }] }],
                },
            ],
        };
        var result = ManifestReader.Validate(manifest, TestHosts.Options);
        Assert.True(result.Succeeded);
        Assert.Empty(result.Diagnostics);
        var json = Encoding.UTF8.GetString(ManifestWriter.Write(manifest));
        Assert.Contains("\"glyph\": \"\\uE768\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', json);
        Assert.EndsWith("}\n", json, StringComparison.Ordinal);
        Assert.StartsWith("{\n  \"schemaVersion\": 3,\n  \"id\": \"example.code\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"settings\": []", Encoding.UTF8.GetString(ManifestWriter.Write(Countdown())), StringComparison.Ordinal);
    }

    [Fact]
    public void TheWriterRefusesIncompleteOrUndefinedModels()
    {
        var manifest = Countdown();
        Assert.Throws<ArgumentException>(() => ManifestWriter.Write(With(manifest, name: null)));
        Assert.Throws<ArgumentException>(() => ManifestWriter.Write(new ExtensionManifest
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Description = manifest.Description,
            Version = manifest.Version,
            Hosts = manifest.Hosts,
            Disclosures = new Disclosures { Network = (NetworkUse)9 },
            Contributions = manifest.Contributions,
        }));
        Assert.Throws<ArgumentNullException>(() => ManifestWriter.Write(null!));
        Assert.Throws<ArgumentNullException>(() => ManifestWriter.ComputeHash(null!));
    }

    [Fact]
    public void UnknownHostsWarnAndTheReadingHostMustBeListed()
    {
        var bytes = Fixtures.Bytes("manifests/valid/countdown.json");
        var withRegistry = ManifestReader.Read(bytes);
        Assert.True(withRegistry.Succeeded);
        var warning = Assert.Single(withRegistry.Diagnostics);
        Assert.Equal(DiagnosticCodes.ManifestHostUnknown, warning.Code);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Equal("/hosts/0", warning.Path);

        var asHost = ManifestReader.Read(bytes, new ManifestReadOptions { KnownHosts = TestHosts.Known, HostId = TestHosts.Id });
        Assert.Empty(asHost.Diagnostics);
        var aliased = new HostInfo { Id = "renamed-host", DisplayName = "Renamed", Status = HostStatus.Active, Aliases = [TestHosts.Id] };
        var asRenamedHost = ManifestReader.Read(bytes, new ManifestReadOptions { KnownHosts = [aliased], HostId = "renamed-host" });
        Assert.Empty(asRenamedHost.Diagnostics);
        var other = ManifestReader.Read(bytes, new ManifestReadOptions { KnownHosts = TestHosts.Known, HostId = "other-host" });
        Assert.Equal(DiagnosticCodes.ManifestHostNotListed, Assert.Single(other.Diagnostics).Code);
    }

    [Fact]
    public void ExperimentalCapabilitiesNeedDeveloperModeAndAHostThatImplementsThem()
    {
        var manifest = With(Countdown(), requires: new ManifestRequirements { Capabilities = ["x.test-echo", "face.image"] });
        Assert.Empty(ManifestReader.Validate(manifest, TestHosts.Options).Diagnostics);
        var host = new ManifestReadOptions { KnownHosts = TestHosts.Known, SupportedCapabilities = ["x.test-echo", "face.image"] };
        var refused = ManifestReader.Validate(manifest, host);
        Assert.Equal(("requires.capability-unsupported", "/requires/capabilities/0"), Assert.Single(Expected.Pairs(refused.Diagnostics)));
        var developer = new ManifestReadOptions { KnownHosts = TestHosts.Known, SupportedCapabilities = ["x.test-echo", "face.image"], AllowExperimentalCapabilities = true };
        Assert.Empty(ManifestReader.Validate(manifest, developer).Diagnostics);
        var missing = new ManifestReadOptions { KnownHosts = TestHosts.Known, SupportedCapabilities = [] };
        Assert.Equal(2, ManifestReader.Validate(manifest, missing).Diagnostics.Count);
    }

    [Fact]
    public void FirstPartyManifestsUseUndottedIds()
    {
        var manifest = new ExtensionManifest
        {
            Id = "clock",
            Name = "Clock",
            Description = "A host's own widget.",
            Version = "1.0.0",
            Hosts = [TestHosts.Id],
            Contributions = [new Contribution { Id = "clock/time", Name = "Time", Description = "The time.", Glyph = "\uE121", Provides = Provides.Face }],
        };
        Assert.Empty(ManifestReader.Validate(manifest, new ManifestReadOptions { KnownHosts = TestHosts.Known, Origin = IdOrigin.FirstParty }).Diagnostics);
        Assert.Equal(DiagnosticCodes.IdRootNotDotted, ManifestReader.Validate(manifest, TestHosts.Options).Diagnostics[0].Code);
    }

    [Fact]
    public void AByteOrderMarkIsIgnoredAndWideOnesAreRefused()
    {
        var bytes = Fixtures.Bytes("manifests/valid/countdown.json");
        Assert.True(ManifestReader.Read([0xEF, 0xBB, 0xBF, .. bytes], TestHosts.Options).Succeeded);
        Assert.Equal(DiagnosticCodes.JsonEncoding, Assert.Single(ManifestReader.Read([0xFE, 0xFF, .. bytes]).Diagnostics).Code);
        Assert.Equal(DiagnosticCodes.JsonEncoding, Assert.Single(ManifestReader.Read([0x00, 0x00, 0xFE, 0xFF, .. bytes]).Diagnostics).Code);
    }

    [Fact]
    public void ReportingStopsAt200Diagnostics()
    {
        var builder = new StringBuilder("{");
        for (var i = 0; i < 300; i++)
        {
            builder.Append("\"unknown").Append(i).Append("\": 1,");
        }

        builder.Append("\"schemaVersion\": 3}");
        var result = ManifestReader.Read(Encoding.UTF8.GetBytes(builder.ToString()));
        Assert.Equal(200, result.Diagnostics.Count);
        Assert.Equal(DiagnosticCodes.DiagnosticsTruncated, result.Diagnostics[^1].Code);
        Assert.Equal(DiagnosticCodes.JsonUnknownMember, result.Diagnostics[198].Code);
    }

    [Fact]
    public void MessagesNeverEchoFileValuesAndDiagnosticsFormatAsLollipopDoes()
    {
        var result = ManifestReader.Read(Encoding.UTF8.GetBytes("{\"schemaVersion\":3,\"secretValue123\":\"p@ss\"}"));
        Assert.All(result.Diagnostics, diagnostic =>
        {
            Assert.DoesNotContain("p@ss", diagnostic.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("secretValue123", diagnostic.Message, StringComparison.Ordinal);
        });
        var unknown = result.Diagnostics.Single(diagnostic => diagnostic.Code == DiagnosticCodes.JsonUnknownMember);
        Assert.Equal("json.unknown-member /secretValue123: " + unknown.Message, unknown.ToString());
        Assert.Equal(1, unknown.Line);
        Assert.Equal(37, unknown.Column);
    }

    [Fact]
    public async Task ReadFileAsyncNeverReadsPastTheLimitAndSurfacesIoFailures()
    {
        var path = Path.Combine(Path.GetTempPath(), "ventana-s2-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            await File.WriteAllBytesAsync(path, new byte[10 * 1024 * 1024]);
            var result = await ManifestReader.ReadFileAsync(path);
            Assert.Equal(DiagnosticCodes.JsonTooLarge, Assert.Single(result.Diagnostics).Code);
            await File.WriteAllBytesAsync(path, Fixtures.Bytes("manifests/valid/countdown.json"));
            Assert.True((await ManifestReader.ReadFileAsync(path, TestHosts.Options)).Succeeded);
        }
        finally
        {
            File.Delete(path);
        }

        await Assert.ThrowsAsync<FileNotFoundException>(() => ManifestReader.ReadFileAsync(path));
    }

    internal static ExtensionManifest Countdown() =>
        ManifestReader.Read(Fixtures.Bytes("manifests/valid/countdown.json"), TestHosts.Options).Value!;

    internal static ExtensionManifest With(ExtensionManifest manifest, IReadOnlyList<Contribution>? contributions = null,
        IReadOnlyList<string>? hosts = null, string? name = "\0", ManifestRequirements? requires = null) => new()
    {
        Schema = manifest.Schema,
        Id = manifest.Id,
        Name = name == "\0" ? manifest.Name : name!,
        Description = manifest.Description,
        Version = manifest.Version,
        Hosts = hosts ?? manifest.Hosts,
        Glyph = manifest.Glyph,
        Publisher = manifest.Publisher,
        SupportUrl = manifest.SupportUrl,
        DefaultLanguage = manifest.DefaultLanguage,
        Disclosures = manifest.Disclosures,
        Requires = requires ?? manifest.Requires,
        Contributions = contributions ?? manifest.Contributions,
    };

    private static TheoryData<string> Names(string folder)
    {
        var data = new TheoryData<string>();
        foreach (var name in Directory.EnumerateFiles(Fixtures.PathOf(folder), "*.json").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal))
        {
            data.Add(name!);
        }

        return data;
    }
}

/// <summary>Deep-copies manifest models into mutable form and sets members by JSON Pointer, for the built-in-code fixture.</summary>
internal static class ModelEditor
{
    public static object? Clone(object? value)
    {
        if (value is null or string || value.GetType().IsValueType)
        {
            return value;
        }

        if (value is IList list)
        {
            var type = list.GetType().GetInterfaces().First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IReadOnlyList<>)).GetGenericArguments()[0];
            var copy = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(type))!;
            foreach (var item in list)
            {
                copy.Add(Clone(item));
            }

            return copy;
        }

        var clone = RuntimeHelpers(value.GetType());
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
        {
            property.SetValue(clone, Clone(property.GetValue(value)));
        }

        return clone;
    }

    public static object? Set(object? root, string pointer, JsonElement value)
    {
        if (pointer.Length == 0)
        {
            return value.ValueKind == JsonValueKind.Null ? null : throw new InvalidOperationException("Only null may replace the root.");
        }

        var tokens = pointer.Split('/')[1..];
        var target = root!;
        foreach (var token in tokens[..^1])
        {
            target = Get(target, token)!;
        }

        var last = tokens[^1];
        if (target is IList list)
        {
            list[int.Parse(last, System.Globalization.CultureInfo.InvariantCulture)] = null;
            return root;
        }

        var property = Property(target, last);
        object? converted = value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.Number => property.PropertyType.IsEnum ? Enum.ToObject(property.PropertyType, value.GetInt32()) : value.GetInt32(),
            JsonValueKind.Object => Enum.ToObject(property.PropertyType, value.GetProperty("$enum").GetInt32()),
            _ => throw new InvalidOperationException("Unsupported value."),
        };
        property.SetValue(target, converted);
        return root;
    }

    private static object? Get(object target, string token) =>
        target is IList list ? list[int.Parse(token, System.Globalization.CultureInfo.InvariantCulture)] : Property(target, token).GetValue(target);

    private static PropertyInfo Property(object target, string token)
    {
        var name = token == "$schema" ? "Schema" : char.ToUpperInvariant(token[0]) + token[1..];
        return target.GetType().GetProperty(name) ?? throw new InvalidOperationException("No member " + token);
    }

    private static object RuntimeHelpers(Type type) => System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
}
