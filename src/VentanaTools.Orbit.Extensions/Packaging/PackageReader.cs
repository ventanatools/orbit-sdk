// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Buffers.Binary;
using System.Collections.ObjectModel;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace VentanaTools.Orbit.Extensions.Packaging;

/// <summary>
/// Verifies package archives (contract §5): archive version 2, in the order of contract §5.7,
/// collecting every diagnostic it can. Nothing in the archive is executed, loaded or extracted.
/// </summary>
/// <remarks>
/// The reader validates the end-of-central-directory record and the whole central directory
/// before decompressing anything, requires every local header to agree with its central record,
/// requires the local records to be ascending, non-overlapping and contiguous from offset 0 to the
/// central directory, and enforces every limit of contract §5.3 while it decompresses. Archive
/// diagnostics name the entry they concern in <see cref="Diagnostic.File"/> when the entry name is
/// printable ASCII. The members are safe to call from any thread.
/// </remarks>
public static class PackageReader
{
    /// <summary>The largest archive, in bytes (32 MiB).</summary>
    public const long MaxArchiveBytes = 32L * 1024 * 1024;

    internal const long MaxExpandedBytes = 64L * 1024 * 1024;
    internal const int MaxPayloadFileBytes = 16 * 1024 * 1024;
    internal const int MaxDescriptorBytes = 256 * 1024;
    internal const int MaxReadmeBytes = 65_536;
    internal const int MaxEntries = 512;
    internal const int MaxNameLength = 240;
    internal const int MaxSegmentLength = 80;
    internal const int MaxStringsFiles = 64;

    internal const string ManifestName = "extension.json";
    internal const string DescriptorName = "extension.package.json";
    internal const string ReadmeName = "README.md";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>Verifies a package from its bytes, which are copied first.</summary>
    /// <param name="archive">The archive.</param>
    /// <param name="options">Options; null for the defaults.</param>
    /// <returns>The package when it is valid, and every diagnostic.</returns>
    public static ReadResult<ExtensionPackage> Read(ReadOnlyMemory<byte> archive, PackageReadOptions? options = null)
    {
        options ??= new PackageReadOptions();
        var bag = new DiagnosticBag();
        if (archive.Length > MaxArchiveBytes)
        {
            bag.Report(DiagnosticCodes.PackageTooLarge, string.Empty);
            return new ReadResult<ExtensionPackage>(null, bag.ToList());
        }

        var bytes = archive.ToArray();
        var package = Verify(bytes, options, bag);
        return new ReadResult<ExtensionPackage>(package, bag.ToList());
    }

    /// <summary>Verifies a package file, never reading more than <see cref="MaxArchiveBytes"/> + 1 bytes.</summary>
    /// <param name="path">The package file's path.</param>
    /// <param name="options">Options; null for the defaults.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The package when it is valid, and every diagnostic.</returns>
    /// <exception cref="IOException">The file cannot be read.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    public static async Task<ReadResult<ExtensionPackage>> ReadFileAsync(string path, PackageReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var bytes = await BoundedFile.ReadAsync(path, MaxArchiveBytes, cancellationToken).ConfigureAwait(false);
        return Read(bytes, options);
    }

    private static ExtensionPackage? Verify(byte[] bytes, PackageReadOptions options, DiagnosticBag bag)
    {
        // 1. The central directory, local headers and their layout.
        var structure = ZipStructure.Read(bytes, bag);
        if (structure is null)
        {
            return null;
        }

        // 2. Entry names, attributes and counts.
        CheckNames(structure.Entries, bag);
        if (structure.Broken)
        {
            return null;
        }

        // 3. Decompression with limits and CRC-32.
        if (!Decompress(bytes, structure.Entries, bag))
        {
            return null;
        }

        var files = structure.Entries.Where(entry => !entry.IsFolder && entry.Data is not null).ToList();
        var byName = new Dictionary<string, ZipEntry>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            byName.TryAdd(file.Name!, file);
        }

        // 4. The three required files.
        foreach (var required in new[] { ManifestName, DescriptorName, ReadmeName })
        {
            if (!structure.Entries.Any(entry => entry.Name == required))
            {
                bag.Report(DiagnosticCodes.PackageFileMissing, string.Empty, required);
            }
        }

        // 5. The descriptor.
        PackageDescriptor? descriptor = null;
        if (byName.TryGetValue(DescriptorName, out var descriptorEntry))
        {
            descriptor = DescriptorReader.Read(descriptorEntry.Data!, bag);
            if (descriptor is null)
            {
                bag.Report(DiagnosticCodes.PackageDescriptor, string.Empty);
            }
        }

        // 6. The inventory.
        if (descriptor is not null)
        {
            Inventory(descriptor, structure.Entries.Where(entry => entry.Name is not null && !entry.IsFolder).ToList(), bag);
        }

        // 7. The manifest and the descriptor's manifest hash.
        ExtensionManifest? manifest = null;
        if (byName.TryGetValue(ManifestName, out var manifestEntry))
        {
            manifest = ManifestReader.ReadInto(manifestEntry.Data!, options.Manifest ?? new ManifestReadOptions(), ManifestName, bag);
            if (manifest is not null && descriptor is not null
                && !string.Equals(ManifestWriter.ComputeHash(manifest), descriptor.ManifestHash, StringComparison.Ordinal))
            {
                bag.Report(DiagnosticCodes.PackageManifestHash, "/manifestHash", DescriptorName);
            }
        }

        // 8. Strings files.
        var strings = Strings(files, manifest, options, bag);

        // 9. The readme.
        string? readme = null;
        if (byName.TryGetValue(ReadmeName, out var readmeEntry))
        {
            readme = Readme(readmeEntry.Data!);
            if (readme is null)
            {
                bag.Report(DiagnosticCodes.PackageReadme, string.Empty, ReadmeName);
            }
        }

        if (bag.HasErrors || manifest is null || descriptor is null || readme is null)
        {
            return null;
        }

        return new ExtensionPackage(manifest, descriptor, strings, readme, Convert.ToHexStringLower(SHA256.HashData(bytes)),
            new ReadOnlyCollection<PackageFile>(files.Select(file => new PackageFile(file.Name!, file.Data!)).ToArray()));
    }

    private static void CheckNames(IReadOnlyList<ZipEntry> entries, DiagnosticBag bag)
    {
        var nodes = new Dictionary<string, (string Spelling, bool Folder)>(StringComparer.OrdinalIgnoreCase);
        var explicitNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.OrderBy(entry => entry.LocalOffset))
        {
            if (entry.Name is null)
            {
                bag.Report(DiagnosticCodes.PackagePath, string.Empty);
                continue;
            }

            var file = entry.DisplayName;
            var name = entry.IsFolder ? entry.Name[..^1] : entry.Name;
            if (!IsEntryName(name))
            {
                bag.Report(DiagnosticCodes.PackagePath, string.Empty, file);
                entry.Usable = false;
                continue;
            }

            if (!IsAllowedPlace(name, entry.IsFolder))
            {
                bag.Report(DiagnosticCodes.PackagePathReserved, string.Empty, file);
                entry.Usable = false;
                continue;
            }

            if (!AttributesAllowed(entry.ExternalAttributes, entry.IsFolder))
            {
                bag.Report(DiagnosticCodes.PackageLink, string.Empty, file);
                entry.Usable = false;
                continue;
            }

            if (entry.IsFolder && (entry.UncompressedSize != 0 || entry.Crc != 0))
            {
                bag.Report(DiagnosticCodes.PackagePath, string.Empty, file);
                entry.Usable = false;
                continue;
            }

            if (!explicitNames.Add(name) || !AddNodes(nodes, name, entry.IsFolder))
            {
                bag.Report(DiagnosticCodes.PackagePathConflict, string.Empty, file);
                entry.Usable = false;
                continue;
            }

            entry.NameValid = true;
        }
    }

    /// <summary>The entry-name grammar of contract §5.4, without the trailing slash of a folder.</summary>
    internal static bool IsEntryName(string name)
    {
        if (name.Length is 0 or > MaxNameLength)
        {
            return false;
        }

        foreach (var segment in name.Split('/'))
        {
            if (segment.Length is 0 or > MaxSegmentLength || segment is "." or ".." || segment.EndsWith('.')
                || !segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            {
                return false;
            }

            var stem = segment.Split('.')[0];
            if (IsDeviceName(stem))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDeviceName(string stem) =>
        stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
        || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
        || (stem.Length == 4 && stem[3] is >= '1' and <= '9'
            && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)));

    private static bool IsAllowedPlace(string name, bool folder)
    {
        if (!folder && name is ManifestName or DescriptorName or ReadmeName)
        {
            return true;
        }

        if (name == "payload" && folder)
        {
            return true;
        }

        if (name.StartsWith("payload/", StringComparison.Ordinal))
        {
            return true;
        }

        if (name == "strings")
        {
            return folder;
        }

        if (name.StartsWith("strings/", StringComparison.Ordinal))
        {
            var rest = name["strings/".Length..];
            return !folder && !rest.Contains('/', StringComparison.Ordinal) && rest.EndsWith(".json", StringComparison.Ordinal);
        }

        return false;
    }

    private static bool AttributesAllowed(uint attributes, bool folder)
    {
        var unixType = (attributes >> 16) & 0xF000;
        if (unixType != 0 && unixType != (folder ? 0x4000u : 0x8000u))
        {
            return false;
        }

        var dos = attributes & 0xFFFF;
        const uint allowed = 0x01 | 0x02 | 0x04 | 0x10 | 0x20 | 0x80;
        return (dos & ~allowed) == 0 && ((dos & 0x10) == 0 || folder);
    }

    private static bool AddNodes(Dictionary<string, (string Spelling, bool Folder)> nodes, string path, bool folder)
    {
        var end = 0;
        while (true)
        {
            end = path.IndexOf('/', end);
            var node = end < 0 ? path : path[..end];
            var nodeFolder = end >= 0 || folder;
            if (nodes.TryGetValue(node, out var existing))
            {
                if (existing.Spelling != node || existing.Folder != nodeFolder || (end < 0 && !folder))
                {
                    return false;
                }
            }
            else
            {
                nodes.Add(node, (node, nodeFolder));
            }

            if (end < 0)
            {
                return true;
            }

            end++;
        }
    }

    private static bool Decompress(byte[] bytes, IReadOnlyList<ZipEntry> entries, DiagnosticBag bag)
    {
        var files = entries.Where(entry => !entry.IsFolder && entry.Usable).ToList();
        if (files.Sum(file => file.UncompressedSize) > MaxExpandedBytes)
        {
            bag.Report(DiagnosticCodes.PackageExpandedTooLarge, string.Empty);
            return false;
        }

        long expanded = 0;
        foreach (var file in files)
        {
            var limit = LimitOf(file.Name!);
            if (file.UncompressedSize > limit)
            {
                bag.Report(file.Name == ReadmeName ? DiagnosticCodes.PackageReadme : DiagnosticCodes.PackageFileTooLarge,
                    string.Empty, file.DisplayName);
                continue;
            }

            var data = Inflate(bytes, file);
            if (data is null || Crc32.Compute(data) != file.Crc)
            {
                bag.Report(DiagnosticCodes.PackageCrc, string.Empty, file.DisplayName);
                continue;
            }

            expanded += data.Length;
            if (expanded > MaxExpandedBytes)
            {
                bag.Report(DiagnosticCodes.PackageExpandedTooLarge, string.Empty);
                return false;
            }

            file.Data = data;
        }

        return true;
    }

    private static long LimitOf(string name) => name switch
    {
        ManifestName => ManifestReader.MaxBytes,
        DescriptorName => MaxDescriptorBytes,
        ReadmeName => MaxReadmeBytes,
        _ when name.StartsWith("strings/", StringComparison.Ordinal) => StringsReader.MaxBytes,
        _ => MaxPayloadFileBytes,
    };

    private static byte[]? Inflate(byte[] bytes, ZipEntry entry)
    {
        var size = checked((int)entry.UncompressedSize);
        if (entry.Method == 0)
        {
            return entry.CompressedSize == entry.UncompressedSize
                ? bytes.AsSpan(entry.DataStart, size).ToArray()
                : null;
        }

        try
        {
            using var input = new MemoryStream(bytes, entry.DataStart, checked((int)entry.CompressedSize), writable: false);
            using var deflate = new DeflateStream(input, CompressionMode.Decompress);
            var data = new byte[size];
            var read = 0;
            while (read < size)
            {
                var count = deflate.Read(data, read, size - read);
                if (count == 0)
                {
                    return null;
                }

                read += count;
            }

            Span<byte> extra = stackalloc byte[1];
            return deflate.Read(extra) == 0 ? data : null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    private static void Inventory(PackageDescriptor descriptor, List<ZipEntry> files, DiagnosticBag bag)
    {
        var archive = new Dictionary<string, ZipEntry>(StringComparer.Ordinal);
        foreach (var file in files.Where(file => file.Name != DescriptorName))
        {
            archive.TryAdd(file.Name!, file);
        }

        var listed = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < descriptor.Files.Count; i++)
        {
            var row = descriptor.Files[i];
            var path = JsonPointer.Append("/files", i);
            listed.Add(row.Path);
            if (!archive.TryGetValue(row.Path, out var file))
            {
                bag.Report(DiagnosticCodes.PackageInventoryMissing, path, DescriptorName);
                continue;
            }

            if (file.Data is null)
            {
                continue;
            }

            if (file.Data.LongLength != row.Size
                || !string.Equals(Convert.ToHexStringLower(SHA256.HashData(file.Data)), row.Sha256, StringComparison.Ordinal))
            {
                bag.Report(DiagnosticCodes.PackageHashMismatch, path, DescriptorName);
            }
        }

        foreach (var file in archive.Values)
        {
            // An entry refused for its name or attributes is reported once, not again as unlisted.
            if (file.NameValid && !listed.Contains(file.Name!))
            {
                bag.Report(DiagnosticCodes.PackageInventoryExtra, string.Empty, file.DisplayName);
            }
        }
    }

    private static ReadOnlyCollection<ExtensionStrings> Strings(List<ZipEntry> files, ExtensionManifest? manifest,
        PackageReadOptions options, DiagnosticBag bag)
    {
        var result = new List<ExtensionStrings>();
        var stringFiles = files.Where(file => file.Name!.StartsWith("strings/", StringComparison.Ordinal)).ToList();
        if (stringFiles.Count > MaxStringsFiles)
        {
            bag.Report(DiagnosticCodes.StringsTooManyFiles, string.Empty);
        }

        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in stringFiles)
        {
            var tag = file.Name!["strings/".Length..^".json".Length];
            if (!tags.Add(tag))
            {
                bag.Report(DiagnosticCodes.StringsLanguageDuplicate, string.Empty, file.DisplayName);
                continue;
            }

            if (manifest is null)
            {
                continue;
            }

            var strings = StringsReader.ReadInto(file.Data!, tag,
                new StringsReadOptions { Manifest = manifest, KnownHosts = options.Manifest?.KnownHosts ?? HostRegistry.Known },
                file.Name, bag);
            if (strings is not null)
            {
                result.Add(strings);
            }
        }

        return result.AsReadOnly();
    }

    private static string? Readme(byte[] data)
    {
        var text = JsonFile.StripUtf8Bom(data);
        try
        {
            var readme = StrictUtf8.GetString(text);
            return string.IsNullOrWhiteSpace(readme) || readme.Contains('\0', StringComparison.Ordinal) ? null : readme;
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }
}

/// <summary>One central directory entry, joined with its local record.</summary>
internal sealed class ZipEntry
{
    public string? Name { get; set; }

    /// <summary>The name, when it is printable ASCII of at most 240 characters; otherwise null.</summary>
    public string? DisplayName { get; set; }

    public bool IsFolder { get; set; }

    public int Method { get; set; }

    public int Flags { get; set; }

    public uint Crc { get; set; }

    public long CompressedSize { get; set; }

    public long UncompressedSize { get; set; }

    public int LocalOffset { get; set; }

    public int DataStart { get; set; }

    public uint ExternalAttributes { get; set; }

    public byte[] NameBytes { get; set; } = [];

    /// <summary>Whether the entry may be decompressed: its central record, local record and name are all valid.</summary>
    public bool Usable { get; set; } = true;

    /// <summary>Whether the name passed the checks of contract §5.4.</summary>
    public bool NameValid { get; set; }

    public byte[]? Data { get; set; }
}

/// <summary>The structure checks of contract §5.5.</summary>
internal sealed class ZipStructure
{
    private const uint LocalSignature = 0x04034B50;
    private const uint CentralSignature = 0x02014B50;
    private const uint EndSignature = 0x06054B50;
    private const uint Zip64LocatorSignature = 0x07064B50;
    private const uint DescriptorSignature = 0x08074B50;

    public required List<ZipEntry> Entries { get; init; }

    /// <summary>Whether a structure or layout problem was reported, so nothing may be decompressed.</summary>
    public bool Broken { get; set; }

    /// <summary>Reads the directory and local records; null when the archive cannot be walked at all.</summary>
    public static ZipStructure? Read(byte[] bytes, DiagnosticBag bag)
    {
        var span = bytes.AsSpan();
        var end = FindEnd(span);
        if (end < 0)
        {
            bag.Report(DiagnosticCodes.PackageArchive, string.Empty);
            return null;
        }

        if (end >= 20 && U32(span, end - 20) == Zip64LocatorSignature)
        {
            bag.Report(DiagnosticCodes.PackageZip64, string.Empty);
            return null;
        }

        var count = U16(span, end + 10);
        var cdSize = U32(span, end + 12);
        var cdOffset = U32(span, end + 16);
        if (count == 0xFFFF || U16(span, end + 8) == 0xFFFF || cdSize == uint.MaxValue || cdOffset == uint.MaxValue)
        {
            bag.Report(DiagnosticCodes.PackageZip64, string.Empty);
            return null;
        }

        if (U16(span, end + 4) != 0 || U16(span, end + 6) != 0 || U16(span, end + 8) != count)
        {
            bag.Report(DiagnosticCodes.PackageArchive, string.Empty);
            return null;
        }

        if (count > PackageReader.MaxEntries)
        {
            bag.Report(DiagnosticCodes.PackageEntries, string.Empty);
            return null;
        }

        if ((long)cdOffset + cdSize != end)
        {
            bag.Report(DiagnosticCodes.PackageArchive, string.Empty);
            return null;
        }

        var structure = new ZipStructure { Entries = [] };
        var at = (int)cdOffset;
        for (var i = 0; i < count; i++)
        {
            if (at > end - 46 || U32(span, at) != CentralSignature)
            {
                bag.Report(DiagnosticCodes.PackageArchive, string.Empty);
                return null;
            }

            var nameLength = U16(span, at + 28);
            var extraLength = U16(span, at + 30);
            var commentLength = U16(span, at + 32);
            var next = at + 46 + nameLength + extraLength + commentLength;
            if (next > end)
            {
                bag.Report(DiagnosticCodes.PackageArchive, string.Empty);
                return null;
            }

            var entry = new ZipEntry
            {
                Flags = U16(span, at + 8),
                Method = U16(span, at + 10),
                Crc = U32(span, at + 16),
                CompressedSize = U32(span, at + 20),
                UncompressedSize = U32(span, at + 24),
                ExternalAttributes = U32(span, at + 38),
                NameBytes = span.Slice(at + 46, nameLength).ToArray(),
            };
            var localOffset = U32(span, at + 42);
            entry.LocalOffset = localOffset > int.MaxValue ? int.MaxValue : (int)localOffset;
            Name(entry);
            structure.Entries.Add(entry);
            var file = entry.DisplayName;

            if ((entry.Flags & (0x0001 | 0x0040 | 0x2000)) != 0)
            {
                bag.Report(DiagnosticCodes.PackageEncrypted, string.Empty, file);
                entry.Usable = false;
            }

            var extra = Extra(span.Slice(at + 46 + nameLength, extraLength));
            if (extra == ExtraKind.Zip64 || entry.CompressedSize == uint.MaxValue || entry.UncompressedSize == uint.MaxValue
                || localOffset == uint.MaxValue)
            {
                bag.Report(DiagnosticCodes.PackageZip64, string.Empty, file);
                entry.Usable = false;
                structure.Broken = true;
            }
            else if (extra == ExtraKind.Malformed || U16(span, at + 34) != 0 || entry.Method is not (0 or 8))
            {
                bag.Report(DiagnosticCodes.PackageArchive, string.Empty, file);
                entry.Usable = false;
                structure.Broken |= extra == ExtraKind.Malformed;
            }

            at = next;
        }

        if (at != end)
        {
            bag.Report(DiagnosticCodes.PackageArchive, string.Empty);
            return null;
        }

        if (!structure.Broken)
        {
            structure.Broken = !Local(span, structure.Entries, (int)cdOffset, bag);
        }

        return structure;
    }

    private static void Name(ZipEntry entry)
    {
        var bytes = entry.NameBytes;
        var utf8 = (entry.Flags & 0x0800) != 0;
        string? name = null;
        if (utf8 || bytes.All(b => b < 0x80))
        {
            try
            {
                name = new UTF8Encoding(false, true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                name = null;
            }
        }

        entry.Name = name;
        entry.IsFolder = name?.EndsWith('/') == true;
        entry.DisplayName = name is { Length: > 0 and <= PackageReader.MaxNameLength + 1 } && name.All(c => c is >= ' ' and <= '~')
            ? name
            : null;
        if (name is null)
        {
            entry.Usable = false;
        }
    }

    /// <summary>Checks every local record against its central record, and the layout.</summary>
    private static bool Local(ReadOnlySpan<byte> span, List<ZipEntry> entries, int cdOffset, DiagnosticBag bag)
    {
        var ok = true;
        var expected = 0;
        foreach (var entry in entries.OrderBy(entry => entry.LocalOffset))
        {
            var file = entry.DisplayName;
            var at = entry.LocalOffset;
            if (at != expected)
            {
                // Bytes before the first entry or between records, or records that overlap.
                bag.Report(DiagnosticCodes.PackageArchive, string.Empty, expected == 0 ? null : file);
                return false;
            }

            if (at > cdOffset - 30 || U32(span, at) != LocalSignature)
            {
                bag.Report(DiagnosticCodes.PackageArchive, string.Empty, file);
                return false;
            }

            var flags = U16(span, at + 6);
            var method = U16(span, at + 8);
            var crc = U32(span, at + 14);
            var compressed = U32(span, at + 18);
            var uncompressed = U32(span, at + 22);
            var nameLength = U16(span, at + 26);
            var extraLength = U16(span, at + 28);
            var dataStart = at + 30 + nameLength + extraLength;
            if (dataStart > cdOffset)
            {
                bag.Report(DiagnosticCodes.PackageArchive, string.Empty, file);
                return false;
            }

            var extra = Extra(span.Slice(at + 30 + nameLength, extraLength));
            if (extra == ExtraKind.Zip64)
            {
                bag.Report(DiagnosticCodes.PackageZip64, string.Empty, file);
                return false;
            }

            var descriptor = (entry.Flags & 0x0008) != 0;
            var agrees = extra != ExtraKind.Malformed
                && span.Slice(at + 30, nameLength).SequenceEqual(entry.NameBytes)
                && flags == entry.Flags
                && method == entry.Method
                && (crc == entry.Crc || (descriptor && crc == 0))
                && (descriptor
                    ? (compressed == entry.CompressedSize || compressed == 0) && (uncompressed == entry.UncompressedSize || uncompressed == 0)
                    : compressed == entry.CompressedSize && uncompressed == entry.UncompressedSize);
            if (!agrees)
            {
                bag.Report(DiagnosticCodes.PackageArchive, string.Empty, file);
                ok = false;
            }

            var dataEnd = (long)dataStart + entry.CompressedSize;
            if (dataEnd > cdOffset)
            {
                bag.Report(DiagnosticCodes.PackageArchive, string.Empty, file);
                return false;
            }

            var recordEnd = (int)dataEnd;
            if (descriptor)
            {
                var length = DescriptorLength(span, recordEnd, cdOffset, entry);
                if (length < 0)
                {
                    bag.Report(DiagnosticCodes.PackageArchive, string.Empty, file);
                    return false;
                }

                recordEnd += length;
            }

            entry.DataStart = dataStart;
            expected = recordEnd;
        }

        if (expected != cdOffset)
        {
            bag.Report(DiagnosticCodes.PackageArchive, string.Empty);
            return false;
        }

        return ok;
    }

    /// <summary>The length of a data descriptor that equals the central record, with or without its signature; -1 otherwise.</summary>
    private static int DescriptorLength(ReadOnlySpan<byte> span, int at, int limit, ZipEntry entry)
    {
        if (at <= limit - 16 && U32(span, at) == DescriptorSignature && Matches(span, at + 4, entry))
        {
            return 16;
        }

        return at <= limit - 12 && Matches(span, at, entry) ? 12 : -1;
    }

    private static bool Matches(ReadOnlySpan<byte> span, int at, ZipEntry entry) =>
        U32(span, at) == entry.Crc && U32(span, at + 4) == entry.CompressedSize && U32(span, at + 8) == entry.UncompressedSize;

    private enum ExtraKind
    {
        Valid = 1,
        Malformed = 2,
        Zip64 = 3,
    }

    private static ExtraKind Extra(ReadOnlySpan<byte> extra)
    {
        var at = 0;
        var zip64 = false;
        while (at < extra.Length)
        {
            if (at > extra.Length - 4)
            {
                return ExtraKind.Malformed;
            }

            var id = U16(extra, at);
            var size = U16(extra, at + 2);
            zip64 |= id == 0x0001;
            at += 4 + size;
            if (at > extra.Length)
            {
                return ExtraKind.Malformed;
            }
        }

        return zip64 ? ExtraKind.Zip64 : ExtraKind.Valid;
    }

    /// <summary>The offset of the end-of-central-directory record whose comment ends at the end of the file; -1 when none.</summary>
    private static int FindEnd(ReadOnlySpan<byte> span)
    {
        var minimum = Math.Max(0, span.Length - 22 - ushort.MaxValue);
        for (var at = span.Length - 22; at >= minimum; at--)
        {
            if (U32(span, at) == EndSignature && at + 22 + U16(span, at + 20) == span.Length)
            {
                return at;
            }
        }

        return -1;
    }

    private static ushort U16(ReadOnlySpan<byte> span, int at) => BinaryPrimitives.ReadUInt16LittleEndian(span[at..]);

    private static uint U32(ReadOnlySpan<byte> span, int at) => BinaryPrimitives.ReadUInt32LittleEndian(span[at..]);
}

/// <summary>Reads the package descriptor (contract §5.6) with the JSON rules of contract §3.1.</summary>
internal static class DescriptorReader
{
    private static readonly string[] RootMembers = ["$schema", "archiveVersion", "manifestHash", "files", "createdBy"];
    private static readonly string[] FileMembers = ["path", "size", "sha256"];
    private static readonly string[] CreatorMembers = ["name", "version"];

    public static PackageDescriptor? Read(byte[] data, DiagnosticBag target)
    {
        var bag = new DiagnosticBag();
        var descriptor = ReadCore(data, bag);
        target.AddRange(bag.Items);
        return bag.HasErrors ? null : descriptor;
    }

    private static PackageDescriptor? ReadCore(byte[] data, DiagnosticBag bag)
    {
        const string file = PackageReader.DescriptorName;
        if (!JsonFile.TryOpen(data, PackageReader.MaxDescriptorBytes, file, bag, out var document))
        {
            return null;
        }

        var parse = JsonTree.Parse(document);
        var v = new JsonValidator(bag, document.ToArray(), 0, document.Length, file);
        if (parse.Root is null)
        {
            v.ParseFailure(parse);
            return null;
        }

        v.Duplicates(parse);
        var root = parse.Root;
        if (!v.Expect(root, string.Empty, JsonKind.Object))
        {
            return null;
        }

        if (root.Members!.FirstOrDefault(member => member.Name == "archiveVersion") is { } version
            && version.Value.TryGetInteger(out var archive) && archive != 2)
        {
            v.Report(DiagnosticCodes.SchemaVersionUnsupported, "/archiveVersion", version.Value);
            return null;
        }

        var m = v.Members(root, string.Empty, RootMembers);
        v.SchemaMember(m, string.Empty, [SchemaUrls.HostNeutral("package", 2)]);
        if (v.Required(m, "archiveVersion", string.Empty, root) is { } versionNode)
        {
            v.AsInteger(versionNode, "/archiveVersion");
        }

        var hash = v.RequiredString(m, "manifestHash", string.Empty, root);
        if (hash is not null && !Grammars.IsSha256(hash))
        {
            v.Report(DiagnosticCodes.PackageManifestHash, "/manifestHash", m["manifestHash"]);
        }

        var files = new List<PackageFileEntry>();
        if (v.Required(m, "files", string.Empty, root) is { } filesNode
            && v.Array(filesNode, "/files", 0, PackageReader.MaxEntries) is { } items)
        {
            string? previous = null;
            for (var i = 0; i < items.Count; i++)
            {
                var path = JsonPointer.Append("/files", i);
                if (!v.Expect(items[i], path, JsonKind.Object, element: true))
                {
                    continue;
                }

                var fm = v.Members(items[i], path, FileMembers);
                var entryPath = v.RequiredString(fm, "path", path, items[i]);
                long? size = v.Required(fm, "size", path, items[i]) is { } sizeNode ? v.AsInteger(sizeNode, JsonPointer.Append(path, "size")) : null;
                var sha = v.RequiredString(fm, "sha256", path, items[i]);
                if (entryPath is not null && (!PackageReader.IsEntryName(entryPath) || entryPath.EndsWith('/')))
                {
                    v.Report(DiagnosticCodes.PackagePath, JsonPointer.Append(path, "path"), fm["path"]);
                    entryPath = null;
                }
                else if (entryPath is not null && (entryPath == PackageReader.DescriptorName
                    || (previous is not null && string.CompareOrdinal(previous, entryPath) >= 0)))
                {
                    v.Report(DiagnosticCodes.PackageDescriptor, JsonPointer.Append(path, "path"), fm["path"]);
                }

                if (size is < 0)
                {
                    v.Report(DiagnosticCodes.PackageDescriptor, JsonPointer.Append(path, "size"), fm["size"]);
                }

                if (sha is not null && !Grammars.IsSha256(sha))
                {
                    v.Report(DiagnosticCodes.PackageHashMismatch, JsonPointer.Append(path, "sha256"), fm["sha256"]);
                }

                if (entryPath is not null)
                {
                    previous = entryPath;
                }

                if (entryPath is not null && size is >= 0 && sha is not null)
                {
                    files.Add(new PackageFileEntry { Path = entryPath, Size = size.Value, Sha256 = sha });
                }
            }
        }

        PackageCreator? creator = null;
        if (m.TryGetValue("createdBy", out var creatorNode) && v.Expect(creatorNode, "/createdBy", JsonKind.Object))
        {
            var cm = v.Members(creatorNode, "/createdBy", CreatorMembers);
            var name = v.RequiredString(cm, "name", "/createdBy", creatorNode);
            var toolVersion = v.RequiredString(cm, "version", "/createdBy", creatorNode);
            if (name is { Length: > 64 })
            {
                v.Report(DiagnosticCodes.StringTooLong, "/createdBy/name", cm["name"]);
            }

            if (toolVersion is { Length: > 32 })
            {
                v.Report(DiagnosticCodes.StringTooLong, "/createdBy/version", cm["version"]);
            }

            creator = name is null || toolVersion is null ? null : new PackageCreator { Name = name, Version = toolVersion };
        }

        return hash is null ? null : new PackageDescriptor(hash, files.AsReadOnly(), creator);
    }
}

/// <summary>CRC-32 (ISO-HDLC, the ZIP checksum).</summary>
internal static class Crc32
{
    private static readonly uint[] Table = CreateTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = uint.MaxValue;
        foreach (var value in data)
        {
            crc = Table[(crc ^ value) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }

    private static uint[] CreateTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var value = index;
            for (var bit = 0; bit < 8; bit++)
            {
                value = (value & 1) != 0 ? (value >> 1) ^ 0xEDB88320U : value >> 1;
            }

            table[index] = value;
        }

        return table;
    }
}
