// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using Microsoft.Extensions.Time.Testing;
using VentanaTools.Orbit.Extensions;
using VentanaTools.Orbit.Extensions.Testing;
using Xunit;

namespace CountdownExtensionSample.Tests;

/// <summary>The handler through the SDK's test kit: a recording session, and a real client against a test host.</summary>
public sealed class HandlerTests
{
    private static async Task<ExtensionManifest> ManifestAsync() =>
        (await ManifestReader.ReadFileAsync("extension.json")).Value ?? throw new InvalidOperationException("extension.json is invalid.");

    /// <summary>
    /// Advances the fake clock a second at a time until the session publishes its next face. The
    /// handler may not be waiting on the clock yet when it first moves, so one step can be too few.
    /// </summary>
    private static async Task<Face> AdvanceUntilNextFaceAsync(RecordingSession session, FakeTimeProvider time)
    {
        for (var step = 0; step < 100; step++)
        {
            time.Advance(TimeSpan.FromSeconds(1));
            try
            {
                return await session.WaitForFaceAsync(TimeSpan.FromMilliseconds(50));
            }
            catch (TimeoutException)
            {
                // Not waiting on the clock yet: advance again.
            }
        }

        throw new TimeoutException("The session published no face while the clock advanced.");
    }

    [Fact]
    public async Task AStatusSessionPublishesWhenAPickChangesTheCountdown()
    {
        var time = new FakeTimeProvider();
        var handler = new CountdownHandler(time);
        using var status = TestSessions.FromManifest(await ManifestAsync(), CountdownChoices.StatusId, time: time);
        var running = status.RunAsync(handler);
        var ready = await status.WaitForFaceAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new TextLine { Text = "5:00" }, ready.Line1);
        Assert.Equal(new TextLine { Text = "Ready" }, ready.Line2);
        Assert.True(ready.Renew);

        using var timer = TestSessions.FromManifest(await ManifestAsync(), CountdownChoices.TimerId,
            new Dictionary<string, string> { ["mode"] = "start", ["duration"] = "one-minute" }, time);
        Assert.Equal(InvokeResult.Done, await handler.InvokeAsync(TestInvocations.Create(timer), CancellationToken.None));
        var started = await status.WaitForFaceAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new TextLine { Text = "1:00" }, started.Line1);
        Assert.Equal(FaceState.Playing, started.State);

        // While it runs, the face follows the clock.
        var ticked = await AdvanceUntilNextFaceAsync(status, time);
        Assert.NotEqual(new TextLine { Text = "1:00" }, ticked.Line1);
        Assert.Equal(FaceState.Playing, ticked.State);

        // A pause shows the time left at once, and the face stays true until the next pick.
        using var pause = TestSessions.FromManifest(await ManifestAsync(), CountdownChoices.TimerId,
            new Dictionary<string, string> { ["mode"] = "pause" }, time);
        Assert.Equal(InvokeResult.Done, await handler.InvokeAsync(TestInvocations.Create(pause), CancellationToken.None));
        var shown = await status.WaitForFaceAsync(TimeSpan.FromSeconds(5));
        var paused = CountdownHandler.CreateFace(handler.Countdown.Read(), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(FaceState.Paused, shown.State);
        Assert.Equal(paused.Line1, shown.Line1);
        Assert.True(shown.Renew);
        status.Stop();
        await running;
        Assert.DoesNotContain(status.Publications, publication => publication.Kind == PublicationKind.AfterStop);
    }

    [Fact]
    public async Task APickOfAStoppedSessionIsRefusedAndACancelledPickChangesNothing()
    {
        var time = new FakeTimeProvider();
        var handler = new CountdownHandler(time);
        using var timer = TestSessions.FromManifest(await ManifestAsync(), CountdownChoices.TimerId, time: time);
        timer.Stop();
        Assert.Equal(InvokeResult.Refused, await handler.InvokeAsync(TestInvocations.Create(timer), CancellationToken.None));
        using var live = TestSessions.FromManifest(await ManifestAsync(), CountdownChoices.TimerId, time: time);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.InvokeAsync(TestInvocations.Create(live), cancelled.Token));
        Assert.Equal(CountdownPhase.Ready, handler.Countdown.Read().Phase);
    }

    [Fact]
    public async Task ARealClientStartsPausesAndShowsTheSharedCountdown()
    {
        // The countdown runs on the fake clock; the client and the test host keep real time, so the
        // SDK's pacing and the waits below behave as they would against a real host.
        var time = new FakeTimeProvider();
        var handler = new CountdownHandler(time);
        await using var host = await CompanionTestHost.StartAsync(await ManifestAsync(), handler);
        var start = await host.StartSessionAsync(CountdownChoices.TimerId, new Dictionary<string, string> { ["mode"] = "start", ["duration"] = "one-minute" });
        var pause = await host.StartSessionAsync(CountdownChoices.TimerId, new Dictionary<string, string> { ["mode"] = "pause", ["duration"] = "one-minute" });
        var status = await host.StartSessionAsync(CountdownChoices.StatusId);
        Assert.Null(status.RefusedCode);
        Assert.Equal(new TextLine { Text = "5:00" }, (await host.WaitForFaceAsync(status, TimeSpan.FromSeconds(5))).Face.Line1);

        Assert.Equal(TestInvokeOutcomeKind.Done, (await host.InvokeAsync(start)).Kind);
        Assert.Equal(new TextLine { Text = "1:00" }, (await host.WaitForFaceAsync(status, TimeSpan.FromSeconds(5))).Face.Line1);
        time.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(TestInvokeOutcomeKind.Done, (await host.InvokeAsync(pause)).Kind);

        // The handler applies a pick before it answers, so the countdown is paused once the result arrives.
        Assert.Equal(CountdownPhase.Paused, handler.Countdown.Read().Phase);
        Assert.Equal(TimeSpan.FromSeconds(40), handler.Countdown.Read().Remaining);
        Assert.Empty(host.Faults);
    }
}
