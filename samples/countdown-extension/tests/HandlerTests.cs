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

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    [Fact]
    public async Task AStatusSessionPublishesWhenAPickChangesTheCountdown()
    {
        var time = new FakeTimeProvider();
        var handler = new CountdownHandler(time);
        using var status = TestSessions.FromManifest(await ManifestAsync(), CountdownChoices.StatusId, time: time);
        var running = status.RunAsync(handler);
        await UntilAsync(() => status.Publications.Count == 1);
        Assert.Equal(new TextLine { Text = "5:00" }, status.LastFace!.Line1);
        Assert.Equal(new TextLine { Text = "Ready" }, status.LastFace.Line2);
        Assert.True(status.LastFace.Renew);

        using var timer = TestSessions.FromManifest(await ManifestAsync(), CountdownChoices.TimerId,
            new Dictionary<string, string> { ["mode"] = "start", ["duration"] = "one-minute" }, time);
        Assert.Equal(InvokeResult.Done, await handler.InvokeAsync(TestInvocations.Create(timer), CancellationToken.None));
        await UntilAsync(() => status.Publications.Count == 2);
        Assert.Equal(new TextLine { Text = "1:00" }, status.LastFace!.Line1);
        Assert.Equal(FaceState.Playing, status.LastFace.State);

        // While it runs, the face follows the clock. The handler waits on the fake clock, so keep
        // advancing it until the handler's next wake has published (it may not be waiting yet).
        await UntilAsync(() =>
        {
            time.Advance(TimeSpan.FromSeconds(1));
            return status.LastFace!.Line1 != new TextLine { Text = "1:00" };
        });
        Assert.Equal(FaceState.Playing, status.LastFace!.State);

        // A pause shows the time left at once, and the face stays true until the next pick.
        using var pause = TestSessions.FromManifest(await ManifestAsync(), CountdownChoices.TimerId,
            new Dictionary<string, string> { ["mode"] = "pause" }, time);
        Assert.Equal(InvokeResult.Done, await handler.InvokeAsync(TestInvocations.Create(pause), CancellationToken.None));
        await UntilAsync(() => status.LastFace!.State == FaceState.Paused);
        var paused = CountdownHandler.CreateFace(handler.Countdown.Read(), null, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(paused.Line1, status.LastFace!.Line1);
        Assert.True(status.LastFace.Renew);
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
        await UntilAsync(() => handler.Countdown.Read().Phase == CountdownPhase.Paused);
        Assert.Equal(TimeSpan.FromSeconds(40), handler.Countdown.Read().Remaining);
        Assert.Empty(host.Faults);
    }
}
