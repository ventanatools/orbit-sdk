// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VentanaTools.Orbit.Extensions.Wire;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Hosting.Tests;

/// <summary>
/// The companion in a real Generic Host: discovery, waiting, logging, exit codes, and how a host
/// stop or the companion's own stop ends the other (contract §9.5).
/// </summary>
public sealed class LifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task OnFirstRunItLogsTheMissingPairingWaitsForItAndStopsWithTheHost()
    {
        using var files = new CompanionFiles();
        var logs = new CapturingLoggerProvider();
        using var host = Build(logs, files, _ => { });
        await host.StartAsync().WaitAsync(Timeout);

        await Manifests.WaitForAsync(() => logs.Companion.Any(entry => entry.EventId.Id == 14), "watching the pairing file");
        var waiting = Assert.Single(logs.Companion, entry => entry.EventId.Id == 5);
        Assert.Equal(LogLevel.Warning, waiting.Level);
        Assert.Equal("pairing.missing", waiting.Values["ReasonCode"]);
        // The test host id is in no registry, so the fix keeps "the host" and has no help link.
        Assert.Equal("In the host, choose Save connection info.", waiting.Values["Fix"]);
        Assert.Equal(string.Empty, waiting.Values["HelpLink"]);

        await host.StopAsync().WaitAsync(Timeout);
        Assert.Equal(0, TestHosts.Service(host).ExitCode);
        Assert.DoesNotContain(logs.Companion, entry => entry.EventId.Id == 21);
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(files.Folder, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task WithoutWatchingAMissingPairingStopsTheApplicationWithExitCodeThree()
    {
        using var files = new CompanionFiles();
        var logs = new CapturingLoggerProvider();
        var codes = new ConcurrentQueue<int>();
        using var host = Build(logs, files, options =>
        {
            options.WatchFiles = false;
            options.TestExitCode = codes.Enqueue;
        });
        var stopping = TestHosts.StoppingAsync(host);
        await host.StartAsync().WaitAsync(Timeout);

        await stopping.WaitAsync(Timeout);
        await host.StopAsync().WaitAsync(Timeout);
        Assert.Equal(3, TestHosts.Service(host).ExitCode);
        Assert.Equal([3], codes);
        var stopped = Assert.Single(logs.Companion, entry => entry.EventId.Id == 7);
        Assert.Equal("pairing.missing", stopped.Values["ReasonCode"]);
        var exited = Assert.Single(logs.Companion, entry => entry.EventId.Id == 21);
        Assert.Equal(LogLevel.Error, exited.Level);
        Assert.Equal(3, exited.Values["ExitCode"]);
        Assert.Contains("stopping the application", exited.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WhenItMustNotStopTheApplicationItOnlyLogsItsExitCode()
    {
        using var files = new CompanionFiles();
        var logs = new CapturingLoggerProvider();
        var codes = new ConcurrentQueue<int>();
        using var host = Build(logs, files, options =>
        {
            options.WatchFiles = false;
            options.StopApplicationOnExit = false;
            options.TestExitCode = codes.Enqueue;
        });
        await host.StartAsync().WaitAsync(Timeout);

        await Manifests.WaitForAsync(() => logs.Companion.Any(entry => entry.EventId.Id == 21), "the exit code");
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping.IsCancellationRequested);
        Assert.Empty(codes);
        Assert.Equal(3, TestHosts.Service(host).ExitCode);
        Assert.DoesNotContain("stopping the application", Assert.Single(logs.Companion, entry => entry.EventId.Id == 21).Message, StringComparison.Ordinal);
        await host.StopAsync().WaitAsync(Timeout);
    }

    [Fact]
    public async Task AUsageErrorIsLoggedAndStopsTheApplicationWithExitCodeTwo()
    {
        using var files = new CompanionFiles();
        var logs = new CapturingLoggerProvider();
        var codes = new ConcurrentQueue<int>();
        using var host = Build(logs, files, options =>
        {
            options.Arguments = ["--manifest"];
            options.TestExitCode = codes.Enqueue;
        });
        var stopping = TestHosts.StoppingAsync(host);
        await host.StartAsync().WaitAsync(Timeout);

        await stopping.WaitAsync(Timeout);
        await host.StopAsync().WaitAsync(Timeout);
        var usage = Assert.Single(logs.Companion, entry => entry.EventId.Id == 19);
        Assert.Equal(LogLevel.Error, usage.Level);
        Assert.Equal("--manifest needs a value.", usage.Values["Usage"]);
        Assert.Equal([2], codes);
    }

    [Fact]
    public async Task AnInvalidManifestIsLoggedByCodeAndPositionAndNeverByPath()
    {
        using var files = new CompanionFiles();
        File.WriteAllText(files.ManifestPath, "{ \"schemaVersion\": 3 }");
        var logs = new CapturingLoggerProvider();
        var codes = new ConcurrentQueue<int>();
        using var host = Build(logs, files, options =>
        {
            options.WatchFiles = false;
            options.TestExitCode = codes.Enqueue;
        });
        var stopping = TestHosts.StoppingAsync(host);
        await host.StartAsync().WaitAsync(Timeout);

        await stopping.WaitAsync(Timeout);
        await host.StopAsync().WaitAsync(Timeout);
        var invalid = logs.Companion.Where(entry => entry.EventId.Id == 10).ToList();
        Assert.NotEmpty(invalid);
        Assert.All(invalid, entry => Assert.Equal(LogLevel.Error, entry.Level));
        Assert.Contains(invalid, entry => (string?)entry.Values["DiagnosticCode"] == DiagnosticCodes.JsonRequiredMissing
            && (long?)entry.Values["Line"] == 1 && (long?)entry.Values["Column"] == 1);
        Assert.Equal([3], codes);
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(files.Folder, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ConnectedItServesSessionsAndAHostStopEndsThemGracefully()
    {
        using var files = new CompanionFiles();
        files.WritePairing();
        var logs = new CapturingLoggerProvider();
        var connections = Channel.CreateUnbounded<Stream>();
        var statuses = new ConcurrentQueue<ConnectionState>();
        using var host = Build(logs, files, options =>
        {
            options.TestTransport = HostPeer.Accepting(connections);
            options.StatusChanged = status => statuses.Enqueue(status.State);
        });
        var handler = host.Services.GetRequiredService<ClockWidget>();
        await host.StartAsync().WaitAsync(Timeout);

        var peer = await HostPeer.AcceptAsync(connections, files.Secret);
        await peer.HandshakeAsync();
        await Manifests.WaitForAsync(() => logs.Companion.Any(entry => entry.EventId.Id == 2), "connected");
        var connected = Assert.Single(logs.Companion, entry => entry.EventId.Id == 2);
        Assert.Equal(LogLevel.Information, connected.Level);
        Assert.Equal(Manifests.Host, connected.Values["HostId"]);
        Assert.Equal(3, connected.Values["ProtocolVersion"]);

        var sessionId = Guid.NewGuid().ToString("N");
        await peer.SendAsync(new StartSessionMessage { SessionId = sessionId, ContributionId = Manifests.Widget, Settings = new Dictionary<string, string>() });
        var face = await peer.ReadUntilAsync<SetFaceMessage>();
        Assert.Equal(sessionId, face.SessionId);
        Assert.Equal(new TextLine { Text = "12:00" }, face.Face.Line1);

        // The host stops: the connection closes, the session's token is cancelled and the companion ends with 0.
        await host.StopAsync().WaitAsync(Timeout);
        await handler.SessionCancelled.Task.WaitAsync(Timeout);
        await peer.WaitForCloseAsync();
        Assert.Equal(0, TestHosts.Service(host).ExitCode);
        Assert.Equal(ConnectionState.Connected, statuses.First(state => state != ConnectionState.Connecting));
        Assert.Equal(ConnectionState.Stopped, statuses.Last());
        Assert.Single(logs.Companion, entry => entry.EventId.Id == 8);
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(PipeNames.Prefix, StringComparison.Ordinal)
            || entry.Message.Contains(Convert.ToBase64String(files.Secret), StringComparison.Ordinal));
    }

    [Fact]
    public async Task AHandlerFaultIsLoggedWithTheAuthorsExceptionAndReachesTheCallback()
    {
        using var files = new CompanionFiles();
        files.WritePairing();
        var logs = new CapturingLoggerProvider();
        var connections = Channel.CreateUnbounded<Stream>();
        var faults = new ConcurrentQueue<HandlerFaultedEventArgs>();
        using var host = Build(logs, files, options =>
        {
            options.TestTransport = HostPeer.Accepting(connections);
            options.HandlerFaulted = faults.Enqueue;
        });
        host.Services.GetRequiredService<ClockWidget>().OnSession = _ => throw new InvalidOperationException("author detail");
        await host.StartAsync().WaitAsync(Timeout);

        var peer = await HostPeer.AcceptAsync(connections, files.Secret);
        await peer.HandshakeAsync();
        await peer.SendAsync(new StartSessionMessage
        {
            SessionId = Guid.NewGuid().ToString("N"),
            ContributionId = Manifests.Widget,
            Settings = new Dictionary<string, string>(),
        });
        await peer.ReadUntilAsync<ClearFaceMessage>();
        await Manifests.WaitForAsync(() => logs.Companion.Any(entry => entry.EventId.Id == 17), "the fault");
        var fault = Assert.Single(logs.Companion, entry => entry.EventId.Id == 17);
        Assert.Equal(LogLevel.Error, fault.Level);
        Assert.Equal("Exception", fault.Values["FaultKind"]);
        Assert.Equal(Manifests.Widget, fault.Values["ContributionId"]);
        Assert.Equal("session.handler-faulted", fault.Values["ReasonCode"]);
        Assert.Equal("author detail", Assert.IsType<InvalidOperationException>(fault.Exception).Message);
        await Manifests.WaitForAsync(() => !faults.IsEmpty, "the fault callback");
        Assert.Equal(HandlerFault.Exception, Assert.Single(faults).Kind);
        await host.StopAsync().WaitAsync(Timeout);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TheHostsFixedTextIsLoggedOnlyWithVerbose(bool verbose)
    {
        using var files = new CompanionFiles();
        files.WritePairing();
        var logs = new CapturingLoggerProvider();
        var connections = Channel.CreateUnbounded<Stream>();
        using var host = Build(logs, files, options =>
        {
            options.Arguments = verbose ? ["--verbose"] : [];
            options.TestTransport = HostPeer.Accepting(connections);
        });
        await host.StartAsync().WaitAsync(Timeout);

        var peer = await HostPeer.AcceptAsync(connections, files.Secret);
        await peer.HandshakeAsync();
        await peer.SendAsync(new ErrorMessage { Code = ReasonCode.HostTurnedOff, Message = "Turned off." });
        await Manifests.WaitForAsync(() => logs.Companion.Any(entry => entry.EventId.Id == 3), "waiting");
        var waiting = Assert.Single(logs.Companion, entry => entry.EventId.Id == 3);
        Assert.Equal("host.turned-off", waiting.Values["ReasonCode"]);
        var messages = logs.Companion.Where(entry => entry.EventId.Id == 16).ToList();
        if (verbose)
        {
            Assert.Equal("Turned off.", Assert.Single(messages).Values["HostMessage"]);
            Assert.Equal(LogLevel.Debug, messages[0].Level);
        }
        else
        {
            Assert.Empty(messages);
            Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("Turned off.", StringComparison.Ordinal));
        }

        await host.StopAsync().WaitAsync(Timeout);
    }

    private static IHost Build(CapturingLoggerProvider logs, CompanionFiles files, Action<CompanionServiceOptions> configure) =>
        TestHosts.Build(logs, services =>
        {
            services.AddSingleton(new Clock());
            services.AddCompanion<ClockWidget>(options =>
            {
                options.Arguments = [];
                options.ManifestPath = files.ManifestPath;
                options.PairingPath = files.PairingPath;
                configure(options);
            });
        });
}
