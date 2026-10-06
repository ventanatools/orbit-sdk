// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Globalization;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CountdownExtensionSample.Tests;

/// <summary>The countdown itself: elapsed time, idempotent picks, pause and resume, reset and completion.</summary>
public sealed class CountdownTests
{
    private static CountdownChoice Choice(CountdownMode mode, int minutes = 1) => new(mode, TimeSpan.FromMinutes(minutes));

    [Fact]
    public void ItStartsReadyAtFiveMinutesAndCountsDownToFinished()
    {
        var time = new FakeTimeProvider();
        var countdown = new Countdown(time);
        Assert.Equal(new CountdownSnapshot(TimeSpan.FromMinutes(5), CountdownPhase.Ready), countdown.Read());
        countdown.Apply(Choice(CountdownMode.Start));
        time.Advance(TimeSpan.FromSeconds(17));
        Assert.Equal(TimeSpan.FromSeconds(43), countdown.Read().Remaining);
        time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(new CountdownSnapshot(TimeSpan.Zero, CountdownPhase.Finished), countdown.Read());
    }

    [Fact]
    public void StartAndPauseAreIdempotentAndResumeKeepsTheTimeLeft()
    {
        var time = new FakeTimeProvider();
        var countdown = new Countdown(time);
        countdown.Apply(Choice(CountdownMode.Start));
        time.Advance(TimeSpan.FromSeconds(10));
        countdown.Apply(Choice(CountdownMode.Start, 25));
        Assert.Equal(TimeSpan.FromSeconds(50), countdown.Read().Remaining);
        countdown.Apply(Choice(CountdownMode.Pause));
        time.Advance(TimeSpan.FromHours(1));
        countdown.Apply(Choice(CountdownMode.Pause, 25));
        Assert.Equal(new CountdownSnapshot(TimeSpan.FromSeconds(50), CountdownPhase.Paused), countdown.Read());
        countdown.Apply(Choice(CountdownMode.Start, 25));
        time.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(new CountdownSnapshot(TimeSpan.FromSeconds(45), CountdownPhase.Running), countdown.Read());
    }

    [Fact]
    public void ResetPreparesTheChosenDurationWithoutStarting()
    {
        var time = new FakeTimeProvider();
        var countdown = new Countdown(time);
        countdown.Apply(Choice(CountdownMode.Start));
        time.Advance(TimeSpan.FromSeconds(30));
        countdown.Apply(Choice(CountdownMode.Reset, 25));
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(new CountdownSnapshot(TimeSpan.FromMinutes(25), CountdownPhase.Ready), countdown.Read());
        countdown.Apply(Choice(CountdownMode.Pause));
        Assert.Equal(CountdownPhase.Ready, countdown.Read().Phase);
    }

    [Fact]
    public void AFreshStartAfterCompletionUsesItsOwnDuration()
    {
        var time = new FakeTimeProvider();
        var countdown = new Countdown(time);
        countdown.Apply(Choice(CountdownMode.Start));
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(CountdownPhase.Finished, countdown.Read().Phase);
        countdown.Apply(Choice(CountdownMode.Start, 5));
        Assert.Equal(new CountdownSnapshot(TimeSpan.FromMinutes(5), CountdownPhase.Running), countdown.Read());
    }

    [Fact]
    public void APickThatChangesSomethingCompletesChanged()
    {
        var countdown = new Countdown(new FakeTimeProvider());
        var changed = countdown.Changed;
        countdown.Apply(Choice(CountdownMode.Pause));
        Assert.False(changed.IsCompleted);
        countdown.Apply(Choice(CountdownMode.Start));
        Assert.True(changed.IsCompleted);
        Assert.False(countdown.Changed.IsCompleted);
    }

    [Theory]
    [InlineData("start", "one-minute", true)]
    [InlineData("pause", "twenty-five-minutes", true)]
    [InlineData("shell", "one-minute", false)]
    [InlineData("start", "forever", false)]
    public void ChoicesAreReadOnlyFromTheDeclaredValues(string mode, string duration, bool valid)
    {
        var settings = new Dictionary<string, string> { ["mode"] = mode, ["duration"] = duration };
        Assert.Equal(valid, CountdownChoices.TryRead(settings, out _));
        settings["extra"] = "value";
        Assert.False(CountdownChoices.TryRead(settings, out _));
    }

    [Fact]
    public void FacesRoundUpAndSayWhatTheyShow()
    {
        var running = CountdownHandler.CreateFace(new CountdownSnapshot(TimeSpan.FromMilliseconds(1), CountdownPhase.Running), null, CultureInfo.InvariantCulture);
        Assert.Equal(new TextLine { Text = "0:01" }, running.Line1);
        Assert.Equal(FaceState.Playing, running.State);
        Assert.False(running.Renew);
        var ready = CountdownHandler.CreateFace(new CountdownSnapshot(TimeSpan.FromMinutes(25), CountdownPhase.Ready), Choice(CountdownMode.Reset), CultureInfo.InvariantCulture);
        Assert.Equal(new TextLine { Text = "25:00" }, ready.Line1);
        Assert.Equal(new TextLine { Text = "Reset · Ready" }, ready.Line2);
        Assert.Equal("Countdown ready. 25:00 remaining.", ready.Detail);
        Assert.True(ready.Renew);
        Assert.Equal(TimeSpan.FromSeconds(1), CountdownHandler.UntilNextSecond(TimeSpan.FromSeconds(42)));
        Assert.Equal(TimeSpan.FromMilliseconds(300), CountdownHandler.UntilNextSecond(TimeSpan.FromMilliseconds(4300)));
    }
}
