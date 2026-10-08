// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tool.Tests;

/// <summary>The repository the tests run in: the nearest folder above the test binaries that holds <c>fixtures/hosts.json</c>.</summary>
internal static class Repository
{
    private static readonly Lazy<string> RootPath = new(() =>
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(Path.Combine(folder.FullName, "fixtures", "hosts.json")))
            {
                return folder.FullName;
            }
        }

        throw new InvalidOperationException("The repository root was not found above the test binaries.");
    });

    public static string Root => RootPath.Value;

    public static string PathOf(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Whether maintainers asked the tests to rewrite golden files instead of comparing them.</summary>
    public static bool Update => Environment.GetEnvironmentVariable("VENTANA_UPDATE_FIXTURES") == "1";
}

/// <summary>The active host of the registry: tests never name a product, so they read it here.</summary>
internal static class ActiveHost
{
    public static HostInfo Info { get; } = HostRegistry.Known.First(host => host.Status == HostStatus.Active);

    public static string Id => Info.Id;

    public static string Extension => Info.PackageExtension!;
}

/// <summary>A folder under the temporary folder, deleted when disposed.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder(string prefix = "ventana-tool-test-")
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(string relative) => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public string Write(string relative, string text)
    {
        var path = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.Replace("\r\n", "\n", StringComparison.Ordinal), new UTF8Encoding(false));
        return path;
    }

    public string Write(string relative, byte[] bytes)
    {
        var path = Combine(relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    foreach (var file in Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories))
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                    }

                    Directory.Delete(Path, recursive: true);
                }

                return;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }
    }
}

/// <summary>What one in-process run of the tool printed and returned.</summary>
internal sealed class ToolRun
{
    public required int ExitCode { get; init; }

    public required string Out { get; init; }

    public required string Error { get; init; }

    public override string ToString() => "exit " + ExitCode + "\n--- out\n" + Out + "\n--- error\n" + Error;
}

/// <summary>Runs the tool in this process, as <c>Program</c> does, with captured output.</summary>
internal static class ToolHarness
{
    public static Task<ToolRun> RunAsync(string workingDirectory, params string[] args) => RunAsync(workingDirectory, args, null, null, default);

    public static async Task<ToolRun> RunAsync(string workingDirectory, string[] args, string? input, Action<string>? fault,
        CancellationToken cancellationToken)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var console = new ToolConsole
        {
            Out = output,
            Error = error,
            In = new StringReader(input ?? string.Empty),
            WorkingDirectory = workingDirectory,
            FaultInjection = fault,
        };
        var exitCode = await ToolApplication.RunAsync(args, console, cancellationToken);
        return new ToolRun { ExitCode = exitCode, Out = Read(output), Error = Read(error) };
    }

    /// <summary>A writer's text so far, safely while another thread may still write to it.</summary>
    public static string Read(StringWriter writer)
    {
        lock (writer)
        {
            return writer.ToString();
        }
    }

    /// <summary>
    /// Output with everything that depends on the machine or the build replaced: paths, the tool's
    /// command name and version, the active host's id and package file extension, hashes and sizes.
    /// </summary>
    public static string Normalize(string text, params string[] folders)
    {
        // Separators first, so one golden text serves Windows and other systems: JSON-escaped
        // backslashes, then plain ones. Golden texts therefore never contain a backslash.
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\\\\", "/", StringComparison.Ordinal)
            .Replace('\\', '/');
        foreach (var folder in folders.Where(folder => folder.Length > 0).OrderByDescending(folder => folder.Length))
        {
            normalized = normalized.Replace(folder.Replace('\\', '/'), "{temp}", StringComparison.OrdinalIgnoreCase);
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).Replace('\\', '/');
        normalized = normalized.Replace(profile, "{profile}", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(ToolIdentity.CommandName, "{tool}", StringComparison.Ordinal)
            .Replace("\"" + ToolIdentity.Version + "\"", "\"{version}\"", StringComparison.Ordinal)
            .Replace(ActiveHost.Extension, "{ext}", StringComparison.Ordinal)
            .Replace("hosts: " + ActiveHost.Id, "hosts: {host}", StringComparison.Ordinal)
            .Replace("\"" + ActiveHost.Id + "\"", "\"{host}\"", StringComparison.Ordinal)
            .Replace("host " + ActiveHost.Id + ")", "host {host})", StringComparison.Ordinal)
            .Replace(ActiveHost.Info.DisplayName + ",", "{host name},", StringComparison.Ordinal)
            .Replace("/" + ActiveHost.Id + "/", "/{host}/", StringComparison.Ordinal);
        normalized = Regex.Replace(normalized, "\\b[0-9a-f]{64}\\b", "{sha256}");
        normalized = Regex.Replace(normalized, "files: (\\d+), \\d+ bytes", "files: $1, {bytes} bytes");
        normalized = Regex.Replace(normalized, "\"bytes\": \\d+", "\"bytes\": {bytes}");
        return normalized;
    }
}

/// <summary>Golden outputs in <c>Golden/</c>: compared after <see cref="ToolHarness.Normalize"/>, rewritten with VENTANA_UPDATE_FIXTURES=1.</summary>
internal static class Golden
{
    public static void Match(string name, string actual)
    {
        var path = Path.Combine(Repository.Root, "tests", typeof(Golden).Assembly.GetName().Name!, "Golden", name + ".txt");
        if (Repository.Update)
        {
            File.WriteAllText(path, actual, new UTF8Encoding(false));
            return;
        }

        Assert.True(File.Exists(path), "Missing golden file " + name + ".txt; run the tests with VENTANA_UPDATE_FIXTURES=1 to write it.");
        Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal), actual);
    }

    /// <summary>A run's exit code, output and error output as one golden text.</summary>
    public static void Match(string name, ToolRun run, params string[] folders) =>
        Match(name, "exit " + run.ExitCode + "\n--- out\n" + ToolHarness.Normalize(run.Out, folders) + "--- error\n" + ToolHarness.Normalize(run.Error, folders));
}
