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
    public async Task ExcludedNamesAreLeftOutWhetherTheyAreFilesOrFolders()
    {
        // A .git FILE is a worktree's or submodule's pointer to its repository, with an absolute local path.
        using var project = TestProject.Create(packConfig: """
            {
              "packVersion": 1,
              "readme": "PACKAGE-README.md",
              "payload": [
                { "from": ".", "to": "root", "include": ["*", "tree/**"] },
                { "from": "tree/.git", "to": "direct" },
                { "from": "tree/obj", "to": "direct" },
                { "from": "tree/notes.user", "to": "userfolder" },
                { "from": "tree/old.pairing.json", "to": "pairingfolder" }
              ]
            }
            """);
        project.Write(".git", "gitdir: C:/Users/someone/source/repo/.git/worktrees/tool-test\n");
        project.Write("tree/kept.txt", "kept");
        project.Write("tree/.git", "gitdir: ../.git/modules/tree\n");
        project.Write("tree/sub/.git", "gitdir: ../../.git/modules/tree/sub\n");
        project.Write("tree/sub/kept.txt", "kept");
        project.Write("tree/.vs", "never packed");
        project.Write("tree/obj", "never packed");
        project.Write("tree/notes.user/inside.txt", "never packed: a folder with a *.user name");
        project.Write("tree/old.pairing.json/inside.txt", "never packed: a folder with a pairing file's name");
        project.Write("tree/pairing.json/inside.txt", "never packed");
        project.Write("tree/node_modules/.cache", "never packed");
        project.Write("tree/node_modules/index.js", "module.exports = 1;\n");
        var run = await ToolHarness.RunAsync(project.Path, "pack");
        Assert.True(run.ExitCode == 0, run.ToString());

        var package = ReadPackage(project.Combine("artifacts/" + PackageName));
        Assert.Equal(
            ["README.md", "extension.json", "extension.package.json", "payload/root/PACKAGE-README.md", "payload/root/extension.json",
                "payload/root/extension.pack.json", "payload/root/tree/kept.txt", "payload/root/tree/node_modules/index.js",
                "payload/root/tree/sub/kept.txt"],
            package.Files.Select(file => file.Path).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(package.Files, file => Text(file).Contains("gitdir", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "artifacts")]
    [InlineData("out/packages", "out/packages")]
    public async Task PackNeverStagesItsOwnOutputFolder(string? outputArgument, string output)
    {
        // A copy rule of the whole project, with the default output folder or a nested -o folder inside it.
        using var project = TestProject.Create(packConfig: """
            {
              "packVersion": 1,
              "readme": "PACKAGE-README.md",
              "payload": [ { "from": ".", "to": "source", "exclude": ["companion/**"] } ]
            }
            """);
        string[] pack = outputArgument is null ? ["pack"] : ["pack", "-o", outputArgument];
        var first = await ToolHarness.RunAsync(project.Path, pack);
        Assert.True(first.ExitCode == 0, first.ToString());
        var packagePath = project.Combine(output + "/" + PackageName);
        var firstBytes = File.ReadAllBytes(packagePath);
        project.Write(output + "/notes.txt", "never packed: in the output folder");

        // The second pack finds the first package (and a note) in the copied folder and leaves them out.
        var second = await ToolHarness.RunAsync(project.Path, [.. pack, "--force"]);
        Assert.True(second.ExitCode == 0, second.ToString());
        Assert.Equal(firstBytes, File.ReadAllBytes(packagePath));
        var top = "payload/source/" + output.Split('/')[0] + "/";
        Assert.DoesNotContain(ReadPackage(packagePath).Files, file => file.Path.StartsWith(top, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACopyRuleOfTheOutputFolderLeavesOutThePackagesInIt()
    {
        using var project = TestProject.Create(packConfig: $$"""
            {
              "packVersion": 1,
              "readme": "PACKAGE-README.md",
              "payload": [
                { "from": "dist", "to": "dist" },
                { "from": "dist/example.tool-test-0.0.1{{ActiveHost.Extension}}", "to": "named" }
              ]
            }
            """);
        project.Write("dist/notes.txt", "kept: the folder is copied, the packages in it are not");
        project.Write("dist/example.tool-test-0.0.1" + ActiveHost.Extension, "an older package");
        project.Write("dist/nested/example.tool-test-0.0.1" + ActiveHost.Extension, "kept: not directly in the output folder");
        foreach (var force in new[] { false, true })
        {
            var run = await ToolHarness.RunAsync(project.Path, force ? ["pack", "-o", "dist", "--force"] : ["pack", "-o", "dist"]);
            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.Equal(
                ["README.md", "extension.json", "extension.package.json", "payload/dist/nested/example.tool-test-0.0.1" + ActiveHost.Extension,
                    "payload/dist/notes.txt"],
                ReadPackage(project.Combine("dist/" + PackageName)).Files.Select(file => file.Path).Order(StringComparer.Ordinal));
        }

        // The project folder as the output folder: the package written beside extension.json is never copied back.
        using var flat = TestProject.Create(packConfig: """
            {
              "packVersion": 1,
              "readme": "PACKAGE-README.md",
              "payload": [ { "from": ".", "to": "source", "include": ["*"] } ]
            }
            """);
        Assert.Equal(0, (await ToolHarness.RunAsync(flat.Path, "pack", "-o", ".")).ExitCode);
        var again = await ToolHarness.RunAsync(flat.Path, "pack", "-o", ".", "--force");
        Assert.True(again.ExitCode == 0, again.ToString());
        Assert.Equal(
            ["README.md", "extension.json", "extension.package.json", "payload/source/PACKAGE-README.md", "payload/source/extension.json",
                "payload/source/extension.pack.json"],
            ReadPackage(flat.Combine(PackageName)).Files.Select(file => file.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ALinkTheGlobsCanReachIsRefusedEvenWhenTheyWouldFilterItsContents()
    {
        using var project = TestProject.Create(packConfig: """
            {
              "packVersion": 1,
              "readme": "PACKAGE-README.md",
              "payload": [
                { "from": "app", "to": "app", "include": ["main.js", "uxp/*.js"] },
                { "from": "site", "to": "site", "include": ["**/*.js"] }
              ]
            }
            """);
        using var outside = new TempFolder();
        outside.Write("uxp/panel.js", "// outside");
        outside.Write("docs/guide.md", "# Only Markdown, which the globs would filter out\n");
        project.Write("app/main.js", "// main");
        project.Write("site/index.js", "// index");
        CreateLink(project.Combine("app/uxp"), outside.Combine("uxp"));
        CreateLink(project.Combine("site/docs"), outside.Combine("docs"));
        var run = await ToolHarness.RunAsync(project.Path, "pack");
        Assert.Equal(1, run.ExitCode);
        Assert.Equal(
            ["app/uxp: error pack.link: A staged file or folder is a symbolic link or junction. []",
                "site/docs: error pack.link: A staged file or folder is a symbolic link or junction. []"],
            ErrorLines(run).Order(StringComparer.Ordinal));
        Assert.False(File.Exists(project.Combine("artifacts/" + PackageName)));
    }

    [FileLinkFact]
    public async Task AFileLinkIsRefusedWhenTheGlobsSelectIt()
    {
        using var project = TestProject.Create(packConfig: """
            {
              "packVersion": 1,
              "readme": "PACKAGE-README.md",
              "payload": [ { "from": "app", "to": "app", "include": ["*.js"] } ]
            }
            """);
        using var outside = new TempFolder();
        project.Write("app/main.js", "// main");
        File.CreateSymbolicLink(project.Combine("app/linked.js"), outside.Write("linked.js", "// outside"));
        File.CreateSymbolicLink(project.Combine("app/notes.md"), outside.Write("notes.md", "# Not selected\n"));
        var run = await ToolHarness.RunAsync(project.Path, "pack");
        Assert.Equal(1, run.ExitCode);
        Assert.Equal("app/linked.js: error pack.link: A staged file or folder is a symbolic link or junction. []", Assert.Single(ErrorLines(run)));
    }

    [Fact]
    public async Task ALinkTheGlobsCannotReachIsLeftOut()
    {
        // npm links a file: dependency into node_modules; a copy rule that never reaches node_modules packs.
        using var project = TestProject.Create(packConfig: """
            {
              "packVersion": 1,
              "readme": "PACKAGE-README.md",
              "payload": [
                { "from": "app", "to": "app", "include": ["main.js", "uxp/*.js"] },
                { "from": "web", "to": "web", "exclude": ["node_modules/**", "vendor/**"] }
              ]
            }
            """);
        using var outside = new TempFolder();
        outside.Write("sdk/index.js", "// linked package");
        project.Write("app/main.js", "// main");
        project.Write("app/uxp/panel.js", "// panel");
        project.Write("web/index.js", "// index");
        Directory.CreateDirectory(project.Combine("app/node_modules/@scope"));
        Directory.CreateDirectory(project.Combine("web/node_modules"));
        CreateLink(project.Combine("app/node_modules/@scope/sdk"), outside.Combine("sdk"));
        CreateLink(project.Combine("web/node_modules/sdk"), outside.Combine("sdk"));

        // vendor/** leaves out everything in vendor, so a link that is vendor itself is never reached either.
        CreateLink(project.Combine("web/vendor"), outside.Combine("sdk"));
        var run = await ToolHarness.RunAsync(project.Path, "pack");
        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(
            ["README.md", "extension.json", "extension.package.json", "payload/app/main.js", "payload/app/uxp/panel.js", "payload/web/index.js"],
            ReadPackage(project.Combine("artifacts/" + PackageName)).Files.Select(file => file.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AQuestionMarkInAGlobMatchesOneCharacter()
    {
        using var project = TestProject.Create(packConfig: """
            {
              "packVersion": 1,
              "readme": "PACKAGE-README.md",
              "payload": [
                { "from": ".", "to": "one", "include": ["src/a?.js"] },
                { "from": ".", "to": "single", "include": ["src/?.js"] },
                { "from": "src", "to": "two", "include": ["??.js"], "exclude": ["?2.js"] }
              ]
            }
            """);
        foreach (var name in new[] { "a1.js", "a2.js", "b.js", "ab1.js" })
        {
            project.Write("src/" + name, "// " + name);
        }

        var run = await ToolHarness.RunAsync(project.Path, "pack");
        Assert.True(run.ExitCode == 0, run.ToString());
        Assert.Equal(
            ["README.md", "extension.json", "extension.package.json", "payload/one/src/a1.js", "payload/one/src/a2.js", "payload/single/src/b.js",
                "payload/two/a1.js"],
            ReadPackage(project.Combine("artifacts/" + PackageName)).Files.Select(file => file.Path).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task AGlobThatLeavesItsFolderIsReportedAtItsPosition()
    {
        // FileSystemGlobbing threw on a .. after the first segment, which reached the user as tool.internal-error (exit 4).
        using var project = TestProject.Create(packConfig: """
            {
              "packVersion": 1,
              "readme": "PACKAGE-README.md",
              "payload": [
                { "from": "companion", "to": "companion", "include": ["a/../*.js", "*.cmd"], "exclude": ["vendor/../vendor/*.tgz"] },
                { "from": "companion", "to": "more", "include": ["../outside/*", ""] }
              ]
            }
            """);
        foreach (var json in new[] { false, true })
        {
            var run = await ToolHarness.RunAsync(project.Path, json ? ["pack", "--json"] : ["pack"]);
            Assert.True(run.ExitCode == 1, run.ToString());
            Assert.DoesNotContain("internal", run.Out + run.Error, StringComparison.OrdinalIgnoreCase);
            if (json)
            {
                using var document = JsonDocument.Parse(run.Out);
                Assert.Equal(
                    ["pack.config", "package.path", "package.path", "package.path", "package.path"],
                    document.RootElement.GetProperty("diagnostics").EnumerateArray().Select(diagnostic => diagnostic.GetProperty("code").GetString()));
                continue;
            }

            Assert.Equal(
                ["extension.pack.json(5,59): error package.path: This entry name breaks the path grammar. [/payload/0/include/0]",
                    "extension.pack.json(5,94): error package.path: This entry name breaks the path grammar. [/payload/0/exclude/0]",
                    "extension.pack.json(6,54): error package.path: This entry name breaks the path grammar. [/payload/1/include/0]",
                    "extension.pack.json(6,70): error package.path: This entry name breaks the path grammar. [/payload/1/include/1]",
                    "extension.pack.json: error pack.config: extension.pack.json is invalid; its own diagnostics follow. []"],
                ErrorLines(run).Order(StringComparer.Ordinal));
        }

        Assert.False(File.Exists(project.Combine("artifacts/" + PackageName)));
    }

    [Fact]
    public async Task TheOutputFolderIsKnownByWhatItIsNotHowItIsSpelled()
    {
        // holder/alias is a junction to holder/real; the project and -o name one folder in two spellings.
        using var holder = new TempFolder();
        holder.Write("real/p20/extension.json", TestProject.Manifest(ActiveHost.Id));
        holder.Write("real/p20/PACKAGE-README.md", "# Tool test\n");
        holder.Write("real/p20/extension.pack.json", """
            {
              "packVersion": 1,
              "readme": "PACKAGE-README.md",
              "payload": [ { "from": ".", "to": "c" } ]
            }
            """);
        CreateLink(holder.Combine("alias"), holder.Combine("real"));
        foreach (var (project, output) in new[] { ("alias/p20", "real/p20/out"), ("real/p20", "alias/p20/out2") })
        {
            string[] pack = ["pack", holder.Combine(project), "-o", holder.Combine(output)];
            Assert.Equal(0, (await ToolHarness.RunAsync(holder.Path, pack)).ExitCode);
            var packagePath = holder.Combine(output + "/" + PackageName);
            var first = File.ReadAllBytes(packagePath);

            var again = await ToolHarness.RunAsync(holder.Path, [.. pack, "--force"]);
            Assert.True(again.ExitCode == 0, again.ToString());
            Assert.Equal(first, File.ReadAllBytes(packagePath));
            Assert.Equal(
                ["README.md", "extension.json", "extension.package.json", "payload/c/PACKAGE-README.md", "payload/c/extension.json",
                    "payload/c/extension.pack.json"],
                ReadPackage(packagePath).Files.Select(file => file.Path).Order(StringComparer.Ordinal));

            // The next spelling packs the same project, where this output folder is an ordinary folder.
            Directory.Delete(holder.Combine(output), recursive: true);
        }
    }

    [Fact]
    public async Task AnOutputFolderThatIsALinkInACopiedFolderIsLeftOutNotRefused()
    {
        // artifacts/ redirected elsewhere by a junction (to a Dev Drive, say), under a copy rule of the whole project.
        string[] expected =
        [
            "README.md", "extension.json", "extension.package.json", "payload/src/PACKAGE-README.md", "payload/src/extension.json",
            "payload/src/extension.pack.json", "payload/src/strings/de-DE.json",
        ];
        const string WholeProject = """
            {
              "packVersion": 1,
              "readme": "PACKAGE-README.md",
              "payload": [ { "from": ".", "to": "src", "exclude": ["companion/**"] } ]
            }
            """;
        using var project = TestProject.Create(packConfig: WholeProject);
        using var elsewhere = new TempFolder();
        elsewhere.Write("out/notes.txt", "never packed: in the output folder");
        CreateLink(project.Combine("artifacts"), elsewhere.Combine("out"));
        var packagePath = project.Combine("artifacts/" + PackageName);
        byte[]? first = null;
        string[][] packs = [["pack"], ["pack", "--force"], ["pack", "-o", "artifacts", "--force"]];
        foreach (var pack in packs)
        {
            var run = await ToolHarness.RunAsync(project.Path, pack);
            Assert.True(run.ExitCode == 0, run.ToString());
            Assert.Equal(first ??= File.ReadAllBytes(packagePath), File.ReadAllBytes(packagePath));
            Assert.Equal(expected, ReadPackage(packagePath).Files.Select(file => file.Path).Order(StringComparer.Ordinal));
        }

        Assert.True(File.Exists(elsewhere.Combine("out/" + PackageName)));

        // Another link to an ordinary output folder is the output folder too, however the walk reaches it.
        using var aliased = TestProject.Create(packConfig: WholeProject);
        Directory.CreateDirectory(aliased.Combine("artifacts"));
        CreateLink(aliased.Combine("alias"), aliased.Combine("artifacts"));
        Assert.Equal(0, (await ToolHarness.RunAsync(aliased.Path, "pack")).ExitCode);
        var again = await ToolHarness.RunAsync(aliased.Path, "pack", "--force");
        Assert.True(again.ExitCode == 0, again.ToString());
        Assert.Equal(expected, ReadPackage(aliased.Combine("artifacts/" + PackageName)).Files.Select(file => file.Path).Order(StringComparer.Ordinal));

        // A link that only holds the output folder is not the output folder, and the link rule applies to it.
        using var holding = TestProject.Create(packConfig: WholeProject);
        using var outside = new TempFolder();
        Directory.CreateDirectory(outside.Combine("packages"));
        CreateLink(holding.Combine("out"), outside.Path);
        var refused = await ToolHarness.RunAsync(holding.Path, "pack", "-o", "out/packages");
        Assert.Equal(1, refused.ExitCode);
        Assert.Equal(["out: error pack.link: A staged file or folder is a symbolic link or junction. []"], ErrorLines(refused));
        Assert.False(File.Exists(outside.Combine("packages/" + PackageName)));
    }

    [Fact]
    public void AFinalPathSeesThroughJunctionsAndLetterCase()
    {
        using var holder = new TempFolder();
        Directory.CreateDirectory(holder.Combine("real/inner"));
        CreateLink(holder.Combine("alias"), holder.Combine("real"));
        var final = FinalPath.Of(holder.Combine("real/inner"));
        Assert.Equal(final, FinalPath.Of(holder.Combine("alias/inner")));
        Assert.Equal(final, FinalPath.Of(holder.Combine("alias/inner") + Path.DirectorySeparatorChar));
        Assert.Equal(final, FinalPath.Of(holder.Combine("alias/../real/inner")));
        Assert.EndsWith(Path.Combine("real", "inner"), final, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(final, FinalPath.Of(holder.Combine("ALIAS/INNER")));

        // An 8.3 short name, where the volume makes them.
        var longFolder = holder.Combine("real/a-folder-with-a-long-name");
        Directory.CreateDirectory(longFolder);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c for %I in (\"" + longFolder + "\") do @echo %~sI")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        })!;
        var shortFolder = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        if (shortFolder.Length > 0 && !string.Equals(shortFolder, longFolder, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Equal(FinalPath.Of(longFolder), FinalPath.Of(shortFolder));
        }
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
        // Names, whether they name a file or a folder.
        Assert.True(PackCommand.IsAlwaysExcluded("a/.git"));
        Assert.True(PackCommand.IsAlwaysExcluded(".GIT"));
        Assert.True(PackCommand.IsAlwaysExcluded("obj"));
        Assert.True(PackCommand.IsAlwaysExcluded("x/.vs"));
        Assert.True(PackCommand.IsAlwaysExcluded("lib/node_modules/.cache"));
        Assert.False(PackCommand.IsAlwaysExcluded("node_modules"));
        Assert.False(PackCommand.IsAlwaysExcluded(".cache"));
        Assert.True(PackCommand.IsAlwaysExcluded("x/My.csproj.user"));
        Assert.True(PackCommand.IsAlwaysExcluded("pairing.json"));
        Assert.True(PackCommand.IsAlwaysExcluded("a/example.x.pairing.json"));
        Assert.False(PackCommand.IsAlwaysExcluded("obj.txt"));
        Assert.False(PackCommand.IsAlwaysExcluded(".gitignore"));
        Assert.False(PackCommand.IsAlwaysExcluded(".github"));
        Assert.False(PackCommand.IsAlwaysExcluded("obj/pairing"));

        // Package file names, which are never staged directly from the output folder.
        Assert.True(PackCommand.IsPackageName("example.x-1.0.0" + ActiveHost.Extension));
        Assert.True(PackCommand.IsPackageName("OLD" + ActiveHost.Extension.ToUpperInvariant()));
        Assert.False(PackCommand.IsPackageName("notes.txt"));
        Assert.False(PackCommand.IsPackageName("example.x-1.0.0" + ActiveHost.Extension + ".txt"));
        Assert.True(PackCommand.IsPairingName("Example.X.Pairing.JSON"));
        Assert.True(PackCommand.IsPairingName("pairing.json"));
        Assert.False(PackCommand.IsPairingName("pairing.json.txt"));
        Assert.False(PackCommand.IsPairingName("my-pairing.json"));
        Assert.True(PackCommand.LooksLikePairing("{\"a\":{\"pipeName\":\"x\",\"secret\":\"y\"}}"u8.ToArray()));
        Assert.True(PackCommand.LooksLikePairing("{\"pipe\\u004eame\":\"x\",\"secret\":\"y\"}"u8.ToArray()));
        Assert.True(PackCommand.LooksLikePairing("not json \"pipeName\" \"secret\""u8.ToArray()));
        Assert.False(PackCommand.LooksLikePairing("{\"pipeName\":\"x\"}"u8.ToArray()));
    }

    /// <summary>A package the test wrote, read and verified for the active host.</summary>
    private static ExtensionPackage ReadPackage(string path)
    {
        var package = PackageReader.Read(File.ReadAllBytes(path), new PackageReadOptions { Manifest = new ManifestReadOptions { HostId = ActiveHost.Id } });
        Assert.True(package.Succeeded, string.Join('\n', package.Diagnostics));
        return package.Value;
    }

    private static string Text(PackageFile file)
    {
        using var reader = new StreamReader(file.OpenRead());
        return reader.ReadToEnd();
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

/// <summary>A test that creates a symbolic link to a file: skipped, with the reason, where the account cannot create one.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class FileLinkFactAttribute : FactAttribute
{
    private static readonly bool CanCreate = Probe();

    public FileLinkFactAttribute()
    {
        if (!CanCreate)
        {
            Skip = "Creating a symbolic link to a file needs Developer Mode or administrator rights on Windows.";
        }
    }

    private static bool Probe()
    {
        using var folder = new TempFolder("ventana-link-probe-");
        try
        {
            File.CreateSymbolicLink(folder.Combine("link.txt"), folder.Write("target.txt", "target"));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
