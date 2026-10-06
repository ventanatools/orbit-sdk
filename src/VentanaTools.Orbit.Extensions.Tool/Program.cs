// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.IO.Compression;
using System.Text;

namespace VentanaTools.Orbit.Extensions.Tool;

internal static class Program
{
    private static readonly DateTimeOffset ZipTimestamp = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private sealed record StagedEntry(string Name, bool Directory, byte[] Bytes);

    public static int Main(string[] args)
    {
        try
        {
            if (args is ["pack", var staging, var destination])
            {
                Pack(staging, destination);
                Console.WriteLine("extension-package.packed");
                return 0;
            }
            if (args is ["verify", var package])
            {
                RequirePackageExtension(package);
                Validate(ReadBounded(Path.GetFullPath(package), ExternalExtensionPackageReader.MaxArchiveBytes));
                Console.WriteLine("extension-package.verified");
                return 0;
            }
            Console.Error.WriteLine("extension-package.usage");
            Console.Error.WriteLine("pack <stagingdir> <output.orbitextension>");
            Console.Error.WriteLine("verify <package.orbitextension>");
            return 2;
        }
        catch (Exception)
        {
            // Inputs can contain private paths or content. Never print exception prose.
            Console.Error.WriteLine("extension-package.failed");
            return 1;
        }
    }

    private static void Pack(string stagingArgument, string destinationArgument)
    {
        var staging = Path.TrimEndingDirectorySeparator(Path.GetFullPath(stagingArgument));
        var destination = Path.GetFullPath(destinationArgument);
        RequirePackageExtension(destination);
        RequireOrdinaryPath(staging, directory: true);
        var parent = Path.GetDirectoryName(destination) ?? throw new InvalidDataException();
        RequireOrdinaryPath(parent, directory: true);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var boundary = Path.EndsInDirectorySeparator(staging) ? staging : staging + Path.DirectorySeparatorChar;
        if (destination.Equals(staging, comparison) || destination.StartsWith(boundary, comparison)
            || File.Exists(destination) || Directory.Exists(destination)) throw new InvalidDataException();
        var entries = ReadStaging(staging);
        using var output = new BoundedArchiveStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true, entryNameEncoding: Encoding.UTF8))
        {
            foreach (var item in entries.OrderBy(item => item.Name, StringComparer.Ordinal))
            {
                var entry = archive.CreateEntry(item.Name, item.Directory ? CompressionLevel.NoCompression : CompressionLevel.Optimal);
                entry.LastWriteTime = ZipTimestamp;
                entry.ExternalAttributes = item.Directory ? 0x10 : 0;
                if (!item.Directory)
                {
                    using var body = entry.Open();
                    body.Write(item.Bytes);
                }
            }
        }
        var bytes = output.ToArray();
        Validate(bytes);
        WriteNewAtomically(destination, bytes);
    }

    private static List<StagedEntry> ReadStaging(string root)
    {
        var result = new List<StagedEntry>();
        var pending = new Stack<string>();
        pending.Push(root);
        var expanded = 0;
        while (pending.TryPop(out var directory))
        {
            RequireOrdinaryPath(directory, directory: true);
            foreach (var path in Directory.EnumerateFileSystemEntries(directory))
            {
                if (result.Count >= ExternalExtensionPackageReader.MaxEntries) throw new InvalidDataException();
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) throw new InvalidDataException();
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                var name = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
                if (name.Length > ExternalExtensionPackageReader.MaxPathLength) throw new InvalidDataException();
                if (directory == root)
                {
                    if (isDirectory ? name != "payload" : name is not ("extension.json" or "package.json" or "README.md"))
                        throw new InvalidDataException();
                }
                else if (!name.StartsWith("payload/", StringComparison.Ordinal)) throw new InvalidDataException();
                var leaf = Path.GetFileName(path);
                if (leaf.Equals(".git", StringComparison.OrdinalIgnoreCase) || leaf.Equals(".hg", StringComparison.OrdinalIgnoreCase)
                    || leaf.Equals(".svn", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                if (isDirectory)
                {
                    result.Add(new(name + "/", true, []));
                    pending.Push(path);
                    continue;
                }
                var limit = name switch
                {
                    "extension.json" => ExternalExtensionManifestReader.MaxBytes,
                    "package.json" => ExternalExtensionPackageDescriptorReader.MaxBytes,
                    "README.md" => ExternalExtensionPackageReader.MaxReadmeBytes,
                    _ => ExternalExtensionPackageReader.MaxFileBytes
                };
                var bytes = ReadBounded(path, Math.Min(limit, ExternalExtensionPackageReader.MaxExpandedBytes - expanded));
                expanded += bytes.Length;
                result.Add(new(name, false, bytes));
            }
        }
        return result;
    }

    private static byte[] ReadBounded(string path, int limit)
    {
        RequireOrdinaryPath(path, directory: false);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.SequentialScan);
        if (input.Length > limit) throw new InvalidDataException();
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var count = input.Read(buffer);
            if (count == 0) break;
            if (count > limit - output.Length) throw new InvalidDataException();
            output.Write(buffer, 0, count);
        }
        if (output.Length != input.Length) throw new InvalidDataException();
        RequireOrdinaryPath(path, directory: false);
        return output.ToArray();
    }

    private static void RequireOrdinaryPath(string path, bool directory)
    {
        var first = true;
        for (string? current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); current is not null; current = Path.GetDirectoryName(current))
        {
            var attributes = File.GetAttributes(current);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                || isDirectory != (first ? directory : true)) throw new InvalidDataException();
            first = false;
        }
    }

    private static void RequirePackageExtension(string path)
    {
        if (!Path.GetExtension(path).Equals(".orbitextension", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
    }

    private static void Validate(byte[] bytes)
    {
        if (ExternalExtensionPackageReader.Read(bytes).Package is null) throw new InvalidDataException();
    }

    private static void WriteNewAtomically(string destination, byte[] bytes)
    {
        var parent = Path.GetDirectoryName(destination) ?? throw new InvalidDataException();
        RequireOrdinaryPath(parent, directory: true);
        var temporary = Path.Combine(parent, ".orbitextension-" + Guid.NewGuid().ToString("N") + ".tmp");
        var created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                created = true;
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            RequireOrdinaryPath(parent, directory: true);
            File.Move(temporary, destination, overwrite: false);
            created = false;
        }
        finally
        {
            if (created)
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private sealed class BoundedArchiveStream : MemoryStream
    {
        private void CheckWrite(int count)
        {
            if (count < 0 || count > ExternalExtensionPackageReader.MaxArchiveBytes - Position) throw new InvalidDataException();
        }
        public override void Write(byte[] buffer, int offset, int count) { CheckWrite(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { CheckWrite(buffer.Length); base.Write(buffer); }
        public override void WriteByte(byte value) { CheckWrite(1); base.WriteByte(value); }
        public override void SetLength(long value)
        {
            if (value < 0 || value > ExternalExtensionPackageReader.MaxArchiveBytes) throw new InvalidDataException();
            base.SetLength(value);
        }
    }
}
