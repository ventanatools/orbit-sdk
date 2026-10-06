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
    public async Task ExcludedNamesAreNeverPackedEvenWhenACopyRuleNamesThem()
    {
        using var project = TestProject.Create(packConfig: """
            {
              "packVersion": 1,
              "readme": "PACKAGE-README.md",
              "payload": [
                { "from": "companion/notes.user", "to": "direct" },
                { "from": "companion/obj", "to": "objfolder" },
                { "from": "companion/obj/cache.bin", "to": "objfile" },
                { "from": "node_modules", "to": "modules" },
                { "from": "node_modules/.cache", "to": "cache" },
                { "from": "node_modules/.cache/hit.txt", "to": "cachefile" },
                { "from": "lib/", "to": "lib" }
              ]
            }
            """);
        project.Write("node_modules/lib.js", "module.exports = 1;\n");
        project.Write("node_modules/.cache/hit.txt", "never packed");
        project.Write("lib/kept.txt", "kept");
        project.Write("lib/.vs/state.txt", "never packed");
        var run = await ToolHarness.RunAsync(project.Path, "pack");
        Assert.True(run.ExitCode == 0, run.ToString());

        var package = PackageReader.Read(File.ReadAllBytes(project.Combine("artifacts/" + PackageName)),
            new PackageReadOptions { Manifest = new ManifestReadOptions { HostId = ActiveHost.Id } });
        Assert.True(package.Succeeded, string.Join("\n", package.Diagnostics));
        Assert.Equal(
            ["README.md", "extension.json", "extension.package.json", "payload/lib/kept.txt", "payload/modules/lib.js"],
            package.Value.Files.Select(file => file.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task APairingFileNamedDirectlyIsRefusedByName()
    {
        using var project = TestProject.Create(packConfig: """
            {
              "packVersion": 1,
              "readme": "docs/pairing.json",
              "payload": [ { "from": "companion/example.tool-test.pairing.json", "to": "companion" } ]
            }
            """);
        project.Write("docs/pairing.json", "never packed: a pairing file's name");
        var run = await ToolHarness.RunAsync(project.Path, "pack");
        Assert.Equal(1, run.ExitCode);
        Assert.Equal(
            ["companion/example.tool-test.pairing.json: error pack.secret: A staged file looks like a pairing file; pairing files are never packed. []",
                "docs/pairing.json: error pack.secret: A staged file looks like a pairing file; pairing files are never packed. []"],
            ErrorLines(run).Order(StringComparer.Ordinal));
        Assert.False(File.Exists(project.Combine("artifacts/" + PackageName)));
    }

    [Fact]
    public async Task AReadmeInAnExcludedFolderLeavesThePackageWithoutOne()
    {
        using var project = TestProject.Create(packConfig: """
            {
              "packVersion": 1,
              "readme": "obj/README.md",
              "payload": [ { "from": "companion", "to": "companion" } ]
            }
            """);
        project.Write("obj/README.md", "# Generated\n");
        var run = await ToolHarness.RunAsync(project.Path, "pack");
        Assert.Equal(1, run.ExitCode);
        Assert.Equal(
            "extension.pack.json(3,13): error package.file-missing: A required file is missing: extension.json, extension.package.json or README.md. [/readme]",
            Assert.Single(ErrorLines(run)));
    }

    [Fact]
    public async Task ALinkOnTheWayToTheReadmeOrTheStringsIsRefused()
    {
        using var project = TestProject.Create(packConfig: """
            {
              "packVersion": 1,
              "readme": "docs/README.md",
              "strings": "languages/strings"
            }
            """);
        using var outside = new TempFolder();
        outside.Write("docs/README.md", "# Outside\n");
        outside.Write("languages/strings/de-DE.json", TestProject.Strings());
        CreateLink(project.Combine("docs"), outside.Combine("docs"));
        CreateLink(project.Combine("languages"), outside.Combine("languages"));
        var run = await ToolHarness.RunAsync(project.Path, "pack");
        Assert.Equal(1, run.ExitCode);
        Assert.Equal(
            ["docs: error pack.link: A staged file or folder is a symbolic link or junction. []",
                "languages: error pack.link: A staged file or folder is a symbolic link or junction. []"],
            ErrorLines(run).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task OutsideTheProjectLinksAreCheckedBelowTheSharedFolderOnly()
    {
        // parent/project packs ../shared/file.txt; parent/shared is a junction, parent is not inspected.
        using var parent = new TempFolder();
        using var target = new TempFolder();
        target.Write("file.txt", "outside");
        parent.Write("plain/file.txt", "plain");
        parent.Write("project/extension.json", TestProject.Manifest(ActiveHost.Id));
        parent.Write("project/PACKAGE-README.md", "# Tool test\n");
        parent.Write("project/extension.pack.json", """
            {
              "packVersion": 1,
              "readme": "PACKAGE-README.md",
              "payload": [ { "from": "../plain/file.txt", "to": "plain" }, { "from": "../shared/file.txt", "to": "shared" } ]
            }
            """);
        CreateLink(parent.Combine("shared"), target.Path);
        var run = await ToolHarness.RunAsync(parent.Combine("project"), "pack");
        Assert.Equal(1, run.ExitCode);
        var line = Assert.Single(ErrorLines(run));
        Assert.EndsWith("shared: error pack.link: A staged file or folder is a symbolic link or junction. []", line, StringComparison.Ordinal);

        // The project folder itself, reached through a link, is not inspected.
        using var holder = new TempFolder();
        using var linked = TestProject.Create();
        CreateLink(holder.Combine("alias"), linked.Path);
        var throughLink = await ToolHarness.RunAsync(holder.Combine("alias"), "pack");
        Assert.True(throughLink.ExitCode == 0, throughLink.ToString());
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
        Assert.False(PackCommand.IsAlwaysExcluded("obj/pairing", folder: true));
        Assert.True(PackCommand.IsPairingName("Example.X.Pairing.JSON"));
        Assert.True(PackCommand.IsPairingName("pairing.json"));
        Assert.False(PackCommand.IsPairingName("pairing.json.txt"));
        Assert.False(PackCommand.IsPairingName("my-pairing.json"));
        Assert.True(PackCommand.LooksLikePairing("{\"a\":{\"pipeName\":\"x\",\"secret\":\"y\"}}"u8.ToArray()));
        Assert.True(PackCommand.LooksLikePairing("{\"pipe\\u004eame\":\"x\",\"secret\":\"y\"}"u8.ToArray()));
        Assert.True(PackCommand.LooksLikePairing("not json \"pipeName\" \"secret\""u8.ToArray()));
        Assert.False(PackCommand.LooksLikePairing("{\"pipeName\":\"x\"}"u8.ToArray()));
    }

    /// <summary>The canonical error lines a run printed, without their <c>fix:</c> lines.</summary>
    private static List<string> ErrorLines(ToolRun run) =>
        run.Out.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Where(line => line.Contains(": error ", StringComparison.Ordinal))
            .Select(line => line.Replace('\\', '/'))
            .ToList();

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
