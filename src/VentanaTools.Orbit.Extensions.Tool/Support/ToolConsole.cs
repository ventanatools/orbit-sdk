// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>The exit codes of contract §11.1.</summary>
internal static class ExitCodes
{
    /// <summary>Success.</summary>
    public const int Success = 0;

    /// <summary>Error diagnostics (or warnings with <c>--warnings-as-errors</c>), a failed expectation, no valid pairing, or a failed test.</summary>
    public const int Failed = 1;

    /// <summary>A usage error.</summary>
    public const int Usage = 2;

    /// <summary>A path is missing or unreadable, the output exists, or a template pack is missing.</summary>
    public const int InputOutput = 3;

    /// <summary>An internal error: <c>json.internal-error</c> from a reader, otherwise <c>tool.internal-error</c>.</summary>
    public const int Internal = 4;

    /// <summary>A companion, <c>dotnet test</c> or <c>npm test</c> could not be started.</summary>
    public const int ProcessStart = 5;
}

/// <summary>Where the tool writes and reads, and the directory relative paths resolve against.</summary>
internal sealed class ToolConsole
{
    /// <summary>Diagnostics, summaries, <c>--json</c> objects and transcripts.</summary>
    public required TextWriter Out { get; init; }

    /// <summary>Usage errors, problems that are not diagnostics, and the output of the programs the tool runs.</summary>
    public required TextWriter Error { get; init; }

    /// <summary>The interactive prompt of <c>simulate</c>.</summary>
    public required TextReader In { get; init; }

    /// <summary>The directory relative paths resolve against, and display paths are relative to.</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Tests only: runs before a command, so a test can make it fail as a defect would.</summary>
    public Action<string>? FaultInjection { get; init; }

    /// <summary>The process console.</summary>
    public static ToolConsole System() => new()
    {
        Out = Console.Out,
        Error = Console.Error,
        In = Console.In,
        WorkingDirectory = Environment.CurrentDirectory,
    };

    /// <summary>The full path of <paramref name="path"/>, relative to <see cref="WorkingDirectory"/>.</summary>
    public string FullPath(string path) => Path.GetFullPath(path, WorkingDirectory);

    /// <summary>
    /// How a path is shown: relative to the working directory with <c>/</c> separators when it is
    /// inside it, else the full path. The tool prints the author's own paths; never file contents.
    /// </summary>
    public string Display(string fullPath)
    {
        var relative = Path.GetRelativePath(WorkingDirectory, fullPath);
        if (relative == ".")
        {
            return ".";
        }

        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            return fullPath;
        }

        return relative.Replace(Path.DirectorySeparatorChar, '/');
    }

    /// <summary>A folder as a sentence starts with it: "The current folder" for the working directory.</summary>
    public string FolderName(string fullPath) => Display(fullPath) is "." ? "The current folder" : Display(fullPath);

    /// <summary>Writes a problem that is not a diagnostic to <see cref="Error"/>.</summary>
    public void Fail(string message) => Error.WriteLine(ToolIdentity.CommandName + ": error: " + message);

    /// <summary>Writes a note to <see cref="Error"/>.</summary>
    public void Note(string message) => Error.WriteLine(ToolIdentity.CommandName + ": " + message);
}
