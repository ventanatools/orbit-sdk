using System.Threading.Channels;
using Orbit.Extensions.Protocol;
using Orbit.Extensions.Sdk;

namespace CountdownExtensionSample.Tests;

internal static class Program
{
    private static readonly IReadOnlyDictionary<string, string> Empty = new Dictionary<string, string>();

    public static async Task<int> Main()
    {
        (string Name, Func<Task> Run)[] cases =
        [
            ("manifest choices match behavior and defaults", () => Sync(ManifestChoices)),
            ("monotonic time, delayed reads and completion", () => Sync(MonotonicTime)),
            ("start and pause are idempotent; resume keeps remaining time", () => Sync(PauseResume)),
            ("reset prepares the chosen duration without starting", () => Sync(Reset)),
            ("fresh start after completion uses its selected duration", () => Sync(Restart)),
            ("invalid and retired invocations cannot change the timer", () => Sync(Refusal)),
            ("fractional seconds are rounded up for display", () => Sync(FaceFormatting)),
            ("renewal follows elapsed time and ends on retirement", SessionLifetime),
            ("cancellation ends a pending renewal", SessionCancellation),
            ("rejected publication stops renewal", RejectedPublication),
            ("configured action faces observe one shared timer", SharedFaces),
            ("invalid session settings fail without a renewal loop", InvalidSession),
            ("64 sessions retain publication rate headroom", RateHeadroom),
        ];
        foreach (var test in cases)
        {
            try
            {
                await test.Run().WaitAsync(TimeSpan.FromSeconds(5));
                Console.WriteLine($"PASS {test.Name}");
            }
            catch (Exception error)
            {
                Console.Error.WriteLine($"FAIL {test.Name}: {error.Message}");
                return 1;
            }
        }
        Console.WriteLine($"{cases.Length} deterministic countdown checks passed.");
        return 0;
    }

    private static Dictionary<string, string> Settings(string mode, string duration = "one-minute") =>
        new(StringComparer.Ordinal) { ["mode"] = mode, ["duration"] = duration };
    private static Task Sync(Action action) { action(); return Task.CompletedTask; }
    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected {expected}; got {actual}.");
    }
    private static CountdownChoice Choice(CountdownMode mode, int minutes = 1) => new(mode, TimeSpan.FromMinutes(minutes));
    private static void Apply(Countdown timer, CountdownMode mode, int minutes = 1) =>
        Equal(true, timer.Apply(Choice(mode, minutes), () => true, CancellationToken.None));

    private static void ManifestChoices()
    {
        var read = ExternalExtensionManifestReader.Read(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "extension.json")));
        var manifest = read.Manifest ?? throw new InvalidOperationException(read.Error);
        Equal(2, manifest.ManifestVersion);
        Equal(CountdownChoices.ExtensionId, manifest.Id);
        Equal(2, manifest.Actions.Count);
        var action = manifest.Actions.Single(row => row.Id == CountdownChoices.TimerAction);
        Equal(true, action.CanInvoke && action.HasFace);
        var defaults = ExternalExtensionSettings.Resolve(action, null) ?? throw new InvalidOperationException("Missing defaults.");
        Equal("start", defaults["mode"]);
        Equal("five-minutes", defaults["duration"]);
        Equal(true, CountdownChoices.TryRead(defaults, out _));
        var combinations = 0;
        foreach (var mode in action.Settings.Single(row => row.Id == "mode").Choices)
        foreach (var duration in action.Settings.Single(row => row.Id == "duration").Choices)
        {
            var resolved = ExternalExtensionSettings.Resolve(action, Settings(mode.Value, duration.Value));
            Equal(true, resolved is not null && CountdownChoices.TryRead(resolved, out _));
            combinations++;
        }
        Equal(9, combinations);
        var passive = manifest.Actions.Single(row => row.Id == CountdownChoices.StatusAction);
        Equal(true, passive.HasFace && !passive.CanInvoke && passive.Settings.Count == 0);
    }

    private static void MonotonicTime()
    {
        var clock = new ManualTime();
        var timer = new Countdown(clock);
        Equal(new CountdownSnapshot(TimeSpan.FromMinutes(5), CountdownPhase.Ready), timer.Read());
        Apply(timer, CountdownMode.Start);
        clock.Utc = clock.Utc.AddDays(-2);
        clock.Advance(TimeSpan.FromSeconds(17));
        Equal(TimeSpan.FromSeconds(43), timer.Read().Remaining);
        clock.Utc = clock.Utc.AddDays(7);
        clock.Advance(TimeSpan.FromHours(2));
        Equal(new CountdownSnapshot(TimeSpan.Zero, CountdownPhase.Finished), timer.Read());
    }

    private static void PauseResume()
    {
        var clock = new ManualTime();
        var timer = new Countdown(clock);
        Apply(timer, CountdownMode.Start);
        clock.Advance(TimeSpan.FromSeconds(10));
        Apply(timer, CountdownMode.Start, 25);
        Equal(TimeSpan.FromSeconds(50), timer.Read().Remaining);
        Apply(timer, CountdownMode.Pause);
        clock.Advance(TimeSpan.FromHours(1));
        Apply(timer, CountdownMode.Pause, 25);
        Equal(new CountdownSnapshot(TimeSpan.FromSeconds(50), CountdownPhase.Paused), timer.Read());
        Apply(timer, CountdownMode.Start, 25);
        clock.Advance(TimeSpan.FromSeconds(5));
        Equal(new CountdownSnapshot(TimeSpan.FromSeconds(45), CountdownPhase.Running), timer.Read());
    }

    private static void Reset()
    {
        var clock = new ManualTime();
        var timer = new Countdown(clock);
        Apply(timer, CountdownMode.Start);
        clock.Advance(TimeSpan.FromSeconds(30));
        Apply(timer, CountdownMode.Reset, 25);
        clock.Advance(TimeSpan.FromHours(1));
        Equal(new CountdownSnapshot(TimeSpan.FromMinutes(25), CountdownPhase.Ready), timer.Read());
        Apply(timer, CountdownMode.Pause);
        Equal(CountdownPhase.Ready, timer.Read().Phase);
    }

    private static void Restart()
    {
        var clock = new ManualTime();
        var timer = new Countdown(clock);
        Apply(timer, CountdownMode.Start);
        clock.Advance(TimeSpan.FromMinutes(2));
        Equal(CountdownPhase.Finished, timer.Read().Phase);
        Apply(timer, CountdownMode.Start, 5);
        Equal(new CountdownSnapshot(TimeSpan.FromMinutes(5), CountdownPhase.Running), timer.Read());
    }

    private static void Refusal()
    {
        var timer = new Countdown(new ManualTime());
        var handler = new CountdownHandler(timer);
        var before = timer.Read();
        Equal(CompanionOutcome.Unsupported, handler.InvokeCore(CountdownChoices.StatusAction, Empty, () => true, default));
        Equal(CompanionOutcome.Unsupported, handler.InvokeCore("example.countdown/unknown", Empty, () => true, default));
        Equal(CompanionOutcome.Refused, handler.InvokeCore(CountdownChoices.TimerAction, Settings("shell"), () => true, default));
        Equal(CompanionOutcome.Refused, handler.InvokeCore(CountdownChoices.TimerAction, Settings("start", "forever"), () => true, default));
        var extra = Settings("start"); extra.Add("unexpected", "value");
        Equal(CompanionOutcome.Refused, handler.InvokeCore(CountdownChoices.TimerAction, extra, () => true, default));
        Equal(CompanionOutcome.Refused, handler.InvokeCore(CountdownChoices.TimerAction, Settings("reset"), () => false, default));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try
        {
            handler.InvokeCore(CountdownChoices.TimerAction, Settings("start"), () => true, canceled.Token);
            throw new InvalidOperationException("Canceled invocation was accepted.");
        }
        catch (OperationCanceledException) { }
        Equal(before, timer.Read());
    }

    private static void FaceFormatting()
    {
        var face = CountdownHandler.CreateFace(new(TimeSpan.FromMilliseconds(1), CountdownPhase.Running));
        Equal("0:01", face.Line1);
        Equal(5, face.GoodForSeconds);
        Equal(CompanionFaceState.Playing, face.State);
        Equal("25:00", CountdownHandler.CreateFace(new(TimeSpan.FromMinutes(25), CountdownPhase.Ready)).Line1);
        Equal("0:00", CountdownHandler.CreateFace(new(TimeSpan.Zero, CountdownPhase.Finished)).Line1);
    }

    private static async Task SessionLifetime()
    {
        var clock = new ManualTime();
        var timer = new Countdown(clock);
        var delay = new ControlledDelay();
        var handler = new CountdownHandler(timer, delay.WaitAsync);
        var faces = new List<CompanionFace>();
        var active = true;
        using var cancellation = new CancellationTokenSource();
        Apply(timer, CountdownMode.Start);
        var loop = handler.RunSessionCoreAsync(CountdownChoices.StatusAction, Empty, () => active,
            face => { faces.Add(face); return true; }, UnexpectedFailure, cancellation.Token);
        try
        {
            var first = await delay.NextAsync();
            Equal("1:00", faces.Single().Line1);
            Equal(TimeSpan.FromSeconds(1), first.Interval);
            clock.Advance(TimeSpan.FromSeconds(13));
            first.Complete();
            var second = await delay.NextAsync();
            Equal("0:47", faces[^1].Line1);
            active = false;
            second.Complete();
            await loop;
            Equal(2, faces.Count);
            clock.Advance(TimeSpan.FromMinutes(2));
            await handler.RunSessionCoreAsync(CountdownChoices.StatusAction, Empty, () => true,
                face => { Equal("0:00", face.Line1); return false; }, UnexpectedFailure, default);
        }
        finally { cancellation.Cancel(); }
    }

    private static async Task SessionCancellation()
    {
        var delay = new ControlledDelay();
        var handler = new CountdownHandler(new Countdown(new ManualTime()), delay.WaitAsync);
        using var cancellation = new CancellationTokenSource();
        var publications = 0;
        var loop = handler.RunSessionCoreAsync(CountdownChoices.StatusAction, Empty, () => true,
            _ => { publications++; return true; }, UnexpectedFailure, cancellation.Token);
        await delay.NextAsync();
        cancellation.Cancel();
        await ExpectCanceled(loop);
        Equal(1, publications);
    }

    private static async Task RejectedPublication()
    {
        var delay = new ControlledDelay();
        var handler = new CountdownHandler(new Countdown(new ManualTime()), delay.WaitAsync);
        var publications = 0;
        await handler.RunSessionCoreAsync(CountdownChoices.StatusAction, Empty, () => true,
            _ => { publications++; return false; }, UnexpectedFailure, default);
        Equal(1, publications);
        Equal(0, delay.Count);
    }

    private static async Task SharedFaces()
    {
        var clock = new ManualTime();
        var timer = new Countdown(clock);
        var handler = new CountdownHandler(timer);
        var start = Settings("start");
        var pause = Settings("pause", "twenty-five-minutes");
        Equal(CompanionOutcome.Done, handler.InvokeCore(CountdownChoices.TimerAction, start, () => true, default));
        clock.Advance(TimeSpan.FromSeconds(20));
        foreach (var settings in new[] { start, pause })
            await handler.RunSessionCoreAsync(CountdownChoices.TimerAction, settings, () => true,
                face => { Equal("0:40", face.Line1); Equal(settings["mode"] == "start" ? "Start · Running" : "Pause · Running", face.Line2); return false; },
                UnexpectedFailure, default);
        Equal("start", start["mode"]);
        Equal("pause", pause["mode"]);
    }

    private static async Task InvalidSession()
    {
        var delay = new ControlledDelay();
        var handler = new CountdownHandler(new Countdown(new ManualTime()), delay.WaitAsync);
        var failures = 0;
        await handler.RunSessionCoreAsync(CountdownChoices.TimerAction, Settings("start", "invalid"), () => true,
            _ => throw new InvalidOperationException("Invalid session published."),
            failure => { Equal(CompanionFailure.NeedsSetup, failure); failures++; }, default);
        Equal(1, failures);
        Equal(0, delay.Count);
    }

    private static async Task RateHeadroom()
    {
        var delay = new ControlledDelay();
        var handler = new CountdownHandler(new Countdown(new ManualTime()), delay.WaitAsync);
        using var cancellation = new CancellationTokenSource();
        var loops = new List<Task>();
        try
        {
            for (var i = 0; i < 64; i++)
                loops.Add(handler.RunSessionCoreAsync(CountdownChoices.StatusAction, Empty, () => true, _ => true, UnexpectedFailure, cancellation.Token));
            ControlledDelay.Pending? last = null;
            for (var i = 0; i < 64; i++) last = await delay.NextAsync();
            Equal(TimeSpan.FromSeconds(2), last!.Interval);
        }
        finally { cancellation.Cancel(); }
        await ExpectCanceled(Task.WhenAll(loops));
    }

    private static void UnexpectedFailure(CompanionFailure _) => throw new InvalidOperationException("Unexpected session failure.");
    private static async Task ExpectCanceled(Task task)
    {
        try { await task; throw new InvalidOperationException("Expected cancellation."); }
        catch (OperationCanceledException) { }
    }
}

internal sealed class ManualTime : TimeProvider
{
    private long _timestamp;
    public DateTimeOffset Utc { get; set; } = DateTimeOffset.UnixEpoch;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => _timestamp;
    public override DateTimeOffset GetUtcNow() => Utc;
    public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
}

internal sealed class ControlledDelay
{
    private readonly Channel<Pending> _requests = Channel.CreateUnbounded<Pending>();
    public int Count { get; private set; }
    public Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        var pending = new Pending(interval);
        Count++;
        if (!_requests.Writer.TryWrite(pending)) throw new InvalidOperationException("Delay queue closed.");
        return pending.Task.WaitAsync(cancellationToken);
    }
    public ValueTask<Pending> NextAsync() => _requests.Reader.ReadAsync();
    internal sealed class Pending(TimeSpan interval)
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan Interval { get; } = interval;
        public Task Task => _completion.Task;
        public void Complete() => _completion.TrySetResult();
    }
}
