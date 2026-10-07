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

    [Fact]
    public async Task EveryEntryIsInTheDocumentedCategory()
    {
        using var files = new CompanionFiles();
        var logs = new CapturingLoggerProvider();
        using (var host = TestHosts.Companion(logs, files, _ => { }))
        {
            await host.StartAsync().WaitAsync(Timeout);
            await Manifests.WaitForAsync(() => logs.Companion.Any(entry => entry.EventId.Id == 14), "watching the pairing file");
            await host.StopAsync().WaitAsync(Timeout);
        }

        // The category is pinned as text: renaming or moving the service must not move its entries unnoticed.
        Assert.Equal(["VentanaTools.Orbit.Extensions.Hosting.CompanionService"],
            logs.Entries.Select(entry => entry.Category).Where(category => !category.StartsWith("Microsoft.", StringComparison.Ordinal)).Distinct());
        foreach (var document in new[] { "src/" + typeof(CompanionServiceOptions).Assembly.GetName().Name + "/README.md", "docs/design/contract-v3.md" })
        {
            Assert.Contains("`" + CapturingLoggerProvider.Category + "`", File.ReadAllText(Repository.PathOf(document)), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task AConnectionThatClosesWithoutAReasonIsLoggedAsARetry()
    {
        using var files = new CompanionFiles();
        files.WritePairing();
        var logs = new CapturingLoggerProvider();
        var connections = Channel.CreateUnbounded<Stream>();
        using var host = TestHosts.Companion(logs, files, options => options.TestTransport = HostPeer.Accepting(connections));
        await host.StartAsync().WaitAsync(Timeout);

        var peer = await HostPeer.AcceptAsync(connections, files.Secret);
        await peer.HandshakeAsync();
        await Manifests.WaitForAsync(() => logs.Companion.Any(entry => entry.EventId.Id == 2), "connected");
        await peer.CloseAsync();
        await Manifests.WaitForAsync(() => logs.Companion.Any(entry => entry.EventId.Id == 4), "the retry");
        await host.StopAsync().WaitAsync(Timeout);

        var retry = Assert.Single(logs.Companion, entry => entry.EventId.Id == 4);
        Assert.Equal(LogLevel.Information, retry.Level);
        // The first retry of the normal backoff: one second, give or take the 20 percent jitter.
        Assert.InRange((double)retry.Values["RetrySeconds"]!, 0.8, 1.2);
        Assert.DoesNotContain(logs.Companion, entry => entry.EventId.Id == 3);
    }

    [Fact]
    public async Task APairingFileThatIsNotJsonIsLoggedByItsDiagnosticCodeAlone()
    {
        using var files = new CompanionFiles();
        File.WriteAllText(files.PairingPath, "{ \"pairingVersion\": ");
        var logs = new CapturingLoggerProvider();
        using var host = TestHosts.Companion(logs, files, _ => { });
        await host.StartAsync().WaitAsync(Timeout);
        await Manifests.WaitForAsync(() => logs.Companion.Any(entry => entry.EventId.Id == 14), "watching the pairing file");
        await host.StopAsync().WaitAsync(Timeout);

        // The reader's code is not a reason code, so the wait names pairing.missing and the diagnostic follows it.
        Assert.Equal("pairing.missing", Assert.Single(logs.Companion, entry => entry.EventId.Id == 5).Values["ReasonCode"]);
        var diagnostic = Assert.Single(logs.Companion, entry => entry.EventId.Id == 6);
        Assert.Equal(LogLevel.Warning, diagnostic.Level);
        Assert.Equal(DiagnosticCodes.JsonSyntax, diagnostic.Values["DiagnosticCode"]);
        Assert.Equal("The pairing file is invalid: " + DiagnosticCodes.JsonSyntax + ".", diagnostic.Message);
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(files.Folder, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AManifestErrorWithoutAPositionIsLoggedAndTheManifestIsWatchedUntilItIsFixed()
    {
        using var files = new CompanionFiles();
        var valid = File.ReadAllBytes(files.ManifestPath);
        // UTF-16, which the reader refuses as a whole (json.encoding), with no line or column.
        File.WriteAllBytes(files.ManifestPath, [0xFF, 0xFE, .. System.Text.Encoding.Unicode.GetBytes("{}")]);
        var logs = new CapturingLoggerProvider();
        using var host = TestHosts.Companion(logs, files, _ => { });
        await host.StartAsync().WaitAsync(Timeout);
        await Manifests.WaitForAsync(() => logs.Companion.Any(entry => entry.EventId.Id == 13), "watching the manifest");

        var invalid = Assert.Single(logs.Companion, entry => entry.EventId.Id == 11);
        Assert.Equal(LogLevel.Error, invalid.Level);
        Assert.Equal(DiagnosticCodes.JsonEncoding, invalid.Values["DiagnosticCode"]);
        Assert.False(invalid.Values.ContainsKey("Line"));
        Assert.DoesNotContain(logs.Companion, entry => entry.EventId.Id == 10);
        Assert.Equal(LogLevel.Information, Assert.Single(logs.Companion, entry => entry.EventId.Id == 13).Level);

        // Once fixed, the manifest is read again and the companion goes on to wait for the pairing.
        File.WriteAllBytes(files.ManifestPath, valid);
        await Manifests.WaitForAsync(() => logs.Companion.Any(entry => entry.EventId.Id == 14), "watching the pairing file");
        await host.StopAsync().WaitAsync(Timeout);
        Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(files.Folder, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task AManifestThatCannotBeReadIsLoggedByTheExceptionsTypeAloneWithoutItsPathOrMessage()
    {
        using var files = new CompanionFiles();
        // A folder where the manifest should be: opening it fails with an exception whose message holds the path.
        var folder = Path.Combine(files.Folder, "folder.json");
        Directory.CreateDirectory(folder);
        var logs = new CapturingLoggerProvider();
        var codes = new ConcurrentQueue<int>();
        using var host = TestHosts.Companion(logs, files, options =>
        {
            options.ManifestPath = folder;
            options.WatchFiles = false;
            options.TestExitCode = codes.Enqueue;
        });
        var stopping = TestHosts.StoppingAsync(host);
        await host.StartAsync().WaitAsync(Timeout);
        await stopping.WaitAsync(Timeout);
        await host.StopAsync().WaitAsync(Timeout);

        var unreadable = Assert.Single(logs.Companion, entry => entry.EventId.Id == 12);
        Assert.Equal(LogLevel.Error, unreadable.Level);
        var type = Assert.IsType<string>(unreadable.Values["ExceptionType"]);
        Assert.Contains(type, new[] { nameof(UnauthorizedAccessException), nameof(IOException) });
        Assert.Equal("The manifest could not be read (" + type + ").", unreadable.Message);
        Assert.Null(unreadable.Exception);
        Assert.DoesNotContain(logs.Entries, entry => entry.Exception is not null
            || entry.Message.Contains(files.Folder, StringComparison.OrdinalIgnoreCase)
            || entry.Values.Values.Any(value => value is string text && text.Contains(files.Folder, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal([3], codes);
    }

    [Fact]
    public async Task ARouterThatMapsNoHandlerForAContributionIsLoggedAtStart()
    {
        using var files = new CompanionFiles();
        var logs = new CapturingLoggerProvider();
        using var host = TestHosts.Build(logs, services => services.AddCompanion(
            _ => new ContributionRouter().MapInvoke(Manifests.Action, (_, _) => Task.FromResult(InvokeResult.Done)),
            options =>
            {
                options.Arguments = [];
                options.ManifestPath = files.ManifestPath;
                options.PairingPath = files.PairingPath;
            }));
        await host.StartAsync().WaitAsync(Timeout);
        await Manifests.WaitForAsync(() => logs.Companion.Any(entry => entry.EventId.Id == 14), "watching the pairing file");
        await host.StopAsync().WaitAsync(Timeout);

        var unmapped = Assert.Single(logs.Companion, entry => entry.EventId.Id == 15);
        Assert.Equal(LogLevel.Warning, unmapped.Level);
        Assert.Equal(Manifests.Widget, unmapped.Values["ContributionId"]);
    }

    [Fact]
    public async Task ACallbackThatThrowsIsLoggedOnceWithItsException()
    {
        using var files = new CompanionFiles();
        files.WritePairing();
        var logs = new CapturingLoggerProvider();
        var calls = 0;
        using var host = TestHosts.Companion(logs, files, options =>
        {
            options.TestTransport = (_, _) => ValueTask.FromResult(false);
            options.Client = TestHosts.FastRetries;
            options.StatusChanged = _ =>
            {
                Interlocked.Increment(ref calls);
                throw new InvalidOperationException("callback detail");
            };
        });
        await host.StartAsync().WaitAsync(Timeout);
        await Manifests.WaitForAsync(() => Volatile.Read(ref calls) >= 5, "five callbacks");
        await host.StopAsync().WaitAsync(Timeout);

        var faulted = Assert.Single(logs.Companion, entry => entry.EventId.Id == 18);
        Assert.Equal(LogLevel.Warning, faulted.Level);
        Assert.Equal("callback detail", Assert.IsType<InvalidOperationException>(faulted.Exception).Message);
    }

    [Fact]
    public async Task AnUnexpectedFailureIsLoggedWithItsExceptionAndStopsTheApplicationWithExitCodeOne()
    {
        using var files = new CompanionFiles();
        files.WritePairing();
        var logs = new CapturingLoggerProvider();
        var codes = new ConcurrentQueue<int>();
        using var host = TestHosts.Companion(logs, files, options =>
        {
            // A failure the contract does not describe: the transport itself throws.
            options.TestTransport = (_, _) => throw new InvalidOperationException("transport detail");
            options.TestExitCode = codes.Enqueue;
        });
        var stopping = TestHosts.StoppingAsync(host);
        await host.StartAsync().WaitAsync(Timeout);
        await stopping.WaitAsync(Timeout);
        await host.StopAsync().WaitAsync(Timeout);

        var unexpected = Assert.Single(logs.Companion, entry => entry.EventId.Id == 20);
        Assert.Equal(LogLevel.Error, unexpected.Level);
        Assert.Equal("transport detail", Assert.IsType<InvalidOperationException>(unexpected.Exception).Message);
        Assert.Equal(1, TestHosts.Service(host).ExitCode);
        Assert.Equal([1], codes);
        var exited = Assert.Single(logs.Companion, entry => entry.EventId.Id == 21);
        Assert.Equal(1, exited.Values["ExitCode"]);
        Assert.Contains("stopping the application", exited.Message, StringComparison.Ordinal);
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
