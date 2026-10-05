// SPDX-License-Identifier: Apache-2.0
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Orbit.Extensions.Protocol.Tests;

/// <summary>Golden ID cases are shared with the product repositories independently of their host implementations.</summary>
public sealed class ExtensionIdGrammarTests
{
    private const string FixtureSha256 = "ab61d18d490431d53f02210141cc7032caa9104e16e49627888ef8e3555ad6f1";

    private static byte[] FixtureBytes()
    {
        using var stream = typeof(ExtensionIdGrammarTests).Assembly.GetManifestResourceStream(
            "Orbit.Extensions.Protocol.Tests.Fixtures.ids.json")
            ?? throw new InvalidDataException("The shared ID fixture is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    public static IEnumerable<object?[]> GoldenCases()
    {
        using var document = JsonDocument.Parse(FixtureBytes());
        foreach (var entry in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            var origin = entry.GetProperty("origin").GetString() switch
            {
                "first-party" => ExtensionOrigin.FirstParty,
                "third-party" => ExtensionOrigin.ThirdParty,
                _ => throw new InvalidDataException("The shared ID fixture contains an unknown origin."),
            };
            yield return [entry.GetProperty("id").GetString(), origin, entry.GetProperty("code").GetString()];
        }
    }

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public void SharedGoldenGrammarKeepsItsDeclaredDiagnostic(string? id, ExtensionOrigin origin, string? expectedCode)
    {
        Assert.Equal(expectedCode, ExtensionIds.Classify(id, origin));
        Assert.Equal(expectedCode is null, ExtensionIds.IsValid(id, origin));
        if (expectedCode is null) Assert.Equal(origin, ExtensionIds.OriginOf(id));
    }

    [Fact]
    public void FixtureMatchesThePinnedCrossProductContract()
    {
        var bytes = FixtureBytes();
        Assert.False(bytes.AsSpan().StartsWith(Encoding.UTF8.Preamble));
        Assert.All(bytes, value => Assert.True(value < 0x80));
        var normalized = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.Equal(FixtureSha256, Convert.ToHexStringLower(SHA256.HashData(normalized)));
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(ExtensionIds.MaxLength, document.RootElement.GetProperty("maxLength").GetInt32());
        var cases = GoldenCases().ToArray();
        Assert.Equal(55, cases.Length);
        Assert.Contains(cases, entry => entry[0] is string id && id.Length == ExtensionIds.MaxLength && entry[2] is null);
        Assert.Contains(cases, entry => entry[0] is string id && id.Length == ExtensionIds.MaxLength + 1 && (string?)entry[2] == ExtensionIds.TooLong);
        Assert.DoesNotContain(ExtensionIds.RootReserved, Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void DefaultPublisherPolicyIsExplicitImmutableAndSeparateFromTheSharedGrammar()
    {
        Assert.Equal(new[] { "orbit", "lollipop", "ventana", "ext" }, ExtensionIds.ReservedPublishers);
        Assert.Throws<NotSupportedException>(() => ((IList<string>)ExtensionIds.ReservedPublishers).Clear());
        foreach (var publisher in ExtensionIds.ReservedPublishers)
        {
            Assert.Equal(ExtensionIds.RootReserved, ExtensionIds.Classify($"{publisher}.tools/run", ExtensionOrigin.ThirdParty));
            Assert.Null(ExtensionIds.Classify($"{publisher}.tools/run", ExtensionOrigin.ThirdParty, Array.Empty<string>()));
            Assert.True(ExtensionIds.IsValid($"{publisher}s.tools/run", ExtensionOrigin.ThirdParty));
            Assert.True(ExtensionIds.IsValid($"contoso.{publisher}/run", ExtensionOrigin.ThirdParty));
        }
        Assert.Equal(ExtensionIds.RootReserved, ExtensionIds.Classify("contoso.tools/run", ExtensionOrigin.ThirdParty, new[] { "contoso" }));
        Assert.Null(ExtensionIds.Classify("example.tools/run", ExtensionOrigin.ThirdParty, new[] { "contoso" }));
        Assert.Equal(ExtensionIds.Grammar, ExtensionIds.Classify("contoso.Tools/run", ExtensionOrigin.ThirdParty, Array.Empty<string>()));
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
}
