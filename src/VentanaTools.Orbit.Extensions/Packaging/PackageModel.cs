// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions.Packaging;

/// <summary>The package descriptor, <c>extension.package.json</c> (contract §5.6). Tools generate it; authors never write it.</summary>
public sealed class PackageDescriptor
{
    internal PackageDescriptor(string manifestHash, IReadOnlyList<PackageFileEntry> files, PackageCreator? createdBy)
    {
        ManifestHash = manifestHash;
        Files = files;
        CreatedBy = createdBy;
    }

    /// <summary>The archive version: 2.</summary>
    public int ArchiveVersion { get; } = 2;

    /// <summary>The manifest hash of <c>extension.json</c> (contract §B.2).</summary>
    public string ManifestHash { get; }

    /// <summary>One entry per file in the archive except the descriptor itself, sorted by path ordinally.</summary>
    public IReadOnlyList<PackageFileEntry> Files { get; }

    /// <summary>The informational <c>createdBy</c> member, when present.</summary>
    public PackageCreator? CreatedBy { get; }
}

/// <summary>One inventory row of the descriptor.</summary>
public sealed class PackageFileEntry
{
    /// <summary>The archive entry name.</summary>
    public required string Path { get; init; }

    /// <summary>The uncompressed length in bytes.</summary>
    public required long Size { get; init; }

    /// <summary>The SHA-256 of the file, in lowercase hexadecimal.</summary>
    public required string Sha256 { get; init; }
}

/// <summary>The descriptor's informational <c>createdBy</c> member.</summary>
public sealed class PackageCreator
{
    /// <summary>The tool's name, at most 64 characters.</summary>
    public required string Name { get; init; }

    /// <summary>The tool's version, at most 32 characters.</summary>
    public required string Version { get; init; }
}

/// <summary>One verified file of a package. Reading it never exposes the package's own buffer.</summary>
public sealed class PackageFile
{
    private readonly byte[] _bytes;

    internal PackageFile(string path, byte[] bytes)
    {
        Path = path;
        _bytes = bytes;
    }

    /// <summary>The archive entry name.</summary>
    public string Path { get; }

    /// <summary>The length in bytes.</summary>
    public long Length => _bytes.LongLength;

    /// <summary>Opens a read-only stream over a private copy of the file's bytes.</summary>
    /// <returns>A read-only stream.</returns>
    public Stream OpenRead() => new MemoryStream(_bytes, writable: false);
}

/// <summary>
/// A verified package (contract §5): its manifest, descriptor, strings, readme, package hash and
/// files. Verification never runs, loads or extracts anything; installing is the host's job (§5.8).
/// </summary>
public sealed class ExtensionPackage
{
    internal ExtensionPackage(ExtensionManifest manifest, PackageDescriptor descriptor, IReadOnlyList<ExtensionStrings> strings,
        string readme, string sha256, IReadOnlyList<PackageFile> files)
    {
        Manifest = manifest;
        Descriptor = descriptor;
        Strings = strings;
        Readme = readme;
        Sha256 = sha256;
        Files = files;
    }

    /// <summary>The manifest, <c>extension.json</c>.</summary>
    public ExtensionManifest Manifest { get; }

    /// <summary>The descriptor, <c>extension.package.json</c>.</summary>
    public PackageDescriptor Descriptor { get; }

    /// <summary>The localized strings files.</summary>
    public IReadOnlyList<ExtensionStrings> Strings { get; }

    /// <summary>The text of <c>README.md</c>, without a byte order mark. Show it as plain text.</summary>
    public string Readme { get; }

    /// <summary>The package hash: the SHA-256 of the whole archive, in lowercase hexadecimal.</summary>
    public string Sha256 { get; }

    /// <summary>Every file of the archive, in archive order.</summary>
    public IReadOnlyList<PackageFile> Files { get; }
}

/// <summary>Options for <see cref="PackageReader"/>.</summary>
public sealed class PackageReadOptions
{
    /// <summary>The options for the manifest inside the package.</summary>
    public ManifestReadOptions Manifest { get; init; } = new();
}

/// <summary>What <see cref="PackageWriter"/> puts in a package.</summary>
public sealed class PackageContents
{
    /// <summary>The bytes of <c>extension.json</c>; the manifest must be valid.</summary>
    public required ReadOnlyMemory<byte> Manifest { get; init; }

    /// <summary>The bytes of <c>README.md</c>.</summary>
    public required ReadOnlyMemory<byte> Readme { get; init; }

    /// <summary>Strings files, each named <c>&lt;language-tag&gt;.json</c>; written under <c>strings/</c>.</summary>
    public IReadOnlyList<PackageContentFile> Strings { get; init; } = [];

    /// <summary>Payload files, with paths relative to <c>payload/</c>.</summary>
    public IReadOnlyList<PackageContentFile> Payload { get; init; } = [];

    /// <summary>The informational <c>createdBy</c> member of the descriptor; null to leave it out.</summary>
    public PackageCreator? CreatedBy { get; init; }
}

/// <summary>One file for <see cref="PackageWriter"/>.</summary>
public sealed class PackageContentFile
{
    /// <summary>The path, relative to the folder the file goes in, with <c>/</c> separators.</summary>
    public required string Path { get; init; }

    /// <summary>The file's bytes.</summary>
    public required ReadOnlyMemory<byte> Content { get; init; }
}
