// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Text;
using System.Text.Json;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tool.Tests;

public sealed class VerifyTests
{
    [Fact]
    public async Task VerifyPrintsWhatThePackageContains()
    {
        using var project = TestProject.Create();
        Assert.Equal(0, (await ToolHarness.RunAsync(project.Path, "pack", "-o", ".")).ExitCode);
        var name = "example.tool-test-1.2.3" + ActiveHost.Extension;
        var text = await ToolHarness.RunAsync(project.Path, "verify", name);
        Assert.Equal(0, text.ExitCode);
        Golden.Match("verify-text", text, project.Path);
        var json = await ToolHarness.RunAsync(project.Path, "verify", name, "--json");
        Assert.Equal(0, json.ExitCode);
        Golden.Match("verify-json", json, project.Path);
    }

    [Fact]
    public async Task APackageNamedForAnotherHostIsPackageExtension()
    {
        using var project = TestProject.Create();
        Assert.Equal(0, (await ToolHarness.RunAsync(project.Path, "pack", "-o", ".")).ExitCode);
        File.Move(project.Combine("example.tool-test-1.2.3" + ActiveHost.Extension), project.Combine("renamed.zip"));
        var run = await ToolHarness.RunAsync(project.Path, "verify", "renamed.zip");
        Assert.Equal(1, run.ExitCode);
        Golden.Match("verify-extension", run, project.Path);
    }

    [Fact]
    public async Task AnArchiveDefectIsReportedWithTheEntry()
    {
        var run = await ToolHarness.RunAsync(Repository.Root, "verify", "fixtures/packages/manifest-invalid.zip", "--host", "example-host", "--json");
        Assert.Equal(1, run.ExitCode);
        Golden.Match("verify-entry-json", run, Repository.Root);
    }

    [Fact]
    public async Task AMissingPackageIsAnInputProblem()
    {
        using var folder = new TempFolder();
        Assert.Equal(3, (await ToolHarness.RunAsync(folder.Path, "verify", "missing" + ActiveHost.Extension)).ExitCode);
    }
}

public sealed class SchemaCommandTests
{
    [Theory]
    [InlineData("manifest", null)]
    [InlineData("strings", null)]
    [InlineData("package", "package.v2.json")]
    [InlineData("pairing", "pairing.v3.json")]
    [InlineData("pack", "pack.v1.json")]
    [InlineData("simulation", "simulation.v1.json")]
    public async Task SchemaWritesTheBundledCopy(string kind, string? neutral)
    {
        var relative = neutral ?? ActiveHost.Id + "/" + kind + ".v3.json";
        var expected = File.ReadAllText(Repository.PathOf("schemas/extensions/" + relative)).Replace("\r\n", "\n", StringComparison.Ordinal);
        var run = await ToolHarness.RunAsync(Repository.Root, "schema", "--kind", kind);
        Assert.Equal(0, run.ExitCode);
        Assert.Equal(expected, run.Out.Replace("\r\n", "\n", StringComparison.Ordinal));

        using var folder = new TempFolder();
        var written = await ToolHarness.RunAsync(folder.Path, "schema", "--kind", kind, "-o", ".schemas/copy.json");
        Assert.Equal(0, written.ExitCode);
        Assert.Equal(expected, File.ReadAllText(folder.Combine(".schemas/copy.json")).Replace("\r\n", "\n", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHostWithoutSchemasIsAUsageError()
    {
        var run = await ToolHarness.RunAsync(Repository.Root, "schema", "--host", "example-host");
        Assert.Equal(2, run.ExitCode);
        Assert.Equal(2, (await ToolHarness.RunAsync(Repository.Root, "schema", "--kind", "other")).ExitCode);
    }
}

public sealed class LinkTests
{
    private static string UniqueId() => "example.link-" + Guid.NewGuid().ToString("N")[..8];

    [Fact]
    public async Task WithoutAPairingLinkNamesTheHostAction()
    {
        using var project = new TempFolder();
        var id = UniqueId();
        project.Write("extension.json", TestProject.Manifest("example-host", id));
        var run = await ToolHarness.RunAsync(project.Path, "link", "--host", "example-host");
        Assert.Equal(1, run.ExitCode);
        Golden.Match("link-missing", Replace(run, id), project.Path);
    }

    [Fact]
    public async Task WithAValidPairingLinkSucceedsAndNeverPrintsTheSecret()
    {
        using var project = new TempFolder();
        var id = UniqueId();
        project.Write("extension.json", TestProject.Manifest("example-host", id));
        project.Write(id + ".pairing.json", TestProject.Pairing("example-host", id));
        var run = await ToolHarness.RunAsync(project.Path, "link", "--host", "example-host", "--json");
        Assert.Equal(0, run.ExitCode);
        Assert.DoesNotContain("AAECAwQF", run.Out + run.Error, StringComparison.Ordinal);
        using var json = JsonDocument.Parse(run.Out);
        Assert.Equal(id + ".pairing.json", json.RootElement.GetProperty("pairing").GetProperty("found").GetString());
        Golden.Match("link-found-json", Replace(run, id), project.Path);
    }

    [Fact]
    public async Task AnInvalidPairingIsReportedWithItsCode()
    {
        using var project = new TempFolder();
        var id = UniqueId();
        project.Write("extension.json", TestProject.Manifest("example-host", id));
        project.Write(id + ".pairing.json", TestProject.Pairing("example-host", "example.someone-else"));
        var run = await ToolHarness.RunAsync(project.Path, "link", "--host", "example-host");
        Assert.Equal(1, run.ExitCode);
        Golden.Match("link-invalid", Replace(run, id), project.Path);
    }

    private static ToolRun Replace(ToolRun run, string id) => new()
    {
        ExitCode = run.ExitCode,
        Out = run.Out.Replace(id, "{id}", StringComparison.Ordinal),
        Error = run.Error.Replace(id, "{id}", StringComparison.Ordinal),
    };
}

public sealed class ExitCodeTests
{
    [Fact]
    public async Task UsageErrorsAreTwo()
    {
        Assert.Equal(2, (await ToolHarness.RunAsync(Repository.Root, "validate", "--unknown")).ExitCode);
        Assert.Equal(2, (await ToolHarness.RunAsync(Repository.Root, "nothing")).ExitCode);
        Assert.Equal(2, (await ToolHarness.RunAsync(Repository.Root, "validate", "--host", "Not A Host")).ExitCode);
        Assert.Equal(2, (await ToolHarness.RunAsync(Repository.Root, "validate", "--", "extra")).ExitCode);
        Assert.Equal(2, (await ToolHarness.RunAsync(Repository.Root, "run")).ExitCode);
        Assert.Equal(2, (await ToolHarness.RunAsync(Repository.Root, "simulate")).ExitCode);
        var run = await ToolHarness.RunAsync(Repository.Root, "new", "action", "--extension-id", "Not_An_Id");
        Assert.Equal(2, run.ExitCode);
        Golden.Match("usage-extension-id", run, Repository.Root);
    }

    [Fact]
    public async Task HelpAndVersionSucceed()
    {
        var help = await ToolHarness.RunAsync(Repository.Root, "--help");
        Assert.Equal(0, help.ExitCode);
        Assert.Contains("simulate", help.Out, StringComparison.Ordinal);
        var version = await ToolHarness.RunAsync(Repository.Root, "--version");
        Assert.Equal(0, version.ExitCode);
        Assert.StartsWith(ToolIdentity.Version, version.Out, StringComparison.Ordinal);
        var validateHelp = await ToolHarness.RunAsync(Repository.Root, "validate", "--help");
        Assert.Equal(0, validateHelp.ExitCode);
        Golden.Match("help-validate", validateHelp, Repository.Root);
    }

    [Fact]
    public async Task AToolDefectIsToolInternalError()
    {
        var run = await ToolHarness.RunAsync(Repository.Root, ["validate", "fixtures/manifests/valid/countdown.json", "--json"], null,
            _ => throw new InvalidOperationException("injected"), default);
        Assert.Equal(4, run.ExitCode);
        Assert.DoesNotContain("injected", run.Out + run.Error, StringComparison.Ordinal);
        Golden.Match("internal-tool", run, Repository.Root);
    }

    [Fact]
    public async Task AReaderDefectIsJsonInternalError()
    {
        var run = await ToolHarness.RunAsync(Repository.Root, ["validate", "fixtures/manifests/valid/countdown.json"], null,
            _ => throw new ReaderDefectException(new FormatException("injected")), default);
        Assert.Equal(4, run.ExitCode);
        Golden.Match("internal-reader", run, Repository.Root);
    }

    [Fact]
    public async Task AProgramThatCannotStartIsFive()
    {
        using var folder = new TempFolder();
        Assert.Equal(5, (await ToolHarness.RunAsync(folder.Path, "run", "--", "ventana-no-such-program-" + Guid.NewGuid().ToString("N"))).ExitCode);
    }

    [Fact]
    public async Task NewWithoutBundledPackagesIsAnInputProblem()
    {
        // The tool under test runs from its build output, which carries no packages; only an installed tool does.
        using var folder = new TempFolder();
        var run = await ToolHarness.RunAsync(folder.Path, "new", "action", "-n", "Example", "--feed", "feed");
        Assert.Equal(3, run.ExitCode);
        Golden.Match("new-no-bundle", run, folder.Path);
        Assert.False(Directory.Exists(folder.Combine("Example")));
    }
}

public sealed class TestAndRunTests
{
    private static string TestAssembly => typeof(TestAndRunTests).Assembly.Location;

    [Fact]
    public async Task TestStopsAtAnInvalidManifest()
    {
        using var project = new TempFolder();
        project.Write("extension.json", """{ "schemaVersion": 3 }""");
        project.Write("package.json", """{ "scripts": { "test": "node -e \"process.exit(0)\"" } }""");
        var run = await ToolHarness.RunAsync(project.Path, "test");
        Assert.Equal(1, run.ExitCode);
        Golden.Match("test-invalid-manifest", run, project.Path);
    }

    [Fact]
    public async Task TestRunsNpmTestForANodeProject()
    {
        if (ChildProcess.ResolveProgram("npm") == "npm" && OperatingSystem.IsWindows())
        {
            return; // npm is not installed here; the template tests cover the .NET path.
        }

        using var project = new TempFolder();
        project.Write("extension.json", TestProject.Manifest("example-host"));
        project.Write("package.json", """{ "name": "t", "private": true, "scripts": { "test": "node -e \"process.exit(process.argv.includes('--fail') ? 3 : 0)\" --" } }""");
        Assert.Equal(0, (await ToolHarness.RunAsync(project.Path, "test", "--host", "example-host")).ExitCode);
        Assert.Equal(1, (await ToolHarness.RunAsync(project.Path, "test", "--host", "example-host", "--", "--fail")).ExitCode);
    }

    [Fact]
    public async Task RunReturnsTheCompanionsExitCode()
    {
        using var folder = new TempFolder();
        var run = await ToolHarness.RunAsync(folder.Path, "run", "--", "dotnet", TestAssembly, "--exit", "3");
        Assert.Equal(3, run.ExitCode);
    }

    [Fact]
    public async Task RunWatchRestartsWhenASourceChanges()
    {
        using var folder = new TempFolder();
        folder.Write("extension.json", TestProject.Manifest("example-host"));
        var source = folder.Write("Handler.cs", "// one");
        var output = new StringWriter();
        var error = new StringWriter();
        using var stop = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var console = new ToolConsole { Out = output, Error = error, In = new StringReader(string.Empty), WorkingDirectory = folder.Path };
        var running = RunCommand.RunAsync(console, ["dotnet", TestAssembly, "--sleep"], watch: true, TimeSpan.FromMilliseconds(200), stop.Token);

        await WaitForAsync(() => Count(ToolHarness.Read(output), "companion started") == 1, stop.Token);
        File.WriteAllText(source, "// two, longer");
        await WaitForAsync(() => Count(ToolHarness.Read(output), "companion started") == 2, stop.Token);
        Assert.Contains("restarting: Handler.cs changed", ToolHarness.Read(error), StringComparison.Ordinal);

        await stop.CancelAsync();
        Assert.Equal(0, await running);
    }

    private static int Count(string text, string value) => (text.Length - text.Replace(value, string.Empty, StringComparison.Ordinal).Length) / value.Length;

    internal static async Task WaitForAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        while (!condition())
        {
            await Task.Delay(100, cancellationToken);
        }
    }
}

public sealed class PackSchemaAgreementTests
{
    public static TheoryData<string> PackConfigurations() =>
    [
        """{ "packVersion": 1, "readme": "README.md" }""",
        """{ "$schema": ".schemas/pack.v1.json", "packVersion": 1, "readme": "r.md", "strings": "strings", "build": { "project": "p.csproj", "runtime": "win-arm64", "configuration": "Debug", "to": "bin" }, "payload": [ { "from": "a", "to": "", "include": ["**/*.dll"], "exclude": [] } ] }""",
        """{ "packVersion": 1 }""",
        """{ "packVersion": 1, "readme": "r.md", "extra": 1 }""",
        """{ "packVersion": 1, "readme": 3 }""",
        """{ "packVersion": 1, "readme": "r.md", "build": {} }""",
        """{ "packVersion": 1, "readme": "r.md", "payload": [ { "from": "a" } ] }""",
        """{ "packVersion": 1, "readme": "r.md", "payload": [ { "from": "a", "to": "b", "include": "x" } ] }""",
        """[]""",
    ];

    [Theory]
    [MemberData(nameof(PackConfigurations))]
    public void TheSchemaIsNeverStricterThanThePackReader(string text)
    {
        var schema = SchemaFiles.Load("pack.v1.json");
        using var document = JsonDocument.Parse(text);
        var schemaValid = schema.Evaluate(document.RootElement).IsValid;
        var readerValid = PackConfigReader.Read(Encoding.UTF8.GetBytes(text)).Succeeded;
        Assert.Equal(readerValid, schemaValid);
    }

    [Theory]
    [InlineData("templates/content/action-csharp/extension.pack.json")]
    [InlineData("templates/content/widget-csharp/extension.pack.json")]
    [InlineData("templates/content/node/extension.pack.json")]
    public void TheTemplatesPackConfigurationsAreValid(string relative)
    {
        var bytes = File.ReadAllBytes(Repository.PathOf(relative));
        var read = PackConfigReader.Read(bytes);
        Assert.True(read.Succeeded, string.Join("\n", read.Diagnostics));
        var schema = SchemaFiles.Load("pack.v1.json");
        using var document = JsonDocument.Parse(bytes);
        Assert.True(schema.Evaluate(document.RootElement).IsValid);
    }

    public static TheoryData<string, bool> Scripts() => new()
    {
        { """[ { "start": "example.sim/widget", "settings": { "mode": "fail" }, "as": "s1" }, { "invoke": "s1", "expect": "Failed", "failure": "Network", "as": "r1", "wait": false }, { "cancel": "r1" }, { "expectFace": "s1", "within": "500ms", "line1": "1", "state": "On" }, { "stop": "s1" }, { "disconnect": "host.reloaded" }, { "wait": "1s" } ]""", true },
        { """[ { "start": "a", "invoke": "b" } ]""", false },
        { """[ { "invoke": "s1", "expect": "Maybe" } ]""", false },
        { """[ { "wait": "soon" } ]""", false },
        { """[ { "disconnect": "Not.A.Code" } ]""", false },
        { """[ { "start": "a", "settings": { "mode": 1 } } ]""", false },
        { """[ {} ]""", false },
        { """{}""", false },
    };

    [Theory]
    [MemberData(nameof(Scripts))]
    public void TheSimulationSchemaAgreesWithTheScriptReader(string text, bool valid)
    {
        var schema = SchemaFiles.Load("simulation.v1.json");
        using var document = JsonDocument.Parse(text);
        Assert.Equal(valid, schema.Evaluate(document.RootElement).IsValid);
        Assert.Equal(valid, SimulationScriptReader.Read(Encoding.UTF8.GetBytes(text)).Succeeded);
    }
}

/// <summary>The repository's JSON Schemas, loaded once each (the schema registry refuses a second registration of an id).</summary>
internal static class SchemaFiles
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Json.Schema.JsonSchema> Loaded = new(StringComparer.Ordinal);

    public static Json.Schema.JsonSchema Load(string relative) => Loaded.GetOrAdd(relative, path =>
        Json.Schema.JsonSchema.FromText(File.ReadAllText(Repository.PathOf("schemas/extensions/" + path)),
            new Json.Schema.BuildOptions { SchemaRegistry = new Json.Schema.SchemaRegistry() }));
}
