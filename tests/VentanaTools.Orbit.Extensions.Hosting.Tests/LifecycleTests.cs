// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
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

    [Theory]
    [InlineData("--environment Development --verbose")]
    [InlineData("--verbose true --environment Development")]
    public void TheDocumentedVerboseFormsLeaveTheHostsOwnArgumentsIntact(string arguments)
    {
        // The Generic Host reads its command line as --key value pairs; contract §9.5 tells authors to put --verbose
        // last or to write --verbose true, which both parsers then read as meant.
        string[] args = arguments.Split(' ');
        Assert.Equal(Environments.Development, Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args }).Environment.EnvironmentName);
        Assert.True(CompanionApp.ParseArguments(args).Verbose);

        // Why: a value-less --verbose before another option takes that option as its value.
        string[] first = ["--verbose", "--environment", "Development"];
        var misread = new ConfigurationBuilder().AddCommandLine(first).Build();
        Assert.Null(misread[HostDefaults.EnvironmentKey]);
        Assert.Equal("--environment", misread["verbose"]);
    }

    [Fact]
    public async Task TheProcessExitsWithTheCompanionsExitCode()
    {
        // The real path, in a child process (Program.cs): the add-on sets Environment.ExitCode and stops the
        // application, and the process ends with that code, not with the 0 a host that stopped normally gives.
        using var files = new CompanionFiles();
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host) ? host : "dotnet";
        var start = new ProcessStartInfo(dotnet)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { typeof(LifecycleTests).Assembly.Location, "--companion", files.ManifestPath, files.PairingPath })
        {
            start.ArgumentList.Add(argument);
        }

        using var child = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        var output = child.StandardOutput.ReadToEndAsync();
        var error = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }

        // Exit code 3: the pairing is missing and the files are not watched.
        Assert.True(child.ExitCode == 3, "Exit code " + child.ExitCode + ".\n" + await output + await error);
    }

    [Fact]
    public async Task StoppingTheHostNeverWaitsForAHandlerThatIgnoresCancellation()
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
            options.Client = new CompanionClientOptions { HandlerStopTimeout = TimeSpan.FromMilliseconds(200) };
        });
        // A session handler that never looks at its token.
        var running = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        host.Services.GetRequiredService<ClockWidget>().OnSession = async _ =>
        {
            running.TrySetResult();
            await release.Task;
        };
        await host.StartAsync().WaitAsync(Timeout);

        var peer = await HostPeer.AcceptAsync(connections, files.Secret);
        await peer.HandshakeAsync();
        await peer.SendAsync(new StartSessionMessage { SessionId = Guid.NewGuid().ToString("N"), ContributionId = Manifests.Widget, Settings = new Dictionary<string, string>() });
        await running.Task.WaitAsync(Timeout);
        try
        {
            // The documented bound (contract §9.5): the stop never waits for a handler, and at most 5 seconds for callbacks.
            var watch = Stopwatch.StartNew();
            await host.StopAsync().WaitAsync(Timeout);
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), "The stop took " + watch.Elapsed + ".");
            Assert.False(release.Task.IsCompleted);
            Assert.Equal(0, TestHosts.Service(host).ExitCode);

            // Past HandlerStopTimeout the handler, still running, is reported as ignoring its cancellation.
            await Manifests.WaitForAsync(() => logs.Companion.Any(entry => entry.EventId.Id == 17), "the fault");
            var fault = Assert.Single(logs.Companion, entry => entry.EventId.Id == 17);
            Assert.Equal("IgnoredCancellation", fault.Values["FaultKind"]);
            Assert.Equal("session.handler-stalled", fault.Values["ReasonCode"]);
            Assert.Null(fault.Exception);
            await Manifests.WaitForAsync(() => !faults.IsEmpty, "the fault callback");
            Assert.Equal(HandlerFault.IgnoredCancellation, Assert.Single(faults).Kind);
        }
        finally
        {
            release.TrySetResult();
        }
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
    [InlineData("--verbose", true)]
    [InlineData("--environment Development --verbose", true)]
    [InlineData("--verbose true --environment Development", true)]
    [InlineData("--environment Development", false)]
    public async Task TheHostsFixedTextIsLoggedOnlyWithVerbose(string arguments, bool verbose)
    {
        using var files = new CompanionFiles();
        files.WritePairing();
        var logs = new CapturingLoggerProvider();
        var connections = Channel.CreateUnbounded<Stream>();
        var waited = new TaskCompletionSource<StatusChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = Build(logs, files, options =>
        {
            options.Arguments = arguments.Split(' ');
            options.TestTransport = HostPeer.Accepting(connections);
            // The callback runs after the status and the host's text are logged, in the same event, so once it has
            // seen the wait, everything the add-on logs for it is in the log.
            options.StatusChanged = status =>
            {
                if (status.State == ConnectionState.Waiting)
                {
                    waited.TrySetResult(status);
                }
            };
        });
        await host.StartAsync().WaitAsync(Timeout);

        var peer = await HostPeer.AcceptAsync(connections, files.Secret);
        await peer.HandshakeAsync();
        await peer.SendAsync(new ErrorMessage { Code = ReasonCode.HostTurnedOff, Message = "Turned off." });
        Assert.Equal(ReasonCode.HostTurnedOff, (await waited.Task.WaitAsync(Timeout)).Reason);
        var waiting = Assert.Single(logs.Companion, entry => entry.EventId.Id == 3);
        Assert.Equal("host.turned-off", waiting.Values["ReasonCode"]);
        Assert.Equal(LogLevel.Information, waiting.Level);
        var messages = logs.Companion.Where(entry => entry.EventId.Id == 16).ToList();
        if (verbose)
        {
            var message = Assert.Single(messages);
            Assert.Equal("Turned off.", message.Values["HostMessage"]);
            Assert.Equal(LogLevel.Debug, message.Level);
        }
        else
        {
            // The host sent its text with the error, and the event that carried it has been logged in full.
            Assert.Empty(messages);
            Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains("Turned off.", StringComparison.Ordinal)
                || entry.Values.Values.Any(value => value is string text && text.Contains("Turned off.", StringComparison.Ordinal)));
        }

        await host.StopAsync().WaitAsync(Timeout);
    }

    private static IHost Build(CapturingLoggerProvider logs, CompanionFiles files, Action<CompanionServiceOptions> configure) =>
        TestHosts.Companion(logs, files, configure);
}
