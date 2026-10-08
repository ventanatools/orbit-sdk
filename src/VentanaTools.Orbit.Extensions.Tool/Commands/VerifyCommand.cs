// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>
/// <c>verify &lt;package&gt; [--host &lt;id&gt;] [--json]</c>: verifies a package (contract §5.7) and
/// prints its id, version, hosts, contributions, file count, size and package hash.
/// </summary>
internal static class VerifyCommand
{
    public static async Task<int> RunAsync(ToolConsole console, string package, string? hostId, bool json, CancellationToken cancellationToken)
    {
        var report = new DiagnosticReport(console, "verify", json);
        var path = console.FullPath(package);
        if (!File.Exists(path))
        {
            console.Fail(console.Display(path) + " does not exist.");
            report.AddPathMissing(console.Display(path));
            return report.Write(ExitCodes.InputOutput);
        }

        var read = await ExtensionFiles.ReadPackageAsync(path, hostId, console, report, cancellationToken).ConfigureAwait(false);
        if (read is not null && report.Ok)
        {
            report.Package = ExtensionFiles.Summarize(read, console.Display(path), new FileInfo(path).Length);
            report.ExtraText = output => ExtensionFiles.WriteSummary(output, report.Package);
        }

        return report.Write();
    }
}
