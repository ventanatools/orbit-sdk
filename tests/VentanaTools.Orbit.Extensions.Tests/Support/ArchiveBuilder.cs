// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VentanaTools.Orbit.Extensions.Packaging;

namespace VentanaTools.Orbit.Extensions.Tests;

/// <summary>One entry of an archive to build, with every field a malformed archive may need to bend.</summary>
internal sealed class EntrySpec
{
    public required string Name { get; init; }

    public byte[]? Content { get; set; }

    public bool Descriptor { get; init; }

    public bool Folder { get; init; }

    public int Method { get; init; }

    public int Flags { get; init; }

    public int MadeBy { get; init; } = 20;

    public uint? ExternalAttributes { get; init; }

    public uint? Crc { get; init; }

    public bool DataDescriptor { get; init; }

    public bool Zip64Sizes { get; init; }

    public byte[] CentralExtra { get; init; } = [];

    public string? LocalName { get; init; }

    public int? LocalMethod { get; init; }

    public int? LocalFlags { get; init; }

    public uint? LocalCrc { get; init; }

    public uint? LocalUncompressedSize { get; init; }

    public byte[] LocalExtra { get; init; } = [];

    public int? LocalOffsetOf { get; init; }

    public byte[] GapBefore { get; init; } = [];
}

/// <summary>An archive to build.</summary>
internal sealed class ArchiveSpec
{
    public List<EntrySpec> Entries { get; init; } = [];

    public byte[] Prefix { get; init; } = [];

    public byte[] Trailing { get; init; } = [];

    public byte[] AfterEnd { get; init; } = [];

    public byte[] Comment { get; init; } = [];

    public int? DiskNumber { get; init; }

    public int? EntriesOnDisk { get; init; }

    public bool Zip64Locator { get; init; }

    public byte[]? Raw { get; init; }

    public JsonElement? DescriptorChanges { get; init; }
}

/// <summary>Builds package archives byte for byte from <see cref="ArchiveSpec"/>, as fixtures/packages/archives.json describes.</summary>
internal static class ArchiveBuilder
{
    public static byte[] Build(ArchiveSpec spec)
    {
        if (spec.Raw is not null)
        {
            return spec.Raw;
        }

        foreach (var entry in spec.Entries.Where(entry => entry.Descriptor))
        {
            entry.Content = Descriptor(spec);
        }

        using var output = new MemoryStream();
        output.Write(spec.Prefix);
        var offsets = new List<uint>();
        var records = new List<(EntrySpec Entry, byte[] Name, uint Crc, uint Compressed, uint Uncompressed, int Method)>();
        foreach (var entry in spec.Entries)
        {
            output.Write(entry.GapBefore);
            var data = entry.Content ?? [];
            var crc = entry.Crc ?? Crc32.Compute(data);
            var compressed = entry.Method == 8 ? Deflate(data) : data;
            var name = Encoding.UTF8.GetBytes(entry.Name);
            var localName = entry.LocalName is null ? name : Encoding.UTF8.GetBytes(entry.LocalName);
            offsets.Add(checked((uint)output.Position));
            var local = new byte[30];
            BinaryPrimitives.WriteUInt32LittleEndian(local, 0x04034B50);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(4), 20);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(6), (ushort)(entry.LocalFlags ?? Flags(entry)));
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(8), (ushort)(entry.LocalMethod ?? entry.Method));
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(12), (1 << 5) | 1);
            if (!entry.DataDescriptor)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(14), entry.LocalCrc ?? crc);
                BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(18), (uint)compressed.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(local.AsSpan(22), entry.LocalUncompressedSize ?? (uint)data.Length);
            }

            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(26), (ushort)localName.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(local.AsSpan(28), (ushort)entry.LocalExtra.Length);
            output.Write(local);
            output.Write(localName);
            output.Write(entry.LocalExtra);
            output.Write(compressed);
            if (entry.DataDescriptor)
            {
                var descriptor = new byte[16];
                BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 0x08074B50);
                BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4), crc);
                BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(8), (uint)compressed.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(12), (uint)data.Length);
                output.Write(descriptor);
            }

            records.Add((entry, name, crc, (uint)compressed.Length, (uint)data.Length, entry.Method));
        }

        output.Write(spec.Trailing);
        var centralOffset = checked((uint)output.Position);
        for (var i = 0; i < records.Count; i++)
        {
            var (entry, name, crc, compressed, uncompressed, method) = records[i];
            var record = new byte[46];
            BinaryPrimitives.WriteUInt32LittleEndian(record, 0x02014B50);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(4), (ushort)entry.MadeBy);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(6), 20);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(8), (ushort)Flags(entry));
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(10), (ushort)method);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(14), (1 << 5) | 1);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(16), crc);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(20), entry.Zip64Sizes ? uint.MaxValue : compressed);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(24), entry.Zip64Sizes ? uint.MaxValue : uncompressed);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(28), (ushort)name.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(30), (ushort)entry.CentralExtra.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(38), entry.ExternalAttributes ?? (entry.Folder ? 0x10u : 0u));
            BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(42), offsets[entry.LocalOffsetOf ?? i]);
            output.Write(record);
            output.Write(name);
            output.Write(entry.CentralExtra);
        }

        var centralSize = checked((uint)output.Position - centralOffset);
        if (spec.Zip64Locator)
        {
            var locator = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(locator, 0x07064B50);
            output.Write(locator);
        }

        var end = new byte[22];
        BinaryPrimitives.WriteUInt32LittleEndian(end, 0x06054B50);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(4), (ushort)(spec.DiskNumber ?? 0));
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(8), (ushort)(spec.EntriesOnDisk ?? records.Count));
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(10), (ushort)records.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(12), centralSize);
        BinaryPrimitives.WriteUInt32LittleEndian(end.AsSpan(16), centralOffset);
        BinaryPrimitives.WriteUInt16LittleEndian(end.AsSpan(20), (ushort)spec.Comment.Length);
        output.Write(end);
        output.Write(spec.Comment);
        output.Write(spec.AfterEnd);
        return output.ToArray();
    }

    /// <summary>The generator's extension.package.json: the archive's grammar-valid files, sorted, with the manifest hash of extension.json.</summary>
    public static byte[] Descriptor(ArchiveSpec spec)
    {
        var files = spec.Entries
            .Where(entry => !entry.Folder && !entry.Descriptor && PackageReader.IsEntryName(entry.Name))
            .GroupBy(entry => entry.Name, StringComparer.Ordinal).Select(group => group.First())
            .Select(entry => (Path: entry.Name, Size: (long)(entry.Content ?? []).Length, Sha256: Convert.ToHexStringLower(SHA256.HashData(entry.Content ?? []))))
            .ToList();
        var manifest = spec.Entries.FirstOrDefault(entry => entry.Name == "extension.json")?.Content ?? [];
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Projection(manifest))));
        var archiveVersion = 2;
        if (spec.DescriptorChanges is { } changes)
        {
            if (changes.TryGetProperty("drop", out var drop))
            {
                files.RemoveAll(file => file.Path == drop.GetString());
            }

            if (changes.TryGetProperty("add", out var add))
            {
                files.Add((add.GetProperty("path").GetString()!, add.GetProperty("size").GetInt64(), add.GetProperty("sha256").GetString()!));
            }

            if (changes.TryGetProperty("sha256", out var sha))
            {
                var at = files.FindIndex(file => file.Path == sha.GetProperty("path").GetString());
                files[at] = (files[at].Path, files[at].Size, sha.GetProperty("value").GetString()!);
            }

            if (changes.TryGetProperty("size", out var size))
            {
                var at = files.FindIndex(file => file.Path == size.GetProperty("path").GetString());
                files[at] = (files[at].Path, size.GetProperty("value").GetInt64(), files[at].Sha256);
            }

            if (changes.TryGetProperty("manifestHash", out var manifestHash))
            {
                hash = manifestHash.GetString()!;
            }

            if (changes.TryGetProperty("archiveVersion", out var version))
            {
                archiveVersion = version.GetInt32();
            }
        }

        files.Sort((x, y) => string.CompareOrdinal(x.Path, y.Path));
        return Fixtures.WriteJson(writer =>
        {
            writer.WriteStartObject();
            writer.WriteNumber("archiveVersion", archiveVersion);
            writer.WriteString("manifestHash", hash);
            writer.WriteStartArray("files");
            foreach (var (path, length, sha256) in files)
            {
                writer.WriteStartObject();
                writer.WriteString("path", path);
                writer.WriteNumber("size", length);
                writer.WriteString("sha256", sha256);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        });
    }

    /// <summary>The contract projection (contract §B.2), computed from JSON without validating it.</summary>
    public static string Projection(byte[] manifest)
    {
        try
        {
            using var document = JsonDocument.Parse(manifest);
            var root = document.RootElement;
            var text = new StringBuilder("{\"contributions\":[");
            var contributions = root.GetProperty("contributions").EnumerateArray()
                .OrderBy(c => c.GetProperty("id").GetString(), StringComparer.Ordinal).ToList();
            for (var i = 0; i < contributions.Count; i++)
            {
                var c = contributions[i];
                text.Append(i == 0 ? "" : ",").Append("{\"id\":\"").Append(c.GetProperty("id").GetString()).Append("\",\"provides\":[");
                text.AppendJoin(',', c.GetProperty("provides").EnumerateArray().Select(p => "\"" + p.GetString() + "\"").Order(StringComparer.Ordinal));
                text.Append(']');
                if (c.TryGetProperty("settings", out var settings) && settings.GetArrayLength() > 0)
                {
                    text.Append(",\"settings\":[");
                    text.AppendJoin(',', settings.EnumerateArray().OrderBy(s => s.GetProperty("id").GetString(), StringComparer.Ordinal).Select(s =>
                        "{\"choices\":[" + string.Join(',', s.GetProperty("choices").EnumerateArray().Select(x => "\"" + x.GetProperty("value").GetString() + "\"").Order(StringComparer.Ordinal))
                        + "],\"id\":\"" + s.GetProperty("id").GetString() + "\",\"kind\":\"" + s.GetProperty("kind").GetString() + "\"}"));
                    text.Append(']');
                }

                text.Append('}');
            }

            text.Append("],\"id\":\"").Append(root.GetProperty("id").GetString()).Append("\",\"schemaVersion\":")
                .Append(root.GetProperty("schemaVersion").GetInt32().ToString(CultureInfo.InvariantCulture)).Append('}');
            return text.ToString();
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return string.Empty;
        }
    }

    /// <summary>Reads one archive description of fixtures/packages/archives.json.</summary>
    public static ArchiveSpec FromJson(JsonElement archive, JsonElement contents)
    {
        if (archive.TryGetProperty("raw", out var raw))
        {
            return new ArchiveSpec { Raw = Encoding.UTF8.GetBytes(raw.GetString()!) };
        }

        var entries = new List<EntrySpec>();
        foreach (var item in archive.GetProperty("entries").EnumerateArray())
        {
            var local = item.TryGetProperty("local", out var l) ? l : (JsonElement?)null;
            byte[]? content = null;
            var descriptor = false;
            if (item.TryGetProperty("content", out var c))
            {
                if (c.ValueKind == JsonValueKind.String && c.GetString() == "$descriptor")
                {
                    descriptor = true;
                }
                else
                {
                    content = Content(c.ValueKind == JsonValueKind.String ? contents.GetProperty(c.GetString()!) : c);
                }
            }

            entries.Add(new EntrySpec
            {
                Name = item.GetProperty("name").GetString()!,
                Content = content,
                Descriptor = descriptor,
                Folder = item.TryGetProperty("folder", out var folder) && folder.GetBoolean(),
                Method = item.TryGetProperty("method", out var method) ? method.GetInt32() : 0,
                Flags = item.TryGetProperty("flags", out var flags) ? flags.GetInt32() : 0,
                MadeBy = item.TryGetProperty("madeBy", out var madeBy) ? madeBy.GetInt32() : 20,
                ExternalAttributes = item.TryGetProperty("externalAttributes", out var attributes) ? attributes.GetUInt32() : null,
                Crc = item.TryGetProperty("crc", out var crc) ? crc.GetUInt32() : null,
                DataDescriptor = item.TryGetProperty("dataDescriptor", out var dd) && dd.GetBoolean(),
                Zip64Sizes = item.TryGetProperty("zip64Sizes", out var z) && z.GetBoolean(),
                CentralExtra = item.TryGetProperty("centralExtra", out var ce) ? Convert.FromHexString(ce.GetString()!) : [],
                LocalName = local?.TryGetProperty("name", out var ln) == true ? ln.GetString() : null,
                LocalMethod = local?.TryGetProperty("method", out var lm) == true ? lm.GetInt32() : null,
                LocalFlags = local?.TryGetProperty("flags", out var lf) == true ? lf.GetInt32() : null,
                LocalCrc = local?.TryGetProperty("crc", out var lc) == true ? lc.GetUInt32() : null,
                LocalUncompressedSize = local?.TryGetProperty("uncompressedSize", out var lu) == true ? lu.GetUInt32() : null,
                LocalExtra = local?.TryGetProperty("extra", out var le) == true ? Convert.FromHexString(le.GetString()!) : [],
                LocalOffsetOf = item.TryGetProperty("localOffsetOf", out var lo) ? lo.GetInt32() : null,
                GapBefore = item.TryGetProperty("gapBefore", out var gap) ? Convert.FromHexString(gap.GetString()!) : [],
            });
        }

        return new ArchiveSpec
        {
            Entries = entries,
            Prefix = archive.TryGetProperty("prefix", out var prefix) ? Convert.FromHexString(prefix.GetString()!) : [],
            Trailing = archive.TryGetProperty("trailing", out var trailing) ? Convert.FromHexString(trailing.GetString()!) : [],
            AfterEnd = archive.TryGetProperty("afterEnd", out var after) ? Convert.FromHexString(after.GetString()!) : [],
            DiskNumber = archive.TryGetProperty("eocd", out var eocd) && eocd.TryGetProperty("diskNumber", out var disk) ? disk.GetInt32() : null,
            EntriesOnDisk = archive.TryGetProperty("eocd", out var eocd2) && eocd2.TryGetProperty("entriesOnDisk", out var count) ? count.GetInt32() : null,
            Zip64Locator = archive.TryGetProperty("zip64Locator", out var locator) && locator.GetBoolean(),
            DescriptorChanges = archive.TryGetProperty("descriptor", out var changes) ? changes.Clone() : null,
        };
    }

    private static byte[] Content(JsonElement content) =>
        content.TryGetProperty("fixture", out var fixture)
            ? Fixtures.Text(fixture.GetString()!)
            : Encoding.UTF8.GetBytes(content.GetProperty("text").GetString()!);

    private static int Flags(EntrySpec entry) => entry.Flags | (entry.DataDescriptor ? 0x0008 : 0);

    private static byte[] Deflate(byte[] data)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            deflate.Write(data);
        }

        return output.ToArray();
    }
}
