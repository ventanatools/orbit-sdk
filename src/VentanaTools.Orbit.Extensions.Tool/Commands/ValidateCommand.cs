// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>
/// <c>validate [&lt;path&gt;] [--host &lt;id&gt;] [--json] [--warnings-as-errors]</c>: validates an
/// <c>extension.json</c>, a folder containing one (with its <c>strings/</c> folder), or a package file.
/// </summary>
internal static class ValidateCommand
{
    public static async Task<int> RunAsync(ToolConsole console, string? path, string? hostId, bool json, bool warningsAsErrors,
        CancellationToken cancellationToken)
    {
        var report = new DiagnosticReport(console, "validate", json, warningsAsErrors);
        var target = console.FullPath(path ?? ".");
        if (Directory.Exists(target))
        {
            var manifestPath = Path.Combine(target, ExtensionFiles.ManifestName);
            if (!File.Exists(manifestPath))
            {
                console.Fail(console.FolderName(target) + " contains no " + ExtensionFiles.ManifestName + ".");
                return report.Write(ExitCodes.InputOutput);
            }

            await ValidateLooseAsync(console, manifestPath, Path.Combine(target, ExtensionFiles.StringsFolder), hostId, report, cancellationToken)
                .ConfigureAwait(false);
            return report.Write();
        }

        if (!File.Exists(target))
        {
            console.Fail(console.Display(target) + " does not exist.");
            return report.Write(ExitCodes.InputOutput);
        }

        if (target.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            await ValidateLooseAsync(console, target, null, hostId, report, cancellationToken).ConfigureAwait(false);
            return report.Write();
        }

        await ExtensionFiles.ReadPackageAsync(target, hostId, console, report, cancellationToken).ConfigureAwait(false);
        return report.Write();
    }

    /// <summary>Validates a loose manifest and, when given, the strings folder beside it.</summary>
    public static async Task<ExtensionManifest?> ValidateLooseAsync(ToolConsole console, string manifestPath, string? stringsFolder, string? hostId,
        DiagnosticReport report, CancellationToken cancellationToken)
    {
        var manifest = await ExtensionFiles.ReadManifestAsync(manifestPath, hostId, console, report, cancellationToken).ConfigureAwait(false);
        if (manifest is not null && stringsFolder is not null)
        {
            await ExtensionFiles.ReadStringsFolderAsync(stringsFolder, manifest, console, report, cancellationToken).ConfigureAwait(false);
        }

        return manifest;
    }
}
