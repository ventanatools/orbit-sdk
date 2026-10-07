// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using Microsoft.Extensions.Logging;

namespace VentanaTools.Orbit.Extensions.Hosting;

/// <summary>
/// What <see cref="CompanionApp"/> reports, logged (contract §9.5): the same events as its status
/// lines, as structured log entries with a level and an event id each. Paths, which name the
/// person's profile, are never logged: neither the files <see cref="CompanionApp"/> watches nor the
/// file of a diagnostic.
/// </summary>
internal sealed class LoggerAppOutput : ICompanionAppOutput
{
    private readonly ILogger _logger;

    public LoggerAppOutput(ILogger logger) => _logger = logger;

    public void Usage(string usage) => CompanionLog.Usage(_logger, usage, null);

    public void Unexpected(Exception error) => CompanionLog.Unexpected(_logger, error);

    public void ManifestUnreadable(string path, Exception error) => CompanionLog.ManifestUnreadable(_logger, error.GetType().Name, null);

    public void ManifestInvalid(string path, Diagnostic diagnostic)
    {
        if (diagnostic.Line is { } line && diagnostic.Column is { } column)
        {
            CompanionLog.ManifestInvalid(_logger, diagnostic.Code, line, column, diagnostic.Message, null);
        }
        else
        {
            CompanionLog.ManifestInvalidNoPosition(_logger, diagnostic.Code, diagnostic.Message, null);
        }
    }

    public void Watching(string path, AppFile file)
    {
        if (file == AppFile.Manifest)
        {
            CompanionLog.WatchingManifest(_logger, null);
        }
        else
        {
            CompanionLog.WatchingPairing(_logger, null);
        }
    }

    public void Unmapped(string contributionId) => CompanionLog.Unmapped(_logger, contributionId, null);

    public void PairingStatus(ConnectionState state, ReasonCode code, Diagnostic? detail, HostInfo? host)
    {
        if (state == ConnectionState.Waiting)
        {
            CompanionLog.PairingUnusable(_logger, code.Value, Fix(code, host), Help(code, host), null);
        }
        else
        {
            CompanionLog.Stopped(_logger, code.Value, Fix(code, host), Help(code, host), null);
        }

        if (detail is not null)
        {
            CompanionLog.PairingDiagnostic(_logger, detail.Code, null);
        }
    }

    public void Status(StatusChangedEventArgs status, HostInfo? host)
    {
        var seconds = status.RetryIn?.TotalSeconds ?? 0;
        switch (status.State)
        {
            case ConnectionState.Connecting:
                CompanionLog.Connecting(_logger, status.Attempt, null);
                break;
            case ConnectionState.Connected:
                CompanionLog.Connected(_logger, status.Host?.Id ?? string.Empty, status.Host?.Version ?? string.Empty,
                    status.ProtocolVersion ?? 0, null);
                break;
            case ConnectionState.Waiting when status.Reason is { } code:
                CompanionLog.Waiting(_logger, code.Value, seconds, Fix(code, host), Help(code, host), null);
                break;
            case ConnectionState.Waiting:
                CompanionLog.Retrying(_logger, seconds, null);
                break;
            case ConnectionState.Stopped when status.Reason is { } code:
                CompanionLog.Stopped(_logger, code.Value, Fix(code, host), Help(code, host), null);
                break;
            default:
                CompanionLog.StoppedQuietly(_logger, null);
                break;
        }
    }

    public void HostMessage(string text) => CompanionLog.HostMessage(_logger, text, null);

    public void Fault(HandlerFaultedEventArgs fault, HostInfo? host)
    {
        var code = AppRunner.FaultCode(fault.Kind);
        CompanionLog.HandlerFaulted(_logger, AppRunner.FaultName(fault.Kind), fault.ContributionId, code.Value, Fix(code, host), Help(code, host),
            fault.Exception);
    }

    public void CallbackFaulted(Exception error) => CompanionLog.CallbackFaulted(_logger, error);

    private static string Fix(ReasonCode code, HostInfo? host) => AppRunner.FixFor(code, host?.DisplayName) ?? string.Empty;

    private static string Help(ReasonCode code, HostInfo? host) => AppRunner.HelpLinkFor(code, host?.Id) ?? string.Empty;
}

/// <summary>The add-on's log entries, defined once each so logging allocates nothing when a level is off.</summary>
internal static class CompanionLog
{
    public static readonly Action<ILogger, int, Exception?> Connecting = LoggerMessage.Define<int>(
        LogLevel.Debug, new EventId(1, nameof(Connecting)), "Connecting to the host (attempt {Attempt}).");

    public static readonly Action<ILogger, string, string, int, Exception?> Connected = LoggerMessage.Define<string, string, int>(
        LogLevel.Information, new EventId(2, nameof(Connected)), "Connected to {HostId} {HostVersion} with protocol {ProtocolVersion}.");

    public static readonly Action<ILogger, string, double, string, string, Exception?> Waiting = LoggerMessage.Define<string, double, string, string>(
        LogLevel.Warning, new EventId(3, nameof(Waiting)), "Waiting ({ReasonCode}), retrying in {RetrySeconds:0.0} s. {Fix} {HelpLink}");

    public static readonly Action<ILogger, double, Exception?> Retrying = LoggerMessage.Define<double>(
        LogLevel.Information, new EventId(4, nameof(Retrying)), "The connection closed; retrying in {RetrySeconds:0.0} s.");

    public static readonly Action<ILogger, string, string, string, Exception?> PairingUnusable = LoggerMessage.Define<string, string, string>(
        LogLevel.Warning, new EventId(5, nameof(PairingUnusable)), "Waiting for connection info ({ReasonCode}). {Fix} {HelpLink}");

    public static readonly Action<ILogger, string, Exception?> PairingDiagnostic = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(6, nameof(PairingDiagnostic)), "The pairing file is invalid: {DiagnosticCode}.");

    public static readonly Action<ILogger, string, string, string, Exception?> Stopped = LoggerMessage.Define<string, string, string>(
        LogLevel.Warning, new EventId(7, nameof(Stopped)), "Stopped ({ReasonCode}). {Fix} {HelpLink}");

    public static readonly Action<ILogger, Exception?> StoppedQuietly = LoggerMessage.Define(
        LogLevel.Information, new EventId(8, nameof(StoppedQuietly)), "Stopped.");

    public static readonly Action<ILogger, string, long, long, string, Exception?> ManifestInvalid = LoggerMessage.Define<string, long, long, string>(
        LogLevel.Error, new EventId(10, nameof(ManifestInvalid)), "The manifest is invalid: {DiagnosticCode} at line {Line}, column {Column}. {DiagnosticMessage}");

    public static readonly Action<ILogger, string, string, Exception?> ManifestInvalidNoPosition = LoggerMessage.Define<string, string>(
        LogLevel.Error, new EventId(11, nameof(ManifestInvalid)), "The manifest is invalid: {DiagnosticCode}. {DiagnosticMessage}");

    public static readonly Action<ILogger, string, Exception?> ManifestUnreadable = LoggerMessage.Define<string>(
        LogLevel.Error, new EventId(12, nameof(ManifestUnreadable)), "The manifest could not be read ({ExceptionType}).");

    public static readonly Action<ILogger, Exception?> WatchingManifest = LoggerMessage.Define(
        LogLevel.Information, new EventId(13, nameof(WatchingManifest)), "Waiting for the manifest to change.");

    public static readonly Action<ILogger, Exception?> WatchingPairing = LoggerMessage.Define(
        LogLevel.Information, new EventId(14, nameof(WatchingPairing)), "Waiting for the pairing file to appear or change.");

    public static readonly Action<ILogger, string, Exception?> Unmapped = LoggerMessage.Define<string>(
        LogLevel.Warning, new EventId(15, nameof(Unmapped)), "No handler is mapped for {ContributionId}.");

    public static readonly Action<ILogger, string, Exception?> HostMessage = LoggerMessage.Define<string>(
        LogLevel.Debug, new EventId(16, nameof(HostMessage)), "The host's message: {HostMessage}");

    public static readonly Action<ILogger, string, string, string, string, string, Exception?> HandlerFaulted =
        LoggerMessage.Define<string, string, string, string, string>(
            LogLevel.Error, new EventId(17, nameof(HandlerFaulted)), "Handler fault {FaultKind} in {ContributionId} ({ReasonCode}). {Fix} {HelpLink}");

    public static readonly Action<ILogger, Exception?> CallbackFaulted = LoggerMessage.Define(
        LogLevel.Warning, new EventId(18, nameof(CallbackFaulted)), "A status or fault callback threw; later callback failures are not logged.");

    public static readonly Action<ILogger, string, Exception?> Usage = LoggerMessage.Define<string>(
        LogLevel.Error, new EventId(19, nameof(Usage)), "The command line is not valid: {Usage}");

    public static readonly Action<ILogger, Exception?> Unexpected = LoggerMessage.Define(
        LogLevel.Error, new EventId(20, nameof(Unexpected)), "The companion failed unexpectedly.");

    public static readonly Action<ILogger, int, Exception?> ExitedStopping = LoggerMessage.Define<int>(
        LogLevel.Error, new EventId(21, "Exited"), "The companion stopped with exit code {ExitCode}; stopping the application.");

    public static readonly Action<ILogger, int, Exception?> Exited = LoggerMessage.Define<int>(
        LogLevel.Error, new EventId(21, nameof(Exited)), "The companion stopped with exit code {ExitCode}.");
}
