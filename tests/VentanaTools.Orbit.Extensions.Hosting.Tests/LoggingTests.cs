// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using VentanaTools.Orbit.Extensions.Wire;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Hosting.Tests;

/// <summary>What the add-on logs, at which level and how often (contract §9.5).</summary>
public sealed class LoggingTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    [Fact]
    public async Task AWaitThatResolvesItselfIsInformationAndARepeatIsLoggedOnceUntilTheStatusChanges()
    {
        using var files = new CompanionFiles();
        files.WritePairing();
        var logs = new CapturingLoggerProvider();
        var connections = Channel.CreateUnbounded<Stream>();
        var hostRunning = 0;
        var waits = new ConcurrentQueue<string?>();
        using var host = TestHosts.Companion(logs, files, options =>
        {
            // A refused attempt is host.not-running, as when the host is closed.
            options.TestTransport = (stream, _) => ValueTask.FromResult(Volatile.Read(ref hostRunning) == 1 && connections.Writer.TryWrite(stream));
            options.StatusChanged = status =>
            {
                if (status.State == ConnectionState.Waiting)
                {
                    waits.Enqueue(status.Reason?.Value);
                }
            };
            options.Client = TestHosts.FastRetries;
        });
        await host.StartAsync().WaitAsync(Timeout);

        // While the host is closed the client probes again and again; the wait is logged once, at Information.
        await Manifests.WaitForAsync(() => waits.Count >= 5, "five waits");
        var first = Assert.Single(logs.Companion, entry => entry.EventId.Id == 3);
        Assert.Equal(LogLevel.Information, first.Level);
        Assert.Equal("host.not-running", first.Values["ReasonCode"]);
        Assert.Equal("Start the host and turn the extension on.", first.Values["Fix"]);

        // The host starts, then closes: each change is logged, and the waits that follow once again.
        Volatile.Write(ref hostRunning, 1);
        var peer = await HostPeer.AcceptAsync(connections, files.Secret);
        Volatile.Write(ref hostRunning, 0);
        await peer.HandshakeAsync();
        await Manifests.WaitForAsync(() => logs.Companion.Any(entry => entry.EventId.Id == 2), "connected");
        var before = waits.Count;
        await peer.SendAsync(new ErrorMessage { Code = ReasonCode.HostShuttingDown, Message = "Closing." });
        await Manifests.WaitForAsync(() => waits.Count >= before + 5, "five more waits");
        await host.StopAsync().WaitAsync(Timeout);

        var logged = logs.Companion.Where(entry => entry.EventId.Id == 3).ToList();
        Assert.Equal(["host.not-running", "host.shutting-down", "host.not-running"], logged.Select(entry => (string?)entry.Values["ReasonCode"]));
        Assert.All(logged, entry => Assert.Equal(LogLevel.Information, entry.Level));
        // The callback still sees every wait; the log has one entry for each run of identical waits.
        Assert.Equal(logged.Select(entry => (string?)entry.Values["ReasonCode"]), Runs(waits));
        Assert.True(waits.Count >= 10, waits.Count + " waits");
        // Nor are the attempts between repeated waits: one is logged after each wait that was logged.
        Assert.Equal(4, logs.Companion.Count(entry => entry.EventId.Id == 1));
        Assert.DoesNotContain(logs.Companion, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task AWaitThatNeedsSomeoneIsAWarningAndItsRepeatsAreNotLogged()
    {
        using var files = new CompanionFiles();
        files.WritePairing();
        var logs = new CapturingLoggerProvider();
        var connections = Channel.CreateUnbounded<Stream>();
        var waits = new ConcurrentQueue<string?>();
        using var host = TestHosts.Companion(logs, files, options =>
        {
            options.Arguments = ["--verbose"];
            options.TestTransport = HostPeer.Accepting(connections);
            options.StatusChanged = status =>
            {
                if (status.State == ConnectionState.Waiting)
                {
                    waits.Enqueue(status.Reason?.Value);
                }
            };
            options.Client = TestHosts.FastRetries;
        });
        using var over = new CancellationTokenSource();

        // A host that admitted another manifest refuses every attempt with manifest.mismatch, which waits on a person.
        var refusing = Task.Run(async () =>
        {
            try
            {
                while (true)
                {
                    var peer = await HostPeer.AcceptAsync(connections, files.Secret, over.Token);
                    await peer.RefuseAsync(ReasonCode.ManifestMismatch);
                }
            }
            catch (Exception) when (over.IsCancellationRequested)
            {
                // The test is over; the companion stops mid-attempt.
            }
        });
        await host.StartAsync().WaitAsync(Timeout);

        await Manifests.WaitForAsync(() => waits.Count >= 3, "three waits");
        await over.CancelAsync();
        await host.StopAsync().WaitAsync(Timeout);
        await refusing.WaitAsync(Timeout);

        Assert.All(waits, reason => Assert.Equal("manifest.mismatch", reason));
        var waiting = Assert.Single(logs.Companion, entry => entry.EventId.Id == 3);
        Assert.Equal(LogLevel.Warning, waiting.Level);
        Assert.Equal("manifest.mismatch", waiting.Values["ReasonCode"]);
        Assert.Equal("Reload the manifest in the host, or update the companion's extension.json.", waiting.Values["Fix"]);
        // The host's text comes with every refusal, and is logged with the first only.
        Assert.Equal("Refused.", Assert.Single(logs.Companion, entry => entry.EventId.Id == 16).Values["HostMessage"]);
    }

    /// <summary>The values with each run of equal neighbours collapsed into one.</summary>
    private static List<string?> Runs(IEnumerable<string?> values)
    {
        var runs = new List<string?>();
        foreach (var value in values)
        {
            if (runs.Count == 0 || runs[^1] != value)
            {
                runs.Add(value);
            }
        }

        return runs;
    }
}
