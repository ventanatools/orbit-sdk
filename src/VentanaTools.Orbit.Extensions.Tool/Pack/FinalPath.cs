// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>
/// The final path of an existing file or folder: the one spelling every other spelling of it
/// resolves to, so <c>pack</c> can tell its output folder by what it is rather than by how the
/// command line spelled it (contract §11.3).
/// </summary>
/// <remarks>
/// On Windows this is the path the file system reports for an open handle, which resolves
/// symbolic links, junctions, substituted drives, mounted folders, 8.3 short names and letter
/// case. Elsewhere, and where Windows cannot report one (a volume without a drive letter), each
/// symbolic link on the way is resolved in turn.
/// </remarks>
internal static partial class FinalPath
{
    /// <summary>The most links followed for one path, as POSIX systems allow.</summary>
    private const int MaxLinks = 40;

    private const uint FileShareAll = 0x7;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x0200_0000;

    /// <summary>The final path of <paramref name="path"/>, without a trailing separator; the full path when it cannot be resolved.</summary>
    public static string Of(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() && FromHandle(full) is { } final ? final : ResolveLinks(full, 0);
    }

    private static string? FromHandle(string path)
    {
        using var handle = CreateFile(path, 0, FileShareAll, 0, OpenExisting, FileFlagBackupSemantics, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new char[512];
        while (true)
        {
            var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
            if (length == 0)
            {
                return null;
            }

            if (length < buffer.Length)
            {
                var final = new string(buffer, 0, (int)length);
                return Path.TrimEndingDirectorySeparator(final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + final[8..]
                    : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final);
            }

            // Too small: length is the size needed, terminating null included.
            buffer = new char[length];
        }
    }

    private static string ResolveLinks(string full, int depth)
    {
        var root = Path.GetPathRoot(full) ?? string.Empty;
        var current = root;
        foreach (var name in full[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (name.Length == 0)
            {
                continue;
            }

            current = Path.Join(current, name);
            string? target;
            try
            {
                target = (Directory.Exists(current) ? new DirectoryInfo(current) : (FileSystemInfo)new FileInfo(current)).LinkTarget;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                target = null;
            }

            if (target is not null && depth < MaxLinks)
            {
                var resolved = Path.GetFullPath(target, Path.GetDirectoryName(current) ?? root);
                current = ResolveLinks(Path.TrimEndingDirectorySeparator(resolved), depth + 1);
            }
        }

        return current;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFinalPathNameByHandle(SafeFileHandle file, [Out] char[] path, uint length, uint flags);
}
