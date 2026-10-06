// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Security.Cryptography;
using System.Text.Json;
using VentanaTools.Orbit.Extensions.Packaging;

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>
/// <c>pack [&lt;project-dir&gt;] [-o &lt;output-dir&gt;] [--host &lt;id&gt;] [--force] [--json]</c>: builds a
/// package from <c>extension.pack.json</c> (contract §11.3), running its <c>build</c> step first.
/// </summary>
internal static class PackCommand
{
    /// <summary>The largest staged JSON file that is checked for pairing members (contract §11.3).</summary>
    public const int SecretCheckBytes = 4_096;

    /// <summary>Names never packed, whether they name a file or a folder (a <c>.git</c> file points a worktree or submodule at its repository).</summary>
    private static readonly string[] ExcludedNames = [".git", ".vs", "obj"];

    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static async Task<int> RunAsync(ToolConsole console, string? projectArgument, string? outputArgument, string? hostId, bool force,
        bool json, CancellationToken cancellationToken)
    {
        var report = new DiagnosticReport(console, "pack", json);
        var project = console.FullPath(projectArgument ?? ".");
        var configPath = Path.Combine(project, ExtensionFiles.PackConfigName);
        var manifestPath = Path.Combine(project, ExtensionFiles.ManifestName);
        foreach (var required in new[] { configPath, manifestPath })
        {
            if (!File.Exists(required))
            {
                console.Fail(console.Display(required) + " does not exist.");
                return report.Write(ExitCodes.InputOutput);
            }
        }

        var config = await ReadConfigAsync(configPath, console, report, cancellationToken).ConfigureAwait(false);
        if (config is null)
        {
            return report.Write();
        }

        var manifest = await ExtensionFiles.ReadManifestAsync(manifestPath, hostId, console, report, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            return report.Write();
        }

        var host = hostId is not null ? HostRegistry.Find(hostId) : HostRegistry.FirstActive(manifest.Hosts);
        if (host?.PackageExtension is not { } extension)
        {
            console.Fail("The package file extension is unknown: hosts lists no active host. Pass --host with the id of an active host.");
            return report.Write(ExitCodes.Usage);
        }

        var outputFolder = Path.TrimEndingDirectorySeparator(console.FullPath(outputArgument ?? Path.Combine(project, "artifacts")));
        var outputPath = Path.Combine(outputFolder, manifest.Id + "-" + manifest.Version + extension);
        if (File.Exists(outputPath) && !force)
        {
            report.Add(DiagnosticCodes.PackOutputExists, string.Empty, console.Display(outputPath));
            return report.Write(ExitCodes.InputOutput);
        }

        var staging = new Staging(console, report, config, configPath, outputFolder);
        await staging.CollectAsync(project, manifestPath, cancellationToken).ConfigureAwait(false);
        if (!report.Ok)
        {
            return report.Write();
        }

        var contents = await staging.ReadContentsAsync(cancellationToken).ConfigureAwait(false);
        if (contents is null)
        {
            return report.Write();
        }

        var bytes = PackageWriter.Write(contents);
        var verified = ExtensionFiles.Guard(() => PackageReader.Read(bytes, new PackageReadOptions { Manifest = ExtensionFiles.Options(host.Id) }));
        var display = console.Display(outputPath);
        foreach (var diagnostic in verified.Diagnostics)
        {
            report.Add(diagnostic, diagnostic.File is null ? display : display + "!/" + diagnostic.File);
        }

        if (verified.Value is not { } package || !report.Ok)
        {
            return report.Write();
        }

        if (!await WriteAtomicallyAsync(outputPath, bytes, force, cancellationToken).ConfigureAwait(false))
        {
            report.Add(DiagnosticCodes.PackOutputExists, string.Empty, display);
            return report.Write(ExitCodes.InputOutput);
        }

        report.Package = ExtensionFiles.Summarize(package, display, bytes.LongLength);
        report.ExtraText = output => ExtensionFiles.WriteSummary(output, report.Package);
        return report.Write();
    }

    private static async Task<PackConfig?> ReadConfigAsync(string path, ToolConsole console, DiagnosticReport report, CancellationToken cancellationToken)
    {
        var bytes = await BoundedFile.ReadAsync(path, PackConfigReader.MaxBytes, cancellationToken).ConfigureAwait(false);
        var result = ExtensionFiles.Guard(() => PackConfigReader.Read(bytes));
        if (!result.Succeeded)
        {
            var display = console.Display(path);
            report.Add(DiagnosticCodes.PackConfig, string.Empty, display);
            foreach (var diagnostic in result.Diagnostics)
            {
                report.Add(diagnostic, display);
            }

            return null;
        }

        foreach (var diagnostic in result.Diagnostics)
        {
            report.Add(diagnostic, console.Display(path));
        }

        return result.Value;
    }

    /// <summary>Writes the package beside its final name and moves it into place; false when the output exists and <paramref name="force"/> is off.</summary>
    private static async Task<bool> WriteAtomicallyAsync(string outputPath, byte[] bytes, bool force, CancellationToken cancellationToken)
    {
        var folder = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, "." + Path.GetFileName(outputPath) + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)) + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            try
            {
                File.Move(temporary, outputPath, overwrite: force);
            }
            catch (IOException) when (!force && File.Exists(outputPath))
            {
                return false;
            }

            return true;
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    /// <summary>One file that goes into the package.</summary>
    private sealed class StagedFile
    {
        public required string Entry { get; init; }

        public required string Source { get; init; }

        public required long Length { get; init; }
    }

    /// <summary>
    /// Collects what a package contains: <c>extension.json</c>, the readme, the strings files, the
    /// build step's publish output and the copy rules' files, minus the names that are never packed.
    /// It refuses links and pairing files among them, and checks names and limits before reading
    /// any content.
    /// </summary>
    /// <remarks>
    /// The same two checks apply to every path the configuration names (the readme, the strings
    /// folder, a copy rule's <c>from</c>) and to everything found under a copied folder: each folder
    /// from the project down to the item, and the item itself, is checked for the always-excluded
    /// names and for links. The project folder and the folders above it are never checked; for a
    /// path outside the project, checking starts below the deepest folder the two share. The
    /// output folder is never staged: a copied folder that holds it leaves it out, and no package
    /// file directly in it is staged. The output folder is told by its final path
    /// (<see cref="FinalPath"/>), so another spelling of it (through a junction, a substituted
    /// drive or a short name) is still the output folder, and so is a junction or symbolic link
    /// inside a copied folder that leads to it.
    /// </remarks>
    private sealed class Staging(ToolConsole console, DiagnosticReport report, PackConfig config, string configPath, string outputFolder)
    {
        private readonly Dictionary<string, StagedFile> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _temporaryFolders = [];
        private readonly string _configFolder = Path.GetDirectoryName(configPath)!;

        /// <summary>The output folder's final path; null while it does not exist, when nothing can be in it.</summary>
        private readonly string? _outputFolder = Directory.Exists(outputFolder) ? FinalPath.Of(outputFolder) : null;

        public async Task CollectAsync(string project, string manifestPath, CancellationToken cancellationToken)
        {
            try
            {
                AddSingle(project, PackageReader.ManifestName, manifestPath, null);
                AddSingle(project, PackageReader.ReadmeName, Resolve(config.Readme), config.ReadmeNode, "/readme");
                if (config.Strings is { } strings)
                {
                    AddStrings(project, Resolve(strings));
                }

                if (config.Build is { } build && !await PublishAsync(build, cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                foreach (var rule in config.Payload)
                {
                    AddRule(rule, project);
                }

                CheckNames();
            }
            finally
            {
                if (!report.Ok)
                {
                    DeleteTemporaryFolders();
                }
            }
        }

        public async Task<PackageContents?> ReadContentsAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!CheckLimits())
                {
                    return null;
                }

                byte[]? manifest = null, readme = null;
                var strings = new List<PackageContentFile>();
                var payload = new List<PackageContentFile>();
                foreach (var file in _files.Values.OrderBy(file => file.Entry, StringComparer.Ordinal))
                {
                    var bytes = await File.ReadAllBytesAsync(file.Source, cancellationToken).ConfigureAwait(false);
                    if (bytes.Length <= SecretCheckBytes && LooksLikePairing(bytes))
                    {
                        report.Add(DiagnosticCodes.PackSecret, string.Empty, console.Display(file.Source));
                        continue;
                    }

                    switch (file.Entry)
                    {
                        case PackageReader.ManifestName:
                            manifest = bytes;
                            break;
                        case PackageReader.ReadmeName:
                            readme = bytes;
                            break;
                        default:
                            var target = file.Entry.StartsWith("strings/", StringComparison.Ordinal) ? strings : payload;
                            target.Add(new PackageContentFile { Path = file.Entry[(file.Entry.IndexOf('/', StringComparison.Ordinal) + 1)..], Content = bytes });
                            break;
                    }
                }

                if (!report.Ok || manifest is null || readme is null)
                {
                    return null;
                }

                return new PackageContents
                {
                    Manifest = manifest,
                    Readme = readme,
                    Strings = strings,
                    Payload = payload,
                    CreatedBy = new PackageCreator { Name = ToolIdentity.CommandName, Version = ToolIdentity.Version },
                };
            }
            finally
            {
                DeleteTemporaryFolders();
            }
        }

        /// <summary>A path from the pack configuration, in full and without a trailing separator.</summary>
        private string Resolve(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path, _configFolder));

        /// <summary>
        /// Stages <c>extension.json</c> or the readme. Both are required, so a readme the tool never
        /// packs is refused rather than left out: a pairing name is <c>pack.secret</c>, any other
        /// always-excluded name on its way leaves the package without its readme.
        /// </summary>
        private void AddSingle(string project, string entry, string source, JsonNode? node, string? path = null)
        {
            if (!File.Exists(source))
            {
                ReportConfig(DiagnosticCodes.PackSourceMissing, path ?? string.Empty, node);
                return;
            }

            if (IsPairingName(Path.GetFileName(source)))
            {
                report.Add(DiagnosticCodes.PackSecret, string.Empty, console.Display(source));
                return;
            }

            if (ExcludedOnTheWay(project, source))
            {
                ReportConfig(DiagnosticCodes.PackageFileMissing, path ?? string.Empty, node);
                return;
            }

            if (LinkOnTheWay(project, source) is { } link)
            {
                report.Add(DiagnosticCodes.PackLink, string.Empty, console.Display(link));
                return;
            }

            Stage(entry, source);
        }

        private void AddStrings(string project, string folder)
        {
            if (!Directory.Exists(folder))
            {
                ReportConfig(DiagnosticCodes.PackSourceMissing, "/strings", config.StringsNode);
                return;
            }

            if (ExcludedOnTheWay(project, folder))
            {
                return;
            }

            if (LinkOnTheWay(project, folder) is { } link)
            {
                report.Add(DiagnosticCodes.PackLink, string.Empty, console.Display(link));
                return;
            }

            var name = Path.GetFileName(folder);
            foreach (var file in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
            {
                if (IsAlwaysExcluded(name + "/" + Path.GetFileName(file)))
                {
                    continue;
                }

                if (IsLink(file))
                {
                    report.Add(DiagnosticCodes.PackLink, string.Empty, console.Display(file));
                    continue;
                }

                Stage("strings/" + Path.GetFileName(file), file);
            }
        }

        private async Task<bool> PublishAsync(PackBuild build, CancellationToken cancellationToken)
        {
            var output = Path.Combine(Path.GetTempPath(), "ventana-pack-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant());
            _temporaryFolders.Add(output);
            int exitCode;
            try
            {
                exitCode = await ChildProcess.RunAsync("dotnet",
                    ["publish", Path.GetFullPath(build.Project, _configFolder), "-c", build.Configuration, "-r", build.Runtime, "-o", output, "-nologo"],
                    _configFolder, console.Error, console.Error, cancellationToken).ConfigureAwait(false);
            }
            catch (ProcessStartException)
            {
                exitCode = -1;
            }

            if (exitCode != 0 || !Directory.Exists(output))
            {
                ReportConfig(DiagnosticCodes.PackBuildFailed, "/build", build.Node);
                return false;
            }

            AddTree(output, build.To, new PackCopyRule
            {
                Index = -1,
                From = output,
                To = build.To,
                FromNode = build.Node,
            });
            return true;
        }

        /// <summary>
        /// Applies one copy rule. A <c>from</c> that is, or lies in, an always-excluded name copies
        /// nothing, as the same name would inside a copied folder; a file it names directly with a
        /// pairing name is refused (<c>pack.secret</c>), since tools never pack a pairing file; and a
        /// package file directly in the output folder copies nothing.
        /// </summary>
        private void AddRule(PackCopyRule rule, string project)
        {
            var from = Resolve(rule.From);
            var path = JsonPointer.Append(JsonPointer.Append("/payload", rule.Index), "from");
            var isFile = File.Exists(from);
            if (!isFile && !Directory.Exists(from))
            {
                ReportConfig(DiagnosticCodes.PackSourceMissing, path, rule.FromNode);
                return;
            }

            if (isFile && IsPairingName(Path.GetFileName(from)))
            {
                report.Add(DiagnosticCodes.PackSecret, string.Empty, console.Display(from));
                return;
            }

            if (ExcludedOnTheWay(project, from)
                || (isFile && IsPackageName(Path.GetFileName(from)) && IsOutputFolder(FinalPath.Of(Path.GetDirectoryName(from)!))))
            {
                return;
            }

            if (LinkOnTheWay(project, from) is { } link)
            {
                report.Add(DiagnosticCodes.PackLink, string.Empty, console.Display(link));
                return;
            }

            if (isFile)
            {
                Stage(Join(rule.To, Path.GetFileName(from)), from);
                return;
            }

            AddTree(from, rule.To, rule);
        }

        /// <summary>
        /// Stages what a copy rule's globs select under <paramref name="root"/>, in ordinal order of
        /// the path below it, and refuses the links they reach (<see cref="Walk"/>).
        /// </summary>
        private void AddTree(string root, string to, PackCopyRule rule)
        {
            var files = new List<CopiedItem>();
            var links = new List<CopiedItem>();
            Walk(rule, root, FinalPath.Of(root), Path.GetFileName(root), [], files, links);
            foreach (var file in files.OrderBy(file => file.Relative, StringComparer.Ordinal))
            {
                Stage(Join(to, file.Relative), file.FullPath);
            }

            foreach (var link in links.OrderBy(link => link.Relative, StringComparer.Ordinal))
            {
                report.Add(DiagnosticCodes.PackLink, string.Empty, console.Display(link.FullPath));
            }
        }

        /// <summary>
        /// Walks one folder of a copied folder (contract §11.3). It leaves out the always-excluded
        /// names, the output folder, the packages directly in it, and every folder an exclude glob
        /// leaves out or no include glob reaches into. It never enters a link: a link is decided on
        /// itself, before its contents are filtered. A link to a folder that resolves to the output
        /// folder is the output folder, and is left out; any other link is refused when the globs
        /// select it (a file) or could select anything inside it (a folder), even if nothing inside
        /// would match. <paramref name="path"/> holds the names from the copied folder down to
        /// <paramref name="folder"/>, and <paramref name="final"/> is the folder's final path: the
        /// root's, extended by name, since the walk never follows a link.
        /// </summary>
        private void Walk(PackCopyRule rule, string folder, string final, string name, List<string> path, List<CopiedItem> files, List<CopiedItem> links)
        {
            var isOutputFolder = IsOutputFolder(final);
            foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos())
            {
                // The containing folder's own name counts, so node_modules/.cache is found when the copied folder is node_modules.
                if (IsAlwaysExcluded(name + "/" + info.Name))
                {
                    continue;
                }

                path.Add(info.Name);
                if ((info.Attributes & FileAttributes.Directory) != 0)
                {
                    if (!rule.Exclude.Any(glob => glob.LeavesOutFolder(path)) && rule.Include.Any(glob => glob.CouldMatchBelow(path)))
                    {
                        // A link the globs reach is resolved only to be compared, never entered: one that leads to the
                        // output folder (an artifacts folder redirected by a junction, say) is the output folder, left out.
                        var isLink = IsLink(info);
                        var childFinal = isLink ? FinalPath.Of(info.FullName) : Path.Join(final, info.Name);
                        if (!IsOutputFolder(childFinal))
                        {
                            if (isLink)
                            {
                                links.Add(new CopiedItem(string.Join('/', path), info.FullName));
                            }
                            else
                            {
                                Walk(rule, info.FullName, childFinal, info.Name, path, files, links);
                            }
                        }
                    }
                }
                else if (!(isOutputFolder && IsPackageName(info.Name))
                    && rule.Include.Any(glob => glob.Matches(path)) && !rule.Exclude.Any(glob => glob.Matches(path)))
                {
                    (IsLink(info) ? links : files).Add(new CopiedItem(string.Join('/', path), info.FullName));
                }

                path.RemoveAt(path.Count - 1);
            }
        }

        private bool IsOutputFolder(string final) => _outputFolder is not null && string.Equals(final, _outputFolder, PathComparison);

        private void Stage(string entry, string source)
        {
            if (!PackageReader.IsEntryName(entry))
            {
                report.Add(DiagnosticCodes.PackagePath, string.Empty, console.Display(source));
                return;
            }

            if (_files.TryGetValue(entry, out var existing) && !string.Equals(existing.Entry, entry, StringComparison.Ordinal))
            {
                report.Add(DiagnosticCodes.PackagePathConflict, string.Empty, console.Display(source));
                return;
            }

            // Copy rules apply in order: a later rule replaces an earlier file at the same name.
            _files[entry] = new StagedFile { Entry = entry, Source = source, Length = new FileInfo(source).Length };
        }

        private void CheckNames()
        {
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in _files.Keys)
            {
                for (var slash = entry.IndexOf('/', StringComparison.Ordinal); slash > 0; slash = entry.IndexOf('/', slash + 1))
                {
                    folders.Add(entry[..slash]);
                }
            }

            foreach (var file in _files.Values.Where(file => folders.Contains(file.Entry)).ToList())
            {
                report.Add(DiagnosticCodes.PackagePathConflict, string.Empty, console.Display(file.Source));
            }
        }

        private bool CheckLimits()
        {
            var ok = true;
            foreach (var file in _files.Values.Where(file => file.Entry.StartsWith("payload/", StringComparison.Ordinal)
                         && file.Length > PackageReader.MaxPayloadFileBytes))
            {
                report.Add(DiagnosticCodes.PackageFileTooLarge, string.Empty, console.Display(file.Source));
                ok = false;
            }

            var folders = _files.Keys.SelectMany(Ancestors).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            if (_files.Count + folders + 1 > PackageReader.MaxEntries)
            {
                report.Add(DiagnosticCodes.PackageEntries, string.Empty, console.Display(_configFolder));
                ok = false;
            }

            if (_files.Values.Sum(file => file.Length) > PackageReader.MaxExpandedBytes)
            {
                report.Add(DiagnosticCodes.PackageExpandedTooLarge, string.Empty, console.Display(_configFolder));
                ok = false;
            }

            return ok;
        }

        private static IEnumerable<string> Ancestors(string entry)
        {
            for (var slash = entry.IndexOf('/', StringComparison.Ordinal); slash > 0; slash = entry.IndexOf('/', slash + 1))
            {
                yield return entry[..slash];
            }
        }

        private void ReportConfig(string code, string path, JsonNode? node)
        {
            var (line, column) = node is { Offset: >= 0 } ? config.Position(node) : (0L, 0L);
            report.Add(code, path, console.Display(configPath), node is { Offset: >= 0 } ? line : null, node is { Offset: >= 0 } ? column : null);
        }

        private void DeleteTemporaryFolders()
        {
            foreach (var folder in _temporaryFolders)
            {
                try
                {
                    if (Directory.Exists(folder))
                    {
                        Directory.Delete(folder, recursive: true);
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // A leftover temporary folder is harmless.
                }
            }

            _temporaryFolders.Clear();
        }

        private static string Join(string folder, string relative) =>
            "payload/" + (folder.Length == 0 ? string.Empty : folder + "/") + relative.Replace('\\', '/');

        /// <summary>
        /// The first link among the folders on the way to <paramref name="target"/> and the target
        /// itself (<see cref="OnTheWay"/>); for the project folder or a folder above it, the target alone.
        /// </summary>
        private static string? LinkOnTheWay(string project, string target)
        {
            var way = OnTheWay(project, target);
            if (way.Count == 0)
            {
                return IsLink(target) ? target : null;
            }

            return way.Find(IsLink);
        }

        /// <summary>Whether a folder on the way to <paramref name="target"/>, or the target itself, has an always-excluded name.</summary>
        private static bool ExcludedOnTheWay(string project, string target) =>
            OnTheWay(project, target).Exists(item => IsAlwaysExcluded(Path.GetFileName(Path.GetDirectoryName(item)) + "/" + Path.GetFileName(item)));
    }

    /// <summary>
    /// The folders from <paramref name="project"/> down to <paramref name="target"/>, and the target,
    /// outermost first, leaving out the project folder and every folder above it (contract §11.3:
    /// those are never inspected). For a target outside the project the list starts below the
    /// deepest folder the two share; for the project folder or a folder above it, it is empty.
    /// </summary>
    private static List<string> OnTheWay(string project, string target)
    {
        var way = new List<string>();
        for (var current = target; current is not null && !IsProjectOrAbove(current, project, PathComparison); current = Path.GetDirectoryName(current))
        {
            way.Add(current);
        }

        way.Reverse();
        return way;
    }

    private static bool IsProjectOrAbove(string folder, string project, StringComparison comparison)
    {
        if (string.Equals(Path.TrimEndingDirectorySeparator(folder), Path.TrimEndingDirectorySeparator(project), comparison))
        {
            return true;
        }

        var prefix = Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar;
        return project.StartsWith(prefix, comparison);
    }

    /// <summary>
    /// Whether <paramref name="name"/> is a package file's: it ends in a registry host's package file
    /// extension. <c>pack</c> never stages such a file directly in its output folder, even when a
    /// copy rule copies the output folder itself or the output folder is the project folder.
    /// </summary>
    public static bool IsPackageName(string name) =>
        HostRegistry.Known.Any(host => host.PackageExtension is { Length: > 0 } extension
            && name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    /// <summary>A file, or a link the globs reach, under a copied folder.</summary>
    /// <param name="Relative">The path below the copied folder, <c>/</c>-separated.</param>
    /// <param name="FullPath">The file or link on disk.</param>
    private readonly record struct CopiedItem(string Relative, string FullPath);

    /// <summary>
    /// Whether the last segment of a <c>/</c>-separated path is a name the tool never packs, whether it
    /// names a file or a folder: <c>.git</c>, <c>.vs</c>, <c>obj</c>, <c>node_modules/.cache</c> (the
    /// segment before counts here), <c>*.user</c>, <c>*.pairing.json</c>, <c>pairing.json</c>. Callers
    /// check every segment of a path in turn.
    /// </summary>
    public static bool IsAlwaysExcluded(string relative)
    {
        var segments = relative.Split('/');
        var name = segments[^1];
        if (Array.Exists(ExcludedNames, excluded => excluded.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (name.Equals(".cache", StringComparison.OrdinalIgnoreCase) && segments.Length > 1
            && segments[^2].Equals("node_modules", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.EndsWith(".user", StringComparison.OrdinalIgnoreCase) || IsPairingName(name);
    }

    /// <summary>Whether a file name is a pairing file's: <c>*.pairing.json</c> or <c>pairing.json</c>.</summary>
    public static bool IsPairingName(string name) =>
        name.EndsWith(".pairing.json", StringComparison.OrdinalIgnoreCase) || name.Equals("pairing.json", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether an enumerated item is a symbolic link, junction or other reparse point.</summary>
    private static bool IsLink(FileSystemInfo info) => (info.Attributes & FileAttributes.ReparsePoint) != 0;

    /// <summary>Whether a path is a symbolic link, junction or other reparse point.</summary>
    public static bool IsLink(string path)
    {
        try
        {
            var attributes = File.GetAttributes(path);
            return (attributes & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Whether a staged file looks like a pairing file: JSON whose objects name both <c>pipeName</c>
    /// and <c>secret</c> (after unescaping), or, when it is not JSON, text that contains both quoted names.
    /// </summary>
    public static bool LooksLikePairing(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
                MaxDepth = 64,
            });
            var names = new HashSet<string>(StringComparer.Ordinal);
            Collect(document.RootElement, names);
            return names.Contains("pipeName") && names.Contains("secret");
        }
        catch (JsonException)
        {
            var text = System.Text.Encoding.UTF8.GetString(bytes);
            return text.Contains("\"pipeName\"", StringComparison.Ordinal) && text.Contains("\"secret\"", StringComparison.Ordinal);
        }
    }

    private static void Collect(JsonElement element, HashSet<string> names)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    names.Add(property.Name);
                    Collect(property.Value, names);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Collect(item, names);
                }

                break;
        }
    }
}
