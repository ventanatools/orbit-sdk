// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Text.Json;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tool.Tests;

public sealed class ValidateTests
{
    private const string TestHost = "example-host";

    [Fact]
    public async Task TheValidFixtureIsOkInJson()
    {
        var run = await ToolHarness.RunAsync(Repository.Root, "validate", "fixtures/manifests/valid/countdown.json", "--json");
        Assert.Equal(0, run.ExitCode);
        using var json = JsonDocument.Parse(run.Out);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(ToolIdentity.CommandName, json.RootElement.GetProperty("tool").GetString());
        Assert.Equal("validate", json.RootElement.GetProperty("command").GetString());
        Golden.Match("validate-valid-json", run, Repository.Root);
    }

    [Fact]
    public async Task TheValidFixtureIsCleanForItsOwnHost()
    {
        var run = await ToolHarness.RunAsync(Repository.Root, "validate", "fixtures/manifests/valid/countdown.json", "--host", TestHost);
        Assert.Equal(0, run.ExitCode);
        Golden.Match("validate-valid-text", run, Repository.Root);
    }

    [Fact]
    public async Task WarningsAsErrorsFailsOnAWarning()
    {
        // Without --host, example-host is not an active registry host: manifest.host-unknown is a warning.
        var run = await ToolHarness.RunAsync(Repository.Root, "validate", "fixtures/manifests/valid/countdown.json", "--warnings-as-errors", "--json");
        Assert.Equal(1, run.ExitCode);
        using var json = JsonDocument.Parse(run.Out);
        Assert.False(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, json.RootElement.GetProperty("summary").GetProperty("warnings").GetInt32());
    }

    public static TheoryData<string> InvalidManifests()
    {
        var data = new TheoryData<string>();
        foreach (var path in Directory.EnumerateFiles(Repository.PathOf("fixtures/manifests/invalid"), "*.json")
                     .Select(Path.GetFileName)
                     .Where(name => !name!.EndsWith(".expected.json", StringComparison.Ordinal) && !name.EndsWith(".options.json", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            data.Add(path!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(InvalidManifests))]
    public async Task EveryInvalidFixturePrintsItsExpectedDiagnostics(string name)
    {
        var options = Repository.PathOf("fixtures/manifests/invalid/" + Path.ChangeExtension(name, ".options.json"));
        string? host = null;
        if (File.Exists(options))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(options));
            if (!document.RootElement.TryGetProperty("hostId", out var hostId))
            {
                return; // Capability options have no command-line equivalent: a tool has no host context.
            }

            host = hostId.GetString()!;
        }

        string[] args = host is null
            ? ["validate", "fixtures/manifests/invalid/" + name, "--json"]
            : ["validate", "fixtures/manifests/invalid/" + name, "--host", host, "--json"];
        var run = await ToolHarness.RunAsync(Repository.Root, args);
        using var expected = JsonDocument.Parse(File.ReadAllBytes(Repository.PathOf("fixtures/manifests/invalid/" + Path.ChangeExtension(name, ".expected.json"))));
        using var actual = JsonDocument.Parse(run.Out);
        var expectedList = Describe(expected.RootElement.GetProperty("diagnostics"));

        // The fixtures were made with the test host as the only known host; the tool knows the registry, so the
        // test host itself is an unknown host here. That warning is the one difference.
        var actualList = Describe(actual.RootElement.GetProperty("diagnostics"))
            .Where(item => !item.StartsWith(DiagnosticCodes.ManifestHostUnknown + " ", StringComparison.Ordinal) || expectedList.Contains(item))
            .ToList();
        Assert.Equal(expectedList, actualList);
        var errors = expected.RootElement.GetProperty("diagnostics").EnumerateArray().Any(item => item.GetProperty("severity").GetString() == "Error");
        Assert.Equal(errors ? 1 : 0, run.ExitCode);
    }

    [Fact]
    public async Task TextOutputIsMsBuildCanonicalWithTheFix()
    {
        var run = await ToolHarness.RunAsync(Repository.Root, "validate", "fixtures/manifests/invalid/json-member-renamed-capabilities.json", "--host", TestHost);
        Assert.Equal(1, run.ExitCode);
        Golden.Match("validate-invalid-text", run, Repository.Root);
    }

    [Fact]
    public async Task AFolderIsValidatedWithItsStrings()
    {
        using var project = TestProject.Create(TestHost);
        var clean = await ToolHarness.RunAsync(project.Path, "validate", "--host", TestHost);
        Assert.Equal(0, clean.ExitCode);
        Golden.Match("validate-folder", clean, project.Path);

        project.Write("strings/fr.json", """{ "schemaVersion": 3, "language": "de", "contributions": { "example.tool-test/missing": { "name": "x" } } }""");
        var broken = await ToolHarness.RunAsync(project.Path, "validate", ".", "--host", TestHost);
        Assert.Equal(1, broken.ExitCode);
        Golden.Match("validate-folder-strings", broken, project.Path);
    }

    [Fact]
    public async Task APackageIsVerifiedAndArchiveFindingsHaveNoPosition()
    {
        var run = await ToolHarness.RunAsync(Repository.Root, "validate", "fixtures/packages/zip64-sizes.zip", "--host", TestHost);
        Assert.Equal(1, run.ExitCode);
        Golden.Match("validate-package-text", run, Repository.Root);
    }

    [Fact]
    public async Task AMissingPathIsAnInputProblem()
    {
        using var folder = new TempFolder();
        var file = await ToolHarness.RunAsync(folder.Path, "validate", "missing.json");
        Assert.Equal(3, file.ExitCode);
        var empty = await ToolHarness.RunAsync(folder.Path, "validate", "--json");
        Assert.Equal(3, empty.ExitCode);
        Golden.Match("validate-missing", empty, folder.Path);
    }

    private static List<string> Describe(JsonElement diagnostics) =>
        diagnostics.EnumerateArray().Select(item => string.Join(' ',
            item.GetProperty("code").GetString(),
            item.GetProperty("path").GetString(),
            item.GetProperty("severity").GetString(),
            item.TryGetProperty("line", out var line) ? line.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : "-",
            item.TryGetProperty("column", out var column) ? column.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture) : "-")).ToList();
}
