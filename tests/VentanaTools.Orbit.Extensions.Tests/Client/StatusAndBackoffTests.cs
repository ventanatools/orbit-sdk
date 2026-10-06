// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using VentanaTools.Orbit.Extensions.Wire;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tests.Client;

/// <summary>Status, the reason to state table, backoff and the handshake checks of contract §7.3 and §9.2.</summary>
public sealed class StatusAndBackoffTests
{
    private static readonly CompanionClientOptions Exact = new() { RetryJitter = 0 };

    /// <summary>
    /// The reason to state table of contract §9.2, every row against a verified and an unverified
    /// server: code, retryAfterMs, then the kind and first delay (seconds, no jitter) for each.
    /// </summary>
    public static TheoryData<string, int?, string, double, string, double> ReasonTable() => new()
    {
        { "pairing.missing", null, "Stop", 0, "Stop", 0 },
        { "pairing.secret-invalid", null, "Stop", 0, "Stop", 0 },
        { "auth.registration-mismatch", null, "Stop", 0, "Wait", 1 },
        { "protocol.version-unsupported", null, "Stop", 0, "Wait", 1 },
        { "auth.host-mismatch", null, "Stop", 0, "Wait", 1 },
        { "auth.server-proof-invalid", null, "Stop", 0, "Wait", 1 },
        { "auth.proof-invalid", null, "Stop", 0, "Wait", 1 },
        { "host.access-revoked", null, "Stop", 0, "Wait", 1 },
        { "manifest.mismatch", null, "Wait", 30, "Wait", 30 },
        { "auth.identity-changed", null, "Wait", 30, "Wait", 30 },
        { "host.not-running", null, "Wait", 1, "Wait", 1 },
        { "host.turned-off", null, "Wait", 1, "Wait", 1 },
        { "host.paused", 300_000, "Wait", 300, "Wait", 30 },
        { "host.paused", 2_000, "Wait", 2, "Wait", 2 },
        { "host.paused", null, "Wait", 1, "Wait", 1 },
        { "future.reason", 120_000, "Wait", 120, "Wait", 30 },
        { "future.reason", null, "Wait", 1, "Wait", 1 },
        { "rate.exceeded", 120_000, "Wait", 1, "Wait", 1 },
        { "host.reloaded", null, "Immediate", 0, "Immediate", 0 },
        { "auth.server-unverified", null, "Wait", 1, "Wait", 1 },
        { "protocol.unexpected", null, "Wait", 1, "Wait", 1 },
        { "frame.json-invalid", null, "Wait", 1, "Wait", 1 },
        { "host.shutting-down", null, "Wait", 1, "Wait", 1 },
    };

    [Theory]
    [MemberData(nameof(ReasonTable))]
    public void TheReasonDecidesTheNextState(string code, int? retryAfterMs, string verifiedKind, double verifiedSeconds,
        string unverifiedKind, double unverifiedSeconds)
    {
        Assert.True(ReasonCode.TryParse(code, out var reason));
        var verified = new RetryPolicy(Exact).Next(reason, retryAfterMs, serverVerified: true);
        Assert.Equal((Enum.Parse<RetryKind>(verifiedKind), TimeSpan.FromSeconds(verifiedSeconds)), (verified.Kind, verified.Delay));
        var unverified = new RetryPolicy(Exact).Next(reason, retryAfterMs, serverVerified: false);
        Assert.Equal((Enum.Parse<RetryKind>(unverifiedKind), TimeSpan.FromSeconds(unverifiedSeconds)), (unverified.Kind, unverified.Delay));
    }

    [Fact]
    public void AServerThatIsNotVerifiedNeverStopsTheSdkOrHoldsItBeyondTheMaximum()
    {
        foreach (var entry in ReasonCodeCatalog.All)
        {
            var policy = new RetryPolicy(new CompanionClientOptions(), () => 1);
            Assert.True(ReasonCode.TryParse(entry.Code, out var reason));
            for (var failure = 0; failure < 10; failure++)
            {
                var decision = policy.Next(reason, 300_000, serverVerified: false);
                if (entry.Code.StartsWith("pairing.", StringComparison.Ordinal))
                {
                    Assert.Equal(RetryKind.Stop, decision.Kind); // Local file problems, never from a server.
                    break;
                }

                Assert.NotEqual(RetryKind.Stop, decision.Kind);
                Assert.InRange(decision.Delay, TimeSpan.Zero, TimeSpan.FromSeconds(30));
            }
        }
    }

    [Fact]
    public void TheNormalBackoffDoublesFromOneSecondToThirtyAndHostAbsenceCapsAtFive()
    {
        var normal = new RetryPolicy(Exact);
        Assert.Equal(new double[] { 1, 2, 4, 8, 16, 30, 30, 30 },
            Enumerable.Range(0, 8).Select(_ => normal.Next(null, null, true).Delay.TotalSeconds));
        var absent = new RetryPolicy(Exact);
        Assert.Equal(new double[] { 1, 2, 4, 5, 5, 5 },
            Enumerable.Range(0, 6).Select(_ => absent.Next(ReasonCode.HostNotRunning, null, false).Delay.TotalSeconds));
        normal.Reset();
        Assert.Equal(1, normal.Next(null, null, true).Delay.TotalSeconds);
    }

    [Fact]
    public void JitterMultipliesByUniformEightyToOneHundredTwentyPercentWithinTheCap()
    {
        var low = new RetryPolicy(new CompanionClientOptions(), () => 0);
        var high = new RetryPolicy(new CompanionClientOptions(), () => 0.999999);
        Assert.Equal(0.8, low.Next(null, null, true).Delay.TotalSeconds, 3);
        Assert.Equal(1.2, high.Next(null, null, true).Delay.TotalSeconds, 3);
        for (var i = 0; i < 10; i++)
        {
            Assert.InRange(high.Next(null, null, true).Delay, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        }

        var random = new RetryPolicy(new CompanionClientOptions());
        Assert.InRange(random.Next(null, null, true).Delay.TotalSeconds, 0.8, 1.2);
    }

    [Fact]
    public void HostReloadedReconnectsAtOnceOnlyOnce()
    {
        var policy = new RetryPolicy(Exact);
        Assert.Equal(RetryKind.Immediate, policy.Next(ReasonCode.HostReloaded, null, true).Kind);
        Assert.Equal(new RetryDecision(RetryKind.Wait, TimeSpan.FromSeconds(1)), policy.Next(ReasonCode.HostReloaded, null, true));
        policy.Reset();
        Assert.Equal(RetryKind.Immediate, policy.Next(ReasonCode.HostReloaded, null, true).Kind);
    }

    [Fact]
    public async Task TheClientFollowsTheBackoffScheduleOnAManualClock()
    {
        var clock = new TestClock();
        await using var harness = new ClientHarness(new TestHandler(), options: new CompanionClientOptions { TimeProvider = clock, RetryJitter = 0 });
        harness.RefuseConnections(5);
        harness.Start();
        var delays = new List<double>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var waiting = await NthAsync(harness, ConnectionState.Waiting, attempt);
            Assert.Equal(ReasonCode.HostNotRunning, waiting.Reason);
            Assert.False(waiting.ServerVerified);
            delays.Add(waiting.RetryIn!.Value.TotalSeconds);
            clock.Advance(waiting.RetryIn.Value - TimeSpan.FromTicks(1));
            await Task.Delay(20);
            Assert.Equal(ConnectionState.Waiting, harness.Client.State);
            clock.Advance(TimeSpan.FromTicks(1));
        }

        Assert.Equal(new double[] { 1, 2, 4, 5, 5 }, delays);
        var peer = await harness.NextPeerAsync();
        await peer.HandshakeAsync(new HostScript { Limits = Limits(pingIntervalMs: 300_000) });
        var connected = await harness.WaitForStatusAsync(status => status.State == ConnectionState.Connected, "connected");
        Assert.True(connected.ServerVerified);
        Assert.Equal(3, connected.ProtocolVersion);
        Assert.Equal(TestHosts.Id, connected.Host!.Id);
        Assert.Equal(6, connected.Attempt);

        // A connection that stays up for a minute starts the backoff again.
        await clock.AdvanceAsync(TimeSpan.FromSeconds(61), TimeSpan.FromSeconds(1));
        await peer.DisposeAsync();
        var after = await NthAsync(harness, ConnectionState.Waiting, 5);
        Assert.Equal(1, after.Attempt);
        Assert.Null(after.Reason);
        Assert.Equal(TimeSpan.FromSeconds(1), after.RetryIn);
    }

    [Fact]
    public async Task AConnectionThatDropsBeforeItIsStableKeepsBackingOff()
    {
        var clock = new TestClock();
        await using var harness = new ClientHarness(new TestHandler(), options: new CompanionClientOptions { TimeProvider = clock, RetryJitter = 0 });
        harness.Start();
        var delays = new List<double>();
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var peer = await harness.NextPeerAsync();
            await peer.DisposeAsync();
            var waiting = await NthAsync(harness, ConnectionState.Waiting, attempt);
            delays.Add(waiting.RetryIn!.Value.TotalSeconds);
            clock.Advance(waiting.RetryIn.Value);
        }

        Assert.Equal(new double[] { 1, 2, 4, 8, 16, 30 }, delays);
    }

    [Fact]
    public async Task ARetryAfterFromAVerifiedHostIsHonouredUpToFiveMinutes()
    {
        var clock = new TestClock();
        await using var harness = new ClientHarness(new TestHandler(), options: new CompanionClientOptions { TimeProvider = clock }, serverVerified: false);
        harness.Start();
        var peer = await harness.ConnectAsync();
        await peer.SendAsync(new ErrorMessage { Code = ReasonCode.HostPaused, RetryAfterMs = 300_000 });
        var waiting = await harness.WaitForStatusAsync(status => status.State == ConnectionState.Waiting, "waiting");
        Assert.True(waiting.ServerVerified); // the challenge proof verified the server
        Assert.Equal(TimeSpan.FromMinutes(5), waiting.RetryIn);
    }

    [Fact]
    public async Task HostReloadedReconnectsWithoutWaiting()
    {
        var clock = new TestClock();
        await using var harness = new ClientHarness(new TestHandler(), options: new CompanionClientOptions { TimeProvider = clock });
        harness.Start();
        var peer = await harness.ConnectAsync();
        await peer.SendAsync(new ErrorMessage { Code = ReasonCode.HostReloaded });
        var again = await harness.NextPeerAsync();
        await again.HandshakeAsync(new HostScript());
        await ClientHarness.WaitForAsync(() => harness.Statuses.Count(status => status.State == ConnectionState.Connected) == 2, "reconnected");
        Assert.DoesNotContain(harness.Statuses, status => status.State == ConnectionState.Waiting);
    }

    [Fact]
    public async Task AThrowingSubscriberDoesNotStopTheClient()
    {
        await using var harness = new ClientHarness(new TestHandler());
        harness.Client.StatusChanged += (_, _) => throw new InvalidOperationException("subscriber");
        harness.Start();
        var peer = await harness.ConnectAsync();
        await peer.SendAsync(new PingMessage { Id = 5 });
        Assert.Equal(5, (await peer.ReadAsync<PongMessage>()).Id);
        Assert.Contains(harness.SubscriberErrors, error => error.Message == "subscriber");
        Assert.Equal(ConnectionState.Connected, harness.Client.State);
    }

    [Fact]
    public async Task ASlowSubscriberCannotStallPongsBecauseEventsAreNotRaisedOnTheReader()
    {
        using var release = new ManualResetEventSlim();
        await using var harness = new ClientHarness(new TestHandler());
        var threads = new List<int>();
        harness.Client.StatusChanged += (_, e) =>
        {
            lock (threads)
            {
                threads.Add(Environment.CurrentManagedThreadId);
            }

            if (e.State == ConnectionState.Connected)
            {
                release.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
            }
        };
        harness.Start();
        var peer = await harness.NextPeerAsync();
        await peer.HandshakeAsync(new HostScript());
        await peer.SendAsync(new PingMessage { Id = 9 });
        Assert.Equal(9, (await peer.ReadAsync<PongMessage>()).Id);
        Assert.False(release.IsSet);
        release.Set();
        await ClientHarness.WaitForAsync(() => harness.Statuses.Any(status => status.State == ConnectionState.Connected), "connected event");
    }

    [Fact]
    public async Task EventsArriveInOrder()
    {
        var clock = new TestClock();
        await using var harness = new ClientHarness(new TestHandler(), options: new CompanionClientOptions { TimeProvider = clock, RetryJitter = 0 });
        harness.RefuseConnections(3);
        harness.Start();
        for (var i = 0; i < 3; i++)
        {
            var waiting = await NthAsync(harness, ConnectionState.Waiting, i);
            clock.Advance(waiting.RetryIn!.Value);
        }

        await harness.ConnectAsync();
        Assert.Equal(
            [ConnectionState.Connecting, ConnectionState.Waiting, ConnectionState.Connecting, ConnectionState.Waiting,
                ConnectionState.Connecting, ConnectionState.Waiting, ConnectionState.Connecting, ConnectionState.Connected],
            harness.Statuses.Select(status => status.State));
        Assert.Equal([1, 1, 2, 2, 3, 3, 4, 4], harness.Statuses.Select(status => status.Attempt));
    }

    [Fact]
    public async Task ThePairingMustBelongToTheManifest()
    {
        var pipe = PipeNames.Create(TestHosts.Id, "test", "a8c06b3027d3fc4a", ClientHarness.RegistrationId);
        using var foreign = new Pairing(TestHosts.Id, pipe, ClientHarness.RegistrationId, TestManifests.Kinds.Id, (byte[])ClientHarness.Secret.Clone());
        Assert.Equal([ReasonCode.PairingExtensionMismatch], await StopReasonsAsync(foreign));

        const string Unlisted = "unlisted-host";
        using var unlisted = new Pairing(Unlisted, PipeNames.Create(Unlisted, "test", "a8c06b3027d3fc4a", ClientHarness.RegistrationId),
            ClientHarness.RegistrationId, "example.countdown", (byte[])ClientHarness.Secret.Clone());
        Assert.Equal([ReasonCode.PairingHostNotListed], await StopReasonsAsync(unlisted));

        static async Task<List<ReasonCode?>> StopReasonsAsync(Pairing pairing)
        {
            var client = new CompanionClient(pairing, TestManifests.Countdown, new TestHandler(), null, new ClientInternals
            {
                Transport = new InMemoryTransport((_, _) => ValueTask.FromResult(true), true),
            });
            var reasons = new List<ReasonCode?>();
            client.StatusChanged += (_, e) => reasons.Add(e.Reason);
            await client.RunCoreAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(ConnectionState.Stopped, client.State);
            return reasons;
        }
    }

    [Fact]
    public async Task AServerThatFailsTheOwnerCheckIsNeverWrittenToAndCannotHoldTheSdk()
    {
        var clock = new TestClock();
        var attempts = 0;
        using var pairing = new Pairing(TestHosts.Id, PipeNames.Create(TestHosts.Id, "test", "a8c06b3027d3fc4a", ClientHarness.RegistrationId),
            ClientHarness.RegistrationId, "example.countdown", (byte[])ClientHarness.Secret.Clone());
        var client = new CompanionClient(pairing, TestManifests.Countdown, new TestHandler(),
            new CompanionClientOptions { TimeProvider = clock }, new ClientInternals { Transport = new OwnerCheckFails(() => attempts++) });
        var statuses = new List<StatusChangedEventArgs>();
        client.StatusChanged += (_, e) =>
        {
            lock (statuses)
            {
                statuses.Add(e);
            }
        };
        using var stop = new CancellationTokenSource();
        var run = client.RunCoreAsync(stop.Token);
        for (var i = 0; i < 8; i++)
        {
            await ClientHarness.WaitForAsync(() =>
            {
                lock (statuses)
                {
                    return statuses.Count(status => status.State == ConnectionState.Waiting) > i;
                }
            }, "waiting");
            StatusChangedEventArgs waiting;
            lock (statuses)
            {
                waiting = statuses.Where(status => status.State == ConnectionState.Waiting).ElementAt(i);
            }

            Assert.Equal(ReasonCode.AuthServerUnverified, waiting.Reason);
            Assert.False(waiting.ServerVerified);
            Assert.InRange(waiting.RetryIn!.Value, TimeSpan.Zero, TimeSpan.FromSeconds(30));
            clock.Advance(waiting.RetryIn.Value);
        }

        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(attempts >= 8);
    }

    [Fact]
    public async Task TheHelloOffersTheSdkVersionTheManifestHashAndTheSupportedCapabilities()
    {
        await using var harness = new ClientHarness(new TestHandler());
        harness.Start();
        var peer = await harness.NextPeerAsync();
        var hello = await peer.ReadHelloAsync();
        var assembly = typeof(CompanionClient).Assembly;
        Assert.Equal(assembly.GetName().Name, hello.Client.Name);
        Assert.Equal(ClientIdentity.FitVersion(assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion), hello.Client.Version);
        Assert.StartsWith(typeof(StatusAndBackoffTests).Assembly.GetName().Version!.ToString(3), hello.Client.Version, StringComparison.Ordinal);
        Assert.Equal(ManifestWriter.ComputeHash(harness.Manifest), hello.ManifestHash);
        Assert.Equal((3, 3), (hello.MinVersion, hello.MaxVersion));
        Assert.Equal([Capabilities.TestEcho], hello.Capabilities);
        Assert.Equal(ClientHarness.RegistrationId, hello.RegistrationId);
        Assert.Equal("0.1.0-preview.1", ClientIdentity.FitVersion("0.1.0-preview.1+0123456789abcdef0123456789abcdef01234567"));
        Assert.Equal("1.0.0", ClientIdentity.FitVersion("1.0.0"));
    }

    [Fact]
    public async Task EffectiveCapabilitiesAreThoseBothSidesList()
    {
        var handler = new TestHandler();
        await using var harness = new ClientHarness(handler, manifest: TestManifests.Kinds);
        harness.Start();
        var peer = await harness.ConnectAsync(new HostScript { Capabilities = ["future.feature", Capabilities.TestEcho] });
        await peer.SendAsync(new StartSessionMessage { SessionId = ScriptedPeer.NewId(), ContributionId = "example.kinds/widget", Settings = new Dictionary<string, string>() });
        await ClientHarness.WaitForAsync(() => !handler.Sessions.IsEmpty, "session");
        var session = handler.Sessions.Single();
        Assert.Equal([Capabilities.TestEcho], session.HostCapabilities);
        Assert.True(session.Supports(Capabilities.TestEcho));
        Assert.False(session.Supports("future.feature"));
        Assert.False(session.Supports(Capabilities.FaceImage));
    }

    public static TheoryData<string> ReadyMismatches() => ["version", "capabilities", "host id", "host version"];

    [Theory]
    [MemberData(nameof(ReadyMismatches))]
    public async Task AReadyThatDiffersFromTheChallengeIsRefused(string difference)
    {
        await using var harness = new ClientHarness(new TestHandler());
        harness.Start();
        var peer = await harness.NextPeerAsync();
        await peer.ReadHelloAsync();
        var script = new HostScript { Capabilities = ["future.feature"] };
        await peer.SendAsync(peer.BuildChallenge(script));
        Assert.IsType<AuthenticateMessage>(await peer.ReadAsync(ConnectionPhase.Handshake));
        var ready = ScriptedPeer.BuildReady(script);
        ready = difference switch
        {
            "version" => new ReadyMessage { Version = 4, Capabilities = ready.Capabilities, Host = ready.Host, UiLanguage = ready.UiLanguage, Limits = ready.Limits },
            "capabilities" => new ReadyMessage { Version = 3, Capabilities = [], Host = ready.Host, UiLanguage = ready.UiLanguage, Limits = ready.Limits },
            "host id" => new ReadyMessage { Version = 3, Capabilities = ready.Capabilities, Host = new HostIdentity { Id = "other-host", Version = "2026.10.1" }, UiLanguage = ready.UiLanguage, Limits = ready.Limits },
            _ => new ReadyMessage { Version = 3, Capabilities = ready.Capabilities, Host = new HostIdentity { Id = TestHosts.Id, Version = "9" }, UiLanguage = ready.UiLanguage, Limits = ready.Limits },
        };
        await peer.SendAsync(ready);
        Assert.Equal(ReasonCode.ProtocolMessageInvalid, await peer.ReadCloseAsync());
        var waiting = await harness.WaitForStatusAsync(status => status.State == ConnectionState.Waiting, "waiting");
        Assert.Equal(ReasonCode.ProtocolMessageInvalid, waiting.Reason);
    }

    [Fact]
    public async Task AHandshakeThatDoesNotFinishInFiveSecondsTimesOut()
    {
        var clock = new TestClock();
        await using var harness = new ClientHarness(new TestHandler(), options: new CompanionClientOptions { TimeProvider = clock });
        harness.Start();
        var peer = await harness.NextPeerAsync();
        await peer.ReadHelloAsync();
        clock.Advance(TimeSpan.FromSeconds(4.9));
        await Task.Delay(20);
        Assert.Equal(ConnectionState.Connecting, harness.Client.State);
        clock.Advance(TimeSpan.FromSeconds(0.2));
        var waiting = await harness.WaitForStatusAsync(status => status.State == ConnectionState.Waiting, "waiting");
        Assert.Equal(ReasonCode.AuthTimeout, waiting.Reason);
    }

    [Fact]
    public async Task TheSdkPingsWhenIdleAndClosesWhenNoPongArrives()
    {
        var clock = new TestClock();
        await using var harness = new ClientHarness(new TestHandler(), options: new CompanionClientOptions { TimeProvider = clock });
        harness.Start();
        var peer = await harness.ConnectAsync(new HostScript { Limits = Limits(pingIntervalMs: 20_000) });
        peer.AnswerPings = false;
        clock.Advance(TimeSpan.FromSeconds(29.9));
        Assert.True(await peer.SilentForAsync(TimeSpan.FromMilliseconds(100))); // the larger of the host's and its own interval: 30 s
        clock.Advance(TimeSpan.FromSeconds(0.2));
        var ping = await peer.ReadAsync<PingMessage>();
        Assert.Equal(1, ping.Id);
        await peer.SendAsync(new PongMessage { Id = 1 });
        await ClientHarness.WaitForAsync(() => harness.Observer.FramesProcessed.Contains("pong"), "pong handled");
        clock.Advance(TimeSpan.FromSeconds(30.1));
        var second = await peer.ReadAsync<PingMessage>();
        Assert.Equal(2, second.Id);
        clock.Advance(TimeSpan.FromSeconds(10.1));
        Assert.Equal(ReasonCode.ProtocolPingTimeout, await peer.ReadCloseAsync());
        var waiting = await harness.WaitForStatusAsync(status => status.State == ConnectionState.Waiting, "waiting");
        Assert.Equal(ReasonCode.ProtocolPingTimeout, waiting.Reason);
    }

    [Fact]
    public async Task AStrayPongIsIgnoredAndHostPingsMustBeHalfAnIntervalApart()
    {
        var clock = new TestClock();
        await using var harness = new ClientHarness(new TestHandler(), options: new CompanionClientOptions { TimeProvider = clock });
        harness.Start();
        var peer = await harness.ConnectAsync();
        await peer.SendAsync(new PongMessage { Id = 77 });
        await peer.SendAsync(new PingMessage { Id = 1 });
        Assert.Equal(1, (await peer.ReadAsync<PongMessage>()).Id);
        clock.Advance(TimeSpan.FromSeconds(15));
        await peer.SendAsync(new PingMessage { Id = 2 });
        Assert.Equal(2, (await peer.ReadAsync<PongMessage>()).Id);
        clock.Advance(TimeSpan.FromSeconds(14.999));
        await peer.SendAsync(new PingMessage { Id = 3 });
        Assert.Equal(ReasonCode.ProtocolUnexpected, await peer.ReadCloseAsync());
    }

    [Fact]
    public async Task AHostFloodBeyondTheHardBucketClosesWithRateExceeded()
    {
        var clock = new TestClock();
        await using var harness = new ClientHarness(new TestHandler(), options: new CompanionClientOptions { TimeProvider = clock });
        harness.Start();
        var peer = await harness.ConnectAsync();
        var unknown = System.Text.Encoding.UTF8.GetBytes("{\"type\":\"futureThing\",\"pad\":\"" + new string('x', 1_000) + "\"}");
        for (var i = 0; i < 1_030; i++)
        {
            await peer.SendBodyAsync(unknown);
        }

        Assert.Equal(ReasonCode.RateExceeded, await peer.ReadCloseAsync());
    }

    [Fact]
    public async Task AdvisoryThrottlingKeepsTheConnection()
    {
        await using var harness = new ClientHarness(new TestHandler());
        harness.Start();
        var peer = await harness.ConnectAsync();
        await peer.SendAsync(new ErrorMessage { Code = ReasonCode.RateThrottled, RetryAfterMs = 10 });
        await peer.SendAsync(new PingMessage { Id = 4 });
        Assert.Equal(4, (await peer.ReadAsync<PongMessage>()).Id);
        Assert.Equal(ConnectionState.Connected, harness.Client.State);
    }

    internal static HostLimits Limits(int? pingIntervalMs = null, int? invokeTimeoutMs = null, int? maxSessions = null, int? maxPendingInvokes = null)
    {
        var d = HostLimits.Protocol3Defaults;
        return new HostLimits
        {
            MaxFrameBytes = d.MaxFrameBytes,
            MaxSessions = maxSessions ?? d.MaxSessions,
            MaxPendingInvokes = maxPendingInvokes ?? d.MaxPendingInvokes,
            InvokeTimeoutMs = invokeTimeoutMs ?? d.InvokeTimeoutMs,
            FaceChangesPerSecond = d.FaceChangesPerSecond,
            FaceBurst = d.FaceBurst,
            MessageRate = d.MessageRate,
            MessageBurst = d.MessageBurst,
            HardMessageRate = d.HardMessageRate,
            HardMessageBurst = d.HardMessageBurst,
            PingIntervalMs = pingIntervalMs ?? d.PingIntervalMs,
            PongTimeoutMs = d.PongTimeoutMs,
        };
    }

    internal static async Task<StatusChangedEventArgs> NthAsync(ClientHarness harness, ConnectionState state, int index)
    {
        await ClientHarness.WaitForAsync(() => harness.Statuses.Count(status => status.State == state) > index, state + " #" + index);
        return harness.Statuses.Where(status => status.State == state).ElementAt(index);
    }

    private sealed class OwnerCheckFails : ICompanionTransport
    {
        private readonly Action _attempt;

        public OwnerCheckFails(Action attempt) => _attempt = attempt;

        public ValueTask<TransportConnection> ConnectAsync(CancellationToken cancellationToken)
        {
            _attempt();
            return ValueTask.FromResult(TransportConnection.Failed(ReasonCode.AuthServerUnverified));
        }
    }
}
