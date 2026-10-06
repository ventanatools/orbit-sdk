// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Reflection;
using System.Text.Json;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tests;

/// <summary>
/// The three code fixtures are generated from the C# catalogs; these tests regenerate them and
/// fail when the checked-in files differ (set VENTANA_UPDATE_FIXTURES=1 to rewrite them).
/// </summary>
public sealed class CodeCatalogTests
{
    /// <summary>Overlap cases of the one-diagnostic-per-path precedence, applied to fixtures/manifests/valid/countdown.json.</summary>
    private static readonly (string Name, string Set, string Remove, string Path, string Expected)[] PrecedenceCases =
    [
        ("an unknown setting kind is setting.kind-unsupported, not enum.undefined", """{ "/contributions/0/settings/0/kind": "Toggle" }""", "[]", "/contributions/0/settings/0/kind", DiagnosticCodes.SettingKindUnsupported),
        ("an unknown network token is enum.undefined", """{ "/disclosures/network": "Satellite" }""", "[]", "/disclosures/network", DiagnosticCodes.EnumUndefined),
        ("a non-string enumeration value is json.type-mismatch", """{ "/disclosures/network": 2 }""", "[]", "/disclosures/network", DiagnosticCodes.JsonTypeMismatch),
        ("an unknown provides value is contribution.provides-unknown", """{ "/contributions/1/provides/0": "images" }""", "[]", "/contributions/1/provides/0", DiagnosticCodes.ContributionProvidesUnknown),
        ("an empty name is chrome.label-required, not text.empty", """{ "/name": "" }""", "[]", "/name", DiagnosticCodes.ChromeLabelRequired),
        ("a white-space name is chrome.label-required", """{ "/contributions/1/name": "   " }""", "[]", "/contributions/1/name", DiagnosticCodes.ChromeLabelRequired),
        ("an empty setting name is setting.label-required", """{ "/contributions/0/settings/0/name": "" }""", "[]", "/contributions/0/settings/0/name", DiagnosticCodes.SettingLabelRequired),
        ("a format-character choice name is choice.label-required", """{ "/contributions/0/settings/0/choices/0/name": "​" }""", "[]", "/contributions/0/settings/0/choices/0/name", DiagnosticCodes.ChoiceLabelRequired),
        ("an empty description is text.empty", """{ "/description": "" }""", "[]", "/description", DiagnosticCodes.TextEmpty),
        ("an over-long white-space description is string.too-long", "{ \"/description\": \"" + new string(' ', 513) + "\" }", "[]", "/description", DiagnosticCodes.StringTooLong),
        ("a missing id is id.required, not json.required-missing", "{}", """["/id"]""", "/id", DiagnosticCodes.IdRequired),
        ("a blank id is id.required", """{ "/id": "  " }""", "[]", "/id", DiagnosticCodes.IdRequired),
        ("a missing contribution id is id.required", "{}", """["/contributions/1/id"]""", "/contributions/1/id", DiagnosticCodes.IdRequired),
        ("a missing setting id is setting.id-required", "{}", """["/contributions/0/settings/0/id"]""", "/contributions/0/settings/0/id", DiagnosticCodes.SettingIdRequired),
        ("a missing choice value is choice.value-required", "{}", """["/contributions/0/settings/0/choices/0/value"]""", "/contributions/0/settings/0/choices/0/value", DiagnosticCodes.ChoiceValueRequired),
        ("a missing glyph is chrome.glyph-required", "{}", """["/contributions/1/glyph"]""", "/contributions/1/glyph", DiagnosticCodes.ChromeGlyphRequired),
        ("a missing name is json.required-missing", "{}", """["/name"]""", "/name", DiagnosticCodes.JsonRequiredMissing),
        ("a null name is json.null-not-allowed", """{ "/name": null }""", "[]", "/name", DiagnosticCodes.JsonNullNotAllowed),
        ("a number for a name is json.type-mismatch", """{ "/name": 7 }""", "[]", "/name", DiagnosticCodes.JsonTypeMismatch),
        ("an over-long id that breaks the grammar is id.grammar", """{ "/id": "Example.abcdefghabcdefghabcdefghabcdefghabcdefghabcdefghabcdefghabcdefghabcdefghabcdefghabcdefghabcdefghabcdefghabcdefghabcdefghabcdefgh" }""", "[]", "/id", DiagnosticCodes.IdGrammar),
        ("a duplicate contribution id outside the namespace is id.outside-namespace", """{ "/contributions/0/id": "example.other/timer", "/contributions/1/id": "example.other/timer" }""", "[]", "/contributions/1/id", DiagnosticCodes.IdOutsideNamespace),
        ("a duplicate choice value that breaks the grammar is choice.value-grammar", """{ "/contributions/0/settings/0/choices/1/value": "Start", "/contributions/0/settings/0/choices/0/value": "Start" }""", "[]", "/contributions/0/settings/0/choices/1/value", DiagnosticCodes.ChoiceValueGrammar),
        ("a non-integer schemaVersion is json.type-mismatch", """{ "/schemaVersion": 3.0 }""", "[]", "/schemaVersion", DiagnosticCodes.JsonTypeMismatch),
    ];

    [Fact]
    public void DiagnosticsFixtureRegeneratesFromTheCatalog() =>
        Fixtures.AssertGenerated("codes/diagnostics.json", Fixtures.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("about", "The diagnostic code table of contract section 4.4, generated from the C# catalog. message is the fixed English text readers report; fix is the author fix, where {tool} stands for the tool's command name; sharedConvention is true when the Ventana code-naming convention (contract section 2.7) shares the code's string and meaning across products.");
            writer.WriteStartArray("codes");
            foreach (var info in DiagnosticCatalog.All)
            {
                writer.WriteStartObject();
                writer.WriteString("code", info.Code);
                writer.WriteString("severity", WireTokens.SeverityToken(info.Severity));
                writer.WriteString("message", info.Message);
                writer.WriteString("fix", info.Fix);
                writer.WriteBoolean("sharedConvention", info.SharedConvention);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }));

    [Fact]
    public void ReasonCodesFixtureRegeneratesFromTheCatalog() =>
        Fixtures.AssertGenerated("codes/reason-codes.json", Fixtures.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("about", "The reason-code catalog of contract section 8.3, generated from the C# catalog. seenIn: L host log, R host registration row, D host developer details, E error frame, S SDK status. pre: may be sent to a peer that has not yet verified the sender's proof. violation: counts toward a host's penalty box. anchor: the help anchor, https://dev.ventana.tools/go/<host-id>/codes#<anchor>.");
            writer.WriteStartArray("codes");
            foreach (var entry in ReasonCodeCatalog.All)
            {
                writer.WriteStartObject();
                writer.WriteString("code", entry.Code);
                writer.WriteString("disposition", WireTokens.DispositionToken(entry.Info.Disposition));
                writer.WriteStartArray("seenIn");
                foreach (var audience in entry.SeenIn.Split(' '))
                {
                    writer.WriteStringValue(audience);
                }

                writer.WriteEndArray();
                writer.WriteBoolean("pre", entry.Info.PreAuthentication);
                writer.WriteBoolean("violation", entry.Info.IsViolation);
                writer.WriteString("meaning", entry.Meaning);
                if (entry.Info.Fix is null)
                {
                    writer.WriteNull("fix");
                }
                else
                {
                    writer.WriteString("fix", entry.Info.Fix);
                }

                writer.WriteString("anchor", entry.Info.HelpAnchor);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }));

    [Fact]
    public void PrecedenceFixtureRegeneratesFromTheCatalog() =>
        Fixtures.AssertGenerated("codes/precedence.json", Fixtures.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteString("about", "The one-diagnostic-per-path precedence of contract section 4.2, generated from the C# catalog: when several codes apply to one file and path, a reader reports the first in this order (tiers in order, codes in order within a tier). Each case edits fixtures/manifests/valid/countdown.json (set: pointer to new value; remove: pointers to delete) and names the one code reported at path.");
            writer.WriteStartArray("tiers");
            foreach (var (name, codes) in DiagnosticPrecedence.Tiers)
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WriteStartArray("codes");
                foreach (var code in codes)
                {
                    writer.WriteStringValue(code);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteStartArray("cases");
            foreach (var (name, set, remove, path, expected) in PrecedenceCases)
            {
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WritePropertyName("set");
                using (var document = JsonDocument.Parse(set))
                {
                    document.RootElement.WriteTo(writer);
                }

                writer.WritePropertyName("remove");
                using (var document = JsonDocument.Parse(remove))
                {
                    document.RootElement.WriteTo(writer);
                }

                writer.WriteString("path", path);
                writer.WriteString("expected", expected);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }));

    public static TheoryData<string> PrecedenceCaseNames()
    {
        var data = new TheoryData<string>();
        using var document = Fixtures.Json("codes/precedence.json");
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            data.Add(item.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PrecedenceCaseNames))]
    public void EachPrecedenceCaseReportsOneCodeAtItsPath(string name)
    {
        using var document = Fixtures.Json("codes/precedence.json");
        var item = document.RootElement.GetProperty("cases").EnumerateArray().Single(entry => entry.GetProperty("name").GetString() == name);
        var manifest = Patch.Apply(Fixtures.Text("manifests/valid/countdown.json"), item.GetProperty("set"), item.GetProperty("remove"));
        var result = ManifestReader.Read(manifest, TestHosts.Options);
        var path = item.GetProperty("path").GetString()!;
        var atPath = result.Diagnostics.Where(diagnostic => diagnostic.Path == path).ToList();
        var diagnostic = Assert.Single(atPath);
        Assert.Equal(item.GetProperty("expected").GetString(), diagnostic.Code);
    }

    [Fact]
    public void EveryDiagnosticCodeConstantIsCataloguedOnceAndRankedOnce()
    {
        var constants = typeof(DiagnosticCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(field => (string)field.GetRawConstantValue()!).ToList();
        Assert.Equal(constants.Count, constants.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(constants.Order(StringComparer.Ordinal), DiagnosticCatalog.All.Select(info => info.Code).Order(StringComparer.Ordinal));
        var ranked = DiagnosticPrecedence.Tiers.SelectMany(tier => tier.Codes).ToList();
        Assert.Equal(ranked.Count, ranked.Distinct(StringComparer.Ordinal).Count());
        Assert.All(constants, code => Assert.Contains(code, ranked));
        Assert.DoesNotContain(constants, code => code.StartsWith("pairing.", StringComparison.Ordinal));
        Assert.All(ReasonCodeCatalog.All.Where(entry => entry.Code.StartsWith("pairing.", StringComparison.Ordinal)),
            entry => Assert.Contains(entry.Code, ranked));
        Assert.All(DiagnosticCatalog.All, info =>
        {
            Assert.True(Grammars.IsDottedCode(info.Code));
            Assert.DoesNotContain("`", info.Fix, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void ReasonCodeStaticsMatchTheCatalogAndTheViolationRuleOfSection7_12()
    {
        var statics = typeof(ReasonCode).GetProperties(BindingFlags.Public | BindingFlags.Static)
            .Where(property => property.PropertyType == typeof(ReasonCode))
            .Select(property => ((ReasonCode)property.GetValue(null)!).Value).ToList();
        Assert.Equal(ReasonCodeCatalog.All.Select(entry => entry.Code).Order(StringComparer.Ordinal), statics.Order(StringComparer.Ordinal));
        foreach (var entry in ReasonCodeCatalog.All)
        {
            var name = string.Concat(entry.Code.Split('.', '-').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
            Assert.Equal(entry.Code, ((ReasonCode)typeof(ReasonCode).GetProperty(name)!.GetValue(null)!).Value);
            Assert.Equal(entry.Code.Replace('.', '-'), entry.Info.HelpAnchor);
            var expectedViolation = entry.Info.Disposition == Disposition.Close
                && !entry.Code.StartsWith("host.", StringComparison.Ordinal)
                && entry.Code is not ("auth.timeout" or "manifest.mismatch" or "auth.identity-changed");
            Assert.Equal(expectedViolation || entry.Code == "auth.abandoned", entry.Info.IsViolation);
            Assert.True(!entry.Info.PreAuthentication || entry.Info.Disposition == Disposition.Close, entry.Code);
        }
    }

    [Fact]
    public void UnknownCodesParseWithTheRulesOfSection7_5()
    {
        Assert.True(ReasonCode.TryParse("future.reason", out var unknown));
        Assert.False(unknown.IsKnown);
        Assert.Equal("future.reason", unknown.ToString());
        Assert.Equal(Disposition.Close, unknown.Info.Disposition);
        Assert.False(unknown.Info.PreAuthentication);
        Assert.False(unknown.Info.IsViolation);
        Assert.Null(unknown.Info.Fix);
        Assert.Null(unknown.Info.HelpAnchor);
        Assert.Null(unknown.Info.HelpUri("example-host"));
        Assert.True(ReasonCode.TryParse("auth.proof-invalid", out var known));
        Assert.True(known.IsKnown);
        Assert.Equal(ReasonCode.AuthProofInvalid, known);
        Assert.Equal(new Uri("https://dev.ventana.tools/go/example-host/codes#auth-proof-invalid"), known.Info.HelpUri("example-host"));
        Assert.Throws<ArgumentException>(() => known.Info.HelpUri("Example Host"));
        foreach (var bad in new[] { null, "", "auth", "auth.", ".auth", "Auth.proof", "auth..proof", "auth.proof_invalid", "a." + new string('b', 63) })
        {
            Assert.False(ReasonCode.TryParse(bad, out var code));
            Assert.Equal(default, code);
        }

        Assert.Equal(string.Empty, default(ReasonCode).Value);
        Assert.False(default(ReasonCode).IsKnown);
        Assert.Equal(Disposition.Close, default(ReasonCode).Info.Disposition);
    }
}
