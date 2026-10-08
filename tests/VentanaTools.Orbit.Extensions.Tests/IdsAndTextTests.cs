// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Text;
using System.Text.Json;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tests;

/// <summary>The id grammar, the reserved lists, the text rules and the host registry against their fixtures.</summary>
public sealed class IdsAndTextTests
{
    public static IEnumerable<object?[]> IdCases()
    {
        using var document = Fixtures.Json("ids.json");
        foreach (var entry in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var origin = entry.GetProperty("origin").GetString() switch
            {
                "first-party" => IdOrigin.FirstParty,
                "third-party" => IdOrigin.ThirdParty,
                _ => throw new InvalidDataException("The id fixture contains an unknown origin."),
            };
            yield return [entry.GetProperty("id").GetString(), origin, entry.GetProperty("code").GetString()];
        }
    }

    [Theory]
    [MemberData(nameof(IdCases))]
    public void EveryIdCaseKeepsItsDeclaredCode(string? id, IdOrigin origin, string? expectedCode)
    {
        Assert.Equal(expectedCode, ExtensionIds.Classify(id, origin));
        Assert.Equal(expectedCode is null, ExtensionIds.IsValid(id, origin));
        if (expectedCode is null)
        {
            Assert.Equal(origin, ExtensionIds.OriginOf(id));
        }

        if (expectedCode == DiagnosticCodes.IdRootReserved && id!.Split('.', '/')[0] is "con" or "nul" or "aux" or "prn" or "lpt1" or "lpt9" or "com1" or "com9")
        {
            Assert.Equal(DiagnosticCodes.IdRootReserved, ExtensionIds.Classify(id, origin, []));
        }
    }

    [Fact]
    public void TheIdFixtureCoversTheLimitsAndNamesNoProduct()
    {
        using var document = Fixtures.Json("ids.json");
        Assert.Equal(ExtensionIds.MaxLength, document.RootElement.GetProperty("maxLength").GetInt32());
        var cases = IdCases().ToArray();
        Assert.Contains(cases, entry => entry[0] is string id && id.Length == ExtensionIds.MaxLength && entry[2] is null);
        Assert.Contains(cases, entry => entry[0] is string id && id.Length == ExtensionIds.MaxLength + 1 && (string?)entry[2] == DiagnosticCodes.IdTooLong);
        var text = Fixtures.Utf8(Fixtures.Text("ids.json"));
        foreach (var product in ProductNames.All())
        {
            Assert.DoesNotContain(product, text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void TheReservedPublishersComeFromTheFixtureAndAreFrozen()
    {
        using var document = Fixtures.Json("reserved-publishers.json");
        var listed = document.RootElement.GetProperty("reservedPublishers").EnumerateArray().Select(item => item.GetString()!).ToList();
        Assert.Equal(listed, ExtensionIds.ReservedPublishers);
        Assert.Equal(["ventana", "ventanatools", "ext"], listed.Skip(3));
        Assert.Throws<NotSupportedException>(() => ((IList<string>)ExtensionIds.ReservedPublishers).Clear());
        foreach (var publisher in ExtensionIds.ReservedPublishers)
        {
            Assert.Equal(DiagnosticCodes.IdRootReserved, ExtensionIds.Classify($"{publisher}.tools/run", IdOrigin.ThirdParty));
            Assert.Equal(DiagnosticCodes.IdRootReserved, ExtensionIds.Classify($"{publisher}.tools", IdOrigin.ThirdParty));
            Assert.Null(ExtensionIds.Classify($"{publisher}.tools/run", IdOrigin.ThirdParty, []));
            Assert.True(ExtensionIds.IsValid($"{publisher}s.tools/run", IdOrigin.ThirdParty));
            Assert.True(ExtensionIds.IsValid($"{publisher}-fan.tools", IdOrigin.ThirdParty));
            Assert.True(ExtensionIds.IsValid($"contoso.{publisher}/run", IdOrigin.ThirdParty));
            Assert.True(ExtensionIds.IsValid(publisher, IdOrigin.FirstParty));
            var manifest = ManifestReaderTests.Countdown();
            var renamed = ManifestReader.Validate(new ExtensionManifest
            {
                Id = $"{publisher}.status",
                Name = manifest.Name,
                Description = manifest.Description,
                Version = manifest.Version,
                Hosts = manifest.Hosts,
                Contributions = [new Contribution { Id = $"{publisher}.status/run", Name = "Run", Description = "Runs.", Glyph = "\uE768", Provides = Provides.Invoke }],
            }, TestHosts.Options);
            Assert.Contains(renamed.Diagnostics, diagnostic => diagnostic.Code == DiagnosticCodes.IdRootReserved && diagnostic.Path == "/id");
        }

        Assert.Equal(DiagnosticCodes.IdRootReserved, ExtensionIds.Classify("contoso.tools/run", IdOrigin.ThirdParty, ["contoso"]));
        Assert.Null(ExtensionIds.Classify("example.tools/run", IdOrigin.ThirdParty, ["contoso"]));
        Assert.Equal(DiagnosticCodes.IdGrammar, ExtensionIds.Classify("contoso.Tools/run", IdOrigin.ThirdParty, []));
    }

    [Theory]
    [InlineData("now-playing/play-pause", "now-playing", true)]
    [InlineData("now-playing", "now-playing", true)]
    [InlineData("now-playing-x/play-pause", "now-playing", false)]
    [InlineData("now-playing/", "now-playing", false)]
    [InlineData("contoso.status/build", "contoso.status", true)]
    [InlineData("contoso.statusbar/build", "contoso.status", false)]
    [InlineData("contoso.status.extra/build", "contoso.status", false)]
    [InlineData(null, "clock", false)]
    [InlineData("clock", null, false)]
    public void NamespaceMatchingRequiresACompleteRootBoundary(string? id, string? root, bool expected) =>
        Assert.Equal(expected, ExtensionIds.IsInNamespace(id, root));

    [Fact]
    public void SegmentsAreLowercaseWithSingleInnerHyphens()
    {
        Assert.True(ExtensionIds.IsSegment("a-b-1"));
        Assert.False(ExtensionIds.IsSegment(""));
        Assert.False(ExtensionIds.IsSegment("-a"));
        Assert.False(ExtensionIds.IsSegment("a-"));
        Assert.False(ExtensionIds.IsSegment("a--b"));
        Assert.False(ExtensionIds.IsSegment("A"));
        Assert.Equal(IdOrigin.FirstParty, ExtensionIds.OriginOf(null));
        Assert.Equal(IdOrigin.ThirdParty, ExtensionIds.OriginOf("a.b/c.d"));
        Assert.Equal(IdOrigin.FirstParty, ExtensionIds.OriginOf("a/b.c"));
    }

    public static IEnumerable<object?[]> DeclarationCases() => Rows("declaration", item =>
        [LooseJson.String(item.GetProperty("text")), item.GetProperty("maxUnits").GetInt32(), item.GetProperty("code").GetString()]);

    [Theory]
    [MemberData(nameof(DeclarationCases))]
    public void DeclarationTextFollowsTheRule(string text, int maxUnits, string? code) =>
        Assert.Equal(code, TextRules.CheckDeclarationText(text, maxUnits));

    public static IEnumerable<object?[]> CleaningCases() => Rows("cleaning", item =>
        [LooseJson.String(item.GetProperty("text")), item.GetProperty("maxElements").GetInt32(), item.GetProperty("maxUnits").GetInt32(), LooseJson.String(item.GetProperty("expected"))]);

    [Theory]
    [MemberData(nameof(CleaningCases))]
    public void FaceTextIsCleanedAndCutAtTextElements(string text, int maxElements, int maxUnits, string? expected) =>
        Assert.Equal(expected, TextRules.Clean(text, maxElements, maxUnits));

    public static IEnumerable<object?[]> FaceCases() => Rows("face", item =>
        [item.GetProperty("part").GetString(), LooseJson.String(item.GetProperty("text")), LooseJson.String(item.GetProperty("expected"))]);

    [Theory]
    [MemberData(nameof(FaceCases))]
    public void SetFacePartsAreCleanedWithTheirDisplayLimits(string part, string text, string expected)
    {
        using var document = Fixtures.Json("text-rules.json");
        var limits = document.RootElement.GetProperty("faceLimits").GetProperty(part);
        var cleaned = TextRules.Clean(text, limits.GetProperty("elements").GetInt32(), limits.GetProperty("units").GetInt32());
        Assert.Equal(expected, cleaned);
        Assert.True(cleaned!.Length <= limits.GetProperty("units").GetInt32());
    }

    public static IEnumerable<object?[]> GrammarCases() => Rows("grammars", item =>
        [item.GetProperty("kind").GetString(), LooseJson.String(item.GetProperty("value")), item.GetProperty("valid").GetBoolean()]);

    [Theory]
    [MemberData(nameof(GrammarCases))]
    public void SharedGrammarsAcceptAndRefuseTheirCases(string kind, string value, bool valid)
    {
        var actual = kind switch
        {
            "settingId" => TextRules.IsSettingId(value),
            "choiceValue" => TextRules.IsChoiceValue(value),
            "hostId" => TextRules.IsHostId(value),
            "languageTag" => TextRules.IsLanguageTag(value),
            "httpsUrl" => TextRules.IsHttpsUrl(value),
            "glyph" => TextRules.IsGlyph(value),
            _ => throw new InvalidDataException("Unknown grammar."),
        };
        Assert.Equal(valid, actual);
    }

    [Fact]
    public void TheDisallowedClassMatchesTheContractTable()
    {
        foreach (var codePoint in new[] { 0x00, 0x09, 0x1F, 0x7F, 0x9F, 0x2028, 0x2029, 0x202A, 0x202E, 0x2066, 0x2069, 0xFEFF, 0xFFFD, 0xFDD0, 0xFDEF, 0xFFFE, 0xFFFF, 0x1FFFE, 0x10FFFF, 0xD800, 0xDFFF, 0x200B, 0x2060, 0x00AD, 0xE0001, 0xE0041, 0xE007F, -1, 0x110000 })
        {
            Assert.True(TextRules.IsDisallowed(codePoint), codePoint.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach (var codePoint in new[] { 0x20, 0x41, 0xA0, 0x200C, 0x200D, 0x200E, 0x200F, 0x061C, 0xE916, 0x1F600, 0x10FFFD })
        {
            Assert.False(TextRules.IsDisallowed(codePoint), codePoint.ToString("X", System.Globalization.CultureInfo.InvariantCulture));
        }

        Assert.Null(TextRules.Clean(null, 10, 10));
        Assert.Null(TextRules.Clean("abc", 0, 10));
        Assert.Equal(DiagnosticCodes.TextEmpty, TextRules.CheckDeclarationText(null, 10));
    }

    [Fact]
    public void TheHostRegistryIsReadFromTheFixture()
    {
        using var document = Fixtures.Json("hosts.json");
        var hosts = document.RootElement.GetProperty("hosts").EnumerateArray().ToList();
        Assert.Equal(hosts.Count, HostRegistry.Known.Count);
        for (var i = 0; i < hosts.Count; i++)
        {
            var info = HostRegistry.Known[i];
            Assert.Equal(hosts[i].GetProperty("id").GetString(), info.Id);
            Assert.Equal(hosts[i].GetProperty("displayName").GetString(), info.DisplayName);
            Assert.Equal(hosts[i].TryGetProperty("packageExtension", out var extension) ? extension.GetString() : null, info.PackageExtension);
            Assert.Equal(hosts[i].GetProperty("status").GetString(), info.Status.ToString());
            Assert.Equal(hosts[i].GetProperty("aliases").EnumerateArray().Select(item => item.GetString()), info.Aliases);
            Assert.True(TextRules.IsHostId(info.Id));
            Assert.Same(info, HostRegistry.Find(info.Id));
            Assert.Contains(info.Id, HostRegistry.ReservedIds);
        }

        Assert.Equal(document.RootElement.GetProperty("reservedIds").EnumerateArray().Select(item => item.GetString()), HostRegistry.ReservedIds);
        Assert.Equal(1, HostRegistry.Known.Count(host => host.Status == HostStatus.Active));
        Assert.Null(HostRegistry.Find(TestHosts.Id));
        Assert.All(HostRegistry.ReservedIds, id => Assert.Contains(id, ExtensionIds.ReservedPublishers));
        var active = HostRegistry.Known.Single(host => host.Status == HostStatus.Active);
        Assert.Same(active, HostRegistry.FirstActive([TestHosts.Id, active.Id]));
        Assert.Null(HostRegistry.FirstActive([TestHosts.Id]));
        Assert.Same(TestHosts.Example, HostRegistry.FirstActive(["missing", TestHosts.Id], TestHosts.Known));
        var renamed = new HostInfo { Id = "new-host", DisplayName = "New", Status = HostStatus.Active, Aliases = ["old-host"] };
        Assert.Same(renamed, HostRegistry.FirstActive(["old-host"], [renamed]));
        Assert.Throws<ArgumentNullException>(() => HostRegistry.FirstActive(null!));
    }

    private static List<object?[]> Rows(string section, Func<JsonElement, object?[]> row)
    {
        using var document = Fixtures.Json("text-rules.json");
        return document.RootElement.GetProperty(section).EnumerateArray().Select(row).ToList();
    }
}

/// <summary>The product names, read from fixtures/hosts.json (contract §2.8).</summary>
internal static class ProductNames
{
    public static IReadOnlyList<string> All()
    {
        using var document = Fixtures.Json("hosts.json");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var host in document.RootElement.GetProperty("hosts").EnumerateArray())
        {
            names.Add(host.GetProperty("id").GetString()!);
            names.Add(host.GetProperty("displayName").GetString()!);
            if (host.TryGetProperty("packageExtension", out var extension))
            {
                names.Add(extension.GetString()!.TrimStart('.'));
            }
        }

        foreach (var id in document.RootElement.GetProperty("reservedIds").EnumerateArray())
        {
            names.Add(id.GetString()!);
        }

        return names.ToList();
    }

    public static bool Contains(string text) => All().Any(name => text.Contains(name, StringComparison.OrdinalIgnoreCase));

    public static string? Find(string text) => All().FirstOrDefault(name => text.Contains(name, StringComparison.OrdinalIgnoreCase));
}
