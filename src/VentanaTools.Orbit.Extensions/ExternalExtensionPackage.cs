// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VentanaTools.Orbit.Extensions;

/// <summary>Host compatibility requirements, never executable entry points or consent.</summary>
public sealed record ExternalExtensionPackageDescriptor(int ProtocolVersion, IReadOnlyList<string> RequiredCapabilities);

public static class ExternalExtensionPackageDescriptorReader
{
    public const int MaxBytes = 4096;

    public static ExternalExtensionPackageDescriptor? Read(ReadOnlySpan<byte> bytes)
    {
        try
        {
            using var document = ExtensionJson.ReadObject(bytes.ToArray(), MaxBytes);
            var root = document.RootElement;
            if (!ExtensionJson.HasFields(root, ["schemaVersion", "protocolVersion", "requiredCapabilities"])
                || !root.GetProperty("schemaVersion").TryGetInt32(out var schema) || schema != 1
                || !root.GetProperty("protocolVersion").TryGetInt32(out var protocol) || protocol != 2)
                return null;
            var array = root.GetProperty("requiredCapabilities");
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 3)
                return null;
            var capabilities = new List<string>();
            foreach (var entry in array.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String || entry.GetString() is not { } capability
                    || capability is not ("invoke" or "face" or "settings") || capabilities.Contains(capability, StringComparer.Ordinal))
                    return null;
                capabilities.Add(capability);
            }
            return new(protocol, capabilities.AsReadOnly());
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    public static byte[] Serialize(ExternalExtensionPackageDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.ProtocolVersion != 2 || descriptor.RequiredCapabilities is not { Count: >= 1 and <= 3 })
            throw new InvalidDataException("Invalid extension package requirements.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var capability in descriptor.RequiredCapabilities)
            if (capability is not ("invoke" or "face" or "settings") || !seen.Add(capability))
                throw new InvalidDataException("Invalid extension package requirements.");
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteNumber("schemaVersion", 1);
            json.WriteNumber("protocolVersion", descriptor.ProtocolVersion);
            json.WriteStartArray("requiredCapabilities");
            foreach (var capability in descriptor.RequiredCapabilities) json.WriteStringValue(capability);
            json.WriteEndArray();
            json.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static ExternalExtensionPackageDescriptor Snapshot(ExternalExtensionPackageDescriptor descriptor) =>
        Read(Serialize(descriptor)) ?? throw new InvalidDataException("Invalid extension package requirements.");

    public static bool CoversManifest(ExternalExtensionPackageDescriptor descriptor, ExternalExtensionManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(manifest);
        return manifest.ManifestVersion == 2 && descriptor.ProtocolVersion == 2
            && (!manifest.Actions.Any(action => action.CanInvoke) || descriptor.RequiredCapabilities.Contains("invoke", StringComparer.Ordinal))
            && (!manifest.Actions.Any(action => action.HasFace) || descriptor.RequiredCapabilities.Contains("face", StringComparer.Ordinal))
            && (!manifest.Actions.Any(action => action.Settings.Count != 0) || descriptor.RequiredCapabilities.Contains("settings", StringComparer.Ordinal));
    }
}

/// <summary>An immutable file body. Reading never exposes the package's owned buffer.</summary>
public sealed class ExternalExtensionPackageFile
{
    private readonly byte[] _bytes;
    internal ExternalExtensionPackageFile(string path, byte[] bytes) { Path = path; _bytes = bytes; }
    public string Path { get; }
    public int Length => _bytes.Length;
    public Stream OpenRead() => new MemoryStream(_bytes, writable: false);
}

/// <summary>Validated inert content for a separate, explicit installation transaction.</summary>
public sealed class ExternalExtensionPackage
{
    internal ExternalExtensionPackage(ExternalExtensionManifest manifest, ExternalExtensionPackageDescriptor descriptor,
        string readme, string sha256, IReadOnlyList<ExternalExtensionPackageFile> files)
    {
        Manifest = manifest; Descriptor = descriptor; Readme = readme; Sha256 = sha256; Files = files;
    }
    public ExternalExtensionManifest Manifest { get; }
    public ExternalExtensionPackageDescriptor Descriptor { get; }
    public string Readme { get; }
    public string Sha256 { get; }
    public IReadOnlyList<ExternalExtensionPackageFile> Files { get; }
    public override string ToString() => nameof(ExternalExtensionPackage);
}

public sealed record ExternalExtensionPackageRead(ExternalExtensionPackage? Package, string? Error);

/// <summary>Reads bounded local ZIP packages without extracting, loading or starting their contents.</summary>
public static class ExternalExtensionPackageReader
{
    public const int MaxArchiveBytes = 32 * 1024 * 1024;
    public const int MaxExpandedBytes = 64 * 1024 * 1024;
    public const int MaxFileBytes = 16 * 1024 * 1024;
    public const int MaxEntries = 128;
    public const int MaxReadmeBytes = 65536;
    public const int MaxPathLength = 240;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly uint[] CrcTable = CreateCrcTable();

    public static ExternalExtensionPackageRead Read(ReadOnlyMemory<byte> bytes)
    {
        if (bytes.Length is 0 or > MaxArchiveBytes) return Failure("package.size");
        try
        {
            // Snapshot the archive once: caller mutation cannot change reviewed content.
            var archiveBytes = bytes.ToArray();
            if (DirectoryError(archiveBytes) is { } directoryError) return Failure(directoryError);
            using var stream = new MemoryStream(archiveBytes, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false, Utf8);
            if (archive.Entries.Count is < 3 or > MaxEntries) return Failure("package.entries");
            var explicitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var nodes = new Dictionary<string, (string Spelling, bool Directory)>(StringComparer.OrdinalIgnoreCase);
            var files = new List<ExternalExtensionPackageFile>();
            byte[]? manifestBytes = null, descriptorBytes = null, readmeBytes = null;
            var expanded = 0;
            foreach (var entry in archive.Entries)
            {
                var path = entry.FullName;
                var directory = path.EndsWith('/');
                var name = directory ? path[..^1] : path;
                if (!ValidPath(name, directory) || !ValidAttributes(entry.ExternalAttributes, directory)
                    || !explicitNames.Add(name) || !AddNodes(nodes, name, directory))
                    return Failure("package.path");
                if (entry.Length < 0 || entry.Length > MaxFileBytes || entry.CompressedLength > MaxArchiveBytes)
                    return Failure("package.size");
                if (directory)
                {
                    if (entry.Length != 0) return Failure("package.path");
                    using var directoryStream = entry.Open();
                    if (directoryStream.ReadByte() != -1 || entry.Crc32 != 0) return Failure("package.path");
                    continue;
                }
                var limit = name switch
                {
                    "extension.json" => ExternalExtensionManifestReader.MaxBytes,
                    "package.json" => ExternalExtensionPackageDescriptorReader.MaxBytes,
                    "README.md" => MaxReadmeBytes,
                    _ => MaxFileBytes
                };
                if (entry.Length > limit || entry.Length > MaxExpandedBytes - expanded)
                    return Failure("package.size");
                using var input = entry.Open();
                using var content = new MemoryStream((int)entry.Length);
                var chunk = new byte[81920];
                var crc = uint.MaxValue;
                while (true)
                {
                    var count = input.Read(chunk, 0, chunk.Length);
                    if (count == 0) break;
                    if (count > limit - content.Length || count > MaxExpandedBytes - expanded)
                        return Failure("package.size");
                    content.Write(chunk, 0, count);
                    crc = UpdateCrc(crc, chunk.AsSpan(0, count));
                    expanded += count;
                }
                if (content.Length != entry.Length || ~crc != entry.Crc32) return Failure("package.archive");
                var body = content.ToArray();
                files.Add(new(name, body));
                switch (name)
                {
                    case "extension.json": manifestBytes = body; break;
                    case "package.json": descriptorBytes = body; break;
                    case "README.md": readmeBytes = body; break;
                }
            }
            if (manifestBytes is null || descriptorBytes is null || readmeBytes is null) return Failure("package.files");
            var manifest = ExternalExtensionManifestReader.Read(manifestBytes).Manifest;
            var descriptor = ExternalExtensionPackageDescriptorReader.Read(descriptorBytes);
            if (manifest is null || manifest.ManifestVersion != 2) return Failure("package.manifest");
            if (descriptor is null || !ExternalExtensionPackageDescriptorReader.CoversManifest(descriptor, manifest))
                return Failure("package.requirements");
            var readme = Utf8.GetString(readmeBytes);
            if (string.IsNullOrWhiteSpace(readme) || readme.Contains('\0', StringComparison.Ordinal))
                return Failure("package.readme");
            return new(new(manifest, descriptor, readme, Convert.ToHexStringLower(SHA256.HashData(archiveBytes)), files.AsReadOnly()), null);
        }
        catch (DecoderFallbackException) { return Failure("package.readme"); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or NotSupportedException or OverflowException)
        {
            return Failure("package.archive");
        }
    }

    private static ExternalExtensionPackageRead Failure(string error) => new(null, error);

    private static uint[] CreateCrcTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++) value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320U : value >> 1;
            table[index] = value;
        }
        return table;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes) crc = CrcTable[(crc ^ value) & 0xFF] ^ (crc >> 8);
        return crc;
    }

    // Bound central-directory allocation before ZipArchive materializes its entries.
    private static string? DirectoryError(ReadOnlySpan<byte> bytes)
    {
        var minimum = Math.Max(0, bytes.Length - 22 - ushort.MaxValue);
        var end = bytes.Length - 22;
        for (; end >= minimum; end--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(bytes[end..]) == 0x06054B50
                && end + 22 + BinaryPrimitives.ReadUInt16LittleEndian(bytes[(end + 20)..]) == bytes.Length) break;
        if (end < minimum) return "package.archive";
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[(end + 4)..]) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(bytes[(end + 6)..]) != 0
            || (end >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(bytes[(end - 20)..]) == 0x07064B50))
            return "package.archive";
        var count = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(end + 10)..]);
        if (count is < 3 or > MaxEntries) return "package.entries";
        if (BinaryPrimitives.ReadUInt16LittleEndian(bytes[(end + 8)..]) != count) return "package.archive";
        var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(end + 12)..]);
        var start = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(end + 16)..]);
        if ((long)start + size != end) return "package.archive";
        var at = (int)start;
        for (var entry = 0; entry < count; entry++)
        {
            if (at > end - 46 || BinaryPrimitives.ReadUInt32LittleEndian(bytes[at..]) != 0x02014B50)
                return "package.archive";
            if ((BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at + 8)..]) & 0x2041) != 0
                || BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at + 34)..]) != 0
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 20)..]) == uint.MaxValue
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 24)..]) == uint.MaxValue
                || BinaryPrimitives.ReadUInt32LittleEndian(bytes[(at + 42)..]) >= start)
                return "package.archive";
            var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at + 28)..]);
            var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at + 30)..]);
            var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(at + 32)..]);
            var extraAt = at + 46 + nameLength;
            var next = extraAt + extraLength + commentLength;
            if (next > end) return "package.archive";
            var extraEnd = extraAt + extraLength;
            while (extraAt < extraEnd)
            {
                if (extraAt > extraEnd - 4 || BinaryPrimitives.ReadUInt16LittleEndian(bytes[extraAt..]) == 1)
                    return "package.archive";
                extraAt += 4 + BinaryPrimitives.ReadUInt16LittleEndian(bytes[(extraAt + 2)..]);
                if (extraAt > extraEnd) return "package.archive";
            }
            at = next;
        }
        return at == end ? null : "package.archive";
    }

    private static bool ValidPath(string path, bool directory)
    {
        if (path.Length is 0 or > MaxPathLength) return false;
        if (directory ? path != "payload" && !path.StartsWith("payload/", StringComparison.Ordinal)
            : path is not ("extension.json" or "package.json" or "README.md") && !path.StartsWith("payload/", StringComparison.Ordinal))
            return false;
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length is 0 or > 80 || segment is "." or ".." || segment.EndsWith('.')
                || segment.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))) return false;
            var basename = segment.Split('.')[0];
            if (basename.Equals("CON", StringComparison.OrdinalIgnoreCase) || basename.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || basename.Equals("AUX", StringComparison.OrdinalIgnoreCase) || basename.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || (basename.Length == 4 && basename[3] is >= '1' and <= '9'
                    && (basename.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || basename.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))))
                return false;
        }
        return true;
    }

    private static bool ValidAttributes(int attributes, bool directory)
    {
        var unixType = (attributes >> 16) & 0xF000;
        if (unixType != 0 && unixType != (directory ? 0x4000 : 0x8000)) return false;
        var dos = attributes & 0xFFFF;
        const int allowed = 0x01 | 0x02 | 0x04 | 0x10 | 0x20 | 0x80;
        return (dos & ~allowed) == 0 && ((dos & 0x10) == 0 || directory);
    }

    private static bool AddNodes(Dictionary<string, (string Spelling, bool Directory)> nodes, string path, bool directory)
    {
        var end = 0;
        while (true)
        {
            end = path.IndexOf('/', end);
            var node = end < 0 ? path : path[..end];
            var nodeDirectory = end >= 0 || directory;
            if (nodes.TryGetValue(node, out var existing))
            {
                if (existing.Spelling != node || existing.Directory != nodeDirectory) return false;
            }
            else nodes.Add(node, (node, nodeDirectory));
            if (end < 0) return true;
            end++;
        }
    }
}
