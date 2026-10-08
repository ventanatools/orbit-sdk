// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>
/// <c>link [&lt;path&gt;] [--host &lt;id&gt;] [--json]</c>: shows where the SDK looks for the project's
/// pairing (contract §6.6, §9.2), whether a valid pairing file is there, and, when there is none,
/// the host action that writes it. It reads pairing files and never prints their secret.
/// </summary>
internal static class LinkCommand
{
    private enum CandidateState
    {
        Missing = 1,
        Invalid = 2,
        Valid = 3,
    }

    private sealed record Candidate(string Path, string Where, CandidateState State);

    public static async Task<int> RunAsync(ToolConsole console, string? pathArgument, string? hostId, bool json, CancellationToken cancellationToken)
    {
        var report = new DiagnosticReport(console, "link", json);
        var target = console.FullPath(pathArgument ?? ".");
        var manifestPath = Directory.Exists(target) ? Path.Combine(target, ExtensionFiles.ManifestName) : target;
        if (!File.Exists(manifestPath))
        {
            console.Fail(console.Display(manifestPath) + " does not exist.");
            report.AddPathMissing(console.Display(manifestPath));
            return report.Write(ExitCodes.InputOutput);
        }

        var manifest = await ExtensionFiles.ReadManifestAsync(manifestPath, hostId, console, report, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            return report.Write();
        }

        var hosts = hostId is not null
            ? [hostId]
            : manifest.Hosts.Where(id => HostRegistry.Find(id) is { Status: HostStatus.Active }).ToList();
        var actionHost = hosts.FirstOrDefault() ?? HostRegistry.FirstActive(manifest.Hosts)?.Id ?? manifest.Hosts[0];
        report.HostDisplayName = HostRegistry.Find(actionHost)?.DisplayName ?? actionHost;

        var places = hosts.Select(id => (Path: PairingReader.DefaultPath(id, manifest.Id), Where: "per-user folder for host " + id)).ToList();
        places.Add((Path.Combine(Path.GetDirectoryName(manifestPath)!, manifest.Id + ".pairing.json"),
            "fallback: the companion's folder or current directory"));

        var candidates = new List<Candidate>();
        string? found = null;
        foreach (var (path, where) in places)
        {
            if (found is not null || !File.Exists(path))
            {
                candidates.Add(new Candidate(path, where, CandidateState.Missing));
                continue;
            }

            var read = await PairingReader.ReadFileAsync(path, new PairingReadOptions { ExpectedExtensionId = manifest.Id, ManifestHosts = manifest.Hosts },
                cancellationToken).ConfigureAwait(false);
            using (read.Value)
            {
                foreach (var diagnostic in read.Diagnostics)
                {
                    report.Add(diagnostic, console.Display(path));
                }

                var valid = read.Value is not null;
                candidates.Add(new Candidate(path, where, valid ? CandidateState.Valid : CandidateState.Invalid));
                found ??= valid ? path : null;
            }
        }

        if (found is null && candidates.All(candidate => candidate.State == CandidateState.Missing))
        {
            report.Add(ReasonCode.PairingMissing.Value, string.Empty, console.Display(candidates[0].Path));
        }

        report.ExtraText = output =>
        {
            output.WriteLine("The SDK looks for the pairing of " + manifest.Id + " here, in order:");
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                output.WriteLine("  " + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + ". " + console.Display(candidate.Path)
                    + " (" + candidate.Where + "): " + Describe(candidate.State));
            }

            output.WriteLine(found is null
                ? "No valid pairing was found. In " + report.HostDisplayName + ", choose Save connection info: it writes the first file above."
                : "The companion will use " + console.Display(found) + ".");
        };
        report.ExtraJson = writer =>
        {
            writer.WriteStartObject("pairing");
            writer.WriteStartArray("searched");
            foreach (var candidate in candidates)
            {
                writer.WriteStartObject();
                writer.WriteString("path", console.Display(candidate.Path));
                writer.WriteString("state", Token(candidate.State));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            if (found is null)
            {
                writer.WriteNull("found");
            }
            else
            {
                writer.WriteString("found", console.Display(found));
            }

            writer.WriteEndObject();
        };

        return report.Write(found is null ? ExitCodes.Failed : ExitCodes.Success);
    }

    private static string Token(CandidateState state) => state switch
    {
        CandidateState.Valid => "Valid",
        CandidateState.Invalid => "Invalid",
        _ => "Missing",
    };

    private static string Describe(CandidateState state) => state switch
    {
        CandidateState.Valid => "valid",
        CandidateState.Invalid => "invalid",
        _ => "not found",
    };
}
