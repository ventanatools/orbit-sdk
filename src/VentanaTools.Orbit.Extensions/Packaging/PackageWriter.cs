// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VentanaTools.Orbit.Extensions.Packaging;

/// <summary>
/// Writes package archives (contract §5): the manifest, the readme, strings and payload files,
/// and the descriptor <c>extension.package.json</c>, which the writer generates itself.
/// </summary>
/// <remarks>
/// Output is deterministic: entries are sorted ordinally, every timestamp is 1980-01-01 00:00,
/// external attributes are 0 for files and 0x10 for folders, and a file is deflated only when
/// that makes it smaller. Verify the result with <see cref="PackageReader"/> before shipping it;
/// the writer checks names and the manifest but leaves the limits of contract §5.3 to verification.
/// </remarks>
public static class PackageWriter
{
    private const ushort DosDate1980 = (1 << 5) | 1;

    /// <summary>Writes a package.</summary>
    /// <param name="contents">What the package holds.</param>
    /// <returns>The archive bytes.</returns>
    /// <exception cref="ArgumentException">The manifest is not valid, a path breaks the entry-name grammar, or two paths conflict.</exception>
    public static byte[] Write(PackageContents contents)
    {
        ArgumentNullException.ThrowIfNull(contents);
        var manifestBytes = contents.Manifest.ToArray();
        var manifest = ManifestReader.Read(manifestBytes);
        if (!manifest.Succeeded)
        {
            throw new ArgumentException("The manifest is not valid.", nameof(contents));
        }

        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            [PackageReader.ManifestName] = manifestBytes,
            [PackageReader.ReadmeName] = contents.Readme.ToArray(),
        };
        foreach (var file in contents.Strings ?? [])
        {
            Add(files, "strings/" + Require(file).Path, file.Content);
        }

        foreach (var file in contents.Payload ?? [])
        {
            Add(files, "payload/" + Require(file).Path, file.Content);
        }

        files[PackageReader.DescriptorName] = Descriptor(ManifestWriter.ComputeHash(manifest.Value), files, contents.CreatedBy);

        var entries = new SortedDictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var (path, bytes) in files)
        {
            entries[path] = bytes;
            for (var slash = path.IndexOf('/', StringComparison.Ordinal); slash >= 0; slash = path.IndexOf('/', slash + 1))
            {
                entries[path[..(slash + 1)]] = null;
            }
        }

        var folders = entries.Keys.Where(key => key.EndsWith('/')).Select(key => key[..^1]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in entries.Keys)
        {
            var name = key.EndsWith('/') ? key[..^1] : key;
            if (!names.Add(name) || (!key.EndsWith('/') && folders.Contains(name)))
            {
                throw new ArgumentException("Two package paths conflict.", nameof(contents));
            }
        }

        return Zip(entries);
    }

    private static void Add(SortedDictionary<string, byte[]> files, string path, ReadOnlyMemory<byte> content)
    {
        if (!PackageReader.IsEntryName(path) || path.StartsWith("strings/", StringComparison.Ordinal)
            && (path.Count(c => c == '/') != 1 || !path.EndsWith(".json", StringComparison.Ordinal)))
        {
            throw new ArgumentException("A package path breaks the entry-name grammar.", nameof(path));
        }

        if (!files.TryAdd(path, content.ToArray()))
        {
            throw new ArgumentException("Two package paths conflict.", nameof(path));
        }
    }

    private static PackageContentFile Require(PackageContentFile? file) =>
        file?.Path is null ? throw new ArgumentException("A package file or its path is null.", nameof(file)) : file;

    private static byte[] Descriptor(string manifestHash, SortedDictionary<string, byte[]> files, PackageCreator? createdBy)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Indented = true,
            NewLine = "\n",
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            json.WriteStartObject();
            json.WriteNumber("archiveVersion", 2);
            json.WriteString("manifestHash", manifestHash);
            if (createdBy is not null)
            {
                json.WriteStartObject("createdBy");
                json.WriteString("name", createdBy.Name ?? throw new ArgumentException("createdBy has no name.", nameof(createdBy)));
                json.WriteString("version", createdBy.Version ?? throw new ArgumentException("createdBy has no version.", nameof(createdBy)));
                json.WriteEndObject();
            }

            json.WriteStartArray("files");
            foreach (var (path, bytes) in files)
            {
                json.WriteStartObject();
                json.WriteString("path", path);
                json.WriteNumber("size", bytes.LongLength);
                json.WriteString("sha256", Convert.ToHexStringLower(SHA256.HashData(bytes)));
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        stream.WriteByte((byte)'\n');
        return stream.ToArray();
    }

    private static byte[] Zip(SortedDictionary<string, byte[]?> entries)
    {
        using var output = new MemoryStream();
        var central = new MemoryStream();
        var count = 0;
        foreach (var (path, bytes) in entries)
        {
            var name = Encoding.ASCII.GetBytes(path);
            var folder = bytes is null;
            var data = bytes ?? [];
            var crc = Crc32.Compute(data);
            var compressed = data;
            ushort method = 0;
            if (data.Length > 0)
            {
                var deflated = Deflate(data);
                if (deflated.Length < data.Length)
                {
                    compressed = deflated;
                    method = 8;
                }
            }

            var offset = checked((uint)output.Position);
            var local = new byte[30];
            BinaryPrimitives.WriteUInt32LittleEndian(local, 0x04034B50);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(4), 20);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(8), method);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(12), DosDate1980);
            BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(14), crc);
            BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(18), (uint)compressed.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(22), (uint)data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(26), (ushort)name.Length);
            output.Write(local);
            output.Write(name);
            output.Write(compressed);

            var record = new byte[46];
            BinaryPrimitives.WriteUInt32LittleEndian(record, 0x02014B50);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), 20);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 20);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(10), method);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(14), DosDate1980);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), crc);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(20), (uint)compressed.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), (uint)data.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(28), (ushort)name.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(38), folder ? 0x10u : 0u);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(42), offset);
            central.Write(record);
            central.Write(name);
            count++;
        }

        var centralOffset = checked((uint)output.Position);
        central.Position = 0;
        central.CopyTo(output);
        var end = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054B50);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), checked((ushort)count));
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), checked((ushort)count));
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), checked((uint)central.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), centralOffset);
        output.Write(end);
        central.Dispose();
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }
}
