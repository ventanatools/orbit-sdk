// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Globalization;
using System.Runtime.Versioning;

namespace VentanaTools.Orbit.Extensions.Tool;

/// <summary>
/// <c>simulate [--manifest &lt;path&gt;] [--script &lt;file&gt;] [--json] -- &lt;command&gt; [args…]</c>: runs a
/// pipe-level fake host with a temporary registration and pairing file, starts the companion with
/// <c>--manifest</c> and <c>--pairing</c> appended, and drives it from an interactive prompt or a
/// script, printing the transcript (contract §11.1, §11.2).
/// </summary>
internal static class SimulateCommand
{
    /// <summary>How long a step waits for the companion to connect; <c>dotnet run</c> builds first.</summary>
    public static TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(90);

    public static async Task<int> RunAsync(ToolConsole console, string? manifestArgument, string? scriptArgument, bool json,
        IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            console.Fail("simulate needs Windows: the companion transport is a Windows named pipe.");
            return ExitCodes.ProcessStart;
        }

        return await RunOnWindowsAsync(console, manifestArgument, scriptArgument, json, command, cancellationToken).ConfigureAwait(false);
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunOnWindowsAsync(ToolConsole console, string? manifestArgument, string? scriptArgument, bool json,
        IReadOnlyList<string> command, CancellationToken cancellationToken)
    {
        var report = new DiagnosticReport(console, "simulate", json);
        var manifestPath = console.FullPath(manifestArgument ?? ExtensionFiles.ManifestName);
        if (!File.Exists(manifestPath))
        {
            console.Fail(console.Display(manifestPath) + " does not exist.");
            report.AddPathMissing(console.Display(manifestPath));
            return report.Write(ExitCodes.InputOutput);
        }

        var manifest = await ExtensionFiles.ReadManifestAsync(manifestPath, null, console, report, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            return report.Write();
        }

        IReadOnlyList<SimulationStep>? steps = null;
        if (scriptArgument is not null)
        {
            var scriptPath = console.FullPath(scriptArgument);
            if (!File.Exists(scriptPath))
            {
                console.Fail(console.Display(scriptPath) + " does not exist.");
                report.AddPathMissing(console.Display(scriptPath));
                return report.Write(ExitCodes.InputOutput);
            }

            var bytes = await BoundedFile.ReadAsync(scriptPath, SimulationScriptReader.MaxBytes, cancellationToken).ConfigureAwait(false);
            var read = ExtensionFiles.Guard(() => SimulationScriptReader.Read(bytes));
            foreach (var diagnostic in read.Diagnostics)
            {
                report.Add(diagnostic, console.Display(scriptPath));
            }

            if (read.Value is null)
            {
                return report.Write();
            }

            steps = read.Value;
        }

        var hostId = HostRegistry.FirstActive(manifest.Hosts)?.Id ?? manifest.Hosts[0];
        using var registration = SimulationRegistration.Create(hostId, manifest.Id);
        var transcript = new Transcript(console, json);
        var host = new SimulatedHost(manifest, registration, transcript);
        ChildProcess? companion = null;
        var finishing = false;
        try
        {
            host.Start();
            try
            {
                companion = ChildProcess.Start(command[0], [.. command.Skip(1), "--manifest", manifestPath, "--pairing", registration.PairingPath],
                    console.WorkingDirectory, console.Error, console.Error, closeInput: true);
            }
            catch (ProcessStartException)
            {
                console.Fail(command[0] + " could not be started.");
                return ExitCodes.ProcessStart;
            }

            transcript.Note("started", "companion process " + companion.Id.ToString(CultureInfo.InvariantCulture));
            var exited = companion.WaitAsync(CancellationToken.None);
            _ = exited.ContinueWith(task =>
            {
                if (!Volatile.Read(ref finishing))
                {
                    transcript.Note("exited", "the companion exited with code "
                        + (task.IsCompletedSuccessfully ? task.Result : -1).ToString(CultureInfo.InvariantCulture));
                }
            }, TaskScheduler.Default);
            return steps is not null
                ? await RunScriptAsync(host, steps, exited, transcript, console, cancellationToken).ConfigureAwait(false)
                : await RunPromptAsync(host, exited, console, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return ExitCodes.Success;
        }
        finally
        {
            Volatile.Write(ref finishing, true);
            await host.DisposeAsync().ConfigureAwait(false);
            companion?.Stop();
            companion?.Dispose();
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunScriptAsync(SimulatedHost host, IReadOnlyList<SimulationStep> steps, Task<int> exited, Transcript transcript,
        ToolConsole console, CancellationToken cancellationToken)
    {
        foreach (var step in steps)
        {
            var failure = exited.IsCompleted
                ? "the companion exited"
                : await RunStepAsync(host, step, exited, cancellationToken).ConfigureAwait(false);
            if (failure is not null)
            {
                var text = "step " + (step.Index + 1).ToString(CultureInfo.InvariantCulture) + ": " + failure;
                transcript.Note("expectationFailed", text);
                console.Fail("expectation failed at " + text);
                return ExitCodes.Failed;
            }
        }

        transcript.Note("finished", steps.Count.ToString(CultureInfo.InvariantCulture) + (steps.Count == 1 ? " step" : " steps") + " passed");
        return ExitCodes.Success;
    }

    [SupportedOSPlatform("windows")]
    private static async Task<string?> RunStepAsync(SimulatedHost host, SimulationStep step, Task<int> exited, CancellationToken cancellationToken)
    {
        switch (step.Kind)
        {
            case StepKind.Start:
            {
                var connected = host.WaitForConnectionAsync(ConnectTimeout, cancellationToken);
                if (await Task.WhenAny(connected, exited).ConfigureAwait(false) == exited || !await connected.ConfigureAwait(false))
                {
                    return "the companion did not connect within " + ConnectTimeout.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s.";
                }

                return (await host.StartSessionAsync(step.Target, step.Settings, step.As).ConfigureAwait(false)).Error;
            }

            case StepKind.Invoke:
            {
                var (request, error) = await host.InvokeAsync(step.Target, step.As).ConfigureAwait(false);
                if (request is null || !step.WaitForResult)
                {
                    return error;
                }

                var outcome = await request.Result.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                if (step.Expect is { } expected && (outcome.Kind != expected || (step.Failure is { } failure && outcome.Failure != failure)))
                {
                    var wanted = new SimulatedOutcome { Kind = expected, Failure = step.Failure };
                    return "expected " + wanted + ", got " + outcome + ".";
                }

                return null;
            }

            case StepKind.Cancel:
                return await host.CancelAsync(step.Target).ConfigureAwait(false);
            case StepKind.Stop:
                return await host.StopSessionAsync(step.Target).ConfigureAwait(false);
            case StepKind.ExpectFace:
                return await host.WaitUntilAsync(() => host.Session(step.Target)?.Face is { } face && Matches(face, step), step.Within, cancellationToken)
                    .ConfigureAwait(false)
                    ? null
                    : "no face matching the step arrived for " + step.Target + " within " + step.Within.TotalSeconds.ToString(CultureInfo.InvariantCulture) + " s.";
            case StepKind.Disconnect:
                return ReasonCode.TryParse(step.Target, out var code)
                    ? await host.DisconnectAsync(code).ConfigureAwait(false)
                    : step.Target + " is not a reason code.";
            case StepKind.Wait:
                await Task.Delay(step.Within, cancellationToken).ConfigureAwait(false);
                return null;
            default:
                return "unknown step.";
        }
    }

    private static bool Matches(Wire.WireFace face, SimulationStep step) =>
        (step.Line1 is null || (face.Line1 is TextLine line1 && line1.Text == step.Line1))
        && (step.Line2 is null || (face.Line2 is TextLine line2 && line2.Text == step.Line2))
        && (step.State is not { } state || face.State == state);

    [SupportedOSPlatform("windows")]
    private static async Task<int> RunPromptAsync(SimulatedHost host, Task<int> exited, ToolConsole console, CancellationToken cancellationToken)
    {
        var interactive = ReferenceEquals(console.In, Console.In) && !Console.IsInputRedirected;
        console.Error.WriteLine("Commands: start <contribution> [key=value...], invoke <session>, cancel <request>, stop <session>, faces, ping, disconnect [code], quit");
        while (true)
        {
            if (interactive)
            {
                console.Error.Write("simulate> ");
            }

            var line = await console.In.ReadLineAsync(cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                return ExitCodes.Success;
            }

            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (words.Length == 0)
            {
                continue;
            }

            string? error;
            switch (words[0])
            {
                case "start" when words.Length >= 2:
                    var settings = new Dictionary<string, string>(StringComparer.Ordinal);
                    error = null;
                    foreach (var pair in words.Skip(2))
                    {
                        var equals = pair.IndexOf('=', StringComparison.Ordinal);
                        if (equals <= 0)
                        {
                            error = "write settings as key=value.";
                            break;
                        }

                        settings[pair[..equals]] = pair[(equals + 1)..];
                    }

                    error ??= (await host.StartSessionAsync(words[1], settings, null).ConfigureAwait(false)).Error;
                    break;
                case "invoke" when words.Length == 2:
                    error = (await host.InvokeAsync(words[1], null).ConfigureAwait(false)).Error;
                    break;
                case "cancel" when words.Length == 2:
                    error = await host.CancelAsync(words[1]).ConfigureAwait(false);
                    break;
                case "stop" when words.Length == 2:
                    error = await host.StopSessionAsync(words[1]).ConfigureAwait(false);
                    break;
                case "faces" when words.Length == 1:
                    WriteFaces(host, console);
                    error = null;
                    break;
                case "ping" when words.Length == 1:
                    error = await host.PingAsync().ConfigureAwait(false);
                    break;
                case "disconnect" when words.Length <= 2:
                    var codeText = words.Length == 2 ? words[1] : ReasonCode.HostReloaded.Value;
                    error = ReasonCode.TryParse(codeText, out var code)
                        ? await host.DisconnectAsync(code).ConfigureAwait(false)
                        : codeText + " is not a reason code.";
                    break;
                case "quit" or "exit" when words.Length == 1:
                    return ExitCodes.Success;
                default:
                    error = "unknown command. Use start, invoke, cancel, stop, faces, ping, disconnect or quit.";
                    break;
            }

            if (error is not null)
            {
                console.Fail(error);
            }

            if (exited.IsCompleted && interactive)
            {
                console.Note("the companion has exited; quit to end the simulation.");
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WriteFaces(SimulatedHost host, ToolConsole console)
    {
        var sessions = host.Sessions;
        if (sessions.Count == 0)
        {
            console.Out.WriteLine("no sessions");
            return;
        }

        foreach (var session in sessions.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            var status = session.Status switch
            {
                SimulatedSessionStatus.Running => "running",
                SimulatedSessionStatus.Refused => "refused (" + session.RefusedCode?.Value + ")",
                SimulatedSessionStatus.Stopped => "stopped",
                _ => "ended",
            };
            var face = session.Failure is { } failure ? "failed (" + Tokens.Failure(failure) + ")"
                : session.Face is { } current ? (current.Line1 is TextLine line1 ? "\"" + line1.Text + "\"" : "a face without text")
                    + (current.Line2 is TextLine line2 ? " / \"" + line2.Text + "\"" : string.Empty)
                : "no face";
            console.Out.WriteLine(session.Name + "  " + session.Contribution.Id + "  " + status + "  " + face);
        }
    }
}
