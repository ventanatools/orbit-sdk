// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using VentanaTools.Orbit.Extensions.Packaging;

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>The reader defect of contract §11.1: a reader threw where the contract says it reports diagnostics.</summary>
internal sealed class ReaderDefectException(Exception inner) : Exception("A reader failed.", inner);

/// <summary>
/// Reads the files an author points the tool at (manifests, strings folders and packages) with the
/// author package's readers, adding every diagnostic to a <see cref="DiagnosticReport"/> under the
/// path the author would type.
/// </summary>
internal static class ExtensionFiles
{
    /// <summary>The manifest's file name (contract §2.2).</summary>
    public const string ManifestName = "extension.json";

    /// <summary>The pack configuration's file name (contract §11.3).</summary>
    public const string PackConfigName = "extension.pack.json";

    /// <summary>The localized strings folder (contract §3.10).</summary>
    public const string StringsFolder = "strings";

    /// <summary>The most strings files one extension may have (contract §3.10).</summary>
    public const int MaxStringsFiles = 64;

    /// <summary>Validation options for <paramref name="hostId"/>; null means no host context (contract §3.2.3).</summary>
    public static ManifestReadOptions Options(string? hostId) => new() { HostId = hostId };

    /// <summary>Reads and validates a manifest file; null when it has errors.</summary>
    public static async Task<ExtensionManifest?> ReadManifestAsync(string path, string? hostId, ToolConsole console, DiagnosticReport report,
        CancellationToken cancellationToken)
    {
        var bytes = await BoundedFile.ReadAsync(path, ManifestReader.MaxBytes, cancellationToken).ConfigureAwait(false);
        var result = Guard(() => ManifestReader.Read(bytes, Options(hostId), null));
        foreach (var diagnostic in result.Diagnostics)
        {
            report.Add(diagnostic, console.Display(path));
        }

        return result.Value;
    }

    /// <summary>Validates the strings folder beside a loose manifest (contract §3.10).</summary>
    public static async Task ReadStringsFolderAsync(string folder, ExtensionManifest manifest, ToolConsole console, DiagnosticReport report,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        var files = Directory.EnumerateFiles(folder, "*.json", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (files.Count > MaxStringsFiles)
        {
            report.Add(DiagnosticCodes.StringsTooManyFiles, string.Empty, console.Display(folder));
        }

        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new StringsReadOptions { Manifest = manifest };
        foreach (var file in files)
        {
            var tag = Path.GetFileNameWithoutExtension(file);
            if (!tags.Add(tag))
            {
                report.Add(DiagnosticCodes.StringsLanguageDuplicate, string.Empty, console.Display(file));
                continue;
            }

            var bytes = await BoundedFile.ReadAsync(file, StringsReader.MaxBytes, cancellationToken).ConfigureAwait(false);
            var result = Guard(() => StringsReader.Read(bytes, tag, options, null));
            foreach (var diagnostic in result.Diagnostics)
            {
                report.Add(diagnostic, console.Display(file));
            }
        }
    }

    /// <summary>
    /// Reads and verifies a package (contract §5.7), then checks its file name against the package
    /// file extension of <paramref name="hostId"/>, or of the first active registry host in its
    /// manifest's <c>hosts</c> (<c>package.extension</c>).
    /// </summary>
    public static async Task<ExtensionPackage?> ReadPackageAsync(string path, string? hostId, ToolConsole console, DiagnosticReport report,
        CancellationToken cancellationToken)
    {
        var bytes = await BoundedFile.ReadAsync(path, PackageReader.MaxArchiveBytes, cancellationToken).ConfigureAwait(false);
        var result = Guard(() => PackageReader.Read(bytes, new PackageReadOptions { Manifest = Options(hostId) }));
        var display = console.Display(path);
        foreach (var diagnostic in result.Diagnostics)
        {
            report.Add(diagnostic, diagnostic.File is null ? display : display + "!/" + diagnostic.File);
        }

        var host = hostId is not null ? HostRegistry.Find(hostId) : result.Value is { } package ? HostRegistry.FirstActive(package.Manifest.Hosts) : null;
        if (host?.PackageExtension is { } extension && !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
        {
            report.Add(DiagnosticCodes.PackageExtension, string.Empty, display);
            return null;
        }

        return result.Value;
    }

    /// <summary>The summary <c>verify</c> and <c>pack</c> print.</summary>
    public static PackageSummary Summarize(ExtensionPackage package, string display, long bytes) => new()
    {
        Path = display,
        Sha256 = package.Sha256,
        Id = package.Manifest.Id,
        Version = package.Manifest.Version,
        Hosts = package.Manifest.Hosts,
        Contributions = package.Manifest.Contributions.Select(contribution => contribution.Id).ToList(),
        Files = package.Files.Count,
        Bytes = bytes,
    };

    /// <summary>The text lines of a <see cref="PackageSummary"/>.</summary>
    public static void WriteSummary(TextWriter output, PackageSummary package)
    {
        output.WriteLine(package.Id + " " + package.Version);
        output.WriteLine("  hosts: " + string.Join(", ", package.Hosts));
        output.WriteLine("  contributions: " + string.Join(", ", package.Contributions));
        output.WriteLine("  files: " + package.Files.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ", " + package.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes");
        output.WriteLine("  package hash: " + package.Sha256);
        output.WriteLine("  path: " + package.Path);
    }

    /// <summary>Runs a reader, turning an unexpected exception into a <see cref="ReaderDefectException"/>.</summary>
    public static T Guard<T>(Func<T> read)
    {
        try
        {
            return read();
        }
        catch (Exception error) when (error is not (OperationCanceledException or IOException or UnauthorizedAccessException))
        {
            throw new ReaderDefectException(error);
        }
    }
}
