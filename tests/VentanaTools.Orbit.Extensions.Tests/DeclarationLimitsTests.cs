// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Text;
using System.Text.Json;
using JsonArray = System.Text.Json.Nodes.JsonArray;
using JsonObject = System.Text.Json.Nodes.JsonObject;
using Node = System.Text.Json.Nodes.JsonNode;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tests;

/// <summary>
/// The declaration text limits of <c>fixtures/text-rules.json</c> (<c>declarationLimits</c>), a
/// Ventana convention (contract §2.7, §3.6): both readers hold every declaration string to its
/// limit, and warn about a long name.
/// </summary>
public sealed class DeclarationLimitsTests
{
    private const string Timer = "example.countdown/timer";

    /// <summary>Each declaration string of the Countdown manifest fixture, by JSON Pointer, with its kind of limit.</summary>
    public static TheoryData<string, string> ManifestMembers() => new()
    {
        { "/name", "name" },
        { "/description", "description" },
        { "/publisher/name", "label" },
        { "/contributions/0/name", "name" },
        { "/contributions/0/description", "description" },
        { "/contributions/0/settings/1/name", "label" },
        { "/contributions/0/settings/1/description", "description" },
        { "/contributions/0/settings/0/choices/0/name", "label" },
    };

    /// <summary>Each declaration string of a strings file for the Countdown manifest, with its kind of limit.</summary>
    public static TheoryData<string, string> StringsMembers() => new()
    {
        { "/name", "name" },
        { "/description", "description" },
        { "/publisher/name", "label" },
        { "/contributions/" + Pointer(Timer) + "/name", "name" },
        { "/contributions/" + Pointer(Timer) + "/description", "description" },
        { "/contributions/" + Pointer(Timer) + "/settings/duration/name", "label" },
        { "/contributions/" + Pointer(Timer) + "/settings/duration/description", "description" },
        { "/contributions/" + Pointer(Timer) + "/settings/mode/choices/start", "label" },
    };

    [Fact]
    public void TheFixtureStatesTheLimitsOfContractSection3()
    {
        Assert.Equal(80, Limit("name"));
        Assert.Equal(512, Limit("description"));
        Assert.Equal(80, Limit("label"));
        Assert.Equal(32, Limit("longNameElements"));
    }

    [Theory]
    [MemberData(nameof(ManifestMembers))]
    public void TheManifestReaderHoldsEachDeclarationStringToItsLimit(string path, string kind)
    {
        Assert.Empty(Errors(ReadManifest(path, new string('a', Limit(kind)))));
        var error = Assert.Single(Errors(ReadManifest(path, new string('a', Limit(kind) + 1))));
        Assert.Equal(DiagnosticCodes.StringTooLong, error.Code);
        Assert.Equal(path, error.Path);
    }

    [Theory]
    [MemberData(nameof(StringsMembers))]
    public void TheStringsReaderHoldsEachTranslationToTheLimitOfItsMember(string path, string kind)
    {
        Assert.Empty(Errors(ReadStrings(path, new string('a', Limit(kind)))));
        var error = Assert.Single(Errors(ReadStrings(path, new string('a', Limit(kind) + 1))));
        Assert.Equal(DiagnosticCodes.StringTooLong, error.Code);
        Assert.Equal(path, error.Path);
    }

    [Theory]
    [InlineData("/name")]
    [InlineData("/publisher/name")]
    [InlineData("/contributions/0/name")]
    [InlineData("/contributions/0/settings/0/name")]
    [InlineData("/contributions/0/settings/0/choices/0/name")]
    public void ANameLongerThanTheElementLimitIsWarnedAboutAndStillValid(string path)
    {
        var elements = Limit("longNameElements");
        Assert.DoesNotContain(ReadManifest(path, new string('a', elements)).Diagnostics, diagnostic => diagnostic.Path == path);
        var result = ReadManifest(path, new string('a', elements + 1));
        var warning = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Path == path);
        Assert.Equal(DiagnosticCodes.TextLong, warning.Code);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ADescriptionIsNeverWarnedAboutForItsLength()
    {
        var result = ReadManifest("/description", new string('a', Limit("description")));
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Path == "/description");
    }

    private static int Limit(string kind)
    {
        using var document = Fixtures.Json("text-rules.json");
        return document.RootElement.GetProperty("declarationLimits").GetProperty(kind).GetInt32();
    }

    private static IEnumerable<Diagnostic> Errors<T>(ReadResult<T> result)
        where T : class => result.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    private static string Pointer(string segment) => segment.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);

    /// <summary>The Countdown manifest fixture with the string at the JSON Pointer <paramref name="path"/> replaced.</summary>
    private static ReadResult<ExtensionManifest> ReadManifest(string path, string value)
    {
        var document = Node.Parse(Fixtures.Text("manifests/valid/countdown.json"))!;
        Set(document, path, value);
        return ManifestReader.Read(Encoding.UTF8.GetBytes(document.ToJsonString()), TestHosts.Options);
    }

    /// <summary>A strings file for the Countdown manifest with one member, the string at the JSON Pointer <paramref name="path"/>.</summary>
    private static ReadResult<ExtensionStrings> ReadStrings(string path, string value)
    {
        var manifest = ManifestReader.Read(Fixtures.Bytes("manifests/valid/countdown.json"), TestHosts.Options).Value!;
        var document = new JsonObject { ["schemaVersion"] = 3, ["language"] = "de-DE" };
        Set(document, path, value);
        return StringsReader.Read(Encoding.UTF8.GetBytes(document.ToJsonString()), "de-DE",
            new StringsReadOptions { Manifest = manifest, KnownHosts = TestHosts.Known });
    }

    /// <summary>Sets the string at a JSON Pointer, creating the objects on the way.</summary>
    private static void Set(Node root, string path, string value)
    {
        var segments = path.Split('/').Skip(1).Select(segment => segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal)).ToArray();
        var node = root;
        for (var i = 0; i < segments.Length; i++)
        {
            var last = i == segments.Length - 1;
            if (node is JsonArray array)
            {
                var index = int.Parse(segments[i], System.Globalization.CultureInfo.InvariantCulture);
                if (last)
                {
                    array[index] = value;
                }
                else
                {
                    node = array[index]!;
                }
            }
            else
            {
                var obj = node.AsObject();
                if (last)
                {
                    obj[segments[i]] = value;
                }
                else
                {
                    node = obj[segments[i]] ??= new JsonObject();
                }
            }
        }
    }
}
