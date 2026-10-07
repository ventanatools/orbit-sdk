// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using VentanaTools.Orbit.Extensions.Wire;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Testing.Tests;

public sealed class RecordingSessionTests
{
    [Fact]
    public void FromManifestDerivesProvidesFillsDefaultsAndValidatesSettings()
    {
        var manifest = Manifests.Kit();
        using var recording = TestSessions.FromManifest(manifest, Manifests.Widget);
        Assert.Equal(Provides.Face, recording.Session.Provides);
        Assert.Equal("small", recording.Session.Settings["size"]);
        Assert.Equal("en-US", recording.Session.UiLanguage);
        using var large = TestSessions.FromManifest(manifest, Manifests.Widget, new Dictionary<string, string> { ["size"] = "large" });
        Assert.Equal("large", large.Session.Settings["size"]);
        Assert.Throws<ArgumentException>(() => TestSessions.FromManifest(manifest, Manifests.Widget, new Dictionary<string, string> { ["size"] = "huge" }));
        Assert.Throws<ArgumentException>(() => TestSessions.FromManifest(manifest, Manifests.Widget, new Dictionary<string, string> { ["color"] = "red" }));
        Assert.Throws<ArgumentException>(() => TestSessions.FromManifest(manifest, "example.kit/missing"));
        using var created = TestSessions.Create("example.any/thing", Provides.Invoke, uiLanguage: "de-DE");
        Assert.Equal(Provides.Invoke, created.Session.Provides);
        Assert.Empty(created.Session.Settings);
        Assert.Equal("de-DE", created.Session.UiCulture.Name);
    }

    [Fact]
    public void ItRecordsCleanedPublicationsAndWhatHappensAfterStop()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var recording = TestSessions.FromManifest(Manifests.Kit(), Manifests.Widget, time: time);
        var session = recording.Session;
        Assert.Equal(PublishResult.Accepted, session.SetFace(new Face { Line1 = "A\nB‮", GoodFor = TimeSpan.FromSeconds(4.2) }));
        Assert.Equal(new Face { Line1 = "A B", GoodFor = TimeSpan.FromSeconds(5) }, recording.LastFace);
        time.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(PublishResult.Accepted, session.Fail(Failure.NeedsSetup));
        Assert.Null(recording.LastFace);
        Assert.Equal(PublishResult.Accepted, session.ClearFace());
        recording.Stop();
        Assert.True(recording.Cancellation.IsCancellationRequested);
        Assert.False(session.IsActive);
        Assert.Equal(PublishResult.SessionEnded, session.SetFace(new Face { Line1 = "late", GoodFor = TimeSpan.FromSeconds(1) }));
        var publications = recording.Publications;
        Assert.Equal([PublicationKind.SetFace, PublicationKind.Fail, PublicationKind.ClearFace, PublicationKind.AfterStop],
            publications.Select(publication => publication.Kind));
        Assert.Equal(TimeSpan.Zero, publications[0].At);
        Assert.Equal(TimeSpan.FromSeconds(3), publications[1].At);
        Assert.Equal(Failure.NeedsSetup, publications[1].Failure);
        Assert.Equal("late", ((TextLine)publications[3].Face!.Line1!).Text);
        Assert.Null(recording.LastFace);
    }

    [Fact]
    public async Task RunAsyncTreatsCancellationAfterStopAsNormalAndRethrowsOtherExceptions()
    {
        using var recording = TestSessions.FromManifest(Manifests.Kit(), Manifests.Widget);
        var run = recording.RunAsync(new GoodWidget());
        Assert.Equal("small", ((TextLine)(await recording.WaitForFaceAsync(TimeSpan.FromSeconds(10))).Line1!).Text);
        recording.Stop();
        await run.WaitAsync(TimeSpan.FromSeconds(10));

        using var failing = TestSessions.Create("example.kit/widget", Provides.Face);
        var thrower = new ScriptedHandler { OnSession = (_, _) => throw new FormatException("author") };
        await Assert.ThrowsAsync<FormatException>(() => failing.RunAsync(thrower));

        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var stuck = TestSessions.Create("example.kit/widget", Provides.Face, time: time);
        var forever = new ScriptedHandler { OnSession = (_, _) => Task.Delay(Timeout.Infinite, CancellationToken.None) };
        var waiting = stuck.RunAsync(forever, TimeSpan.FromSeconds(2));
        await Task.Delay(20);
        time.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<TimeoutException>(() => waiting);
    }

    [Fact]
    public async Task TheDocumentedPatternSeesTheFirstFaceEveryTime()
    {
        // RunAsync, wait for the face, assert, Stop, await: the pattern of the XML example, the README and the docs site.
        for (var attempt = 0; attempt < 100; attempt++)
        {
            using var recording = TestSessions.FromManifest(Manifests.Kit(), Manifests.Widget);
            var run = recording.RunAsync(new GoodWidget());
            var face = await recording.WaitForFaceAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("small", ((TextLine)face.Line1!).Text);
            recording.Stop();
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.DoesNotContain(recording.Publications, publication => publication.Kind == PublicationKind.AfterStop);
        }
    }

    [Fact]
    public async Task AHandlerRunsToItsFirstAwaitBeforeRunAsyncReturns()
    {
        using var recording = TestSessions.FromManifest(Manifests.Kit(), Manifests.Widget);
        var run = recording.RunAsync(new GoodWidget());

        // GoodWidget publishes before it first awaits, so a Stop right away comes after that face, never before it.
        Assert.Equal("small", ((TextLine)recording.LastFace!.Line1!).Text);
        recording.Stop();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal([PublicationKind.SetFace], recording.Publications.Select(publication => publication.Kind));
    }

    [Fact]
    public async Task WaitForFaceReturnsFacesInOrderAndItsTimeoutIsRealTime()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        using var recording = TestSessions.FromManifest(Manifests.Kit(), Manifests.Widget, time: time);
        var session = recording.Session;
        session.SetFace(new Face { Line1 = "one", GoodFor = TimeSpan.FromSeconds(5) });
        session.ClearFace();
        session.SetFace(new Face { Line1 = "two", GoodFor = TimeSpan.FromSeconds(5) });
        Assert.Equal("one", ((TextLine)(await recording.WaitForFaceAsync(TimeSpan.FromSeconds(1))).Line1!).Text);
        Assert.Equal("two", ((TextLine)(await recording.WaitForFaceAsync(TimeSpan.FromSeconds(1))).Line1!).Text);
        var third = recording.WaitForFaceAsync(TimeSpan.FromSeconds(10));
        Assert.False(third.IsCompleted);
        session.SetFace(new Face { Line1 = "three", GoodFor = TimeSpan.FromSeconds(5) });
        Assert.Equal("three", ((TextLine)(await third).Line1!).Text);

        // The session's manual clock never moves, yet the timeout ends the wait.
        await Assert.ThrowsAsync<TimeoutException>(() => recording.WaitForFaceAsync(TimeSpan.FromMilliseconds(50)));
        await recording.WaitForPublicationsAsync(4, TimeSpan.FromSeconds(1));
        var fifth = recording.WaitForPublicationsAsync(5, TimeSpan.FromSeconds(10));
        Assert.False(fifth.IsCompleted);
        recording.Stop();
        session.SetFace(new Face { Line1 = "late", GoodFor = TimeSpan.FromSeconds(5) });
        await fifth;

        // A face after stop is a publication, but no face is shown.
        await Assert.ThrowsAsync<TimeoutException>(() => recording.WaitForFaceAsync(TimeSpan.FromMilliseconds(50)));
        await Assert.ThrowsAsync<TimeoutException>(() => recording.WaitForPublicationsAsync(6, TimeSpan.FromMilliseconds(50)));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recording.WaitForFaceAsync(TimeSpan.FromSeconds(10), cancelled.Token));
    }

    [Fact]
    public async Task InvocationsCanBeCreatedForAnAction()
    {
        using var recording = TestSessions.FromManifest(Manifests.Kit(), Manifests.Action);
        var invocation = TestInvocations.Create(recording);
        Assert.Same(recording.Session, invocation.Session);
        Assert.Matches("^[0-9a-f]{32}$", invocation.RequestId);
        Assert.Equal("chosen", TestInvocations.Create(recording, "chosen").RequestId);
        var handler = new ScriptedHandler();
        Assert.Equal(InvokeResult.Done, await handler.InvokeAsync(invocation, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => recording.Session.SetFace(new Face { GoodFor = TimeSpan.FromSeconds(1) }));
    }
}

public sealed class CompanionTestHostTests
{
    [Fact]
    public async Task ItRunsTheRealClientStartsSessionsAndReceivesCleanedFaces()
    {
        await using var host = await CompanionTestHost.StartAsync(Manifests.Kit(), new GoodWidget());
        Assert.Contains(host.Statuses, status => status.State == ConnectionState.Connected);
        var session = await host.StartSessionAsync(Manifests.Widget, new Dictionary<string, string> { ["size"] = "large" });
        Assert.Null(session.RefusedCode);
        Assert.Equal(Manifests.Widget, session.ContributionId);
        var face = await host.WaitForFaceAsync(session, TimeSpan.FromSeconds(10));
        Assert.Equal(new Face { Line1 = "large", GoodFor = TimeSpan.FromSeconds(60) }, face.Face);
        await host.StopSessionAsync(session);
        Assert.Empty(host.Faults);
    }

    [Fact]
    public async Task ItInvokesCancelsAndAnswersLikeAHost()
    {
        var gate = new TaskCompletionSource();
        var handler = new ScriptedHandler
        {
            OnInvoke = async (invocation, token) =>
            {
                if (invocation.Session.ContributionId == Manifests.Both)
                {
                    await Task.Delay(Timeout.Infinite, token);
                }

                await gate.Task;
                return InvokeResult.Failed(Failure.Network);
            },
        };
        var manifest = Manifests.Kit(Manifests.Action1(), Manifests.Action1(Manifests.Both, Provides.Invoke | Provides.Face));
        await using var host = await CompanionTestHost.StartAsync(manifest, handler);
        var action = await host.StartSessionAsync(Manifests.Action);
        var pending = host.StartInvoke(action);
        Assert.False(pending.Result.IsCompleted);
        gate.SetResult();
        var outcome = await pending.Result.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((TestInvokeOutcomeKind.Failed, (Failure?)Failure.Network), (outcome.Kind, outcome.Failure));

        var both = await host.StartSessionAsync(Manifests.Both);
        var cancelled = host.StartInvoke(both);
        await Manifests.WaitForAsync(() => handler.Invocations == 2, "invoked");
        await host.CancelAsync(cancelled);
        Assert.Equal(TestInvokeOutcomeKind.Cancelled, (await cancelled.Result).Kind);
        using var source = new CancellationTokenSource();
        var viaToken = host.InvokeAsync(both, source.Token);
        await Manifests.WaitForAsync(() => handler.Invocations == 3, "invoked again");
        await source.CancelAsync();
        Assert.Equal(TestInvokeOutcomeKind.Cancelled, (await viaToken.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
    }

    [Fact]
    public async Task TheSdkDeadlineAnswersBeforeTheHostTimesOut()
    {
        var time = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var never = new TaskCompletionSource<InvokeResult>();
        var handler = new ScriptedHandler { OnInvoke = (_, _) => never.Task };
        try
        {
            await using var host = await CompanionTestHost.StartAsync(Manifests.Kit(), handler, new CompanionTestHostOptions { TimeProvider = time });
            var session = await host.StartSessionAsync(Manifests.Action);
            var invocation = host.StartInvoke(session);
            await Manifests.WaitForAsync(() => handler.Invocations == 1, "invoked");
            time.Advance(TimeSpan.FromSeconds(12));
            Assert.Equal(TestInvokeOutcomeKind.Failed, (await invocation.Result.WaitAsync(TimeSpan.FromSeconds(10))).Kind);
        }
        finally
        {
            never.SetResult(InvokeResult.Done);
        }
    }

    [Fact]
    public async Task ARefusedSessionCarriesItsCodeAndTheTranscriptRecordsEveryFrame()
    {
        await using var host = await CompanionTestHost.StartAsync(Manifests.Kit(), new GoodWidget());
        var refused = await host.StartSessionAsync(Manifests.Widget, new Dictionary<string, string> { ["size"] = "huge" });
        Assert.Equal(ReasonCode.SessionSettingsInvalid, refused.RefusedCode);
        await Assert.ThrowsAsync<ArgumentException>(() => host.StartSessionAsync("example.kit/missing"));
        var entries = host.Transcript.Entries;
        Assert.Equal(["hello", "challenge", "authenticate", "ready", "startSession", "sessionRefused"], entries.Select(entry => entry.Type));
        Assert.Equal([Sender.Companion, Sender.Host, Sender.Companion, Sender.Host, Sender.Host, Sender.Companion], entries.Select(entry => entry.From));
        Assert.Equal(refused.SessionId, entries[^1].SessionId);
        var lines = host.Transcript.ToJsonLines().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(entries.Count, lines.Length);
        using var last = JsonDocument.Parse(lines[^1]);
        Assert.Equal("sessionRefused", last.RootElement.GetProperty("type").GetString());
        Assert.Equal("Companion", last.RootElement.GetProperty("from").GetString());
        Assert.Equal(refused.SessionId, last.RootElement.GetProperty("sessionId").GetString());
        Assert.True(last.RootElement.TryGetProperty("at", out _));
    }

    [Fact]
    public async Task TheHostIdDefaultsToTheFirstActiveRegistryHostInTheManifest()
    {
        var active = HostRegistry.Known.First(host => host.Status == HostStatus.Active).Id;
        var manifest = new ExtensionManifest
        {
            Id = "example.kit",
            Name = "Kit",
            Description = "Two hosts.",
            Version = "1.0.0",
            Hosts = [Manifests.Host, active],
            Contributions = [Manifests.Widget1()],
        };
        await using (var host = await CompanionTestHost.StartAsync(manifest, new GoodWidget()))
        {
            Assert.Equal(active, host.Statuses.Single(status => status.State == ConnectionState.Connected).Host!.Id);
        }

        await using (var host = await CompanionTestHost.StartAsync(Manifests.Kit(), new GoodWidget()))
        {
            Assert.Equal(Manifests.Host, host.Statuses.Single(status => status.State == ConnectionState.Connected).Host!.Id);
        }

        await using (var host = await CompanionTestHost.StartAsync(manifest, new GoodWidget(), new CompanionTestHostOptions { HostId = Manifests.Host }))
        {
            Assert.Equal(Manifests.Host, host.Statuses.Single(status => status.State == ConnectionState.Connected).Host!.Id);
        }
    }

    [Fact]
    public async Task CapabilitiesLanguageAndLimitsReachTheSession()
    {
        Session? seen = null;
        var handler = new ScriptedHandler { OnSession = (session, _) => { seen = session; return Task.CompletedTask; } };
        await using var host = await CompanionTestHost.StartAsync(Manifests.Kit(), handler, new CompanionTestHostOptions
        {
            Capabilities = [Capabilities.TestEcho, "future.feature"],
            UiLanguage = "fr-FR",
        });
        await host.StartSessionAsync(Manifests.Action);
        await Manifests.WaitForAsync(() => seen is not null, "session");
        Assert.True(seen!.Supports(Capabilities.TestEcho));
        Assert.False(seen.Supports("future.feature"));
        Assert.Equal("fr-FR", seen.UiLanguage);
        await Assert.ThrowsAsync<ArgumentException>(() => CompanionTestHost.StartAsync(Manifests.Kit(), handler,
            new CompanionTestHostOptions { UiLanguage = "not a tag" }));
    }

    [Fact]
    public async Task DisconnectingWithACodeShowsInTheStatusesAndFaultsAreRecorded()
    {
        var handler = new ScriptedHandler { OnSession = (_, _) => throw new InvalidOperationException("boom") };
        await using var host = await CompanionTestHost.StartAsync(Manifests.Kit(), handler);
        await host.StartSessionAsync(Manifests.Widget);
        await Manifests.WaitForAsync(() => host.Faults.Count == 1, "fault");
        Assert.Equal(HandlerFault.Exception, host.Faults[0].Kind);
        Assert.Equal("boom", host.Faults[0].Exception!.Message);
        await host.DisconnectAsync(ReasonCode.HostTurnedOff);
        await Manifests.WaitForAsync(() => host.Statuses.Any(status => status.State == ConnectionState.Waiting), "waiting");
        Assert.Equal(ReasonCode.HostTurnedOff, host.Statuses.Last(status => status.State == ConnectionState.Waiting).Reason);
    }
}

public sealed class ContractSuiteTests
{
    [Fact]
    public async Task AWellBehavedExtensionPasses()
    {
        var manifest = Manifests.Kit();
        Assert.Empty(await ExtensionConformance.CollectContractFailuresAsync(manifest, new GoodWidget()));
        await ExtensionConformance.AssertAuthoringContractAsync(manifest, new GoodWidget());
        var router = new ContributionRouter().Map(Manifests.Widget, new GoodWidget()).MapInvoke(Manifests.Action, (_, _) => Task.FromResult(InvokeResult.Done));
        Assert.Empty(await new Suite(manifest, () => router, [Manifests.Action]).CollectFailuresAsync());
    }

    [Fact]
    public async Task ItReportsAHandlerThatPublishesAfterStop()
    {
        var handler = new ScriptedHandler
        {
            OnSession = async (session, token) =>
            {
                session.SetFace(new Face { Line1 = "up", GoodFor = TimeSpan.FromSeconds(10) });
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                finally
                {
                    session.SetFace(new Face { Line1 = "bye", GoodFor = TimeSpan.FromSeconds(10) });
                }
            },
        };
        var failures = await ExtensionConformance.CollectContractFailuresAsync(Manifests.Kit(Manifests.Widget1()), handler);
        Assert.Contains(failures, failure => failure.Contains("published after the session stopped", StringComparison.Ordinal));
        Assert.DoesNotContain(failures, failure => failure.Contains("raised a fault", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ItReportsAHandlerThatIgnoresCancellation()
    {
        var release = new TaskCompletionSource();
        try
        {
            var handler = new ScriptedHandler
            {
                OnSession = async (session, _) =>
                {
                    session.SetFace(new Face { Line1 = "up", GoodFor = TimeSpan.FromSeconds(10) });
                    await release.Task;
                },
            };
            var failures = await new Suite(Manifests.Kit(Manifests.Widget1()), () => handler, maxCombinations: 1).CollectFailuresAsync();
            Assert.Contains(failures, failure => failure.Contains("did not end within 5 s", StringComparison.Ordinal));
            Assert.DoesNotContain(failures, failure => failure.Contains("raised a fault", StringComparison.Ordinal));
        }
        finally
        {
            release.SetResult();
        }
    }

    [Fact]
    public async Task ItReportsAHandlerThatFaultsOnStop()
    {
        var handler = new ScriptedHandler
        {
            OnSession = async (session, token) =>
            {
                session.SetFace(new Face { Line1 = "up", GoodFor = TimeSpan.FromSeconds(10) });
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                catch (OperationCanceledException)
                {
                    throw new InvalidOperationException("cleanup failed");
                }
            },
        };
        var failures = await ExtensionConformance.CollectContractFailuresAsync(Manifests.Kit(Manifests.Widget1()), handler);
        Assert.Contains(failures, failure => failure.Contains("stopping the session raised a fault", StringComparison.Ordinal)
            && failure.Contains("cleanup failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ItReportsAnInvokeOnlyContributionThatPublishes()
    {
        var handler = new ScriptedHandler
        {
            OnSession = (session, _) =>
            {
                session.SetFace(new Face { Line1 = "no", GoodFor = TimeSpan.FromSeconds(10) });
                return Task.CompletedTask;
            },
        };
        var failures = await ExtensionConformance.CollectContractFailuresAsync(Manifests.Kit(Manifests.Action1()), handler);
        Assert.Contains(failures, failure => failure.Contains("does not provide face, but its handler published one", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ItReportsAWidgetWithoutAFirstFace()
    {
        var handler = new ScriptedHandler { OnSession = (_, token) => Task.Delay(Timeout.Infinite, token) };
        var failures = await ExtensionConformance.CollectContractFailuresAsync(Manifests.Kit(Manifests.Widget1()), handler);
        Assert.Contains(failures, failure => failure.Contains("no first face within 2 s with every default", StringComparison.Ordinal));
        Assert.Single(failures);
    }

    [Fact]
    public async Task ItRunsNoInvocationUnlessListedAndChecksListedOnes()
    {
        var actionOnly = Manifests.Kit(Manifests.Action1());
        var quiet = new ScriptedHandler();
        Assert.Empty(await ExtensionConformance.CollectContractFailuresAsync(actionOnly, quiet));
        Assert.Equal(0, quiet.Invocations);

        var listed = new ScriptedHandler();
        Assert.Empty(await new Suite(actionOnly, () => listed, [Manifests.Action]).CollectFailuresAsync());
        Assert.InRange(listed.Invocations, 1, 2);

        var slow = new ScriptedHandler
        {
            OnInvoke = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return InvokeResult.Done;
            },
        };
        var failures = await new Suite(actionOnly, () => slow, [Manifests.Action]).CollectFailuresAsync();
        Assert.Contains(failures, failure => failure.Contains("did not settle before the SDK's deadline of 12 s", StringComparison.Ordinal));

        var notAnAction = await new Suite(Manifests.Kit(), () => new GoodWidget(), [Manifests.Widget]).CollectFailuresAsync();
        Assert.Contains(notAnAction, failure => failure.Contains("listed in InvokeContributions", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ItReportsUnmappedContributionsAndInvalidManifests()
    {
        var router = new ContributionRouter().Map(Manifests.Widget, new GoodWidget());
        var failures = await new Suite(Manifests.Kit(), () => router).CollectFailuresAsync();
        Assert.Contains(Manifests.Action + ": no handler is mapped for what it provides.", failures);

        var invalid = Manifests.Kit(new Contribution { Id = "other.root/x", Name = "X", Description = "X.", Glyph = "A", Provides = Provides.Invoke });
        var invalidFailures = await ExtensionConformance.CollectContractFailuresAsync(invalid, new GoodWidget());
        Assert.Contains(invalidFailures, failure => failure.StartsWith("id.outside-namespace", StringComparison.Ordinal));
        var thrown = await Assert.ThrowsAsync<ConformanceException>(() => ExtensionConformance.AssertAuthoringContractAsync(invalid, new GoodWidget()));
        Assert.IsAssignableFrom<InvalidOperationException>(thrown);
        Assert.Equal(invalidFailures, thrown.Failures);
        Assert.Contains(invalidFailures[0], thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ManifestChecksReportDiagnosticsForObjectsAndFiles()
    {
        var invalid = Manifests.Kit(new Contribution { Id = "example.kit/x", Name = "X", Description = "X.", Glyph = "A", Provides = Provides.Invoke });
        Assert.Contains(ExtensionConformance.CollectManifestFailures(invalid), failure => failure.StartsWith("chrome.glyph-invalid", StringComparison.Ordinal));
        Assert.Throws<ConformanceException>(() => ExtensionConformance.AssertManifestValid(invalid));
        ExtensionConformance.AssertManifestValid(Manifests.Kit());

        var path = Path.Combine(Path.GetTempPath(), "ventana-kit-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllBytes(path, ManifestWriter.Write(Manifests.Kit()));
            Assert.Empty(ExtensionConformance.CollectManifestFileFailures(path));
            ExtensionConformance.AssertManifestFileValid(path);
            File.WriteAllText(path, "{ \"schemaVersion\": 3, }");
            Assert.Contains(ExtensionConformance.CollectManifestFileFailures(path), failure => failure.StartsWith("json.syntax", StringComparison.Ordinal));
            Assert.Throws<ConformanceException>(() => ExtensionConformance.AssertManifestFileValid(path));
        }
        finally
        {
            File.Delete(path);
        }

        Assert.Throws<FileNotFoundException>(() => ExtensionConformance.CollectManifestFileFailures(path));
    }

    [Fact]
    public void ConformanceExceptionListsEveryFailure()
    {
        var error = new ConformanceException(["one", "two"]);
        Assert.Equal(["one", "two"], error.Failures);
        Assert.Contains("one", error.Message, StringComparison.Ordinal);
        Assert.Equal(["single"], new ConformanceException("single").Failures);
        Assert.Single(new ConformanceException().Failures);
        Assert.Same(error, new ConformanceException("outer", error).InnerException);
    }

    private sealed class Suite : ContributionContractSuite
    {
        private readonly ExtensionManifest _manifest;
        private readonly Func<IContributionHandler> _create;
        private readonly string[] _invoke;
        private readonly int _maxCombinations;

        public Suite(ExtensionManifest manifest, Func<IContributionHandler> create, string[]? invoke = null, int maxCombinations = 64)
        {
            _manifest = manifest;
            _create = create;
            _invoke = invoke ?? [];
            _maxCombinations = maxCombinations;
        }

        protected override ExtensionManifest Manifest => _manifest;

        protected override IEnumerable<string> InvokeContributions => _invoke;

        protected override int MaxSettingCombinations => _maxCombinations;

        protected override IContributionHandler CreateHandler() => _create();
    }
}

/// <summary>No public type of the Testing package is a positional record or has a primary constructor (contract §9).</summary>
public sealed class PublicShapeTests
{
    public static TheoryData<string> PublicTypes()
    {
        var data = new TheoryData<string>();
        foreach (var type in typeof(CompanionTestHost).Assembly.GetExportedTypes().OrderBy(type => type.FullName, StringComparer.Ordinal))
        {
            data.Add(type.FullName!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(PublicTypes))]
    public void NoPublicTypeIsAPositionalRecordOrHasAPrimaryConstructor(string name)
    {
        var type = typeof(CompanionTestHost).Assembly.GetType(name)!;
        Assert.Null(type.GetMethod("Deconstruct", BindingFlags.Public | BindingFlags.Instance));

        // The standard exception constructors (message, inner exception) are not positional data constructors.
        var exception = typeof(Exception).IsAssignableFrom(type);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(property => property.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance))
        {
            var parameters = constructor.GetParameters();
            Assert.False(!exception && parameters.Length > 0 && parameters.All(parameter => properties.Contains(parameter.Name!)),
                name + " has a public constructor whose parameters mirror its properties.");
        }

        Assert.DoesNotContain(type.GetFields(BindingFlags.NonPublic | BindingFlags.Instance), field => Regex.IsMatch(field.Name, "^<.+>P$"));
    }

    [Fact]
    public void TheTestKitIsPortableAotCompatibleAndDependsOnlyOnTheAuthorPackage()
    {
        var assembly = typeof(CompanionTestHost).Assembly;
        var author = typeof(Session).Assembly.GetName().Name;
        Assert.Equal(author + ".Testing", assembly.GetName().Name);
        Assert.All(assembly.GetReferencedAssemblies(), reference =>
            Assert.True(reference.Name!.StartsWith("System", StringComparison.Ordinal) || reference.Name == "netstandard" || reference.Name == author, reference.Name));
        Assert.Equal(".NETCoreApp,Version=v10.0", assembly.GetCustomAttribute<TargetFrameworkAttribute>()!.FrameworkName);
        Assert.Contains(assembly.GetCustomAttributes<AssemblyMetadataAttribute>(), attribute => attribute.Key == "IsTrimmable" && attribute.Value == "True");
        Assert.Equal([assembly.GetName().Name], assembly.GetExportedTypes().Select(type => type.Namespace).Distinct());
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference => reference.Name!.Contains("Assert", StringComparison.OrdinalIgnoreCase)
            || reference.Name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase));
    }
}
