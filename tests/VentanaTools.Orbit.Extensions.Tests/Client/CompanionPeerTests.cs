// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using VentanaTools.Orbit.Extensions.Wire;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tests.Client;

/// <summary>Transport cases require Windows; validators and public API tests stay portable.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Companion named pipes require Windows.";
        }
    }
}

/// <summary>A theory that needs Windows.</summary>
public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Companion named pipes require Windows.";
        }
    }
}

/// <summary>A fact that needs an elevated Windows process, where a pipe's owner can be set to another principal.</summary>
public sealed class ElevatedWindowsFactAttribute : FactAttribute
{
    public ElevatedWindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
        {
            Skip = "Companion named pipes require Windows.";
        }
        else if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            Skip = "Setting a pipe's owner to another principal requires an elevated process.";
        }
    }
}

// Named-pipe transport cases; the Ubuntu CI job filters them out with Platform!=Windows.
[Trait("Platform", "Windows")]
public sealed class CompanionPeerTests
{
    private const string SetState = "example.sdk/set-state";
    private const string Passive = "example.sdk/status";

    [WindowsFact]
    public async Task EqualConfigurationsKeepSeparateHandlesAndStoppingOneLeavesTheOtherAlive()
    {
        var handler = new TestHandler();
        await using var host = new IndependentHost(handler);
        await host.AuthenticateAsync();
        var firstId = NewId();
        var secondId = NewId();
        await host.StartAsync(firstId, SetState);
        await host.StartAsync(secondId, SetState);
        await ClientHarness.WaitForAsync(() => handler.Sessions.Count == 2, "two sessions");
        var first = handler.Sessions.Single(session => session.Id == firstId);
        var second = handler.Sessions.Single(session => session.Id == secondId);
        Assert.NotSame(first, second);
        Assert.Equal(first.Settings["mode"], second.Settings["mode"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)first.Settings)["mode"] = "off");
        await host.SendAsync(new StopSessionMessage { SessionId = firstId });
        await ClientHarness.WaitForAsync(() => !first.IsActive, "first stopped");
        Assert.True(second.IsActive);
        Assert.Equal(PublishResult.SessionEnded, first.ClearFace());
        Assert.Equal(PublishResult.SessionEnded, first.SetFace(new Face { Line1 = "Late", GoodFor = TimeSpan.FromSeconds(5) }));
        Assert.Equal(PublishResult.Accepted, second.SetFace(new Face { Line1 = "Live", GoodFor = TimeSpan.FromSeconds(5) }));
        using (var face = await host.ReadDocumentAsync())
        {
            Assert.Equal("setFace", face.RootElement.GetProperty("type").GetString());
            Assert.Equal(secondId, face.RootElement.GetProperty("sessionId").GetString());
        }

        await host.SendAsync(new InvokeMessage { SessionId = secondId, RequestId = NewId() });
        using var result = await host.ReadDocumentAsync();
        Assert.Equal("Done", result.RootElement.GetProperty("outcome").GetString());
        Assert.Same(second, Assert.Single(handler.Invocations).Session);
    }

    [WindowsFact]
    public async Task PassiveContributionsPublishFiniteCleanFacesAndRefuseInvocations()
    {
        var handler = new TestHandler();
        await using var host = new IndependentHost(handler);
        await host.AuthenticateAsync();
        var sessionId = NewId();
        await host.StartAsync(sessionId, Passive);
        await ClientHarness.WaitForAsync(() => handler.Sessions.Count == 1, "session");
        var session = handler.Sessions.Single();
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetFace(new Face { Line1 = "Forever", GoodFor = TimeSpan.Zero }));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetFace(new Face { Line1 = "Too long", GoodFor = TimeSpan.FromSeconds(86_401) }));
        Assert.Throws<ArgumentException>(() => session.SetFace(new Face { Line1 = "Foreign glyph", GoodFor = TimeSpan.FromSeconds(5), Picture = FacePicture.Glyph("A") }));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SetFace(new Face { Line1 = "Foreign state", GoodFor = TimeSpan.FromSeconds(5), State = (FaceState)99 }));
        Assert.Equal(PublishResult.Accepted, session.SetFace(new Face { Line1 = "A\nB‮", GoodFor = TimeSpan.FromSeconds(5), State = FaceState.On }));
        using (var publication = await host.ReadDocumentAsync())
        {
            Assert.Equal("setFace", publication.RootElement.GetProperty("type").GetString());
            Assert.Equal(sessionId, publication.RootElement.GetProperty("sessionId").GetString());
            var face = publication.RootElement.GetProperty("face");
            Assert.Equal("A B", face.GetProperty("line1").GetProperty("text").GetString());
            Assert.Equal("On", face.GetProperty("state").GetString());
            Assert.Equal(5, face.GetProperty("goodForSeconds").GetInt32());
        }

        var requestId = NewId();
        await host.SendAsync(new InvokeMessage { SessionId = sessionId, RequestId = requestId });
        using (var refusal = await host.ReadDocumentAsync())
        {
            Assert.Equal("Refused", refusal.RootElement.GetProperty("outcome").GetString());
        }

        Assert.Empty(handler.Invocations);
        Assert.Equal(PublishResult.Accepted, session.Fail(Failure.NeedsSetup));
        using (var failure = await host.ReadDocumentAsync())
        {
            Assert.Equal("fail", failure.RootElement.GetProperty("type").GetString());
            Assert.Equal("NeedsSetup", failure.RootElement.GetProperty("failure").GetString());
        }

        Assert.Equal(PublishResult.Accepted, session.ClearFace());
        using var cleared = await host.ReadDocumentAsync();
        Assert.Equal("clearFace", cleared.RootElement.GetProperty("type").GetString());
    }

    [WindowsFact]
    public async Task ActionOnlyContributionUsesTheSameAuthenticatedSessionsWithoutFacePermission()
    {
        var handler = new TestHandler();
        var manifest = Manifest([TestManifests.Contribution(SetState, Provides.Invoke)]);
        await using var host = new IndependentHost(handler, manifest);
        await host.AuthenticateAsync();
        var sessionId = NewId();
        await host.SendAsync(new StartSessionMessage { SessionId = sessionId, ContributionId = SetState, Settings = new Dictionary<string, string>() });
        await ClientHarness.WaitForAsync(() => handler.Sessions.Count == 1, "session");
        var session = handler.Sessions.Single();
        Assert.Empty(session.Settings);
        Assert.Throws<InvalidOperationException>(() => session.SetFace(new Face { Line1 = "Not allowed", GoodFor = TimeSpan.FromSeconds(5) }));
        Assert.Throws<InvalidOperationException>(() => session.ClearFace());
        Assert.Throws<InvalidOperationException>(() => session.Fail(Failure.NoResult));
        var requestId = NewId();
        await host.SendAsync(new InvokeMessage { SessionId = sessionId, RequestId = requestId });
        using (var result = await host.ReadDocumentAsync())
        {
            Assert.Equal("result", result.RootElement.GetProperty("type").GetString());
            Assert.Equal(requestId, result.RootElement.GetProperty("requestId").GetString());
            Assert.Equal("Done", result.RootElement.GetProperty("outcome").GetString());
        }

        Assert.Same(session, Assert.Single(handler.Invocations).Session);
        await host.SendAsync(new StopSessionMessage { SessionId = sessionId });
        await ClientHarness.WaitForAsync(() => !session.IsActive, "stopped");
        await host.SendAsync(new InvokeMessage { SessionId = sessionId, RequestId = NewId() });
        using (var refused = await host.ReadDocumentAsync())
        {
            Assert.Equal("Refused", refused.RootElement.GetProperty("outcome").GetString());
        }

        Assert.Single(handler.Invocations);
    }

    [WindowsFact]
    public async Task WrongServerProofNeverGetsClientProofOrCallsAuthorCode()
    {
        var handler = new TestHandler();
        await using var host = new IndependentHost(handler);
        await host.AuthenticateAsync(correctProof: false);
        Assert.Null(await host.ReadAsync());
        Assert.Empty(handler.Sessions);
        Assert.Empty(handler.Invocations);
        var waiting = await host.WaitForStatusAsync(ConnectionState.Stopped);
        Assert.Equal(ReasonCode.AuthServerProofInvalid, waiting.Reason); // the operating system verified this server
    }

    [WindowsFact]
    public async Task CallerMutationDoesNotChangeTheValidatedRunningCatalog()
    {
        var handler = new TestHandler();
        var contributions = DefaultContributions().ToList();
        var manifest = Manifest(contributions);
        await using var host = new IndependentHost(handler, manifest);
        contributions.Clear(); // the client copied the manifest when it was created
        await host.AuthenticateAsync();
        await host.StartAsync(NewId(), SetState);
        await ClientHarness.WaitForAsync(() => handler.Sessions.Count == 1, "session");
        Assert.Equal(SetState, handler.Sessions.Single().ContributionId);
    }

    [WindowsTheory]
    [InlineData("""{"type":"startSession","sessionId":"11111111111111111111111111111111","contributionId":"example.sdk/set-state","settings":{"mode":"on","mode":"off"}}""", "frame.json-duplicate")]
    [InlineData("""{"type":"startSession","sessionId":"11111111111111111111111111111111","contributionId":"example.sdk/set-state","settings":{"mode":"undeclared"}}""", "session.settings-invalid")]
    [InlineData("""{"type":"startSession","sessionId":"11111111111111111111111111111111","contributionId":"example.sdk/set-state","settings":{},"foreign":true}""", "session.settings-invalid")]
    [InlineData("""{"type":"startSession","sessionId":"11111111111111111111111111111111","contributionId":"example.sdk/set-state","settings":{}}""", "session.settings-invalid")]
    [InlineData("""{"type":"startSession","type":"stopSession","sessionId":"11111111111111111111111111111111","contributionId":"example.sdk/set-state","settings":{"mode":"on"}}""", "frame.json-duplicate")]
    public async Task AmbiguousOrIncompleteHostConfigurationDisconnectsWithoutDispatch(string message, string code)
    {
        var handler = new TestHandler();
        await using var host = new IndependentHost(handler);
        await host.AuthenticateAsync();
        await host.SendAsync(Encoding.UTF8.GetBytes(message));
        using var answer = await host.ReadDocumentAsync();
        if (code.StartsWith("session.", StringComparison.Ordinal))
        {
            // Incomplete or undeclared settings are refused; the connection stays.
            Assert.Equal("sessionRefused", answer.RootElement.GetProperty("type").GetString());
            Assert.Equal(code, answer.RootElement.GetProperty("code").GetString());
            await host.SendAsync(new PingMessage { Id = 1 });
            using var pong = await host.ReadDocumentAsync();
            Assert.Equal("pong", pong.RootElement.GetProperty("type").GetString());
        }
        else
        {
            // An ambiguous frame closes the connection.
            Assert.Equal("error", answer.RootElement.GetProperty("type").GetString());
            Assert.Equal(code, answer.RootElement.GetProperty("code").GetString());
            Assert.Null(await host.ReadAsync());
        }

        Assert.Empty(handler.Sessions);
    }

    [WindowsFact]
    public async Task AStaleSessionIsRefusedAndADuplicateRequestIsNeverReplayed()
    {
        var handler = new TestHandler();
        await using var host = new IndependentHost(handler);
        await host.AuthenticateAsync();
        var sessionId = NewId();
        await host.StartAsync(sessionId, SetState);
        await ClientHarness.WaitForAsync(() => handler.Sessions.Count == 1, "session");
        await host.SendAsync(new InvokeMessage { SessionId = NewId(), RequestId = NewId() });
        using (var refused = await host.ReadDocumentAsync())
        {
            Assert.Equal("Refused", refused.RootElement.GetProperty("outcome").GetString());
        }

        Assert.Empty(handler.Invocations);
        var requestId = NewId();
        await host.SendAsync(new InvokeMessage { SessionId = sessionId, RequestId = requestId });
        using (var done = await host.ReadDocumentAsync())
        {
            Assert.Equal("Done", done.RootElement.GetProperty("outcome").GetString());
        }

        await host.SendAsync(new InvokeMessage { SessionId = sessionId, RequestId = requestId });
        using (var error = await host.ReadDocumentAsync())
        {
            Assert.Equal("invoke.replay", error.RootElement.GetProperty("code").GetString());
        }

        Assert.Null(await host.ReadAsync());
        Assert.Single(handler.Invocations);
        await ClientHarness.WaitForAsync(() => !handler.Sessions.Single().IsActive, "inactive");
    }

    [WindowsFact]
    public async Task BlockingAuthorCancellationCannotBlockReaderOrClientShutdown()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstId = NewId();
        var handler = new TestHandler
        {
            OnSession = async (session, token) =>
            {
                using var registration = token.Register(() =>
                {
                    if (session.Id != firstId)
                    {
                        return;
                    }

                    entered.TrySetResult();
                    release.Wait(CancellationToken.None);
                });
                if (session.Id == firstId)
                {
                    registered.TrySetResult();
                }

                await Task.Delay(Timeout.Infinite, token);
            },
        };
        await using var host = new IndependentHost(handler);
        try
        {
            await host.AuthenticateAsync();
            await host.StartAsync(firstId, SetState);
            await registered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await host.SendAsync(new StopSessionMessage { SessionId = firstId });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(handler.Sessions.First().IsActive);
            var secondId = NewId();
            await host.StartAsync(secondId, SetState);
            await ClientHarness.WaitForAsync(() => handler.Sessions.Count == 2, "second session", seconds: 3);
            await host.SendAsync(new InvokeMessage { SessionId = secondId, RequestId = NewId() });
            using (var result = await host.ReadDocumentAsync())
            {
                Assert.Equal("Done", result.RootElement.GetProperty("outcome").GetString());
            }

            await host.StopClientAsync().WaitAsync(TimeSpan.FromSeconds(8));
            Assert.False(handler.Sessions.Last().IsActive);
        }
        finally
        {
            release.Set();
        }
    }

    [WindowsFact]
    public async Task RemoteCancelRetiresOnlyThatCallAndReaderStillAcceptsOtherCalls()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequest = NewId();
        var handler = new TestHandler
        {
            OnInvoke = async (invocation, token) =>
            {
                if (invocation.RequestId != firstRequest)
                {
                    return InvokeResult.Done;
                }

                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                finally
                {
                    cancelled.TrySetResult();
                }

                return InvokeResult.Done;
            },
        };
        await using var host = new IndependentHost(handler);
        await host.AuthenticateAsync();
        var sessionId = NewId();
        await host.StartAsync(sessionId, SetState);
        await host.SendAsync(new InvokeMessage { SessionId = sessionId, RequestId = firstRequest });
        await ClientHarness.WaitForAsync(() => handler.Invocations.Count == 1, "invoked");
        await host.SendAsync(new CancelMessage { RequestId = firstRequest });
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var next = NewId();
        await host.SendAsync(new InvokeMessage { SessionId = sessionId, RequestId = next });
        using var result = await host.ReadDocumentAsync();
        Assert.Equal(next, result.RootElement.GetProperty("requestId").GetString());
        Assert.Equal("Done", result.RootElement.GetProperty("outcome").GetString());
        Assert.True(handler.Sessions.Single().IsActive);
    }

    [WindowsFact]
    public async Task InvocationDeadlineCancelsAuthorWorkWithoutAHostCancelMessage()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new TestHandler
        {
            OnInvoke = async (_, token) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, token);
                }
                finally
                {
                    cancelled.TrySetResult();
                }

                return InvokeResult.Done;
            },
        };
        await using var host = new IndependentHost(handler);
        await host.AuthenticateAsync(limits: StatusAndBackoffTests.Limits(invokeTimeoutMs: 4_000));
        var sessionId = NewId();
        await host.StartAsync(sessionId, SetState);
        var requestId = NewId();
        await host.SendAsync(new InvokeMessage { SessionId = sessionId, RequestId = requestId });
        await ClientHarness.WaitForAsync(() => handler.Invocations.Count == 1, "invoked");
        var watch = Stopwatch.StartNew();
        using (var result = await host.ReadDocumentAsync())
        {
            Assert.Equal(requestId, result.RootElement.GetProperty("requestId").GetString());
            Assert.Equal("Failed", result.RootElement.GetProperty("outcome").GetString());
        }

        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(handler.Sessions.Single().IsActive);
    }

    [WindowsFact]
    public async Task FullSessionAndInvokeBudgetsRefuseOverflowWithoutRunningExtraHandlers()
    {
        var handler = new TestHandler
        {
            OnInvoke = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return InvokeResult.Done;
            },
        };
        await using var host = new IndependentHost(handler);
        await host.AuthenticateAsync();
        var sessions = Enumerable.Range(0, 64).Select(_ => NewId()).ToArray();
        foreach (var session in sessions)
        {
            await host.StartAsync(session, SetState);
        }

        await ClientHarness.WaitForAsync(() => handler.Sessions.Count == 64, "64 sessions");
        for (var index = 0; index < 32; index++)
        {
            await host.SendAsync(new InvokeMessage { SessionId = sessions[index], RequestId = NewId() });
        }

        await ClientHarness.WaitForAsync(() => handler.Invocations.Count == 32, "32 invocations");
        var overflow = NewId();
        await host.SendAsync(new InvokeMessage { SessionId = sessions[0], RequestId = overflow });
        using (var refusal = await host.ReadDocumentAsync())
        {
            Assert.Equal(overflow, refusal.RootElement.GetProperty("requestId").GetString());
            Assert.Equal("Refused", refusal.RootElement.GetProperty("outcome").GetString());
        }

        Assert.Equal(32, handler.Invocations.Count);
        var extra = NewId();
        await host.StartAsync(extra, SetState);
        using (var capacity = await host.ReadDocumentAsync())
        {
            Assert.Equal("sessionRefused", capacity.RootElement.GetProperty("type").GetString());
            Assert.Equal(extra, capacity.RootElement.GetProperty("sessionId").GetString());
            Assert.Equal("session.capacity", capacity.RootElement.GetProperty("code").GetString());
        }

        Assert.Equal(64, handler.Sessions.Count);
        Assert.True(handler.Sessions.All(session => session.IsActive));
        await host.StopClientAsync();
        Assert.True(handler.Sessions.All(session => !session.IsActive));
    }

    [WindowsFact]
    public async Task FaceFloodHasBoundedAdmissionAndOldSessionCannotPublishAfterDisconnect()
    {
        var handler = new TestHandler();
        await using var host = new IndependentHost(handler);
        await host.AuthenticateAsync();
        await host.StartAsync(NewId(), SetState);
        await ClientHarness.WaitForAsync(() => handler.Sessions.Count == 1, "session");
        var session = handler.Sessions.Single();
        for (var index = 0; index < 1_024; index++)
        {
            // Pressure coalesces into one pending face; it is never a failure and never revokes the session.
            Assert.Equal(PublishResult.Accepted, session.SetFace(new Face { Line1 = "Live", GoodFor = TimeSpan.FromSeconds(5) }));
        }

        Assert.True(session.IsActive);
        await host.StopClientAsync().WaitAsync(TimeSpan.FromSeconds(8));
        Assert.False(session.IsActive);
        Assert.Equal(PublishResult.SessionEnded, session.SetFace(new Face { Line1 = "Late", GoodFor = TimeSpan.FromSeconds(5) }));
    }

    [WindowsFact]
    public async Task ASameUserServerIsVerifiedByTheOperatingSystemBeforeTheChallenge()
    {
        await using var host = new IndependentHost(new TestHandler());
        await host.AcceptHelloAsync();
        await host.SendAsync(new ErrorMessage { Code = ReasonCode.AuthRegistrationMismatch });
        var stopped = await host.WaitForStatusAsync(ConnectionState.Stopped);
        Assert.Equal(ReasonCode.AuthRegistrationMismatch, stopped.Reason);
        Assert.True(stopped.ServerVerified);
    }

    [WindowsFact]
    public async Task AMissingPipeIsReportedAtOnceAsHostNotRunning()
    {
        var name = PipeNames.Create(TestHosts.Id, "test", PipeNames.UserHash(WindowsIdentity.GetCurrent().User!.Value), NewId());
        using var pairing = new Pairing(TestHosts.Id, name, PipeNames.TryParse(name, out var parts) ? parts.RegistrationId : "", "example.sdk",
            RandomNumberGenerator.GetBytes(32));
        var client = new CompanionClient(pairing, Manifest(DefaultContributions()), new TestHandler());
        var waiting = new TaskCompletionSource<StatusChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.StatusChanged += (_, e) =>
        {
            if (e.State == ConnectionState.Waiting)
            {
                waiting.TrySetResult(e);
            }
        };
        using var stop = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        var run = client.RunAsync(stop.Token);
        var status = await waiting.Task.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(ReasonCode.HostNotRunning, status.Reason);
        Assert.InRange(watch.Elapsed, TimeSpan.Zero, TimeSpan.FromSeconds(4));
        Assert.InRange(status.RetryIn!.Value, TimeSpan.Zero, TimeSpan.FromSeconds(5));
        await stop.CancelAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(ConnectionState.Stopped, client.State);
    }

    [WindowsFact]
    public async Task APipeWhoseOnlyInstanceIsTakenIsReportedAsBusy()
    {
        await using var host = new IndependentHost(new TestHandler(), start: false);
        using var squatter = new NamedPipeClientStream(".", host.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accept = host.WaitForConnectionAsync();
        await squatter.ConnectAsync(5_000);
        await accept;
        host.StartClient();
        var waiting = await host.WaitForStatusAsync(ConnectionState.Waiting, seconds: 15);
        Assert.Equal(ReasonCode.HostPipeBusy, waiting.Reason);
    }

    [WindowsFact]
    public void TheOperatingSystemCheckVerifiesThisProcessAndRefusesALowIntegrityOne()
    {
        Assert.Equal(ServerCheck.Verified, PipeNatives.CheckProcess((uint)Environment.ProcessId));
        using var low = LowIntegrity.Start("\"" + LowIntegrity.WindowsPowerShell + "\" -NoProfile -NonInteractive -Command Start-Sleep 30");
        Assert.Equal(ServerCheck.Refused, PipeNatives.CheckProcess(low.Id));
        Assert.Equal(ServerCheck.Unchecked, PipeNatives.CheckProcess(uint.MaxValue - 3));
    }

    [WindowsFact]
    public async Task ALowIntegrityProcessThatHoldsThePipeNameIsRefusedBeforeAnyByteIsWritten()
    {
        // A Low-integrity process of the same user can read a pairing file that has no mandatory label and create the
        // pipe while the host is not listening. Its pipe is owned by the user, so only the process check refuses it.
        await using var host = new IndependentHost(new TestHandler(), start: false, listen: false);
        var script = "$p = New-Object System.IO.Pipes.NamedPipeServerStream('" + host.PipeName + "', 'InOut', 1, 'Byte', 'Asynchronous'); "
            + "if (-not $p.WaitForConnectionAsync().Wait(60000)) { exit 30 }; $b = New-Object byte[] 1; $r = $p.ReadAsync($b, 0, 1); "
            + "if (-not $r.Wait(15000)) { exit 20 }; exit (10 + $r.Result)";
        using var squatter = LowIntegrity.Start("\"" + LowIntegrity.WindowsPowerShell + "\" -NoProfile -NonInteractive -Command \"" + script + "\"");
        host.StartClient();
        var refused = await host.WaitForStatusAsync(ConnectionState.Waiting, ReasonCode.AuthServerUnverified, seconds: 60);
        Assert.False(refused.ServerVerified);

        // 10: the squatter saw the client close without a byte; 11 would mean the client wrote hello to it.
        Assert.Equal(10, await squatter.WaitForExitAsync(TimeSpan.FromSeconds(30)));
    }

    [ElevatedWindowsFact]
    public async Task APipeOwnedByAnotherPrincipalIsRefusedBeforeAnyByteIsWritten()
    {
        // An elevated process's new objects are owned by Administrators, its token's default owner, so a pipe owned by the
        // user's own SID is "another principal" to PipeOptions.CurrentUserOnly.
        var user = WindowsIdentity.GetCurrent().User!;
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.FullControl, AccessControlType.Allow));
        await using var host = new IndependentHost(new TestHandler(), start: false, security: security);
        host.StartClient();
        var accept = host.WaitForConnectionAsync();
        var waiting = await host.WaitForStatusAsync(ConnectionState.Waiting, seconds: 15);
        Assert.Equal(ReasonCode.AuthServerUnverified, waiting.Reason);
        Assert.False(waiting.ServerVerified);
        await accept.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await host.ReadAsync(TimeSpan.FromMilliseconds(500)));
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private static Contribution[] DefaultContributions() =>
    [
        new Contribution
        {
            Id = SetState,
            Name = "Set state",
            Description = "Change state.",
            Glyph = "",
            Provides = Provides.Invoke | Provides.Face,
            Settings =
            [
                new Setting
                {
                    Id = "mode",
                    Name = "Mode",
                    Default = "on",
                    Choices = [new SettingChoice { Value = "on", Name = "On" }, new SettingChoice { Value = "off", Name = "Off" }],
                },
            ],
        },
        TestManifests.Contribution(Passive, Provides.Face),
    ];

    private static ExtensionManifest Manifest(IReadOnlyList<Contribution> contributions) => new()
    {
        Id = "example.sdk",
        Name = "SDK test",
        Description = "Test companion.",
        Version = "0.1.0",
        Hosts = [TestHosts.Id],
        Contributions = contributions,
    };

    /// <summary>
    /// A pipe server that plays the host. It computes the transcript and proofs itself, independently
    /// of the SDK's helpers, and writes its challenge in fragments, header included.
    /// </summary>
    private sealed class IndependentHost : IAsyncDisposable
    {
        private const string HostVersion = "2026.10.1";
        private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
        private readonly string _registrationId = NewId();
        private readonly ExtensionManifest _manifest;
        private readonly string _manifestHash;
        private readonly Pairing _pairing;
        private readonly CompanionClient _client;
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(60));
        private readonly List<StatusChangedEventArgs> _statuses = [];
        private Task _run = Task.CompletedTask;

        public IndependentHost(IContributionHandler handler, ExtensionManifest? manifest = null, bool start = true, PipeSecurity? security = null,
            bool listen = true)
        {
            _manifest = manifest ?? Manifest(DefaultContributions());
            _manifestHash = ManifestWriter.ComputeHash(_manifest);
            PipeName = PipeNames.Create(TestHosts.Id, "test", PipeNames.UserHash(WindowsIdentity.GetCurrent().User!.Value), _registrationId);
            Pipe = !listen
                ? null!
                : security is null
                ? new NamedPipeServerStream(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance)
                : NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
            _pairing = new Pairing(TestHosts.Id, PipeName, _registrationId, _manifest.Id, (byte[])_secret.Clone());
            _client = new CompanionClient(_pairing, _manifest, handler);
            _client.StatusChanged += (_, e) =>
            {
                lock (_statuses)
                {
                    _statuses.Add(e);
                }
            };
            if (start)
            {
                StartClient();
            }
        }

        public string PipeName { get; }

        public NamedPipeServerStream Pipe { get; }

        public string? ClientNonce { get; private set; }

        public void StartClient() => _run = _client.RunAsync(_stop.Token);

        public Task WaitForConnectionAsync() => Pipe.WaitForConnectionAsync(_deadline.Token);

        public async Task<HelloMessage> AcceptHelloAsync()
        {
            await Pipe.WaitForConnectionAsync(_deadline.Token);
            var frame = await ReadAsync() ?? throw new InvalidDataException("The companion disconnected.");
            var hello = Assert.IsType<HelloMessage>(MessageReader.Read(frame, Sender.Companion, ConnectionPhase.Handshake).Message);
            Assert.Equal(_registrationId, hello.RegistrationId);
            Assert.Equal(_manifestHash, hello.ManifestHash);
            ClientNonce = hello.ClientNonce;
            return hello;
        }

        public async Task AuthenticateAsync(bool correctProof = true, HostLimits? limits = null)
        {
            var hello = await AcceptHelloAsync();
            var serverNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var challenge = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["type"] = "challenge",
                ["serverNonce"] = serverNonce,
                ["version"] = 3,
                ["capabilities"] = Array.Empty<string>(),
                ["host"] = new Dictionary<string, string> { ["id"] = TestHosts.Id, ["version"] = HostVersion },
                ["proof"] = correctProof ? Proof("server", hello, serverNonce) : Convert.ToBase64String(new byte[32]),
            }));
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)challenge.Length);
            foreach (var value in header)
            {
                await Pipe.WriteAsync(new[] { value }, _deadline.Token);
            }

            await Pipe.WriteAsync(challenge.AsMemory(0, 3), _deadline.Token);
            await Pipe.WriteAsync(challenge.AsMemory(3), _deadline.Token);
            await Pipe.FlushAsync(_deadline.Token);
            if (!correctProof)
            {
                return;
            }

            using var authenticate = await ReadDocumentAsync();
            Assert.Equal("authenticate", authenticate.RootElement.GetProperty("type").GetString());
            Assert.Equal(Proof("client", hello, serverNonce), authenticate.RootElement.GetProperty("proof").GetString());
            await SendAsync(new ReadyMessage
            {
                Version = 3,
                Capabilities = [],
                Host = new HostIdentity { Id = TestHosts.Id, Version = HostVersion },
                UiLanguage = "en-US",
                Limits = limits ?? HostLimits.Protocol3Defaults,
            });
        }

        public Task StartAsync(string sessionId, string contributionId) => SendAsync(new StartSessionMessage
        {
            SessionId = sessionId,
            ContributionId = contributionId,
            Settings = contributionId == SetState ? new Dictionary<string, string> { ["mode"] = "on" } : new Dictionary<string, string>(),
        });

        public Task SendAsync(WireMessage message) => SendAsync(MessageWriter.Write(message));

        public async Task SendAsync(byte[] body) => await Framing.WriteFrameAsync(Pipe, body, _deadline.Token);

        public async Task<byte[]?> ReadAsync(TimeSpan? timeout = null)
        {
            using var bound = CancellationTokenSource.CreateLinkedTokenSource(_deadline.Token);
            bound.CancelAfter(timeout ?? TimeSpan.FromSeconds(20));
            try
            {
                return await Framing.ReadFrameAsync(Pipe, Framing.MaxFrameBytes, TimeProvider.System, bound.Token);
            }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
                return null;
            }
        }

        public async Task<JsonDocument> ReadDocumentAsync() =>
            JsonDocument.Parse(await ReadAsync() ?? throw new InvalidDataException("The test peer disconnected."));

        public Task<StatusChangedEventArgs> WaitForStatusAsync(ConnectionState state, int seconds = 10) => WaitForStatusAsync(state, null, seconds);

        public async Task<StatusChangedEventArgs> WaitForStatusAsync(ConnectionState state, ReasonCode? reason, int seconds = 10)
        {
            StatusChangedEventArgs? found = null;
            await ClientHarness.WaitForAsync(() =>
            {
                lock (_statuses)
                {
                    found = _statuses.FirstOrDefault(status => status.State == state && (reason is null || status.Reason == reason));
                }

                return found is not null;
            }, state.ToString(), seconds);
            return found!;
        }

        public async Task StopClientAsync()
        {
            await _stop.CancelAsync();
            await _run.WaitAsync(TimeSpan.FromSeconds(8));
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await StopClientAsync();
            }
            finally
            {
                if (Pipe is not null)
                {
                    await Pipe.DisposeAsync();
                }

                _pairing.Dispose();
                _stop.Dispose();
                _deadline.Dispose();
                CryptographicOperations.ZeroMemory(_secret);
            }
        }

        /// <summary>The transcript of contract §7.3.4, built here rather than with the SDK's helper.</summary>
        private string Proof(string role, HelloMessage hello, string serverNonce)
        {
            var transcript = string.Join('\n',
                "Ventana.Extensions.v3", role, TestHosts.Id, HostVersion, _registrationId, hello.ClientNonce, serverNonce,
                "3", hello.MinVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                hello.MaxVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
                string.Join(',', hello.Capabilities.Order(StringComparer.Ordinal)), string.Empty, hello.ManifestHash);
            return Convert.ToBase64String(HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(transcript)));
        }
    }
}
