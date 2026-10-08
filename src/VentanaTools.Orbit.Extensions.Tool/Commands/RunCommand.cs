// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Globalization;

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>
/// <c>run [--watch] -- &lt;command&gt; [args…]</c>: runs the companion command and prints its status
/// lines. With <c>--watch</c>, restarts it when <c>extension.json</c>, the pairing file, or the
/// files the command builds change (for <c>dotnet run</c>, the project's sources).
/// </summary>
internal static class RunCommand
{
    private static readonly string[] SourceExtensions = [".cs", ".csproj", ".props", ".targets", ".json", ".js", ".cjs", ".mjs", ".ts"];
    private static readonly string[] SkippedFolders = ["bin", "obj", "node_modules", ".git", ".vs", "artifacts"];

    public static async Task<int> RunAsync(ToolConsole console, IReadOnlyList<string> command, bool watch, TimeSpan pollInterval,
        CancellationToken cancellationToken)
    {
        var watched = watch ? await WatchedFiles.FindAsync(console, command, cancellationToken).ConfigureAwait(false) : null;
        while (true)
        {
            var snapshot = watched?.Snapshot();
            ChildProcess child;
            try
            {
                child = ChildProcess.Start(command[0], command.Skip(1), console.WorkingDirectory, console.Out, console.Error);
            }
            catch (ProcessStartException)
            {
                console.Fail(command[0] + " could not be started.");
                return ExitCodes.ProcessStart;
            }

            using (child)
            {
                var exited = child.WaitAsync(CancellationToken.None);
                if (watched is null)
                {
                    try
                    {
                        return await exited.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        child.Stop();
                        return ExitCodes.Success;
                    }
                }

                string? changed;
                try
                {
                    changed = await WaitForChangeAsync(watched, snapshot!, exited, console, pollInterval, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    child.Stop();
                    return ExitCodes.Success;
                }

                child.Stop();
                console.Note("restarting: " + changed + " changed");
            }
        }
    }

    /// <summary>Polls until a watched file changes; reports the companion's own exit and keeps waiting.</summary>
    private static async Task<string> WaitForChangeAsync(WatchedFiles watched, Dictionary<string, (bool, DateTime, long)> snapshot, Task<int> exited,
        ToolConsole console, TimeSpan pollInterval, CancellationToken cancellationToken)
    {
        var reportedExit = false;
        while (true)
        {
            await Task.Delay(pollInterval, cancellationToken).ConfigureAwait(false);
            if (exited.IsCompleted && !reportedExit)
            {
                reportedExit = true;
                console.Note("the companion exited with code " + (exited.IsCompletedSuccessfully ? exited.Result : -1).ToString(CultureInfo.InvariantCulture)
                    + "; waiting for a change");
            }

            if (watched.FirstChange(snapshot) is { } changed)
            {
                // Let a save or a build finish before restarting.
                await Task.Delay(pollInterval / 2, cancellationToken).ConfigureAwait(false);
                return console.Display(changed);
            }
        }
    }

    /// <summary>The files <c>run --watch</c> polls: the manifest, the pairing files and the sources the command builds.</summary>
    private sealed class WatchedFiles
    {
        private readonly List<string> _files = [];
        private readonly List<string> _sourceRoots = [];

        public static async Task<WatchedFiles> FindAsync(ToolConsole console, IReadOnlyList<string> command, CancellationToken cancellationToken)
        {
            var watched = new WatchedFiles();
            var manifest = console.FullPath(OptionValue(command, "--manifest") ?? ExtensionFiles.ManifestName);
            watched._files.Add(manifest);
            if (OptionValue(command, "--pairing") is { } pairing)
            {
                watched._files.Add(console.FullPath(pairing));
            }
            else if (File.Exists(manifest))
            {
                try
                {
                    var read = await ManifestReader.ReadFileAsync(manifest, cancellationToken: cancellationToken).ConfigureAwait(false);
                    if (read.Value is { } value)
                    {
                        watched._files.AddRange(value.Hosts.Where(id => HostRegistry.Find(id) is { Status: HostStatus.Active })
                            .Select(id => PairingReader.DefaultPath(id, value.Id)));
                        watched._files.Add(Path.Combine(console.WorkingDirectory, value.Id + ".pairing.json"));
                    }
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // The companion reports an unreadable manifest itself.
                }
            }

            var project = string.Equals(Path.GetFileNameWithoutExtension(command[0]), "dotnet", StringComparison.OrdinalIgnoreCase)
                && command.Count > 1 && command[1] == "run"
                ? OptionValue(command, "--project") ?? OptionValue(command, "-p")
                : null;
            var root = project is null ? console.WorkingDirectory : console.FullPath(project);
            watched._sourceRoots.Add(File.Exists(root) ? Path.GetDirectoryName(root)! : root);
            return watched;
        }

        public Dictionary<string, (bool, DateTime, long)> Snapshot()
        {
            var snapshot = new Dictionary<string, (bool, DateTime, long)>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in _files.Concat(_sourceRoots.SelectMany(Sources)))
            {
                snapshot[file] = Stamp(file);
            }

            return snapshot;
        }

        public string? FirstChange(Dictionary<string, (bool, DateTime, long)> before)
        {
            var now = Snapshot();
            foreach (var (file, stamp) in now)
            {
                if (!before.TryGetValue(file, out var old) || old != stamp)
                {
                    return file;
                }
            }

            return before.Keys.FirstOrDefault(file => !now.ContainsKey(file));
        }

        private static (bool, DateTime, long) Stamp(string file)
        {
            var info = new FileInfo(file);
            return info.Exists ? (true, info.LastWriteTimeUtc, info.Length) : (false, default, 0);
        }

        private static IEnumerable<string> Sources(string root)
        {
            if (!Directory.Exists(root))
            {
                yield break;
            }

            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                var folder = pending.Pop();
                IEnumerable<FileSystemInfo> entries;
                try
                {
                    entries = new DirectoryInfo(folder).EnumerateFileSystemInfos().ToList();
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var entry in entries)
                {
                    if ((entry.Attributes & FileAttributes.Directory) != 0)
                    {
                        if ((entry.Attributes & FileAttributes.ReparsePoint) == 0
                            && !SkippedFolders.Contains(entry.Name, StringComparer.OrdinalIgnoreCase))
                        {
                            pending.Push(entry.FullName);
                        }
                    }
                    else if (SourceExtensions.Contains(entry.Extension, StringComparer.OrdinalIgnoreCase)
                        && !entry.Name.EndsWith(".pairing.json", StringComparison.OrdinalIgnoreCase))
                    {
                        yield return entry.FullName;
                    }
                }
            }
        }

        private static string? OptionValue(IReadOnlyList<string> command, string option)
        {
            for (var i = 1; i < command.Count - 1; i++)
            {
                if (command[i] == option)
                {
                    return command[i + 1];
                }
            }

            return null;
        }
    }
}
