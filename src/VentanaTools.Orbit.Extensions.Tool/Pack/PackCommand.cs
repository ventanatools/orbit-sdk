// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.FileSystemGlobbing;
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

    private static readonly string[] ExcludedFolders = [".git", ".vs", "obj"];

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

        var outputFolder = console.FullPath(outputArgument ?? Path.Combine(project, "artifacts"));
        var outputPath = Path.Combine(outputFolder, manifest.Id + "-" + manifest.Version + extension);
        if (File.Exists(outputPath) && !force)
        {
            report.Add(DiagnosticCodes.PackOutputExists, string.Empty, console.Display(outputPath));
            return report.Write(ExitCodes.InputOutput);
        }

        var staging = new Staging(console, report, config, configPath);
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
    private sealed class Staging(ToolConsole console, DiagnosticReport report, PackConfig config, string configPath)
    {
        private readonly Dictionary<string, StagedFile> _files = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _temporaryFolders = [];
        private readonly string _configFolder = Path.GetDirectoryName(configPath)!;

        public async Task CollectAsync(string project, string manifestPath, CancellationToken cancellationToken)
        {
            try
            {
                AddSingle(PackageReader.ManifestName, manifestPath, null);
                AddSingle(PackageReader.ReadmeName, Path.GetFullPath(config.Readme, _configFolder), config.ReadmeNode, "/readme");
                if (config.Strings is { } strings)
                {
                    AddStrings(Path.GetFullPath(strings, _configFolder));
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

        private void AddSingle(string entry, string source, JsonNode? node, string? path = null)
        {
            if (!File.Exists(source))
            {
                ReportConfig(DiagnosticCodes.PackSourceMissing, path ?? string.Empty, node);
                return;
            }

            if (IsLink(source))
            {
                report.Add(DiagnosticCodes.PackLink, string.Empty, console.Display(source));
                return;
            }

            Stage(entry, source);
        }

        private void AddStrings(string folder)
        {
            if (!Directory.Exists(folder))
            {
                ReportConfig(DiagnosticCodes.PackSourceMissing, "/strings", config.StringsNode);
                return;
            }

            if (IsLink(folder))
            {
                report.Add(DiagnosticCodes.PackLink, string.Empty, console.Display(folder));
                return;
            }

            foreach (var file in Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
            {
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

        private void AddRule(PackCopyRule rule, string project)
        {
            var from = Path.GetFullPath(rule.From, _configFolder);
            var path = JsonPointer.Append(JsonPointer.Append("/payload", rule.Index), "from");
            if (!File.Exists(from) && !Directory.Exists(from))
            {
                ReportConfig(DiagnosticCodes.PackSourceMissing, path, rule.FromNode);
                return;
            }

            if (LinkOnTheWay(project, from) is { } link)
            {
                report.Add(DiagnosticCodes.PackLink, string.Empty, console.Display(link));
                return;
            }

            if (File.Exists(from))
            {
                Stage(Join(rule.To, Path.GetFileName(from)), from);
                return;
            }

            AddTree(from, rule.To, rule);
        }

        private void AddTree(string root, string to, PackCopyRule rule)
        {
            var matcher = rule.Matcher();
            foreach (var item in Walk(root))
            {
                var probe = item.Folder ? Path.Combine(item.FullPath, "file") : item.FullPath;
                if (!matcher.Match(root, probe).HasMatches)
                {
                    continue;
                }

                if (item.Link)
                {
                    report.Add(DiagnosticCodes.PackLink, string.Empty, console.Display(item.FullPath));
                    continue;
                }

                if (!item.Folder)
                {
                    Stage(Join(to, item.Relative), item.FullPath);
                }
            }
        }

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

        /// <summary>The first link from the project folder down to <paramref name="target"/> (both inclusive when inside it), or null.</summary>
        private static string? LinkOnTheWay(string project, string target)
        {
            var relative = Path.GetRelativePath(project, target);
            if (relative == "." || Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
            {
                return IsLink(target) ? target : null;
            }

            var current = project;
            foreach (var segment in relative.Split(Path.DirectorySeparatorChar))
            {
                current = Path.Combine(current, segment);
                if (IsLink(current))
                {
                    return current;
                }
            }

            return null;
        }
    }

    /// <summary>One file or folder under a copied folder, never descending into links.</summary>
    private readonly record struct WalkItem(string Relative, string FullPath, bool Folder, bool Link);

    /// <summary>Every file and folder under <paramref name="root"/>, without the names that are never packed (contract §11.3).</summary>
    private static IEnumerable<WalkItem> Walk(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var folder = pending.Pop();
            foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos().OrderBy(info => info.Name, StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(root, info.FullName).Replace(Path.DirectorySeparatorChar, '/');
                var isFolder = (info.Attributes & FileAttributes.Directory) != 0;
                if (IsAlwaysExcluded(relative, isFolder))
                {
                    continue;
                }

                var link = (info.Attributes & FileAttributes.ReparsePoint) != 0 || info.LinkTarget is not null;
                yield return new WalkItem(relative, info.FullName, isFolder, link);
                if (isFolder && !link)
                {
                    pending.Push(info.FullName);
                }
            }
        }
    }

    /// <summary>Whether a path is one the tool never packs: <c>.git</c>, <c>.vs</c>, <c>obj</c>, <c>node_modules/.cache</c>, <c>*.user</c>, <c>*.pairing.json</c>, <c>pairing.json</c>.</summary>
    public static bool IsAlwaysExcluded(string relative, bool folder)
    {
        var segments = relative.Split('/');
        var name = segments[^1];
        if (folder && Array.Exists(ExcludedFolders, excluded => excluded.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (folder && name.Equals(".cache", StringComparison.OrdinalIgnoreCase) && segments.Length > 1
            && segments[^2].Equals("node_modules", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !folder && (name.EndsWith(".user", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".pairing.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("pairing.json", StringComparison.OrdinalIgnoreCase));
    }

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
