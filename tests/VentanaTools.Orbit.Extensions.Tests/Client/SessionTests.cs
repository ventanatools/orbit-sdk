// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Globalization;
using System.Text.Json;
using VentanaTools.Orbit.Extensions.Wire;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tests.Client;

/// <summary>
/// A picture variant this SDK version does not know: no capability makes it effective. Only this
/// test assembly, a friend of the library, can derive one.
/// </summary>
internal sealed class FuturePicture : FacePicture
{
    public override bool Equals(FacePicture? other) => other is FuturePicture;

    public override int GetHashCode() => 2;
}

/// <summary>A line variant this SDK version does not know.</summary>
internal sealed class FutureLine : FaceLine
{
    public override bool Equals(FaceLine? other) => other is FutureLine;

    public override int GetHashCode() => 2;
}

/// <summary>Sessions, faces and invocations (contract §7.6 to §7.8, §9.2).</summary>
public sealed class SessionTests
{
    private const string Action = "example.kinds/action";
    private const string Widget = "example.kinds/widget";
    private const string Both = "example.kinds/both";
    private static readonly Dictionary<string, string> None = [];

    [Fact]
    public async Task OneHundredSetFaceCallsCoalesceIntoAtMostTwoFrames()
    {
        var clock = new TestClock();
        var published = new TaskCompletionSource();
        var handler = new TestHandler
        {
            OnSession = async (session, token) =>
            {
                for (var i = 0; i < 100; i++)
                {
                    Assert.Equal(PublishResult.Accepted, session.SetFace(new Face { Line1 = i.ToString(CultureInfo.InvariantCulture), GoodFor = TimeSpan.FromSeconds(5) }));
                }

                published.SetResult();
                await Task.Delay(Timeout.Infinite, token);
            },
        };
        await using var harness = Harness(handler, clock);
        var peer = await harness.ConnectAsync();
        var sessionId = await StartAsync(peer, Widget);
        await published.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var frames = new List<SetFaceMessage> { await peer.ReadAsync<SetFaceMessage>() };
        if (Line1(frames[0]) != "99")
        {
            // The writer sent an earlier face while the loop ran; the last one waits for the face spacing.
            Assert.True(await peer.SilentForAsync(TimeSpan.FromMilliseconds(100)));
            clock.Advance(TimeSpan.FromMilliseconds(500));
            frames.Add(await peer.ReadNudgingAsync<SetFaceMessage>(clock, TimeSpan.FromMilliseconds(100)));
        }

        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.True(await peer.SilentForAsync(TimeSpan.FromMilliseconds(100)));
        Assert.InRange(frames.Count, 1, 2);
        Assert.All(frames, frame => Assert.Equal(sessionId, frame.SessionId));
        Assert.Equal("99", Assert.IsType<TextLine>(frames[^1].Face.Line1).Text);
    }

    public static TheoryData<int> FaceTextCases()
    {
        using var document = Fixtures.Json("text-rules.json");
        var data = new TheoryData<int>();
        for (var i = 0; i < document.RootElement.GetProperty("face").GetArrayLength(); i++)
        {
            data.Add(i);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FaceTextCases))]
    public async Task SetFaceNeverThrowsForTextAndSendsItCleaned(int index)
    {
        using var document = Fixtures.Json("text-rules.json");
        var item = document.RootElement.GetProperty("face")[index];
        var text = item.GetProperty("text").GetString()!;
        var expected = item.GetProperty("expected").ValueKind == JsonValueKind.Null ? null : item.GetProperty("expected").GetString();
        var part = item.GetProperty("part").GetString();
        var face = part switch
        {
            "line1" => new Face { Line1 = text, GoodFor = TimeSpan.FromSeconds(5) },
            "line2" => new Face { Line2 = text, GoodFor = TimeSpan.FromSeconds(5) },
            _ => new Face { Detail = text, GoodFor = TimeSpan.FromSeconds(5) },
        };
        var handler = new TestHandler
        {
            OnSession = async (session, token) =>
            {
                Assert.Equal(PublishResult.Accepted, session.SetFace(face));
                await Task.Delay(Timeout.Infinite, token);
            },
        };
        await using var harness = Harness(handler);
        var peer = await harness.ConnectAsync();
        await StartAsync(peer, Widget);
        var sent = (await peer.ReadAsync<SetFaceMessage>()).Face;
        var actual = part switch
        {
            "line1" => (sent.Line1 as TextLine)?.Text,
            "line2" => (sent.Line2 as TextLine)?.Text,
            _ => sent.Detail,
        };
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SetFaceChecksItsArgumentsThenVariantsThenProvidesThenState()
    {
        var sink = new CountingSink();
        var action = NewSession(sink, Provides.Invoke);
        var widget = NewSession(sink, Provides.Face);

        // 1. arguments, even for a contribution that cannot publish
        Assert.Throws<ArgumentNullException>(() => action.SetFace(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => action.SetFace(new Face { GoodFor = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => widget.SetFace(new Face { GoodFor = TimeSpan.FromMilliseconds(999) }));
        Assert.Throws<ArgumentOutOfRangeException>(() => widget.SetFace(new Face { GoodFor = TimeSpan.FromDays(1) + TimeSpan.FromTicks(1) }));
        Assert.Throws<ArgumentException>(() => widget.SetFace(new Face { GoodFor = TimeSpan.FromSeconds(1), Picture = FacePicture.Glyph("A") }));
        Assert.Throws<ArgumentException>(() => widget.SetFace(new Face { GoodFor = TimeSpan.FromSeconds(1), Picture = null! }));
        Assert.Throws<ArgumentOutOfRangeException>(() => widget.SetFace(new Face { GoodFor = TimeSpan.FromSeconds(1), State = (FaceState)99 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => action.Fail((Failure)6));
        Assert.Throws<ArgumentOutOfRangeException>(() => widget.Fail((Failure)0));

        // 2. variants: no capability makes an unknown variant effective
        Assert.Throws<NotSupportedException>(() => action.SetFace(new Face { GoodFor = TimeSpan.FromSeconds(1), Picture = new FuturePicture() }));
        Assert.Throws<NotSupportedException>(() => widget.SetFace(new Face { GoodFor = TimeSpan.FromSeconds(1), Line2 = new FutureLine() }));

        // 3. provides
        Assert.Throws<InvalidOperationException>(() => action.SetFace(new Face { GoodFor = TimeSpan.FromSeconds(1) }));
        Assert.Throws<InvalidOperationException>(() => action.ClearFace());
        Assert.Throws<InvalidOperationException>(() => action.Fail(Failure.Network));
        Assert.Equal(3, sink.WithoutFace);
        Assert.Equal(0, sink.Published);

        // 4. state
        Assert.Equal(PublishResult.Accepted, widget.SetFace(new Face { GoodFor = TimeSpan.FromSeconds(1.2), Line1 = "​" }));
        Assert.Equal(2, Assert.IsType<FaceCommand>(sink.Last).Face!.GoodForSeconds);
        Assert.Null(sink.Last!.Face!.Line1);
        widget.End();
        Assert.Equal(PublishResult.SessionEnded, widget.SetFace(new Face { GoodFor = TimeSpan.FromSeconds(1) }));
        Assert.Equal(PublishResult.SessionEnded, widget.ClearFace());
        Assert.Equal(PublishResult.SessionEnded, widget.Fail(Failure.NoResult));
        Assert.Equal(3, sink.AfterEnd);
        Assert.Equal(1, sink.Published);
    }

    [Fact]
    public async Task AVariantWhoseCapabilityIsNotEffectiveThrowsAndSendsNothing()
    {
        Exception? thrown = null;
        var handler = new TestHandler
        {
            OnSession = async (session, token) =>
            {
                thrown = Record.Exception(() => session.SetFace(new Face { GoodFor = TimeSpan.FromSeconds(5), Picture = new FuturePicture() }));
                await Task.Delay(Timeout.Infinite, token);
            },
        };
        await using var harness = Harness(handler);
        var peer = await harness.ConnectAsync(new HostScript { Capabilities = [Capabilities.FaceImage] });
        await StartAsync(peer, Widget);
        await ClientHarness.WaitForAsync(() => thrown is not null, "thrown");
        Assert.IsType<NotSupportedException>(thrown);
        Assert.True(await peer.SilentForAsync(TimeSpan.FromMilliseconds(200)));
        Assert.Equal(ConnectionState.Connected, harness.Client.State);
    }

    [Fact]
    public async Task ADefaultInvokeResultIsAnsweredFailedAndRaisesInvalidResult()
    {
        var handler = new TestHandler { OnInvoke = (_, _) => Task.FromResult(default(InvokeResult)) };
        await using var harness = Harness(handler);
        var peer = await harness.ConnectAsync();
        var sessionId = await StartAsync(peer, Action);
        var requestId = ScriptedPeer.NewId();
        await peer.SendAsync(new InvokeMessage { RequestId = requestId, SessionId = sessionId });
        var result = await peer.ReadAsync<ResultMessage>();
        Assert.Equal((requestId, Outcome.Failed, (Failure?)null), (result.RequestId, result.Outcome, result.Failure));
        await ClientHarness.WaitForAsync(() => harness.Faults.Count == 1, "fault");
        var fault = harness.Faults.Single();
        Assert.Equal((HandlerFault.InvalidResult, Action, sessionId, requestId), (fault.Kind, fault.ContributionId, fault.SessionId, fault.RequestId));
        Assert.Null(fault.Exception);
    }

    [Fact]
    public void InvokeResultsAreTheFourValidValues()
    {
        Assert.Equal(Outcome.Done, InvokeResult.Done.Outcome);
        Assert.Equal(Outcome.Refused, InvokeResult.Refused.Outcome);
        Assert.Equal(Outcome.Unsupported, InvokeResult.Unsupported.Outcome);
        Assert.Equal((Outcome.Failed, (Failure?)Failure.AppUnavailable), (InvokeResult.Failed(Failure.AppUnavailable).Outcome, InvokeResult.Failed(Failure.AppUnavailable).Failure));
        Assert.Null(InvokeResult.Failed().Failure);
        Assert.Throws<ArgumentOutOfRangeException>(() => InvokeResult.Failed((Failure)6));
        Assert.Equal(InvokeResult.Failed(Failure.Network), InvokeResult.Failed(Failure.Network));
        Assert.True(InvokeResult.Done != InvokeResult.Refused);
        Assert.False(default(InvokeResult).IsValid);
        Assert.True(InvokeResult.Failed(Failure.NoResult).IsValid);
        Assert.Equal("Failed (Network)", InvokeResult.Failed(Failure.Network).ToString());
        Assert.Empty(typeof(InvokeResult).GetConstructors());
    }

    [Fact]
    public async Task ASessionHandlerThatThrowsRaisesAFaultClearsItsFaceAndStaysInvocable()
    {
        var clock = new TestClock();
        var shown = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TestHandler
        {
            OnSession = async (session, _) =>
            {
                session.SetFace(new Face { Line1 = "up", GoodFor = TimeSpan.FromSeconds(5) });

                // A session keeps one pending face, so a clear that came first would replace the face unsent.
                await shown.Task;
                throw new InvalidOperationException("author bug");
            },
        };
        await using var harness = Harness(handler, clock);
        var peer = await harness.ConnectAsync();
        var sessionId = await StartAsync(peer, Both);
        Assert.Equal("up", Assert.IsType<TextLine>((await peer.ReadAsync<SetFaceMessage>()).Face.Line1).Text);
        shown.SetResult();
        await ClientHarness.WaitForAsync(() => harness.Faults.Count == 1, "fault");
        clock.Advance(TimeSpan.FromMilliseconds(500)); // one face per session per 1/faceChangesPerSecond
        Assert.Equal(sessionId, (await peer.ReadNudgingAsync<ClearFaceMessage>(clock, TimeSpan.FromMilliseconds(100))).SessionId);
        var fault = harness.Faults.Single();
        Assert.Equal(HandlerFault.Exception, fault.Kind);
        Assert.Equal("author bug", fault.Exception!.Message);
        Assert.Null(fault.RequestId);
        var requestId = ScriptedPeer.NewId();
        await peer.SendAsync(new InvokeMessage { RequestId = requestId, SessionId = sessionId });
        Assert.Equal(Outcome.Done, (await peer.ReadAsync<ResultMessage>()).Outcome);
    }

    [Fact]
    public async Task AnInvocationThatThrowsIsAnsweredFailedWithNoFailure()
    {
        var handler = new TestHandler { OnInvoke = (_, _) => throw new FormatException("bad") };
        await using var harness = Harness(handler);
        var peer = await harness.ConnectAsync();
        var sessionId = await StartAsync(peer, Action);
        var requestId = ScriptedPeer.NewId();
        await peer.SendAsync(new InvokeMessage { RequestId = requestId, SessionId = sessionId });
        var result = await peer.ReadAsync<ResultMessage>();
        Assert.Equal(Outcome.Failed, result.Outcome);
        Assert.Null(result.Failure);
        await ClientHarness.WaitForAsync(() => harness.Faults.Count == 1, "fault");
        Assert.IsType<FormatException>(harness.Faults.Single().Exception);
        Assert.Equal(requestId, harness.Faults.Single().RequestId);
    }

    [Fact]
    public async Task CancellationIsANormalCompletionAndACancelledInvocationSendsNoResult()
    {
        var started = new TaskCompletionSource();
        var handler = new TestHandler
        {
            OnInvoke = async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return InvokeResult.Done;
            },
        };
        await using var harness = Harness(handler);
        var peer = await harness.ConnectAsync();
        var sessionId = await StartAsync(peer, Action);
        var requestId = ScriptedPeer.NewId();
        await peer.SendAsync(new InvokeMessage { RequestId = requestId, SessionId = sessionId });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await peer.SendAsync(new CancelMessage { RequestId = requestId });
        await ClientHarness.WaitForAsync(() => harness.Observer.InvocationsEnded.Contains(requestId), "ended");
        Assert.True(await peer.SilentForAsync(TimeSpan.FromMilliseconds(200)));
        await peer.SendAsync(new StopSessionMessage { SessionId = sessionId });
        await ClientHarness.WaitForAsync(() => harness.Observer.SessionsEnded.Contains(sessionId), "session ended");
        await harness.Client.FlushEventsAsync();
        Assert.Empty(harness.Faults);
    }

    [Fact]
    public async Task StoppingASessionRefusesItsRunningInvocations()
    {
        var started = new TaskCompletionSource();
        var handler = new TestHandler
        {
            OnInvoke = async (_, token) =>
            {
                started.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
                return InvokeResult.Done;
            },
        };
        await using var harness = Harness(handler);
        var peer = await harness.ConnectAsync();
        var sessionId = await StartAsync(peer, Action);
        var requestId = ScriptedPeer.NewId();
        await peer.SendAsync(new InvokeMessage { RequestId = requestId, SessionId = sessionId });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await peer.SendAsync(new StopSessionMessage { SessionId = sessionId });
        var result = await peer.ReadAsync<ResultMessage>();
        Assert.Equal((requestId, Outcome.Refused), (result.RequestId, result.Outcome));
    }

    [Fact]
    public async Task InvocationsForUnknownSessionsFaceOnlyContributionsOrBeyondThePendingLimitAreRefused()
    {
        var release = new TaskCompletionSource<InvokeResult>();
        var handler = new TestHandler { OnInvoke = (_, _) => release.Task };
        await using var harness = Harness(handler);
        var peer = await harness.ConnectAsync(new HostScript { Limits = StatusAndBackoffTests.Limits(maxPendingInvokes: 2) });
        var unknown = ScriptedPeer.NewId();
        await peer.SendAsync(new InvokeMessage { RequestId = unknown, SessionId = ScriptedPeer.NewId() });
        Assert.Equal((unknown, Outcome.Refused), Pair(await peer.ReadAsync<ResultMessage>()));
        var widget = await StartAsync(peer, Widget);
        var faceOnly = ScriptedPeer.NewId();
        await peer.SendAsync(new InvokeMessage { RequestId = faceOnly, SessionId = widget });
        Assert.Equal((faceOnly, Outcome.Refused), Pair(await peer.ReadAsync<ResultMessage>()));
        var action = await StartAsync(peer, Action);
        await peer.SendAsync(new InvokeMessage { RequestId = ScriptedPeer.NewId(), SessionId = action });
        await peer.SendAsync(new InvokeMessage { RequestId = ScriptedPeer.NewId(), SessionId = action });
        var third = ScriptedPeer.NewId();
        await peer.SendAsync(new InvokeMessage { RequestId = third, SessionId = action });
        Assert.Equal((third, Outcome.Refused), Pair(await peer.ReadAsync<ResultMessage>()));
        release.SetResult(InvokeResult.Done);
        Assert.Equal(Outcome.Done, (await peer.ReadAsync<ResultMessage>()).Outcome);
        Assert.Equal(Outcome.Done, (await peer.ReadAsync<ResultMessage>()).Outcome);
        Assert.Equal(2, handler.Invocations.Count);

        static (string, Outcome) Pair(ResultMessage result) => (result.RequestId, result.Outcome);
    }

    [Fact]
    public async Task ReusedSessionAndRequestIdsAreReplaysThatClose()
    {
        await using (var harness = Harness(new TestHandler()))
        {
            var peer = await harness.ConnectAsync();
            var sessionId = await StartAsync(peer, Action);
            await peer.SendAsync(new StopSessionMessage { SessionId = sessionId });
            await peer.SendAsync(new StartSessionMessage { SessionId = sessionId, ContributionId = Action, Settings = None });
            Assert.Equal(ReasonCode.SessionReplay, await peer.ReadCloseAsync());
        }

        await using (var harness = Harness(new TestHandler()))
        {
            var peer = await harness.ConnectAsync();
            var sessionId = await StartAsync(peer, Action);
            var requestId = ScriptedPeer.NewId();
            await peer.SendAsync(new InvokeMessage { RequestId = requestId, SessionId = sessionId });
            Assert.Equal(Outcome.Done, (await peer.ReadAsync<ResultMessage>()).Outcome);
            await peer.SendAsync(new InvokeMessage { RequestId = requestId, SessionId = sessionId });
            Assert.Equal(ReasonCode.InvokeReplay, await peer.ReadCloseAsync());
        }
    }

    [Fact]
    public void TheReplayWindowKeepsTheLast1024Ids()
    {
        var window = new ReplayWindow(1_024);
        for (var i = 0; i < 5_000; i++)
        {
            window.Add(i.ToString(CultureInfo.InvariantCulture));
        }

        Assert.Equal(1_024, window.Count);
        Assert.True(window.Contains("4999"));
        Assert.True(window.Contains("3976"));
        Assert.False(window.Contains("3975"));
    }

    [Fact]
    public async Task TheInvocationDeadlineIsTheCompanionsInvokeTimeoutMinusThreeSeconds()
    {
        var clock = new TestClock();
        var never = new TaskCompletionSource<InvokeResult>();
        var handler = new TestHandler { OnInvoke = (_, _) => never.Task };
        try
        {
            await using var harness = Harness(handler, clock);
            var peer = await harness.ConnectAsync(new HostScript { Limits = StatusAndBackoffTests.Limits(invokeTimeoutMs: 5_000) });
            var sessionId = await StartAsync(peer, Action);
            var requestId = ScriptedPeer.NewId();
            await peer.SendAsync(new InvokeMessage { RequestId = requestId, SessionId = sessionId });
            await ClientHarness.WaitForAsync(() => handler.Invocations.Count == 1, "invoked");
            clock.Advance(TimeSpan.FromMilliseconds(1_999));
            Assert.True(await peer.SilentForAsync(TimeSpan.FromMilliseconds(100)));
            clock.Advance(TimeSpan.FromMilliseconds(2));
            var result = await peer.ReadAsync<ResultMessage>();
            Assert.Equal((requestId, Outcome.Failed), (result.RequestId, result.Outcome));
            Assert.Contains(requestId, harness.Observer.Deadlines);
            clock.Advance(TimeSpan.FromSeconds(4.9));
            await harness.Client.FlushEventsAsync();
            Assert.Empty(harness.Faults);
            clock.Advance(TimeSpan.FromSeconds(0.2));
            await ClientHarness.WaitForAsync(() => harness.Faults.Count == 1, "ignored cancellation");
            Assert.Equal((HandlerFault.IgnoredCancellation, requestId), (harness.Faults[0].Kind, harness.Faults[0].RequestId));
        }
        finally
        {
            never.TrySetResult(InvokeResult.Done);
        }
    }

    [Fact]
    public async Task RenewalRepublishesAtEightyPercentWhileTheHandlerRunsAndNothingNewerWasPublished()
    {
        var clock = new TestClock();
        Session? live = null;
        var handler = new TestHandler
        {
            OnSession = async (session, token) =>
            {
                live = session;
                session.SetFace(new Face { Line1 = "kept", GoodFor = TimeSpan.FromSeconds(10), Renew = true });
                await Task.Delay(Timeout.Infinite, token);
            },
        };
        await using var harness = Harness(handler, clock);
        var peer = await harness.ConnectAsync(new HostScript { Limits = StatusAndBackoffTests.Limits(pingIntervalMs: 300_000) });
        await StartAsync(peer, Widget);
        Assert.Equal("kept", Line1(await peer.ReadAsync<SetFaceMessage>()));
        clock.Advance(TimeSpan.FromSeconds(7.9));
        Assert.True(await peer.SilentForAsync(TimeSpan.FromMilliseconds(100)));
        clock.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal("kept", Line1(await peer.ReadAsync<SetFaceMessage>()));
        clock.Advance(TimeSpan.FromSeconds(8));
        Assert.Equal("kept", Line1(await peer.ReadAsync<SetFaceMessage>()));
        live!.SetFace(new Face { Line1 = "newer", GoodFor = TimeSpan.FromSeconds(10) });
        clock.Advance(TimeSpan.FromMilliseconds(500)); // one face per session per 1/faceChangesPerSecond
        Assert.Equal("newer", Line1(await peer.ReadNudgingAsync<SetFaceMessage>(clock, TimeSpan.FromMilliseconds(100))));
        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.True(await peer.SilentForAsync(TimeSpan.FromMilliseconds(150)));
    }

    [Fact]
    public async Task RenewalStopsWhenTheHandlerEndsAndAfterOneDay()
    {
        var clock = new TestClock();
        var handler = new TestHandler
        {
            OnSession = (session, _) =>
            {
                session.SetFace(new Face { Line1 = session.Settings.Count.ToString(CultureInfo.InvariantCulture), GoodFor = TimeSpan.FromSeconds(10), Renew = true });
                return Task.CompletedTask;
            },
        };
        await using (var harness = Harness(handler, clock))
        {
            var peer = await harness.ConnectAsync(new HostScript { Limits = StatusAndBackoffTests.Limits(pingIntervalMs: 300_000) });
            var sessionId = await StartAsync(peer, Widget);
            await peer.ReadAsync<SetFaceMessage>();
            await ClientHarness.WaitForAsync(() => harness.Observer.SessionsEnded.Contains(sessionId), "ended");
            clock.Advance(TimeSpan.FromSeconds(30));
            Assert.True(await peer.SilentForAsync(TimeSpan.FromMilliseconds(150)));
        }

        var dayClock = new TestClock();
        var daily = new TestHandler
        {
            OnSession = async (session, token) =>
            {
                session.SetFace(new Face { Line1 = "day", GoodFor = TimeSpan.FromHours(12), Renew = true });
                await Task.Delay(Timeout.Infinite, token);
            },
        };
        await using (var harness = Harness(daily, dayClock))
        {
            var peer = await harness.ConnectAsync(new HostScript { Limits = StatusAndBackoffTests.Limits(pingIntervalMs: 300_000) });
            await StartAsync(peer, Widget);
            await peer.ReadAsync<SetFaceMessage>();

            // Renewals at 9.6 h and 19.2 h; the next would be 28.8 h after the author's SetFace, past one day.
            var renewals = await AdvanceKeepingAliveAsync(peer, dayClock, TimeSpan.FromHours(30));
            Assert.Equal(2, renewals.Count);
            Assert.All(renewals, renewal => Assert.Equal("day", Line1(renewal)));
            Assert.Equal(ConnectionState.Connected, harness.Client.State);
        }
    }

    /// <summary>Advances the clock in 200-second steps, pinging between them so the SDK never goes idle; returns the faces received.</summary>
    private static async Task<List<SetFaceMessage>> AdvanceKeepingAliveAsync(ScriptedPeer peer, TestClock clock, TimeSpan total)
    {
        var faces = new List<SetFaceMessage>();
        var step = TimeSpan.FromSeconds(200);
        var id = 0;
        for (var advanced = TimeSpan.Zero; advanced < total; advanced += step)
        {
            await peer.SendAsync(new PingMessage { Id = ++id });
            while (true)
            {
                var message = await peer.ReadAsync();
                if (message is SetFaceMessage face)
                {
                    faces.Add(face);
                    continue;
                }

                Assert.True(message is PongMessage, "Expected the pong to ping " + id + ", got " + Describe(message));
                Assert.Equal(id, ((PongMessage)message).Id);
                break;
            }

            clock.Advance(step);
        }

        await peer.SendAsync(new PingMessage { Id = ++id });
        for (var message = await peer.ReadAsync(); message is not PongMessage; message = await peer.ReadAsync())
        {
            Assert.True(message is SetFaceMessage, "Expected a face or the last pong, got " + Describe(message));
            faces.Add((SetFaceMessage)message);
        }

        return faces;

        static string Describe(WireMessage? message) => message switch
        {
            null => "the end of the stream",
            ErrorMessage error => "error " + error.Code.Value,
            _ => message.Type,
        };
    }

    [Fact]
    public async Task TheContractsWidgetStopsWithoutAFaultAndSendsNothingAfterStop()
    {
        var clock = new TestClock();
        await using var harness = Harness(new TimeWidget(), clock);
        var peer = await harness.ConnectAsync(new HostScript { UiLanguage = "de-DE", Limits = StatusAndBackoffTests.Limits(pingIntervalMs: 300_000) });
        var sessionId = await StartAsync(peer, Widget);
        var face = (await peer.ReadAsync<SetFaceMessage>()).Face;
        Assert.Equal(60, face.GoodForSeconds);
        Assert.Equal(clock.GetLocalNow().ToString("t", CultureInfo.GetCultureInfo("de-DE")), Assert.IsType<TextLine>(face.Line1).Text);
        await peer.SendAsync(new StopSessionMessage { SessionId = sessionId });
        await ClientHarness.WaitForAsync(() => harness.Observer.SessionsEnded.Contains(sessionId), "ended");
        clock.Advance(TimeSpan.FromSeconds(120));
        Assert.True(await peer.SilentForAsync(TimeSpan.FromMilliseconds(150)));
        await harness.Client.FlushEventsAsync();
        Assert.Empty(harness.Faults);
        Assert.Empty(harness.Observer.AfterEnd);
    }

    [Fact]
    public async Task TheContractsActionAnswersDone()
    {
        await using var harness = new ClientHarness(new Greeter(), manifest: GreeterManifest());
        harness.Start();
        var peer = await harness.ConnectAsync();
        var sessionId = ScriptedPeer.NewId();
        await peer.SendAsync(new StartSessionMessage { SessionId = sessionId, ContributionId = "example.greeter", Settings = new Dictionary<string, string> { ["greeting"] = "hello" } });
        await peer.SendAsync(new InvokeMessage { RequestId = ScriptedPeer.NewId(), SessionId = sessionId });
        Assert.Equal(Outcome.Done, (await peer.ReadAsync<ResultMessage>()).Outcome);
    }

    [Fact]
    public async Task FiveThousandStartStopAndInvokeCyclesOnOneConnectionStayConnected()
    {
        var clock = new TestClock();
        await using var harness = new ClientHarness(new QuickHandler(), manifest: TestManifests.Kinds,
            options: new CompanionClientOptions { TimeProvider = clock });
        harness.Start();
        var peer = await harness.ConnectAsync();
        for (var cycle = 0; cycle < 5_000; cycle++)
        {
            var sessionId = ScriptedPeer.NewId();
            var requestId = ScriptedPeer.NewId();
            await peer.SendAsync(new StartSessionMessage { SessionId = sessionId, ContributionId = Action, Settings = None });
            await peer.SendAsync(new InvokeMessage { RequestId = requestId, SessionId = sessionId });
            var result = await peer.ReadAsync<ResultMessage>();
            Assert.Equal((requestId, Outcome.Done), (result.RequestId, result.Outcome));
            await peer.SendAsync(new StopSessionMessage { SessionId = sessionId });
            clock.Advance(TimeSpan.FromMilliseconds(10));
        }

        await peer.SendAsync(new PingMessage { Id = 1 });
        Assert.Equal(1, (await peer.ReadAsync<PongMessage>()).Id);
        Assert.Equal(ConnectionState.Connected, harness.Client.State);
        Assert.Single(harness.Statuses, status => status.State == ConnectionState.Connected);
    }

    [Fact]
    public async Task HandlersThatIgnoreCancellationAcrossReconnectsReachTheCapAndAreRefusedWithoutDroppingTheConnection()
    {
        var clock = new TestClock();
        var never = new TaskCompletionSource();
        var handler = new TestHandler { OnSession = (_, _) => never.Task };
        try
        {
            await using var harness = Harness(handler, clock);
            var script = new HostScript { Limits = StatusAndBackoffTests.Limits(maxSessions: 22, pingIntervalMs: 300_000) };
            var peer = await harness.ConnectAsync(script);
            for (var connection = 0; connection < 3; connection++)
            {
                if (connection > 0)
                {
                    await peer.DisposeAsync();
                    var waiting = await StatusAndBackoffTests.NthAsync(harness, ConnectionState.Waiting, connection - 1);
                    clock.Advance(waiting.RetryIn!.Value);
                    peer = await harness.NextPeerAsync();
                    await peer.HandshakeAsync(script);
                    await ClientHarness.WaitForAsync(() => harness.Client.State == ConnectionState.Connected, "reconnected");
                }

                var count = connection < 2 ? 22 : 20;
                for (var i = 0; i < count; i++)
                {
                    await StartAsync(peer, Widget);
                }

                await ClientHarness.WaitForAsync(() => harness.Client.Tracker.RunningSessions == (connection * 22) + count, "running");
            }

            Assert.Equal(64, harness.Client.Tracker.RunningSessions);
            var refusedId = ScriptedPeer.NewId();
            await peer.SendAsync(new StartSessionMessage { SessionId = refusedId, ContributionId = Widget, Settings = None });
            var refused = await peer.ReadAsync<SessionRefusedMessage>();
            Assert.Equal((refusedId, ReasonCode.SessionCapacity), (refused.SessionId, refused.Code));
            await ClientHarness.WaitForAsync(() => harness.Faults.Any(fault => fault.Kind == HandlerFault.SessionCapacity), "capacity fault");
            Assert.Equal(refusedId, harness.Faults.Single(fault => fault.Kind == HandlerFault.SessionCapacity).SessionId);
            await peer.SendAsync(new PingMessage { Id = 1 });
            Assert.Equal(1, (await peer.ReadAsync<PongMessage>()).Id);
            Assert.Equal(ConnectionState.Connected, harness.Client.State);
            clock.Advance(TimeSpan.FromSeconds(6));
            await ClientHarness.WaitForAsync(() => harness.Faults.Count(fault => fault.Kind == HandlerFault.IgnoredCancellation) == 44, "ignored cancellation");
        }
        finally
        {
            never.TrySetResult();
        }
    }

    [Fact]
    public async Task PingsAreAnsweredWhileAHandlerBlocks()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource();
        var handler = new TestHandler
        {
            OnSession = (_, _) =>
            {
                entered.TrySetResult();
                release.Wait(TimeSpan.FromSeconds(20), CancellationToken.None);
                return Task.CompletedTask;
            },
        };
        try
        {
            await using var harness = Harness(handler);
            var peer = await harness.ConnectAsync();
            await StartAsync(peer, Widget);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await peer.SendAsync(new PingMessage { Id = 3 });
            Assert.Equal(3, (await peer.ReadAsync<PongMessage>(TimeSpan.FromSeconds(5))).Id);
            Assert.False(release.IsSet);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task ASessionCarriesItsSettingsLanguageClockAndCapabilities()
    {
        var clock = new TestClock();
        var handler = new TestHandler();
        await using var harness = new ClientHarness(handler, options: new CompanionClientOptions { TimeProvider = clock });
        harness.Start();
        var peer = await harness.ConnectAsync(new HostScript { UiLanguage = "de-DE" });
        var settings = new Dictionary<string, string> { ["mode"] = "pause", ["duration"] = "one-minute" };
        await peer.SendAsync(new StartSessionMessage { SessionId = ScriptedPeer.NewId(), ContributionId = TestManifests.Timer, Settings = settings });
        await ClientHarness.WaitForAsync(() => !handler.Sessions.IsEmpty, "session");
        var session = handler.Sessions.Single();
        Assert.Equal(settings, session.Settings);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)session.Settings)["mode"] = "start");
        Assert.Equal("de-DE", session.UiLanguage);
        Assert.Equal("de-DE", session.UiCulture.Name);
        Assert.Same(clock, session.Time);
        Assert.Equal(Provides.Invoke | Provides.Face, session.Provides);
        Assert.True(session.IsActive);
        Assert.Equal(TestManifests.Timer, session.ContributionId);
        Assert.NotNull(NewSession(new CountingSink(), Provides.Face, "zz-Invalid-Tag-Xx").UiCulture);
    }

    [Fact]
    public async Task TheRouterRoutesByContributionAndReportsUnmappedContributions()
    {
        var router = new ContributionRouter()
            .MapSession(Widget, (_, _) => Task.CompletedTask)
            .MapInvoke(Both, (_, _) => Task.FromResult(InvokeResult.Refused));
        Assert.Equal([Action, Both], router.FindUnmapped(TestManifests.Kinds));
        router.Map(Action, new Greeter());
        router.MapSession(Both, (_, _) => Task.CompletedTask);
        Assert.Empty(router.FindUnmapped(TestManifests.Kinds));
        Assert.Throws<ArgumentException>(() => router.MapSession(Widget, (_, _) => Task.CompletedTask));
        Assert.Throws<ArgumentException>(() => router.Map(Widget, new Greeter()));
        Assert.Throws<ArgumentException>(() => router.MapInvoke(Action, (_, _) => Task.FromResult(InvokeResult.Done)));
        var both = NewSession(new CountingSink(), Provides.Invoke | Provides.Face, contributionId: Both);
        Assert.Equal(InvokeResult.Refused, await router.InvokeAsync(new Invocation("r", both), CancellationToken.None));
        var unmapped = NewSession(new CountingSink(), Provides.Invoke, contributionId: "example.kinds/other");
        Assert.Equal(InvokeResult.Unsupported, await router.InvokeAsync(new Invocation("r", unmapped), CancellationToken.None));
        Assert.True(router.RunSessionAsync(unmapped, CancellationToken.None).IsCompletedSuccessfully);
    }

    internal static ExtensionManifest GreeterManifest() => new()
    {
        Id = "example.greeter",
        Name = "Greeter",
        Description = "Greets.",
        Version = "1.0.0",
        Hosts = [TestHosts.Id],
        Contributions =
        [
            new Contribution
            {
                Id = "example.greeter",
                Name = "Greet",
                Description = "Greets the world.",
                Glyph = "",
                Provides = Provides.Invoke,
                Settings =
                [
                    new Setting
                    {
                        Id = "greeting",
                        Name = "Greeting",
                        Default = "hello",
                        Choices = [new SettingChoice { Value = "hello", Name = "Hello" }, new SettingChoice { Value = "hi", Name = "Hi" }],
                    },
                ],
            },
        ],
    };

    private static ClientHarness Harness(IContributionHandler handler, TestClock? clock = null)
    {
        var harness = new ClientHarness(handler, manifest: TestManifests.Kinds,
            options: clock is null ? null : new CompanionClientOptions { TimeProvider = clock });
        harness.Start();
        return harness;
    }

    private static async Task<string> StartAsync(ScriptedPeer peer, string contributionId)
    {
        var sessionId = ScriptedPeer.NewId();
        await peer.SendAsync(new StartSessionMessage { SessionId = sessionId, ContributionId = contributionId, Settings = None });
        return sessionId;
    }

    private static string? Line1(SetFaceMessage message) => (message.Face.Line1 as TextLine)?.Text;

    private static Session NewSession(ISessionSink sink, Provides provides, string uiLanguage = "en-US", string contributionId = Widget) =>
        new(ScriptedPeer.NewId(), contributionId, provides, None, uiLanguage, [], TimeProvider.System, sink);

    /// <summary>The widget of contract §9.4, exactly as shown there.</summary>
    private sealed class TimeWidget : ContributionHandler
    {
        public override async Task RunSessionAsync(Session session, CancellationToken cancellationToken)
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), session.Time);
            do
            {
                var now = session.Time.GetLocalNow();
                session.SetFace(new Face
                {
                    Line1 = now.ToString("t", session.UiCulture),
                    Detail = now.ToString("F", session.UiCulture),
                    GoodFor = TimeSpan.FromSeconds(60),
                });
            }
            while (await timer.WaitForNextTickAsync(cancellationToken));
        }
    }

    /// <summary>The action of contract §9.4, exactly as shown there.</summary>
    private sealed class Greeter : ContributionHandler
    {
        public override Task<InvokeResult> InvokeAsync(Invocation invocation, CancellationToken cancellationToken)
        {
            var greeting = invocation.Session.Settings["greeting"];
            Console.WriteLine($"{greeting}, world");
            return Task.FromResult(InvokeResult.Done);
        }
    }

    private sealed class QuickHandler : ContributionHandler
    {
        public override Task<InvokeResult> InvokeAsync(Invocation invocation, CancellationToken cancellationToken) => Task.FromResult(InvokeResult.Done);
    }

    private sealed class CountingSink : ISessionSink
    {
        public int Published { get; private set; }

        public int AfterEnd { get; private set; }

        public int WithoutFace { get; private set; }

        public FaceCommand? Last { get; private set; }

        public PublishResult Publish(Session session, FaceCommand command)
        {
            Published++;
            Last = command;
            return PublishResult.Accepted;
        }

        public void PublishedAfterEnd(Session session, FaceCommand command) => AfterEnd++;

        public void FaceWithoutProvides(Session session) => WithoutFace++;
    }
}
