// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Text;
using System.Threading.Channels;
using VentanaTools.Orbit.Extensions.Wire;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tests.Client;

/// <summary><see cref="CompanionApp"/>: arguments, discovery, the first-run wait, watching, output and exit codes (contract §9.2).</summary>
public sealed class CompanionAppTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ventana-app-tests-" + Guid.NewGuid().ToString("N"));

    public CompanionAppTests() => Directory.CreateDirectory(_folder);

    private string ManifestPath => Path.Combine(_folder, "extension.json");

    private string PairingPath => Path.Combine(_folder, "example.countdown.pairing.json");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void ArgumentsAreReadAndTheRestIsLeftForTheAuthor()
    {
        var parsed = CompanionApp.ParseArguments(["--name", "x", "--manifest", "m.json", "--verbose", "--pairing", "p.json", "tail"]);
        Assert.Equal("m.json", parsed.ManifestPath);
        Assert.Equal("p.json", parsed.PairingPath);
        Assert.True(parsed.Verbose);
        Assert.Equal(["--name", "x", "tail"], parsed.Remaining);
        Assert.Throws<ArgumentException>(() => CompanionApp.ParseArguments(["--pairing"]));
        Assert.Throws<ArgumentException>(() => CompanionApp.ParseArguments(["--manifest"]));
        Assert.False(CompanionApp.ParseArguments([]).Verbose);
    }

    [Fact]
    public async Task AnOptionWithoutItsValueExitsTwo()
    {
        var output = new StringWriter();
        Assert.Equal(2, await CompanionApp.RunCoreAsync(["--manifest"], new TestHandler(), new CompanionAppOptions { Output = output }, null,
            CancellationToken.None));
        Assert.Equal("ventana: --manifest needs a value." + Environment.NewLine, output.ToString());
        var pairing = new StringWriter();
        Assert.Equal(2, await CompanionApp.RunCoreAsync(["--verbose", "--pairing"], new TestHandler(), new CompanionAppOptions { Output = pairing }, null,
            CancellationToken.None));
        Assert.Equal("ventana: --pairing needs a value." + Environment.NewLine, pairing.ToString());
    }

    [Fact]
    public async Task AMissingPairingExitsThreeWhenNotWatching()
    {
        WriteManifest(TestManifests.Countdown);
        var output = new StringWriter();
        var code = await CompanionApp.RunCoreAsync(Args(), new TestHandler(), new CompanionAppOptions { Output = output, WatchFiles = false }, null,
            CancellationToken.None);
        Assert.Equal(3, code);
        // The test host is not in the registry, so the line keeps "the host" and links nowhere.
        Assert.StartsWith("ventana: stopped (pairing.missing) In the host, choose Save connection info." + Environment.NewLine,
            output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARegistryHostNamesTheActionAndLinksToItsHelp()
    {
        var host = HostRegistry.Known.First(entry => entry.Status == HostStatus.Active);
        var manifest = TestManifests.Countdown;
        WriteManifest(new ExtensionManifest
        {
            Id = manifest.Id,
            Name = manifest.Name,
            Description = manifest.Description,
            Version = manifest.Version,
            Hosts = [host.Id],
            Contributions = manifest.Contributions,
        });
        var output = new StringWriter();
        Assert.Equal(3, await CompanionApp.RunCoreAsync(Args(), new TestHandler(), new CompanionAppOptions { Output = output, WatchFiles = false }, null,
            CancellationToken.None));
        Assert.StartsWith("ventana: stopped (pairing.missing) In " + host.DisplayName + ", choose Save connection info. https://dev.ventana.tools/go/"
            + host.Id + "/codes#pairing-missing" + Environment.NewLine, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInvalidManifestExitsThreeWhenNotWatching()
    {
        File.WriteAllText(ManifestPath, "{ \"schemaVersion\": 3 }");
        var output = new StringWriter();
        var code = await CompanionApp.RunCoreAsync(Args(), new TestHandler(), new CompanionAppOptions { Output = output, WatchFiles = false }, null,
            CancellationToken.None);
        Assert.Equal(3, code);
        Assert.Contains("json.required-missing", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnFirstRunItPrintsTheCodeAndThePathThenWaitsForThePairing()
    {
        WriteManifest(TestManifests.Countdown);
        var clock = new TestClock();
        var output = new SyncWriter();
        var hosts = Channel.CreateUnbounded<Stream>();
        using var stop = new CancellationTokenSource();
        var run = CompanionApp.RunCoreAsync(Args(), new TestHandler(), Options(output, clock), Transport(hosts), stop.Token);
        await ClientHarness.WaitForAsync(() => output.Text.Contains("watching " + PairingPath, StringComparison.Ordinal), "watching");
        Assert.Contains("ventana: waiting (pairing.missing) In the host, choose Save connection info.", output.Text, StringComparison.Ordinal);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.True(hosts.Reader.Count == 0);

        WritePairing();
        await AdvanceUntilAsync(clock, () => hosts.Reader.Count > 0);
        var peer = new ScriptedPeer(await hosts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        await peer.HandshakeAsync(new HostScript());
        await ClientHarness.WaitForAsync(() => output.Text.Contains("ventana: connected", StringComparison.Ordinal), "connected");
        await stop.CancelAsync();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.DoesNotContain(Convert.ToBase64String(ClientHarness.Secret), output.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(PipeNames.Prefix, output.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AManifestMismatchReReadsExtensionJsonAndRetriesWhenItChanges()
    {
        WriteManifest(TestManifests.Countdown);
        WritePairing();
        var clock = new TestClock();
        var output = new SyncWriter();
        var hosts = Channel.CreateUnbounded<Stream>();
        using var stop = new CancellationTokenSource();
        var run = CompanionApp.RunCoreAsync(Args(), new TestHandler(), Options(output, clock), Transport(hosts), stop.Token);
        var first = new ScriptedPeer(await hosts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        var hello = await first.ReadHelloAsync();
        Assert.Equal(ManifestWriter.ComputeHash(TestManifests.Countdown), hello.ManifestHash);
        await first.SendAsync(first.BuildChallenge(new HostScript()));
        Assert.IsType<AuthenticateMessage>(await first.ReadAsync(ConnectionPhase.Handshake));
        await first.SendAsync(new ErrorMessage { Code = ReasonCode.ManifestMismatch, Message = "Manifest hash differs." });
        await ClientHarness.WaitForAsync(() => output.Text.Contains("ventana: waiting (manifest.mismatch)", StringComparison.Ordinal), "waiting");
        Assert.Contains("ventana: waiting (manifest.mismatch) Reload the manifest in the host, or update the companion's extension.json.",
            output.Text.Split('\n').Select(line => line.TrimEnd('\r')));
        Assert.DoesNotContain("https://", output.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Manifest hash differs.", output.Text, StringComparison.Ordinal);

        var changed = WithExtraContribution(TestManifests.Countdown);
        WriteManifest(changed);
        await AdvanceUntilAsync(clock, () => hosts.Reader.Count > 0);
        var second = new ScriptedPeer(await hosts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(ManifestWriter.ComputeHash(changed), (await second.ReadHelloAsync()).ManifestHash);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30));
        await stop.CancelAsync();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task VerboseShowsTheHostsFixedTextCleanedAndAStopExitsFourWhenNotWatching()
    {
        WriteManifest(TestManifests.Countdown);
        WritePairing();
        var output = new SyncWriter();
        var hosts = Channel.CreateUnbounded<Stream>();
        var run = CompanionApp.RunCoreAsync([.. Args(), "--verbose"], new TestHandler(),
            new CompanionAppOptions { Output = output, WatchFiles = false }, Transport(hosts), CancellationToken.None);
        var peer = new ScriptedPeer(await hosts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        await peer.HandshakeAsync(new HostScript());
        await peer.SendAsync(new ErrorMessage { Code = ReasonCode.HostAccessRevoked, Message = "Access was revoked." });
        Assert.Equal(4, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Contains("ventana: stopped (host.access-revoked) Get new connection info after access is allowed again.", output.Text, StringComparison.Ordinal);
        Assert.Contains("ventana: host message: Access was revoked.", output.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FaultsPrintTheKindTheContributionAndTheExceptionAndCallbacksRunWhileAThrowingOneIsReportedOnce()
    {
        WriteManifest(TestManifests.Countdown);
        WritePairing();
        var output = new SyncWriter();
        var hosts = Channel.CreateUnbounded<Stream>();
        var faults = 0;
        using var stop = new CancellationTokenSource();
        var handler = new TestHandler { OnSession = (_, _) => throw new InvalidOperationException("author detail") };
        var options = new CompanionAppOptions
        {
            Output = output,
            StatusChanged = _ => throw new FormatException("callback broke"),
            HandlerFaulted = _ => Interlocked.Increment(ref faults),
        };
        var run = CompanionApp.RunCoreAsync(Args(), handler, options, Transport(hosts), stop.Token);
        var peer = new ScriptedPeer(await hosts.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
        await peer.HandshakeAsync(new HostScript());
        await peer.SendAsync(new StartSessionMessage { SessionId = ScriptedPeer.NewId(), ContributionId = TestManifests.Status, Settings = new Dictionary<string, string>() });
        await ClientHarness.WaitForAsync(() => Volatile.Read(ref faults) == 1, "fault callback");
        await ClientHarness.WaitForAsync(() => output.Text.Contains("author detail", StringComparison.Ordinal), "fault printed");
        Assert.Contains("ventana: fault Exception in " + TestManifests.Status + " (session.handler-faulted)", output.Text, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException: author detail", output.Text, StringComparison.Ordinal);
        await stop.CancelAsync();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, CountOf(output.Text, "callback threw System.FormatException: callback broke"));
    }

    [Fact]
    public async Task ARouterPrintsItsUnmappedContributionsAtStart()
    {
        WriteManifest(TestManifests.Countdown);
        var output = new StringWriter();
        var router = new ContributionRouter().MapSession(TestManifests.Status, (_, _) => Task.CompletedTask);
        Assert.Equal(3, await CompanionApp.RunCoreAsync(Args(), router, new CompanionAppOptions { Output = output, WatchFiles = false }, null,
            CancellationToken.None));
        Assert.Contains("ventana: no handler is mapped for " + TestManifests.Timer, output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("mapped for " + TestManifests.Status, output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void StatusLinesUseTheRegistrysDisplayNameAndHelpLinks()
    {
        var host = HostRegistry.Known.First(entry => entry.Status == HostStatus.Active);
        Assert.Equal(
            "ventana: waiting (auth.identity-changed) In " + host.DisplayName + ", allow the new program or revoke access. https://dev.ventana.tools/go/"
            + host.Id + "/codes#auth-identity-changed",
            AppRunner.StatusLine(ConnectionState.Waiting, ReasonCode.AuthIdentityChanged, host.Id, host.DisplayName));
        Assert.Equal("ventana: connected", AppRunner.StatusLine(ConnectionState.Connected, null, host.Id, host.DisplayName));
        Assert.True(ReasonCode.TryParse("future.reason", out var unknown));
        Assert.Equal("ventana: waiting (future.reason)", AppRunner.StatusLine(ConnectionState.Waiting, unknown, host.Id, host.DisplayName));
    }

    internal static ExtensionManifest WithExtraContribution(ExtensionManifest manifest) => new()
    {
        Id = manifest.Id,
        Name = manifest.Name,
        Description = manifest.Description,
        Version = manifest.Version,
        Hosts = manifest.Hosts,
        Contributions = [.. manifest.Contributions, TestManifests.Contribution(manifest.Id + "/extra", Provides.Invoke)],
    };

    /// <summary>Advances the clock a second at a time, with real pauses for the polling loop, until <paramref name="condition"/> holds.</summary>
    private static async Task AdvanceUntilAsync(TestClock clock, Func<bool> condition)
    {
        for (var i = 0; i < 20 && !condition(); i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await ClientHarness.WaitForAsync(() => true, "pause");
            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    private static int CountOf(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static CompanionAppOptions Options(TextWriter output, TestClock clock) => new()
    {
        Output = output,
        Client = new CompanionClientOptions { TimeProvider = clock },
    };

    private static Func<Pairing, ICompanionTransport> Transport(Channel<Stream> hosts) =>
        _ => new InMemoryTransport((stream, _) =>
        {
            hosts.Writer.TryWrite(stream);
            return ValueTask.FromResult(true);
        }, serverVerified: true);

    private string[] Args() => ["--manifest", ManifestPath, "--pairing", PairingPath];

    private void WriteManifest(ExtensionManifest manifest)
    {
        var bytes = ManifestWriter.Write(manifest);
        File.WriteAllBytes(ManifestPath, bytes);
        File.SetLastWriteTimeUtc(ManifestPath, DateTime.UtcNow.AddSeconds(Random.Shared.Next(1, 1000)));
    }

    private void WritePairing()
    {
        var json = "{\n  \"pairingVersion\": 3,\n  \"mode\": \"Persistent\",\n  \"hostId\": \"" + TestHosts.Id + "\",\n  \"pipeName\": \""
            + PipeNames.Create(TestHosts.Id, "test", "a8c06b3027d3fc4a", ClientHarness.RegistrationId) + "\",\n  \"registrationId\": \""
            + ClientHarness.RegistrationId + "\",\n  \"extensionId\": \"example.countdown\",\n  \"secret\": \""
            + Convert.ToBase64String(ClientHarness.Secret) + "\"\n}\n";
        File.WriteAllText(PairingPath, json, new UTF8Encoding(false));
    }

    /// <summary>A thread-safe writer whose text can be read while the app runs.</summary>
    private sealed class SyncWriter : TextWriter
    {
        private readonly StringBuilder _text = new();

        public override Encoding Encoding => Encoding.UTF8;

        public string Text
        {
            get
            {
                lock (_text)
                {
                    return _text.ToString();
                }
            }
        }

        public override void Write(char value)
        {
            lock (_text)
            {
                _text.Append(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_text)
            {
                _text.Append(value);
            }
        }

        public override void WriteLine(string? value)
        {
            lock (_text)
            {
                _text.Append(value).Append('\n');
            }
        }
    }
}
