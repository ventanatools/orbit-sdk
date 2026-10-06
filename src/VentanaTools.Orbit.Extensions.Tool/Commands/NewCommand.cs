// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>The options of <c>new</c>, passed on to <c>dotnet new</c>.</summary>
internal sealed class NewOptions
{
    public string? Name { get; init; }

    public string? Output { get; init; }

    public string? ExtensionId { get; init; }

    public string? Host { get; init; }

    public string? DisplayName { get; init; }

    public bool NoTests { get; init; }

    public string? Feed { get; init; }

    /// <summary><c>--debug:custom-hive</c>, passed to <c>dotnet new</c> so tests never touch the person's template cache.</summary>
    public string? TemplateHive { get; init; }
}

/// <summary>
/// The per-user, per-version package feed of contract §11.1: the packages the tool carries, and the
/// tool's own package from its install folder, so a new project and its local tool manifest restore
/// before anything is published.
/// </summary>
internal static class Feed
{
    /// <summary>The folder inside the tool where the bundled packages live.</summary>
    public static string BundleFolder => Path.Combine(AppContext.BaseDirectory, "packages");

    /// <summary><c>%LOCALAPPDATA%\VentanaTools\packages\&lt;version&gt;\</c>.</summary>
    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VentanaTools", "packages", ToolIdentity.Version);

    /// <summary>The bundled packages a project cannot do without.</summary>
    public static IEnumerable<string> RequiredPackages =>
        new[] { ToolIdentity.AuthorPackageId, ToolIdentity.TestingPackageId, ToolIdentity.TemplatesPackageId }
            .Select(id => id + "." + ToolIdentity.Version + ".nupkg");

    /// <summary>The template pack's file in a feed.</summary>
    public static string TemplatesPackage(string feed) => Path.Combine(feed, ToolIdentity.TemplatesPackageId + "." + ToolIdentity.Version + ".nupkg");

    /// <summary>
    /// Copies the bundled packages and the tool's own package into <paramref name="feed"/>. Returns
    /// false when this build of the tool carries none of them (a build that was not installed from its package).
    /// </summary>
    public static bool Prepare(string feed, ToolConsole console)
    {
        var bundle = BundleFolder;
        if (!RequiredPackages.All(name => File.Exists(Path.Combine(bundle, name))))
        {
            return false;
        }

        Directory.CreateDirectory(feed);
        foreach (var file in Directory.EnumerateFiles(bundle).Where(file => file.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase)
                     || file.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase)))
        {
            Copy(file, Path.Combine(feed, Path.GetFileName(file)));
        }

        if (OwnPackage() is { } own)
        {
            Copy(own, Path.Combine(feed, Path.GetFileName(own)));
        }
        else
        {
            console.Note("the tool's own package was not found beside it, so the project's local tool manifest will not restore from "
                + console.Display(feed) + ". Install the tool from its package to fix this.");
        }

        return true;
    }

    /// <summary>
    /// The tool's own <c>.nupkg</c> in its install folder: NuGet extracts a tool to
    /// <c>&lt;id&gt;/&lt;version&gt;/tools/&lt;framework&gt;/any/</c> and keeps the package file at <c>&lt;id&gt;/&lt;version&gt;/</c>.
    /// </summary>
    public static string? OwnPackage()
    {
        // Path.GetDirectoryName rather than "..": a long base directory arrives with a \\?\ prefix, which is never normalized.
        string? root = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        for (var level = 0; level < 3 && root is not null; level++)
        {
            root = Path.GetDirectoryName(root);
        }

        if (root is null || !Directory.Exists(root))
        {
            return null;
        }

        var name = ToolIdentity.ToolPackageId + "." + ToolIdentity.Version + ".nupkg";
        return Directory.EnumerateFiles(root, "*.nupkg").FirstOrDefault(file => Path.GetFileName(file).Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    private static void Copy(string source, string destination)
    {
        if (File.Exists(destination) && new FileInfo(destination).Length == new FileInfo(source).Length
            && File.ReadAllBytes(destination).AsSpan().SequenceEqual(File.ReadAllBytes(source)))
        {
            return;
        }

        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.Copy(source, temporary);
        File.Move(temporary, destination, overwrite: true);
    }
}

/// <summary>
/// <c>new &lt;action|widget|node&gt; [-n &lt;name&gt;] [-o &lt;dir&gt;] [--extension-id &lt;id&gt;] [--host &lt;id&gt;] [--feed &lt;dir&gt;]</c>:
/// prepares the feed, then runs <c>dotnet new &lt;command&gt;-&lt;kind&gt;</c> with the same arguments
/// plus <c>--package-source &lt;feed&gt;</c> (contract §11.1).
/// </summary>
internal static class NewCommand
{
    public static IReadOnlyList<string> Kinds { get; } = ["action", "widget", "node"];

    public static async Task<int> RunAsync(ToolConsole console, string kind, NewOptions options, CancellationToken cancellationToken)
    {
        if (options.ExtensionId is { } id && (ExtensionIds.Classify(id, IdOrigin.ThirdParty) ?? (id.Contains('/', StringComparison.Ordinal) ? DiagnosticCodes.IdGrammar : null)) is { } code)
        {
            console.Fail(code + ": " + DiagnosticCatalog.Find(code)!.Message + " [--extension-id]");
            return ExitCodes.Usage;
        }

        if (kind == "node" && options.NoTests)
        {
            console.Fail("--no-tests applies to the .NET templates only.");
            return ExitCodes.Usage;
        }

        var feed = console.FullPath(options.Feed ?? Feed.DefaultFolder);
        try
        {
            if (!Feed.Prepare(feed, console))
            {
                console.Fail("This copy of the tool carries none of the packages a new project needs. Install the tool from its package.");
                return ExitCodes.InputOutput;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            console.Fail("Cannot prepare the package feed " + console.Display(feed) + ".");
            return ExitCodes.InputOutput;
        }

        var tarball = Path.Combine(feed, ToolIdentity.NodeTarballName);
        if (kind == "node" && !File.Exists(tarball))
        {
            console.Fail("The Node SDK tarball " + ToolIdentity.NodeTarballName + " is not in " + console.Display(feed) + ".");
            return ExitCodes.InputOutput;
        }

        var shortName = ToolIdentity.TemplateShortName(kind);
        var hive = options.TemplateHive is { } h ? new[] { "--debug:custom-hive", console.FullPath(h) } : [];
        int listed;
        try
        {
            listed = await ChildProcess.RunAsync("dotnet", ["new", "list", shortName, .. hive], console.WorkingDirectory, TextWriter.Null, TextWriter.Null,
                cancellationToken).ConfigureAwait(false);
        }
        catch (ProcessStartException)
        {
            console.Fail("dotnet could not be started.");
            return ExitCodes.ProcessStart;
        }

        if (listed != 0)
        {
            console.Fail("The template " + shortName + " is not installed. Install the template pack from the feed, then run this command again:");
            console.Error.WriteLine("  dotnet new install \"" + Feed.TemplatesPackage(feed) + "\"" + (hive.Length > 0 ? " --debug:custom-hive \"" + hive[1] + "\"" : string.Empty));
            return ExitCodes.InputOutput;
        }

        var arguments = new List<string> { "new", shortName };
        Add(arguments, "-n", options.Name);
        Add(arguments, "-o", options.Output);
        Add(arguments, "--extension-id", options.ExtensionId);
        Add(arguments, "--host", options.Host);
        Add(arguments, "--display-name", options.DisplayName);
        if (options.NoTests)
        {
            arguments.Add("--no-tests");
        }

        arguments.AddRange(["--package-source", feed, .. hive]);
        int created;
        try
        {
            created = await ChildProcess.RunAsync("dotnet", arguments, console.WorkingDirectory, console.Out, console.Error, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ProcessStartException)
        {
            console.Fail("dotnet could not be started.");
            return ExitCodes.ProcessStart;
        }

        if (created != 0)
        {
            return created switch
            {
                103 or 73 => ExitCodes.InputOutput, // template not found; output exists or cannot be created
                127 => ExitCodes.Usage,             // invalid template option
                _ => ExitCodes.Failed,
            };
        }

        var folder = console.FullPath(options.Output ?? options.Name ?? ".");
        if (kind == "node")
        {
            var vendor = Path.Combine(folder, "vendor");
            Directory.CreateDirectory(vendor);
            File.Copy(tarball, Path.Combine(vendor, ToolIdentity.NodeTarballName), overwrite: true);
        }

        console.Out.WriteLine("Next:");
        if (folder != console.WorkingDirectory)
        {
            console.Out.WriteLine("  cd " + console.Display(folder));
        }

        if (kind == "node")
        {
            console.Out.WriteLine("  npm install");
        }

        console.Out.WriteLine("  dotnet tool restore");
        console.Out.WriteLine("  dotnet " + ToolIdentity.CommandName + " test");
        return ExitCodes.Success;
    }

    private static void Add(List<string> arguments, string option, string? value)
    {
        if (value is not null)
        {
            arguments.Add(option);
            arguments.Add(value);
        }
    }
}
