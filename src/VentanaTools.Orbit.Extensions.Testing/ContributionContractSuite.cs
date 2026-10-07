// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Diagnostics;
using System.Globalization;

namespace VentanaTools.Orbit.Extensions.Testing;

/// <summary>
/// Checks that an extension's handler keeps the authoring contract (contract §9.3), against a real
/// <see cref="CompanionClient"/> on a <see cref="CompanionTestHost"/> driven by a manual clock.
/// Derive from it in your test project and call <see cref="AssertAllAsync"/> from a test.
/// </summary>
/// <remarks>
/// <para>
/// By default it checks sessions only, so it never runs your real actions. For every contribution
/// and every setting combination up to <see cref="MaxSettingCombinations"/>, it checks that the
/// session is accepted; that a widget publishes a first face within
/// <see cref="InitialFaceTimeout"/> with every default; that every face has a finite lifetime; that
/// nothing is published after the session stops; that the session handler ends within
/// <see cref="CancellationBound"/> after stop; that stopping produces no fault (an
/// <see cref="OperationCanceledException"/> after cancellation is a normal completion); and that an
/// invoke-only contribution never publishes. A <see cref="ContributionRouter"/>'s unmapped
/// contributions are failures.
/// </para>
/// <para>
/// Invocations run only for the contributions listed in <see cref="InvokeContributions"/>, and those
/// checks run the real action: each invocation must settle before the SDK's deadline, and a
/// cancelled invocation must end within the cancellation bound.
/// </para>
/// <para>
/// Time: while a session check waits, the manual clock moves ahead in steps up to the bound, so a
/// handler that waits on <see cref="Session.Time"/> sees the bound pass at once. A check fails only
/// when its bound has also passed in real time, so real work (network, files, a save on stop)
/// counts against the same bound; a failing check therefore takes the bound in real time. While an
/// invocation runs, the manual clock follows real time and never runs ahead of it, so the SDK's
/// invocation deadline is real time too.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// public sealed class ClockContract : ContributionContractSuite
/// {
///     protected override ExtensionManifest Manifest { get; } =
///         ManifestReader.Read(File.ReadAllBytes("extension.json")).Value!;
///     protected override IContributionHandler CreateHandler() =&gt; new TimeWidget();
/// }
///
/// [Fact]
/// public Task KeepsTheContract() =&gt; new ClockContract().AssertAllAsync();
/// </code>
/// </example>
public abstract class ContributionContractSuite
{
    /// <summary>The extension's manifest.</summary>
    protected abstract ExtensionManifest Manifest { get; }

    /// <summary>The contributions whose invocations the suite runs. These run the real action. Empty by default.</summary>
    protected virtual IEnumerable<string> InvokeContributions => [];

    /// <summary>How long a widget may take to publish its first face with every default, on the suite's clock and in real time.</summary>
    protected virtual TimeSpan InitialFaceTimeout => TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a handler may run after cancellation, on the suite's clock and in real time; the
    /// client's <see cref="CompanionClientOptions.HandlerStopTimeout"/> by default.
    /// </summary>
    protected virtual TimeSpan CancellationBound => new CompanionClientOptions().HandlerStopTimeout;

    /// <summary>The most setting combinations checked per contribution, defaults first.</summary>
    protected virtual int MaxSettingCombinations => 64;

    /// <summary>How the manifest is validated.</summary>
    protected virtual ManifestReadOptions ManifestValidation => new();

    /// <summary>Creates the handler under test; called once per run.</summary>
    /// <returns>The handler.</returns>
    protected abstract IContributionHandler CreateHandler();

    /// <summary>The settings the suite invokes a contribution with; null for the defaults.</summary>
    /// <param name="contributionId">A contribution listed in <see cref="InvokeContributions"/>.</param>
    /// <returns>The settings, or null.</returns>
    protected virtual IReadOnlyDictionary<string, string>? InvokeSettings(string contributionId) => null;

    /// <summary>Runs every check and throws if any failed.</summary>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>A task that completes when every check passed.</returns>
    /// <exception cref="ConformanceException">One or more checks failed; it lists every failure.</exception>
    public async Task AssertAllAsync(CancellationToken cancellationToken = default)
    {
        var failures = await CollectFailuresAsync(cancellationToken).ConfigureAwait(false);
        if (failures.Count > 0)
        {
            throw new ConformanceException(failures);
        }
    }

    /// <summary>Runs every check and returns the failures.</summary>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>Every failure, as one line each; empty when the handler keeps the contract.</returns>
    public async Task<IReadOnlyList<string>> CollectFailuresAsync(CancellationToken cancellationToken = default)
    {
        var manifest = Manifest ?? throw new InvalidOperationException("Manifest returned null.");
        var failures = new List<string>(ExtensionConformance.CollectManifestFailures(manifest, ManifestValidation));
        if (failures.Count > 0)
        {
            return failures.AsReadOnly();
        }

        var handler = CreateHandler() ?? throw new InvalidOperationException("CreateHandler returned null.");
        if (handler is ContributionRouter router)
        {
            failures.AddRange(router.FindUnmapped(manifest).Select(id => id + ": no handler is mapped for what it provides."));
        }

        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var run = new SuiteRun(this, time, failures, cancellationToken);
        await using var host = await CompanionTestHost.StartAsync(manifest, handler, new CompanionTestHostOptions { TimeProvider = time },
            cancellationToken).ConfigureAwait(false);
        var invoke = new HashSet<string>(InvokeContributions ?? [], StringComparer.Ordinal);
        foreach (var id in invoke.Where(id => !manifest.Contributions.Any(item => item.Id == id && (item.Provides & Provides.Invoke) != 0)))
        {
            failures.Add(id + ": listed in InvokeContributions, but the manifest has no contribution with this id that provides invoke.");
        }

        foreach (var contribution in manifest.Contributions)
        {
            var first = true;
            foreach (var settings in TestSettings.Combinations(contribution, Math.Max(1, MaxSettingCombinations)))
            {
                await run.CheckSessionAsync(host, contribution, settings, first).ConfigureAwait(false);
                first = false;
            }

            if (invoke.Contains(contribution.Id) && (contribution.Provides & Provides.Invoke) != 0)
            {
                await run.CheckInvocationsAsync(host, contribution, InvokeSettings(contribution.Id)).ConfigureAwait(false);
            }
        }

        return failures.AsReadOnly();
    }

    /// <summary>One run of the checks.</summary>
    private sealed class SuiteRun
    {
        private static readonly TimeSpan RealTimePoll = TimeSpan.FromMilliseconds(10);
        private readonly ContributionContractSuite _suite;
        private readonly ManualTimeProvider _time;
        private readonly List<string> _failures;
        private readonly CancellationToken _cancellationToken;

        public SuiteRun(ContributionContractSuite suite, ManualTimeProvider time, List<string> failures, CancellationToken cancellationToken)
        {
            _suite = suite;
            _time = time;
            _failures = failures;
            _cancellationToken = cancellationToken;
        }

        public async Task CheckSessionAsync(CompanionTestHost host, Contribution contribution, IReadOnlyDictionary<string, string> settings,
            bool defaults)
        {
            var label = Label(contribution.Id, settings);
            var faultsBefore = host.Faults.Count;
            var session = await host.StartSessionAsync(contribution.Id, settings, _cancellationToken).ConfigureAwait(false);
            if (session.RefusedCode is { } refused)
            {
                _failures.Add(label + ": the session was refused (" + refused.Value + ").");
                return;
            }

            var face = (contribution.Provides & Provides.Face) != 0;

            // Only a missing first face with every default is a failure, so only that wait keeps a real-time floor.
            await WaitUntilAsync(() => (face && session.FaceCount > 0) || host.Observer.SessionEnded(session.SessionId),
                _suite.InitialFaceTimeout, realTime: face && defaults).ConfigureAwait(false);
            if (face && defaults && session.FaceCount == 0)
            {
                _failures.Add(label + ": no first face within " + Seconds(_suite.InitialFaceTimeout)
                    + " with every default (host and real time); publish a first face before slow work, or raise InitialFaceTimeout.");
            }

            await host.Client.FlushEventsAsync().ConfigureAwait(false);
            var withoutFace = host.Observer.PublishedWithoutFace(session.SessionId);
            foreach (var fault in host.Faults.Skip(faultsBefore).Where(item => item.SessionId == session.SessionId && item.RequestId is null))
            {
                if (!withoutFace)
                {
                    _failures.Add(label + ": RunSessionAsync faulted (" + Describe(fault) + ").");
                }
            }

            var faultsAtStop = host.Faults.Count;
            await host.StopSessionAsync(session).ConfigureAwait(false);
            var ended = await WaitUntilAsync(() => host.Observer.SessionEnded(session.SessionId), _suite.CancellationBound, realTime: true)
                .ConfigureAwait(false);
            if (!ended)
            {
                _failures.Add(label + ": RunSessionAsync did not end within " + Seconds(_suite.CancellationBound)
                    + " after the session stopped (host and real time); pass the cancellation token to every await and keep work after cancellation short.");
            }

            await Settle().ConfigureAwait(false);
            await host.Client.FlushEventsAsync().ConfigureAwait(false);
            foreach (var fault in host.Faults.Skip(faultsAtStop).Where(item => item.SessionId == session.SessionId
                && item.RequestId is null && item.Kind != HandlerFault.IgnoredCancellation))
            {
                _failures.Add(label + ": stopping the session raised a fault (" + Describe(fault) + ").");
            }

            if (host.Observer.PublishedAfterEnd(session.SessionId))
            {
                _failures.Add(label + ": published after the session stopped.");
            }

            if (withoutFace || (!face && session.FaceCount > 0))
            {
                _failures.Add(label + ": the contribution does not provide face, but its handler published one.");
            }

            foreach (var received in session.AllFaces.Where(item => item.Face.GoodFor < TimeSpan.FromSeconds(1) || item.Face.GoodFor > TimeSpan.FromDays(1)))
            {
                _failures.Add(label + ": a face has no finite lifetime (" + Seconds(received.Face.GoodFor) + ").");
            }
        }

        public async Task CheckInvocationsAsync(CompanionTestHost host, Contribution contribution, IReadOnlyDictionary<string, string>? settings)
        {
            var label = Label(contribution.Id, settings ?? new Dictionary<string, string>());
            var session = await host.StartSessionAsync(contribution.Id, settings, _cancellationToken).ConfigureAwait(false);
            if (session.RefusedCode is { } refused)
            {
                _failures.Add(label + ": the session for the invocation checks was refused (" + refused.Value + ").");
                return;
            }

            var deadline = TimeSpan.FromMilliseconds(Math.Max(1_000, host.Limits.ForCompanion().InvokeTimeoutMs - 3_000));
            var faultsBefore = host.Faults.Count;
            var invocation = host.StartInvoke(session);
            await FollowRealTimeUntilAsync(() => invocation.Result.IsCompleted, deadline + TimeSpan.FromMilliseconds(100)).ConfigureAwait(false);
            if (host.Observer.DeadlineReached(invocation.RequestId) || !invocation.Result.IsCompleted)
            {
                _failures.Add(label + ": the invocation did not settle before the SDK's deadline of " + Seconds(deadline) + " (real time).");
            }

            await WaitUntilAsync(() => host.Observer.InvocationEnded(invocation.RequestId), _suite.CancellationBound, realTime: true)
                .ConfigureAwait(false);
            await host.Client.FlushEventsAsync().ConfigureAwait(false);
            foreach (var fault in host.Faults.Skip(faultsBefore).Where(item => item.RequestId == invocation.RequestId && item.Kind == HandlerFault.InvalidResult))
            {
                _failures.Add(label + ": InvokeAsync returned an invalid result (" + Describe(fault) + ").");
            }

            var cancelled = host.StartInvoke(session);
            await host.CancelAsync(cancelled).ConfigureAwait(false);
            if (!await WaitUntilAsync(() => host.Observer.InvocationEnded(cancelled.RequestId), _suite.CancellationBound, realTime: true)
                .ConfigureAwait(false))
            {
                _failures.Add(label + ": a cancelled invocation did not end within " + Seconds(_suite.CancellationBound) + " (host and real time).");
            }

            await host.StopSessionAsync(session).ConfigureAwait(false);
            await WaitUntilAsync(() => host.Observer.SessionEnded(session.SessionId), _suite.CancellationBound, realTime: true).ConfigureAwait(false);
        }

        /// <summary>
        /// Waits for <paramref name="condition"/>, advancing the manual clock in small steps up to
        /// <paramref name="bound"/>, with a short real pause per step so asynchronous work can run.
        /// With <paramref name="realTime"/>, it then keeps waiting, without moving the clock, until
        /// <paramref name="bound"/> has also passed in real time since the wait began.
        /// </summary>
        private async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan bound, bool realTime = false)
        {
            var started = Stopwatch.GetTimestamp();
            var step = TimeSpan.FromMilliseconds(Math.Clamp(bound.TotalMilliseconds / 50, 10, 250));
            var advanced = TimeSpan.Zero;
            while (true)
            {
                if (await PauseAsync(condition).ConfigureAwait(false))
                {
                    return true;
                }

                if (advanced < bound)
                {
                    _time.Advance(step);
                    advanced += step;
                    continue;
                }

                if (!realTime || Stopwatch.GetElapsedTime(started) >= bound)
                {
                    return false;
                }

                await Task.Delay(RealTimePoll, _cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Waits for <paramref name="condition"/> up to <paramref name="bound"/> in real time, moving
        /// the manual clock with real time and never ahead of it, so the client's timers fire when
        /// they would against a real host.
        /// </summary>
        private async Task<bool> FollowRealTimeUntilAsync(Func<bool> condition, TimeSpan bound)
        {
            var started = Stopwatch.GetTimestamp();
            var advanced = TimeSpan.Zero;
            while (true)
            {
                if (await PauseAsync(condition).ConfigureAwait(false))
                {
                    return true;
                }

                var elapsed = Stopwatch.GetElapsedTime(started);
                if (advanced >= bound)
                {
                    return false;
                }

                var target = elapsed < bound ? elapsed : bound;
                if (target > advanced)
                {
                    _time.Advance(target - advanced);
                    advanced = target;
                }

                await Task.Delay(RealTimePoll, _cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task Settle() => await PauseAsync(static () => false).ConfigureAwait(false);

        private async Task<bool> PauseAsync(Func<bool> condition)
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromMilliseconds(3))
            {
                _cancellationToken.ThrowIfCancellationRequested();
                if (condition())
                {
                    return true;
                }

                await Task.Yield();
            }

            return condition();
        }

        private static string Label(string contributionId, IReadOnlyDictionary<string, string> settings) => settings.Count == 0
            ? contributionId
            : contributionId + " {" + string.Join(", ", settings.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => pair.Key + "=" + pair.Value)) + "}";

        private static string Seconds(TimeSpan value) => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture) + " s";

        private static string Describe(HandlerFaultedEventArgs fault) => fault.Exception is { } error
            ? fault.Kind + ": " + error.GetType().FullName + ": " + error.Message
            : fault.Kind.ToString();
    }
}

/// <summary>
/// Conformance entry points (contract §9.3), with the Ventana test-kit names: manifest checks and
/// the authoring contract of <see cref="ContributionContractSuite"/> with its defaults.
/// </summary>
public static class ExtensionConformance
{
    /// <summary>Every error a host would refuse <paramref name="manifest"/> for, formatted <c>code path: message</c>.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="options">How to validate; null for the third-party rules.</param>
    /// <returns>The failures; empty when the manifest is valid.</returns>
    public static IReadOnlyList<string> CollectManifestFailures(ExtensionManifest manifest, ManifestReadOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return Errors(ManifestReader.Validate(manifest, options));
    }

    /// <summary>Throws when a host would refuse <paramref name="manifest"/>.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="options">How to validate; null for the third-party rules.</param>
    /// <exception cref="ConformanceException">The manifest is invalid; it lists every error.</exception>
    public static void AssertManifestValid(ExtensionManifest manifest, ManifestReadOptions? options = null) =>
        ThrowIfAny(CollectManifestFailures(manifest, options));

    /// <summary>Every error a host would refuse the manifest file for, formatted <c>code path: message</c>.</summary>
    /// <param name="manifestPath">The path of <c>extension.json</c>.</param>
    /// <param name="options">How to validate; null for the third-party rules.</param>
    /// <returns>The failures; empty when the file is valid.</returns>
    /// <exception cref="IOException">The file cannot be read.</exception>
    public static IReadOnlyList<string> CollectManifestFileFailures(string manifestPath, ManifestReadOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(manifestPath);
        return Errors(ManifestReader.Read(ReadBounded(manifestPath, ManifestReader.MaxBytes), options));
    }

    /// <summary>Throws when a host would refuse the manifest file.</summary>
    /// <param name="manifestPath">The path of <c>extension.json</c>.</param>
    /// <param name="options">How to validate; null for the third-party rules.</param>
    /// <exception cref="ConformanceException">The file is invalid; it lists every error.</exception>
    public static void AssertManifestFileValid(string manifestPath, ManifestReadOptions? options = null) =>
        ThrowIfAny(CollectManifestFileFailures(manifestPath, options));

    /// <summary>The failures of the <see cref="ContributionContractSuite"/> checks with their defaults (sessions only).</summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="handler">The handler under test.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>The failures; empty when the handler keeps the contract.</returns>
    public static Task<IReadOnlyList<string>> CollectContractFailuresAsync(ExtensionManifest manifest, IContributionHandler handler,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(handler);
        return new DefaultSuite(manifest, handler).CollectFailuresAsync(cancellationToken);
    }

    /// <summary>Throws when the handler breaks the authoring contract (the suite's default checks).</summary>
    /// <param name="manifest">The manifest.</param>
    /// <param name="handler">The handler under test.</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <returns>A task that completes when every check passed.</returns>
    /// <exception cref="ConformanceException">One or more checks failed; it lists every failure.</exception>
    public static async Task AssertAuthoringContractAsync(ExtensionManifest manifest, IContributionHandler handler,
        CancellationToken cancellationToken = default) =>
        ThrowIfAny(await CollectContractFailuresAsync(manifest, handler, cancellationToken).ConfigureAwait(false));

    private static string[] Errors(ReadResult<ExtensionManifest> read) =>
        read.Diagnostics.Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(diagnostic => diagnostic.ToString()).ToArray();

    private static void ThrowIfAny(IReadOnlyList<string> failures)
    {
        if (failures.Count > 0)
        {
            throw new ConformanceException(failures);
        }
    }

    private static byte[] ReadBounded(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[maxBytes + 1];
        var total = 0;
        int read;
        while (total < buffer.Length && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
        {
            total += read;
        }

        return buffer.AsSpan(0, total).ToArray();
    }

    private sealed class DefaultSuite : ContributionContractSuite
    {
        private readonly ExtensionManifest _manifest;
        private readonly IContributionHandler _handler;

        public DefaultSuite(ExtensionManifest manifest, IContributionHandler handler)
        {
            _manifest = manifest;
            _handler = handler;
        }

        protected override ExtensionManifest Manifest => _manifest;

        protected override IContributionHandler CreateHandler() => _handler;
    }
}

/// <summary>A conformance check failed. <see cref="Failures"/> lists every failure.</summary>
public sealed class ConformanceException : InvalidOperationException
{
    /// <summary>Creates the exception with no failures listed.</summary>
    public ConformanceException()
        : this("The extension does not conform.")
    {
    }

    /// <summary>Creates the exception with one failure.</summary>
    /// <param name="message">The failure.</param>
    public ConformanceException(string message)
        : base(message)
    {
        Failures = [message];
    }

    /// <summary>Creates the exception with one failure and its cause.</summary>
    /// <param name="message">The failure.</param>
    /// <param name="innerException">The cause.</param>
    public ConformanceException(string message, Exception innerException)
        : base(message, innerException)
    {
        Failures = [message];
    }

    /// <summary>Creates the exception for several failures.</summary>
    /// <param name="failures">Every failure, one line each.</param>
    public ConformanceException(IEnumerable<string> failures)
        : this(failures?.ToArray() ?? throw new ArgumentNullException(nameof(failures)), true)
    {
    }

    private ConformanceException(string[] failures, bool list)
        : base((list ? failures.Length : 0) + " conformance failure(s):" + Environment.NewLine + string.Join(Environment.NewLine, failures))
    {
        Failures = failures.AsReadOnly();
    }

    /// <summary>Every failure, one line each.</summary>
    public IReadOnlyList<string> Failures { get; }
}
