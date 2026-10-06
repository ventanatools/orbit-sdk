// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Diagnostics;
using System.Text.Json;
using VentanaTools.Orbit.Extensions.Packaging;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tool.Tests;

public sealed class PackTests
{
    private static string PackageName => "example.tool-test-1.2.3" + ActiveHost.Extension;

    [Fact]
    public async Task PackWritesAVerifiedPackageWithoutTheNamesThatAreNeverPacked()
    {
        using var project = TestProject.Create();
        var run = await ToolHarness.RunAsync(project.Path, "pack");
        Assert.Equal(0, run.ExitCode);
        Golden.Match("pack-text", run, project.Path);

        var path = project.Combine("artifacts/" + PackageName);
        var package = PackageReader.Read(File.ReadAllBytes(path), new PackageReadOptions { Manifest = new ManifestReadOptions { HostId = ActiveHost.Id } });
        Assert.True(package.Succeeded, string.Join("\n", package.Diagnostics));
        Assert.Equal(
            ["README.md", "extension.json", "extension.package.json", "payload/companion/run.cmd", "payload/companion/settings.json", "payload/docs/PACKAGE-README.md", "strings/de-DE.json"],
            package.Value.Files.Select(file => file.Path).Order(StringComparer.Ordinal));
        Assert.Equal(ToolIdentity.CommandName, package.Value.Descriptor.CreatedBy!.Name);
        Assert.Equal(ToolIdentity.Version, package.Value.Descriptor.CreatedBy.Version);
    }

    [Fact]
    public async Task PackJsonAddsThePackage()
    {
        using var project = TestProject.Create();
        var run = await ToolHarness.RunAsync(project.Path, "pack", "-o", "out", "--json");
        Assert.Equal(0, run.ExitCode);
        using var json = JsonDocument.Parse(run.Out);
        var package = json.RootElement.GetProperty("package");
        Assert.Equal("out/" + PackageName, package.GetProperty("path").GetString());
        Assert.Equal("example.tool-test", package.GetProperty("id").GetString());
        Assert.Equal(new FileInfo(project.Combine("out/" + PackageName)).Length, package.GetProperty("bytes").GetInt64());
        Golden.Match("pack-json", run, project.Path);
    }

    [Fact]
    public async Task PackingTheSameInputTwiceGivesTheSameBytes()
    {
        using var project = TestProject.Create();
        Assert.Equal(0, (await ToolHarness.RunAsync(project.Path, "pack", "-o", "first")).ExitCode);
        File.SetLastWriteTimeUtc(project.Combine("companion/run.cmd"), DateTime.UtcNow.AddDays(-3));
        Assert.Equal(0, (await ToolHarness.RunAsync(project.Path, "pack", "-o", "second")).ExitCode);
        Assert.Equal(File.ReadAllBytes(project.Combine("first/" + PackageName)), File.ReadAllBytes(project.Combine("second/" + PackageName)));
    }

    [Fact]
    public async Task AnExistingPackageIsReplacedOnlyWithForce()
    {
        using var project = TestProject.Create();
        Assert.Equal(0, (await ToolHarness.RunAsync(project.Path, "pack")).ExitCode);
        var again = await ToolHarness.RunAsync(project.Path, "pack");
        Assert.Equal(3, again.ExitCode);
        Golden.Match("pack-output-exists", again, project.Path);
        Assert.Equal(0, (await ToolHarness.RunAsync(project.Path, "pack", "--force")).ExitCode);
        Assert.Empty(Directory.EnumerateFiles(project.Combine("artifacts"), "*.tmp"));
    }

    [Fact]
    public async Task AStagedPairingFileIsRefused()
    {
        using var project = TestProject.Create();
        project.Write("companion/connection.json", TestProject.Pairing("example-host", "example.tool-test"));
        var run = await ToolHarness.RunAsync(project.Path, "pack");
        Assert.Equal(1, run.ExitCode);
        Golden.Match("pack-secret", run, project.Path);
        Assert.False(File.Exists(project.Combine("artifacts/" + PackageName)));
        Assert.DoesNotContain("AAECAwQF", run.Out + run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStagedLinkIsRefused()
    {
        using var project = TestProject.Create();
        using var target = new TempFolder();
        target.Write("outside.txt", "outside");
        CreateLink(project.Combine("companion/linked"), target.Path);
        var run = await ToolHarness.RunAsync(project.Path, "pack");
        Assert.Equal(1, run.ExitCode);
        Golden.Match("pack-link", run, project.Path);
    }

    [Fact]
    public async Task AFailedBuildStepIsPackBuildFailed()
    {
        using var project = TestProject.Create(packConfig: """
            {
              "packVersion": 1,
              "readme": "PACKAGE-README.md",
              "build": { "project": "missing/Missing.csproj" }
            }
            """);
        var run = await ToolHarness.RunAsync(project.Path, "pack");
        Assert.Equal(1, run.ExitCode);
        var diagnostics = run.Out.Split('\n').Where(line => line.Contains(": error ", StringComparison.Ordinal)).ToList();
        Assert.Equal("extension.pack.json(4,12): error pack.build-failed: The build step's dotnet publish failed. [/build]", Assert.Single(diagnostics).TrimEnd('\r'));
    }

    [Fact]
    public async Task AMissingSourceIsReportedAtItsMember()
    {
        using var project = TestProject.Create(packConfig: """
            {
              "packVersion": 1,
              "readme": "MISSING.md",
              "payload": [ { "from": "nowhere", "to": "companion" } ]
            }
            """);
        var run = await ToolHarness.RunAsync(project.Path, "pack");
        Assert.Equal(1, run.ExitCode);
        Golden.Match("pack-source-missing", run, project.Path);
    }

    [Fact]
    public async Task AnInvalidPackConfigurationListsItsOwnDiagnostics()
    {
        using var project = TestProject.Create(packConfig: """
            {
              "packVersion": "1",
              "readme": "PACKAGE-README.md",
              "payload": [ { "from": "companion", "to": "../outside", "exclude": "*.cmd" } ],
              "files": []
            }
            """);
        var run = await ToolHarness.RunAsync(project.Path, "pack", "--json");
        Assert.Equal(1, run.ExitCode);
        Golden.Match("pack-config", run, project.Path);
    }

    [Fact]
    public async Task AnotherPackVersionIsOnlyReportedAsUnsupported()
    {
        using var project = TestProject.Create(packConfig: """{ "packVersion": 2, "anything": true }""");
        var run = await ToolHarness.RunAsync(project.Path, "pack");
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("error schema.version-unsupported", run.Out, StringComparison.Ordinal);
        Assert.DoesNotContain("json.unknown-member", run.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAnActiveHostTheExtensionIsUnknown()
    {
        using var project = TestProject.Create("example-host");
        var run = await ToolHarness.RunAsync(project.Path, "pack");
        Assert.Equal(2, run.ExitCode);
        var forced = await ToolHarness.RunAsync(project.Path, "pack", "--host", ActiveHost.Id);
        Assert.Equal(1, forced.ExitCode);
        Assert.Contains("error manifest.host-not-listed", forced.Out, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMissingPackConfigurationIsAnInputProblem()
    {
        using var folder = new TempFolder();
        folder.Write("extension.json", TestProject.Manifest(ActiveHost.Id));
        Assert.Equal(3, (await ToolHarness.RunAsync(folder.Path, "pack")).ExitCode);
    }

    [Fact]
    public void AlwaysExcludedNamesAndPairingDetection()
    {
        Assert.True(PackCommand.IsAlwaysExcluded("a/.git", folder: true));
        Assert.True(PackCommand.IsAlwaysExcluded("obj", folder: true));
        Assert.True(PackCommand.IsAlwaysExcluded("lib/node_modules/.cache", folder: true));
        Assert.False(PackCommand.IsAlwaysExcluded("node_modules", folder: true));
        Assert.True(PackCommand.IsAlwaysExcluded("x/My.csproj.user", folder: false));
        Assert.True(PackCommand.IsAlwaysExcluded("pairing.json", folder: false));
        Assert.True(PackCommand.IsAlwaysExcluded("a/example.x.pairing.json", folder: false));
        Assert.False(PackCommand.IsAlwaysExcluded("obj.txt", folder: false));
        Assert.True(PackCommand.LooksLikePairing("{\"a\":{\"pipeName\":\"x\",\"secret\":\"y\"}}"u8.ToArray()));
        Assert.True(PackCommand.LooksLikePairing("{\"pipe\\u004eame\":\"x\",\"secret\":\"y\"}"u8.ToArray()));
        Assert.True(PackCommand.LooksLikePairing("not json \"pipeName\" \"secret\""u8.ToArray()));
        Assert.False(PackCommand.LooksLikePairing("{\"pipeName\":\"x\"}"u8.ToArray()));
    }

    /// <summary>A junction on Windows (no privilege needed), a symbolic link elsewhere.</summary>
    internal static void CreateLink(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }

        using var process = Process.Start(new ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }
}
