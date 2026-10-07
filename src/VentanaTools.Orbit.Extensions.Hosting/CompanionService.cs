// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VentanaTools.Orbit.Extensions.Hosting;

/// <summary>
/// The hosted service <c>AddCompanion</c> (<see cref="CompanionServiceCollectionExtensions"/>)
/// registers: it runs <see cref="CompanionApp"/>'s work until the host stops, logging what
/// <see cref="CompanionApp.RunAsync"/> would print.
/// </summary>
internal sealed class CompanionService : BackgroundService
{
    private readonly IContributionHandler _handler;
    private readonly CompanionServiceOptions _options;
    private readonly ILogger _logger;
    private readonly IHostApplicationLifetime? _lifetime;
    private int _exitCode = -1;

    public CompanionService(IContributionHandler handler, CompanionServiceOptions options, ILoggerFactory loggerFactory,
        IHostApplicationLifetime? lifetime)
    {
        _handler = handler;
        _options = options;
        _logger = loggerFactory.CreateLogger<CompanionService>();
        _lifetime = lifetime;
    }

    /// <summary>The companion's exit code (contract §9.2) once it has stopped; null while it runs.</summary>
    public int? ExitCode => Volatile.Read(ref _exitCode) is var code and >= 0 ? code : null;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Reading the manifest and the pairing file never delays the host's start.
        await Task.Yield();
        string[] arguments = _options.Arguments is { } given ? [.. given] : Environment.GetCommandLineArgs().Skip(1).ToArray();
        var appOptions = new CompanionAppOptions
        {
            ManifestPath = _options.ManifestPath,
            PairingPath = _options.PairingPath,
            WatchFiles = _options.WatchFiles,
            Output = TextWriter.Null,
            StatusChanged = _options.StatusChanged,
            HandlerFaulted = _options.HandlerFaulted,
            Client = _options.Client,
        };
        Func<Pairing, ICompanionTransport>? transport = _options.TestTransport is { } accept
            ? _ => new InMemoryTransport(accept, serverVerified: true)
            : null;
        var code = await CompanionApp.RunCoreAsync(arguments, _handler, appOptions, transport, new LoggerAppOutput(_logger), hookConsole: false,
            stoppingToken).ConfigureAwait(false);
        Volatile.Write(ref _exitCode, code);
        if (stoppingToken.IsCancellationRequested)
        {
            return;
        }

        // The companion stopped by itself; CompanionApp.RunAsync would have returned this code.
        if (_options.StopApplicationOnExit && _lifetime is not null)
        {
            CompanionLog.ExitedStopping(_logger, code, null);
            if (code != 0)
            {
                (_options.TestExitCode ?? (static value => Environment.ExitCode = value))(code);
            }

            _lifetime.StopApplication();
        }
        else
        {
            CompanionLog.Exited(_logger, code, null);
        }
    }
}
