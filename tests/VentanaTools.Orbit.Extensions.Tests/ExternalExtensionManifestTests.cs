// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Text;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tests;

public sealed class ExternalExtensionManifestTests
{
    private const string Manifest = """
        {"schemaVersion":2,"id":"example.status","name":"Status sample",
         "description":"A live state display.","version":"1.0.0","hosts":["orbit"],"contributions":[
          {"id":"example.status/toggle","name":"Toggle","description":"Change state.","glyph":"\uE7B3",
           "capabilities":["invoke","face"],"settings":[
            {"id":"mode","name":"Mode","default":"on","choices":[
             {"value":"on","name":"On"},{"value":"off","name":"Off"}]}]}]}
        """;

    private static ExternalExtensionManifestRead Read(string json) =>
        ExternalExtensionManifestReader.Read(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void ValidManifestRoundTripsItsDeclaredCapabilitiesAndSettings()
    {
        var result = Read(Manifest);
        Assert.Null(result.Error);
        var manifest = Assert.IsType<ExternalExtensionManifest>(result.Manifest);
        Assert.Equal("example.status", manifest.Id);
        Assert.Equal(2, manifest.ManifestVersion);
        Assert.Equal("1.0.0", manifest.PackageVersion);
        Assert.Equal(new[] { "orbit" }, manifest.Hosts);
        var contribution = Assert.Single(manifest.Actions);
        Assert.True(contribution.CanInvoke);
        Assert.True(contribution.HasFace);
        Assert.Equal("\uE7B3", contribution.Glyph);
        Assert.Equal("on", Assert.Single(contribution.Settings).DefaultValue);
        var copy = ExternalExtensionManifestReader.Snapshot(manifest);
        Assert.Equal(ExternalExtensionManifestReader.Serialize(manifest), ExternalExtensionManifestReader.Serialize(copy));
        Assert.Equal("ExternalExtensionManifest", manifest.ToString());
        Assert.Equal("ExternalExtensionAction", contribution.ToString());
    }

    [Theory]
    [InlineData("\"schemaVersion\":2", "\"schemaVersion\":1")]
    [InlineData("\"schemaVersion\":2", "\"schemaVersion\":3")]
    [InlineData("\"schemaVersion\":2", "\"schemaVersion\":2,\"schemaVersion\":2")]
    [InlineData("\"schemaVersion\":2", "\"schemaVersion\":2,\"entrypoint\":\"run.exe\"")]
    [InlineData("\"schemaVersion\":2", "\"SchemaVersion\":2")]
    [InlineData("example.status", "orbit.status")]
    [InlineData("example.status", "status")]
    [InlineData("example.status/toggle", "other.extension/toggle")]
    [InlineData("example.status/toggle", "now-playing/play-pause")]
    [InlineData("example.status/toggle", "example.status")]
    [InlineData("Status sample", "Status\\u202Esample")]
    [InlineData("Status sample", "Status\\nSample")]
    [InlineData("\\uE7B3", "not a glyph")]
    [InlineData("\\uE7B3", "A")]
    [InlineData("\"glyph\":", "\"source\":\"file:///x\",\"glyph\":")]
    [InlineData("\"invoke\",\"face\"", "\"invoke\",\"invoke\"")]
    [InlineData("\"invoke\",\"face\"", "\"images\"")]
    [InlineData("\"invoke\",\"face\"", "")]
    [InlineData("\"default\":\"on\"", "\"default\":\"unknown\"")]
    [InlineData("\"value\":\"off\"", "\"value\":\"on\"")]
    public void UntrustedOrAmbiguousDeclarationsAreRejectedAsAWhole(string before, string after)
    {
        var result = Read(Manifest.Replace(before, after, StringComparison.Ordinal));
        Assert.Null(result.Manifest);
        Assert.StartsWith("manifest.", result.Error);
    }

    [Fact]
    public void EmptyDuplicateAndOversizedContributionCatalogsAreRejected()
    {
        const string empty = """
            {"schemaVersion":2,"id":"a.b","name":"A","description":"B","version":"1.0.0","hosts":["orbit"],"contributions":[]}
            """;
        const string action = """
            {"id":"a.b/run","name":"Run","description":"Run","glyph":"\uE7B3","capabilities":["invoke"],"settings":[]}
            """;
        Assert.Null(Read(empty).Manifest);
        Assert.Null(Read(empty.Replace("\"contributions\":[]", "\"contributions\":[" + action + "," + action + "]", StringComparison.Ordinal)).Manifest);
        Assert.Null(Read(empty.Replace("\"contributions\":[]", "\"contributions\":[" + string.Join(',', Enumerable.Repeat(action, 33)) + "]", StringComparison.Ordinal)).Manifest);
        Assert.Null(Read(Manifest.Replace("Status sample", new string('x', 81), StringComparison.Ordinal)).Manifest);
        Assert.Equal("manifest.size", ExternalExtensionManifestReader.Read(new byte[ExternalExtensionManifestReader.MaxBytes + 1]).Error);
    }

    [Fact]
    public void SnapshotDetachesAndFreezesAllNestedCollections()
    {
        var source = Read(Manifest).Manifest!;
        var choices = source.Actions[0].Settings[0].Choices.ToList();
        var settings = new List<ExternalExtensionSetting> { source.Actions[0].Settings[0] with { Choices = choices } };
        var actions = new List<ExternalExtensionAction> { source.Actions[0] with { Settings = settings } };
        var hosts = source.Hosts.ToList();
        var snapshot = ExternalExtensionManifestReader.Snapshot(source with { Actions = actions, Hosts = hosts });
        choices.Clear();
        settings.Clear();
        actions.Clear();
        hosts.Clear();
        Assert.Single(snapshot.Actions);
        Assert.Single(snapshot.Hosts);
        Assert.Equal(2, Assert.Single(snapshot.Actions[0].Settings).Choices.Count);
        Assert.Throws<NotSupportedException>(() => ((IList<ExternalExtensionAction>)snapshot.Actions).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ExternalExtensionSetting>)snapshot.Actions[0].Settings).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<ExternalExtensionChoice>)snapshot.Actions[0].Settings[0].Choices).Clear());
    }

    [Fact]
    public void SettingsUseDeclaredDefaultsAndRejectForeignOrUndeclaredValues()
    {
        var contribution = Read(Manifest).Manifest!.Actions[0];
        Assert.Equal("on", ExternalExtensionSettings.Resolve(contribution, null)!["mode"]);
        var chosen = new Dictionary<string, string> { ["mode"] = "off" };
        var resolved = ExternalExtensionSettings.Resolve(contribution, chosen)!;
        chosen["mode"] = "on";
        Assert.Equal("off", resolved["mode"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)resolved)["mode"] = "on");
        Assert.Null(ExternalExtensionSettings.Resolve(contribution, new Dictionary<string, string> { ["mode"] = "other" }));
        Assert.Null(ExternalExtensionSettings.Resolve(contribution, new Dictionary<string, string> { ["foreign"] = "on" }));
    }

    [Theory]
    [InlineData("orbit")]
    [InlineData("lollipop")]
    [InlineData("ventana")]
    [InlineData("ext")]
    public void ReservedPublishersCannotBeClaimedByAnExternalManifest(string publisher)
    {
        Assert.Equal(ExtensionIds.RootReserved, ExtensionIds.Classify($"{publisher}.tools", ExtensionOrigin.ThirdParty));
        Assert.True(ExtensionIds.IsValid($"{publisher}s.tools", ExtensionOrigin.ThirdParty));
        Assert.True(ExtensionIds.IsValid($"contoso.{publisher}", ExtensionOrigin.ThirdParty));
        Assert.Null(Read(Manifest.Replace("example.status", $"{publisher}.status", StringComparison.Ordinal)).Manifest);
    }

    [Fact]
    public void LegacySchemaCannotBeSerializedIntoASupportedDeclaration()
    {
        var legacy = Read(Manifest).Manifest! with { ManifestVersion = 1 };
        Assert.Throws<InvalidDataException>(() => ExternalExtensionManifestReader.Serialize(legacy));
        Assert.Throws<InvalidDataException>(() => ExternalExtensionManifestReader.Snapshot(legacy));
    }
}
