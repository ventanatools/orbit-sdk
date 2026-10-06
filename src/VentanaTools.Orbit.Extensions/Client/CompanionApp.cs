// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Runtime.Versioning;
using System.Text;

namespace VentanaTools.Orbit.Extensions;

/// <summary>The command line a companion was started with, as <see cref="CompanionApp"/> reads it.</summary>
public sealed class CompanionArguments
{
    /// <summary><c>--manifest &lt;path&gt;</c>: the manifest to load.</summary>
    public string? ManifestPath { get; init; }

    /// <summary><c>--pairing &lt;path&gt;</c>: the pairing file to load.</summary>
    public string? PairingPath { get; init; }

    /// <summary><c>--verbose</c>: also print the host's fixed error text.</summary>
    public bool Verbose { get; init; }

    /// <summary>Every other argument, in order, for the author.</summary>
    public IReadOnlyList<string> Remaining { get; init; } = [];
}

/// <summary>Options for <see cref="CompanionApp.RunAsync"/>.</summary>
public sealed class CompanionAppOptions
{
    /// <summary>The manifest to load; overrides <c>--manifest</c>.</summary>
    public string? ManifestPath { get; init; }

    /// <summary>The pairing file to load; overrides <c>--pairing</c>.</summary>
    public string? PairingPath { get; init; }

    /// <summary>Whether to watch the pairing file and <c>extension.json</c>, waiting for them instead of exiting (default true).</summary>
    public bool WatchFiles { get; init; } = true;

    /// <summary>Where status lines go; null for <see cref="Console.Error"/>, <see cref="TextWriter.Null"/> to silence.</summary>
    public TextWriter? Output { get; init; }

    /// <summary>Called for every <see cref="CompanionClient.StatusChanged"/>, on the thread pool.</summary>
    public Action<StatusChangedEventArgs>? StatusChanged { get; init; }

    /// <summary>Called for every <see cref="CompanionClient.HandlerFaulted"/>, on the thread pool.</summary>
    public Action<HandlerFaultedEventArgs>? HandlerFaulted { get; init; }

    /// <summary>Options for the client.</summary>
    public CompanionClientOptions? Client { get; init; }
}

/// <summary>
/// The one-call entry point of a companion (contract §9.2): it reads the command line, finds and
/// reads <c>extension.json</c> and the pairing file with bounded reads, waits for the pairing on
/// first run, runs a <see cref="CompanionClient"/>, prints one status line per change, handles
/// Ctrl+C, and returns an exit code.
/// </summary>
/// <remarks>
/// <para>
/// The manifest is <c>--manifest</c>, else <c>extension.json</c> in
/// <see cref="AppContext.BaseDirectory"/>, else in the current directory. The pairing is
/// <c>--pairing</c>, else the per-user default for each active host in the manifest's
/// <c>hosts</c>, else <c>&lt;extension-id&gt;.pairing.json</c> in
/// <see cref="AppContext.BaseDirectory"/>, then in the current directory.
/// </para>
/// <para>
/// Status lines read <c>ventana: &lt;state&gt; (&lt;code&gt;) &lt;fix&gt; &lt;help link&gt;</c>. Pairing
/// contents are never printed, and the host's fixed error text only with <c>--verbose</c>.
/// Exit codes: 0 stopped by Ctrl+C or cancellation; 1 unexpected; 2 usage; 3 a file is missing or
/// invalid with <see cref="CompanionAppOptions.WatchFiles"/> off; 4 stopped with
/// <see cref="CompanionAppOptions.WatchFiles"/> off.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// return await CompanionApp.RunAsync(args, new TimeWidget());
///
/// sealed class TimeWidget : ContributionHandler
/// {
///     public override async Task RunSessionAsync(Session session, CancellationToken cancellationToken)
///     {
///         using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), session.Time);
///         do
///         {
///             var now = session.Time.GetLocalNow();
///             session.SetFace(new Face
///             {
///                 Line1 = now.ToString("t", session.UiCulture),
///                 Detail = now.ToString("F", session.UiCulture),
///                 GoodFor = TimeSpan.FromSeconds(60),
///             });
///         }
///         while (await timer.WaitForNextTickAsync(cancellationToken));
///     }
/// }
/// </code>
/// </example>
public static class CompanionApp
{
    internal const string ManifestFileName = "extension.json";
    internal const string PairingSuffix = ".pairing.json";

    /// <summary>Runs a companion until Ctrl+C, cancellation, or a stop it cannot recover from.</summary>
    /// <param name="args">The command line.</param>
    /// <param name="handler">The contribution handler.</param>
    /// <param name="options">Options, or null for the defaults.</param>
    /// <param name="cancellationToken">Stops the companion; it then returns 0.</param>
    /// <returns>The exit code.</returns>
    [SupportedOSPlatform("windows")]
    public static Task<int> RunAsync(string[] args, IContributionHandler handler, CompanionAppOptions? options = null,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(args, handler, options, null, cancellationToken);

    /// <summary>
    /// Reads <c>--manifest &lt;path&gt;</c>, <c>--pairing &lt;path&gt;</c> and <c>--verbose</c>; every
    /// other argument is left in <see cref="CompanionArguments.Remaining"/>.
    /// </summary>
    /// <param name="args">The command line.</param>
    /// <returns>The arguments.</returns>
    /// <exception cref="ArgumentException">A recognized option has no value.</exception>
    public static CompanionArguments ParseArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string? manifest = null;
        string? pairing = null;
        var verbose = false;
        var remaining = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--manifest":
                    manifest = Value(args, ref i, "--manifest");
                    break;
                case "--pairing":
                    pairing = Value(args, ref i, "--pairing");
                    break;
                case "--verbose":
                    verbose = true;
                    break;
                default:
                    remaining.Add(args[i]);
                    break;
            }
        }

        return new CompanionArguments
        {
            ManifestPath = manifest,
            PairingPath = pairing,
            Verbose = verbose,
            Remaining = remaining.AsReadOnly(),
        };
    }

    /// <summary>Runs a companion; <paramref name="transport"/> replaces the named pipe (the in-memory seam).</summary>
    internal static async Task<int> RunCoreAsync(string[] args, IContributionHandler handler, CompanionAppOptions? options,
        Func<Pairing, ICompanionTransport>? transport, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(handler);
        options ??= new CompanionAppOptions();
        var output = options.Output ?? Console.Error;
        CompanionArguments parsed;
        try
        {
            parsed = ParseArguments(args);
        }
        catch (ArgumentException error)
        {
            output.WriteLine("ventana: " + error.Message);
            return 2;
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        var hooked = Hook(onCancel);
        try
        {
            return await new AppRunner(parsed, handler, options, output, transport).RunAsync(stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception error)
        {
            output.WriteLine("ventana: unexpected " + error.GetType().FullName + ": " + error.Message);
            output.WriteLine(error.StackTrace);
            return 1;
        }
        finally
        {
            if (hooked)
            {
                Unhook(onCancel);
            }
        }
    }

    private static string Value(string[] args, ref int index, string option)
    {
        if (index + 1 >= args.Length)
        {
            throw new ArgumentException(option + " needs a value.", nameof(args));
        }

        index++;
        return args[index];
    }

    private static bool Hook(ConsoleCancelEventHandler handler)
    {
        try
        {
            Console.CancelKeyPress += handler;
            return true;
        }
        catch (Exception error) when (error is PlatformNotSupportedException or InvalidOperationException or IOException)
        {
            return false;
        }
    }

    private static void Unhook(ConsoleCancelEventHandler handler)
    {
        try
        {
            Console.CancelKeyPress -= handler;
        }
        catch (Exception error) when (error is PlatformNotSupportedException or InvalidOperationException or IOException)
        {
            // Nothing to undo.
        }
    }
}

/// <summary>The work of <see cref="CompanionApp.RunAsync"/>.</summary>
internal sealed class AppRunner
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private readonly CompanionArguments _arguments;
    private readonly IContributionHandler _handler;
    private readonly CompanionAppOptions _options;
    private readonly TextWriter _output;
    private readonly Func<Pairing, ICompanionTransport>? _transport;
    private readonly TimeProvider _time;
    private int _subscriberReported;
    private bool _unmappedPrinted;

    public AppRunner(CompanionArguments arguments, IContributionHandler handler, CompanionAppOptions options, TextWriter output,
        Func<Pairing, ICompanionTransport>? transport)
    {
        _arguments = arguments;
        _handler = handler;
        _options = options;
        _output = output;
        _transport = transport;
        _time = options.Client?.TimeProvider ?? TimeProvider.System;
    }

    private enum ClientEnd
    {
        Cancelled = 1,
        Stopped = 2,
        Restart = 3,
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var manifestPath = _options.ManifestPath ?? _arguments.ManifestPath ?? FindManifest();
        while (true)
        {
            var manifest = await LoadManifestAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            if (manifest is null)
            {
                return 3;
            }

            PrintUnmapped(manifest);
            var loaded = await LoadPairingAsync(manifest, manifestPath, cancellationToken).ConfigureAwait(false);
            if (loaded is not { } found)
            {
                return 3;
            }

            ClientEnd end;
            using (found.Pairing)
            {
                end = await RunClientAsync(manifest, manifestPath, found.Pairing, found.Path, cancellationToken).ConfigureAwait(false);
            }

            switch (end)
            {
                case ClientEnd.Cancelled:
                    return 0;
                case ClientEnd.Stopped when !_options.WatchFiles:
                    return 4;
                case ClientEnd.Stopped:
                    await WaitForChangeAsync([found.Path, manifestPath], cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static string FindManifest()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, CompanionApp.ManifestFileName),
            Path.Combine(Environment.CurrentDirectory, CompanionApp.ManifestFileName),
        };
        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    private async Task<ExtensionManifest?> LoadManifestAsync(string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            var manifest = await ReadManifestAsync(path, report: true, cancellationToken).ConfigureAwait(false);
            if (manifest is not null)
            {
                return manifest;
            }

            if (!_options.WatchFiles)
            {
                return null;
            }

            _output.WriteLine("ventana: watching " + path);
            await WaitForChangeAsync([path], cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<ExtensionManifest?> ReadManifestAsync(string path, bool report, CancellationToken cancellationToken)
    {
        ReadResult<ExtensionManifest> read;
        try
        {
            read = await ManifestReader.ReadFileAsync(path, null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            if (report)
            {
                _output.WriteLine("ventana: cannot read the manifest " + path + " (" + error.GetType().Name + ")");
            }

            return null;
        }

        if (read.Value is { } manifest)
        {
            return manifest;
        }

        if (report)
        {
            foreach (var diagnostic in read.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error))
            {
                _output.WriteLine("ventana: " + path + ": " + diagnostic);
            }
        }

        return null;
    }

    private void PrintUnmapped(ExtensionManifest manifest)
    {
        if (_unmappedPrinted || _handler is not ContributionRouter router)
        {
            return;
        }

        _unmappedPrinted = true;
        foreach (var id in router.FindUnmapped(manifest))
        {
            _output.WriteLine("ventana: no handler is mapped for " + id);
        }
    }

    private async Task<(Pairing Pairing, string Path)?> LoadPairingAsync(ExtensionManifest manifest, string manifestPath,
        CancellationToken cancellationToken)
    {
        var explicitPath = _options.PairingPath ?? _arguments.PairingPath;
        var candidates = explicitPath is not null ? [explicitPath] : PairingCandidates(manifest);
        var watched = candidates[0];
        var hostId = HostIdFor(manifest, null);
        while (true)
        {
            var path = candidates.FirstOrDefault(File.Exists);
            ReasonCode code = ReasonCode.PairingMissing;
            string? detail = null;
            if (path is not null)
            {
                try
                {
                    var read = await PairingReader.ReadFileAsync(path,
                        new PairingReadOptions { ExpectedExtensionId = manifest.Id, ManifestHosts = manifest.Hosts }, cancellationToken)
                        .ConfigureAwait(false);
                    if (read.Value is { } pairing)
                    {
                        return (pairing, path);
                    }

                    var first = read.Diagnostics.First(item => item.Severity == DiagnosticSeverity.Error);
                    if (ReasonCode.TryParse(first.Code, out var parsed) && parsed.IsKnown)
                    {
                        code = parsed;
                    }
                    else
                    {
                        detail = first.ToString();
                    }

                    watched = path;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    watched = path;
                }
            }

            var state = _options.WatchFiles ? ConnectionState.Waiting : ConnectionState.Stopped;
            _output.WriteLine(StatusLine(state, code, hostId, HostName(manifest, null)) + (detail is null ? string.Empty : " " + detail));
            if (!_options.WatchFiles)
            {
                return null;
            }

            _output.WriteLine("ventana: watching " + watched);
            await WaitForChangeAsync([.. candidates, manifestPath], cancellationToken).ConfigureAwait(false);
        }
    }

    private static string[] PairingCandidates(ExtensionManifest manifest)
    {
        var candidates = new List<string>();
        foreach (var id in manifest.Hosts)
        {
            if (HostRegistry.Find(id) is { Status: HostStatus.Active })
            {
                candidates.Add(PairingReader.DefaultPath(id, manifest.Id));
            }
        }

        candidates.Add(Path.Combine(AppContext.BaseDirectory, manifest.Id + CompanionApp.PairingSuffix));
        candidates.Add(Path.Combine(Environment.CurrentDirectory, manifest.Id + CompanionApp.PairingSuffix));
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<ClientEnd> RunClientAsync(ExtensionManifest manifest, string manifestPath, Pairing pairing, string pairingPath,
        CancellationToken cancellationToken)
    {
        using var run = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var current = manifest;
        var hostId = HostIdFor(manifest, pairing);
        var hostName = HostName(manifest, pairing);
        var waitingOnPerson = 0;
        var restart = 0;

        async Task<ExtensionManifest?> ReloadAsync(CancellationToken token)
        {
            var reread = await ReadManifestAsync(manifestPath, report: false, token).ConfigureAwait(false);
            if (reread is null || string.Equals(ManifestWriter.ComputeHash(reread), ManifestWriter.ComputeHash(current), StringComparison.Ordinal))
            {
                return null;
            }

            current = reread;
            return reread;
        }

        var client = new CompanionClient(pairing, manifest, _handler, _options.Client, new ClientInternals
        {
            Transport = _transport?.Invoke(pairing),
            ReloadManifest = ReloadAsync,
        });
        client.StatusChanged += (_, e) =>
        {
            Volatile.Write(ref waitingOnPerson,
                e.State == ConnectionState.Waiting && (e.Reason == ReasonCode.ManifestMismatch || e.Reason == ReasonCode.AuthIdentityChanged) ? 1 : 0);
            _output.WriteLine(StatusLine(e.State, e.Reason, hostId, hostName));
            if (_arguments.Verbose && e.PeerMessage is { } message)
            {
                _output.WriteLine("ventana: host message: " + PrintableAscii(message));
            }

            _options.StatusChanged?.Invoke(e);
        };
        client.HandlerFaulted += (_, e) =>
        {
            _output.WriteLine(FaultLine(e, hostId, hostName));
            if (e.Exception is { } exception)
            {
                _output.WriteLine(exception.ToString());
            }

            _options.HandlerFaulted?.Invoke(e);
        };
        client.SubscriberFaulted += error =>
        {
            if (Interlocked.Exchange(ref _subscriberReported, 1) == 0)
            {
                _output.WriteLine("ventana: a status or fault callback threw " + error.GetType().FullName + ": " + error.Message);
            }
        };

        using var watchStop = CancellationTokenSource.CreateLinkedTokenSource(run.Token);
        var watch = _options.WatchFiles
            ? WatchAsync(pairingPath, manifestPath, () => Volatile.Read(ref waitingOnPerson) == 1, client, () =>
            {
                Volatile.Write(ref restart, 1);
                run.Cancel();
            }, watchStop.Token)
            : Task.CompletedTask;
        try
        {
            await client.RunCoreAsync(run.Token).ConfigureAwait(false);
        }
        finally
        {
            await watchStop.CancelAsync().ConfigureAwait(false);
            try
            {
                await watch.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The watch ends with the client.
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return ClientEnd.Cancelled;
        }

        return Volatile.Read(ref restart) == 1 ? ClientEnd.Restart : ClientEnd.Stopped;
    }

    /// <summary>
    /// While the client waits on a person (<c>manifest.mismatch</c>, <c>auth.identity-changed</c>):
    /// a changed manifest retries at once (the client re-reads it), and a changed pairing restarts
    /// the client with it.
    /// </summary>
    private async Task WatchAsync(string pairingPath, string manifestPath, Func<bool> waitingOnPerson, CompanionClient client,
        Action restart, CancellationToken cancellationToken)
    {
        var pairing = FileStamp.Of(pairingPath);
        var manifest = FileStamp.Of(manifestPath);
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(PollInterval, _time, cancellationToken).ConfigureAwait(false);
            var nextPairing = FileStamp.Of(pairingPath);
            var nextManifest = FileStamp.Of(manifestPath);
            var pairingChanged = nextPairing != pairing;
            var manifestChanged = nextManifest != manifest;
            pairing = nextPairing;
            manifest = nextManifest;
            if (!waitingOnPerson())
            {
                continue;
            }

            if (pairingChanged)
            {
                restart();
                return;
            }

            if (manifestChanged)
            {
                client.RetryNow();
            }
        }
    }

    private async Task WaitForChangeAsync(IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var initial = paths.Select(FileStamp.Of).ToArray();
        while (true)
        {
            await Task.Delay(PollInterval, _time, cancellationToken).ConfigureAwait(false);
            if (!paths.Select(FileStamp.Of).SequenceEqual(initial))
            {
                return;
            }
        }
    }

    /// <summary>The host whose help links and wording a status line uses: the pairing's, else the manifest's first active host.</summary>
    private static string? HostIdFor(ExtensionManifest manifest, Pairing? pairing) =>
        pairing?.HostId ?? HostRegistry.FirstActive(manifest.Hosts)?.Id ?? FirstHost(manifest);

    private static string? HostName(ExtensionManifest manifest, Pairing? pairing)
    {
        if (pairing is not null)
        {
            return HostRegistry.Find(pairing.HostId)?.DisplayName ?? pairing.HostId;
        }

        return HostRegistry.FirstActive(manifest.Hosts)?.DisplayName ?? FirstHost(manifest);
    }

    private static string? FirstHost(ExtensionManifest manifest) => manifest.Hosts.Count > 0 ? manifest.Hosts[0] : null;

    internal static string StatusLine(ConnectionState state, ReasonCode? reason, string? hostId, string? hostName)
    {
        var line = new StringBuilder("ventana: ").Append(StateWord(state));
        if (reason is { } code)
        {
            line.Append(" (").Append(code.Value).Append(')');
            AppendFix(line, code, hostId, hostName);
        }

        return line.ToString();
    }

    internal static string FaultLine(HandlerFaultedEventArgs fault, string? hostId, string? hostName)
    {
        var code = fault.Kind switch
        {
            HandlerFault.IgnoredCancellation => ReasonCode.SessionHandlerStalled,
            HandlerFault.SessionCapacity => ReasonCode.SessionCapacity,
            _ => ReasonCode.SessionHandlerFaulted,
        };
        var line = new StringBuilder("ventana: fault ").Append(fault.Kind switch
        {
            HandlerFault.Exception => "Exception",
            HandlerFault.IgnoredCancellation => "IgnoredCancellation",
            HandlerFault.SessionCapacity => "SessionCapacity",
            HandlerFault.InvalidResult => "InvalidResult",
            _ => "Unknown",
        }).Append(" in ").Append(fault.ContributionId).Append(" (").Append(code.Value).Append(')');
        AppendFix(line, code, hostId, hostName);
        return line.ToString();
    }

    private static void AppendFix(StringBuilder line, ReasonCode code, string? hostId, string? hostName)
    {
        if (code.Info.Fix is { } fix)
        {
            line.Append(' ').Append(hostName is null ? fix : fix.Replace("the host", hostName, StringComparison.Ordinal));
        }

        if (hostId is not null && TextRules.IsHostId(hostId) && code.Info.HelpUri(hostId) is { } help)
        {
            line.Append(' ').Append(help.AbsoluteUri);
        }
    }

    private static string StateWord(ConnectionState state) => state switch
    {
        ConnectionState.Connecting => "connecting",
        ConnectionState.Connected => "connected",
        ConnectionState.Waiting => "waiting",
        ConnectionState.Stopped => "stopped",
        _ => "not started",
    };

    private static string PrintableAscii(string text) => new(text.Where(c => c is >= ' ' and <= '~').Take(256).ToArray());

    /// <summary>What polling compares: whether a file exists, its last write time and its length.</summary>
    private readonly record struct FileStamp(bool Exists, DateTime LastWriteUtc, long Length)
    {
        public static FileStamp Of(string path)
        {
            try
            {
                var info = new FileInfo(path);
                return info.Exists ? new FileStamp(true, info.LastWriteTimeUtc, info.Length) : default;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return default;
            }
        }
    }
}
