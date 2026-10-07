// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions.Hosting;

/// <summary>
/// Options for a companion that runs in the .NET Generic Host: set them in the delegate given to
/// <c>AddCompanion</c> (<see cref="CompanionServiceCollectionExtensions"/>), with
/// <c>services.Configure&lt;CompanionServiceOptions&gt;</c>, or by binding a configuration section.
/// </summary>
/// <remarks>
/// Each option but <see cref="Arguments"/> and <see cref="StopApplicationOnExit"/> is the
/// <see cref="CompanionAppOptions"/> option of the same name, with the same default. The options
/// are validated and read once, when the host starts the companion: a <see cref="Client"/> option
/// out of its range, or an empty <see cref="ManifestPath"/> or <see cref="PairingPath"/>, fails the
/// start with <c>OptionsValidationException</c>. Configuration binds every option but the callbacks
/// and the client's clock.
/// </remarks>
public sealed class CompanionServiceOptions
{
    /// <summary>
    /// The command line the companion reads <c>--manifest</c>, <c>--pairing</c> and <c>--verbose</c>
    /// from, as <see cref="CompanionApp.RunAsync"/> reads its <c>args</c>; null (the default) for the
    /// process's own command line without the program name. Other arguments are ignored.
    /// </summary>
    public IReadOnlyList<string>? Arguments { get; set; }

    /// <summary>The manifest to load; overrides <c>--manifest</c>.</summary>
    public string? ManifestPath { get; set; }

    /// <summary>The pairing file to load; overrides <c>--pairing</c>.</summary>
    public string? PairingPath { get; set; }

    /// <summary>Whether to watch the pairing file and <c>extension.json</c>, waiting for them instead of stopping (default true).</summary>
    public bool WatchFiles { get; set; } = true;

    /// <summary>
    /// Whether the companion stops the application when it stops by itself (default true): after a
    /// usage error (exit code 2) or an unexpected failure (1), and, with <see cref="WatchFiles"/> off,
    /// when a file is missing or invalid (3) or the client stopped (4). The exit code then becomes
    /// the process's <see cref="Environment.ExitCode"/>. False leaves the application running.
    /// </summary>
    public bool StopApplicationOnExit { get; set; } = true;

    /// <summary>Called for every status change, on the thread pool; the same status is also logged.</summary>
    public Action<StatusChangedEventArgs>? StatusChanged { get; set; }

    /// <summary>Called for every handler fault, on the thread pool; the same fault is also logged, with its exception.</summary>
    public Action<HandlerFaultedEventArgs>? HandlerFaulted { get; set; }

    /// <summary>Options for the client: retry delays, the handler stop timeout and the clock.</summary>
    public CompanionClientOptions? Client { get; set; }

    /// <summary>Tests: takes the host end of an in-memory connection in place of the named pipe.</summary>
    internal Func<Stream, CancellationToken, ValueTask<bool>>? TestTransport { get; set; }

    /// <summary>Tests: receives the exit code in place of <see cref="Environment.ExitCode"/>.</summary>
    internal Action<int>? TestExitCode { get; set; }
}
