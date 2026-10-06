// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace VentanaTools.Orbit.Extensions.Tool.Tests;

/// <summary>
/// Packs the author, Testing and Templates packages (and the Node SDK tarball when the Node SDK
/// exists) with a fresh version, packs and installs the tool that carries them with
/// <c>--tool-path</c>, and installs the template pack into a custom hive. Everything lives under
/// <c>artifacts/</c> in the repository, and every restore uses <c>artifacts/nuget-packages</c>, so
/// nothing reaches the person's tools, templates or package cache.
/// </summary>
public sealed class TemplateFeed : IDisposable
{
    public TemplateFeed()
    {
        Version = "0.1.0-dev." + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var run = "run-" + Guid.NewGuid().ToString("N")[..8];
        Work = Path.Combine(Repository.Root, "artifacts", "template-tests", run);
        Packages = Path.Combine(Work, "packages");
        Feed = Path.Combine(Repository.Root, "artifacts", "feed", run);
        Hive = Path.Combine(Repository.Root, "artifacts", "template-hive", run);
        Tools = Path.Combine(Work, "tools");
        NuGetPackages = Path.Combine(Repository.Root, "artifacts", "nuget-packages");
        Projects = Path.Combine(Path.GetTempPath(), "ventana-template-tests", run);
        Directory.CreateDirectory(Projects);

        var build = Path.Combine(Work, "build");
        foreach (var project in new[]
                 {
                     "src/VentanaTools.Orbit.Extensions/VentanaTools.Orbit.Extensions.csproj",
                     "src/VentanaTools.Orbit.Extensions.Testing/VentanaTools.Orbit.Extensions.Testing.csproj",
                     "templates/VentanaTools.Orbit.Extensions.Templates.csproj",
                 })
        {
            Run("dotnet", ["pack", Repository.PathOf(project), "-c", "Release", "-p:VentanaExtensionsVersion=" + Version, "-o", Packages,
                "--artifacts-path", build, "-nologo"], Repository.Root);
        }

        NodeSdk = NodeSdkFactAttribute.Folder is not null ? PackNode() : null;

        Run("dotnet", ["pack", Repository.PathOf("src/VentanaTools.Orbit.Extensions.Tool/VentanaTools.Orbit.Extensions.Tool.csproj"), "-c", "Release",
            "-p:VentanaExtensionsVersion=" + Version, "-p:VentanaBundleDirectory=" + Packages, "-o", Packages, "--artifacts-path", build, "-nologo"],
            Repository.Root);
        // The repository's NuGet.config maps package sources, which `dotnet tool install --add-source` refuses to
        // combine with; a configuration file of its own restores the tool from the packages just made.
        var config = Path.Combine(Work, "nuget.config");
        File.WriteAllText(config, "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<configuration>\n  <packageSources>\n    <clear />\n"
            + "    <add key=\"packages\" value=\"" + Packages + "\" />\n  </packageSources>\n</configuration>\n");
        Run("dotnet", ["tool", "install", ToolIdentity.ToolPackageId, "--tool-path", Tools, "--configfile", config, "--version", Version], Work);
        Run("dotnet", ["new", "install", Path.Combine(Packages, ToolIdentity.TemplatesPackageId + "." + Version + ".nupkg"), "--debug:custom-hive", Hive],
            Work);
    }

    public string Version { get; }

    public string Work { get; }

    public string Packages { get; }

    public string Feed { get; }

    public string Hive { get; }

    public string Tools { get; }

    public string NuGetPackages { get; }

    public string Projects { get; }

    /// <summary>The Node SDK folder, when the repository has it and npm is available; null otherwise.</summary>
    public string? NodeSdk { get; }

    /// <summary>The installed tool.</summary>
    public string Tool => Path.Combine(Tools, ToolIdentity.CommandName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));

    public void Dispose()
    {
        foreach (var folder in new[] { Projects, Work, Feed, Hive })
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // Build servers may still hold files; the temporary folder is cleaned up later.
            }
        }
    }

    /// <summary>Runs a program with the isolated package folder; throws with its output when it fails.</summary>
    public string Run(string program, IEnumerable<string> arguments, string workingDirectory, int expectedExitCode = 0)
    {
        var info = new ProcessStartInfo(ChildProcess.ResolveProgram(program))
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["NUGET_PACKAGES"] = NuGetPackages;

        // No build servers outlive the test: no MSBuild node reuse and no shared compiler server.
        info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        info.Environment["UseSharedCompilation"] = "false";
        info.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_NOLOGO"] = "1";
        using var process = Process.Start(info)!;
        var output = new StringBuilder();
        process.OutputDataReceived += (_, line) => { lock (output) { output.AppendLine(line.Data); } };
        process.ErrorDataReceived += (_, line) => { lock (output) { output.AppendLine(line.Data); } };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(TimeSpan.FromMinutes(10)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException(program + " " + string.Join(' ', arguments) + " did not finish.");
        }

        process.WaitForExit();
        var text = output.ToString();
        if (process.ExitCode != expectedExitCode)
        {
            throw new InvalidOperationException(program + " " + string.Join(' ', info.ArgumentList) + " exited " + process.ExitCode + ":\n" + text);
        }

        return text;
    }

    private string? PackNode()
    {
        var folder = NodeSdkFactAttribute.Folder!;

        Run("npm", ["pack", "--pack-destination", Packages], folder);
        var tarball = Directory.EnumerateFiles(Packages, "*.tgz").Single();
        var expected = Path.Combine(Packages, ToolIdentity.NodeTarballName.Replace(ToolIdentity.Version, Version, StringComparison.Ordinal));
        if (!string.Equals(tarball, expected, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(tarball, expected);
        }

        return folder;
    }
}

/// <summary>
/// Template tests (S4a.6): each template is created with <c>orbit-ext new … --feed</c> (never a
/// package source by hand), then its local tool is restored and the project is built, tested and
/// tested again through the tool's <c>test</c> command.
/// </summary>
[Trait("Platform", "Windows")]
public sealed class TemplateTests(TemplateFeed feed, ITestOutputHelper output) : IClassFixture<TemplateFeed>
{
    [WindowsTheory]
    [InlineData("action", "contoso.greeter")]
    [InlineData("widget", "contoso.tally")]
    public void ADotnetTemplateBuildsTestsAndPacks(string kind, string extensionId)
    {
        var name = "My " + kind;
        var folder = Path.Combine(feed.Projects, kind);
        output.WriteLine(feed.Run(feed.Tool, ["new", kind, "-n", name, "-o", folder, "--extension-id", extensionId, "--feed", feed.Feed,
            "--debug:custom-hive", feed.Hive], feed.Projects));

        Assert.True(File.Exists(Path.Combine(feed.Feed, ToolIdentity.ToolPackageId + "." + feed.Version + ".nupkg"))
            || Directory.EnumerateFiles(feed.Feed, "*.nupkg").Any(file => Path.GetFileName(file)
                .Equals(ToolIdentity.ToolPackageId + "." + feed.Version + ".nupkg", StringComparison.OrdinalIgnoreCase)),
            "new copies the tool's own package into the feed.");
        var manifest = File.ReadAllText(Path.Combine(folder, "extension.json"));
        Assert.Contains("\"id\": \"" + extensionId + "\"", manifest, StringComparison.Ordinal);
        Assert.Contains("\"$schema\": \".schemas/manifest.v3.json\"", manifest, StringComparison.Ordinal);
        Assert.Contains(feed.Feed, File.ReadAllText(Path.Combine(folder, "nuget.config")), StringComparison.Ordinal);
        Assert.Contains(feed.Version, File.ReadAllText(Path.Combine(folder, ".config", "dotnet-tools.json")), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(folder, "Properties", "launchSettings.json")));
        Assert.Contains("*.pairing.json", File.ReadAllText(Path.Combine(folder, ".gitignore")), StringComparison.Ordinal);

        output.WriteLine(feed.Run("dotnet", ["tool", "restore"], folder));
        output.WriteLine(feed.Run("dotnet", ["build", "-nologo", "-warnaserror"], folder));
        output.WriteLine(feed.Run("dotnet", ["test", "-nologo", "--no-build"], folder));
        var tested = feed.Run("dotnet", ["tool", "run", ToolIdentity.CommandName, "test"], folder);
        output.WriteLine(tested);
        Assert.Contains(ToolIdentity.CommandName + ": 0 errors", tested, StringComparison.Ordinal);

        var packed = feed.Run("dotnet", ["tool", "run", ToolIdentity.CommandName, "pack", "--json"], folder);
        output.WriteLine(packed);
        var package = Directory.EnumerateFiles(Path.Combine(folder, "artifacts")).Single();
        output.WriteLine(feed.Run(feed.Tool, ["verify", package], folder));
    }

    [WindowsFact]
    public void TheNoTestsOptionLeavesOutTheTestProject()
    {
        var folder = Path.Combine(feed.Projects, "no-tests");
        feed.Run(feed.Tool, ["new", "action", "-n", "Plain", "-o", folder, "--no-tests", "--feed", feed.Feed, "--debug:custom-hive", feed.Hive], feed.Projects);
        Assert.False(Directory.Exists(Path.Combine(folder, "tests")));
        Assert.DoesNotContain("Tests", File.ReadAllText(Path.Combine(folder, "Plain.slnx")), StringComparison.Ordinal);
        output.WriteLine(feed.Run("dotnet", ["build", "-nologo", "-warnaserror"], folder));
    }

    [WindowsFact]
    public void AnInvalidExtensionIdIsRefusedBeforeAnythingIsCreated()
    {
        var folder = Path.Combine(feed.Projects, "invalid-id");
        var text = feed.Run(feed.Tool, ["new", "action", "-n", "Bad", "-o", folder, "--extension-id", "ventana-Bad", "--feed", feed.Feed,
            "--debug:custom-hive", feed.Hive], feed.Projects, expectedExitCode: 2);
        Assert.Contains("id.grammar", text, StringComparison.Ordinal);
        Assert.False(Directory.Exists(folder));
    }

    [WindowsFact]
    public void AMissingTemplatePackPrintsItsInstallCommand()
    {
        var hive = Path.Combine(feed.Work, "empty-hive");
        var text = feed.Run(feed.Tool, ["new", "widget", "-n", "Lost", "--feed", feed.Feed, "--debug:custom-hive", hive], feed.Projects, expectedExitCode: 3);
        Assert.Contains("dotnet new install \"" + Path.Combine(feed.Feed, ToolIdentity.TemplatesPackageId + "." + feed.Version + ".nupkg") + "\"",
            text, StringComparison.Ordinal);
    }

    [NodeSdkFact]
    public void TheNodeTemplateInstallsAndTests()
    {
        Assert.NotNull(feed.NodeSdk);
        var folder = Path.Combine(feed.Projects, "node");
        feed.Run(feed.Tool, ["new", "node", "-n", "Node greeter", "-o", folder, "--feed", feed.Feed, "--debug:custom-hive", feed.Hive], feed.Projects);
        Assert.True(File.Exists(Path.Combine(folder, "vendor", ToolIdentity.NodeTarballName.Replace(ToolIdentity.Version, feed.Version, StringComparison.Ordinal))));
        output.WriteLine(feed.Run("npm", ["install", "--no-audit", "--no-fund"], folder));
        output.WriteLine(feed.Run("dotnet", ["tool", "restore"], folder));
        output.WriteLine(feed.Run("dotnet", ["tool", "run", ToolIdentity.CommandName, "test"], folder));
    }
}

/// <summary>A test that needs the Node SDK: skipped, with the reason, until the repository has it and npm is installed.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class NodeSdkFactAttribute : FactAttribute
{
    public NodeSdkFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = WindowsFactAttribute.Reason;
        }
        else if (Folder is null)
        {
            Skip = "The Node SDK (a node/<package>/package.json) is not in this repository yet, or npm is not installed.";
        }
    }

    /// <summary>The Node SDK's folder, or null when it or npm is missing.</summary>
    public static string? Folder { get; } = Find();

    private static string? Find()
    {
        var node = Repository.PathOf("node");
        if (!Directory.Exists(node) || (OperatingSystem.IsWindows() && ChildProcess.ResolveProgram("npm") == "npm"))
        {
            return null;
        }

        return Directory.EnumerateDirectories(node).FirstOrDefault(path => File.Exists(Path.Combine(path, "package.json")));
    }
}

/// <summary>A test of Windows behaviour (named pipes, net10.0-windows companions): skipped elsewhere, with the reason.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsFactAttribute : FactAttribute
{
    public const string Reason = "Windows only: companions use Windows named pipes and target net10.0-windows.";

    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = Reason;
        }
    }
}

/// <summary>A theory of Windows behaviour: skipped elsewhere, with the reason.</summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = WindowsFactAttribute.Reason;
        }
    }
}
