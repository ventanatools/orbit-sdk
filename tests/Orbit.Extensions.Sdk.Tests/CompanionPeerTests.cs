using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;
using Orbit.Extensions.Protocol;
using Orbit.Extensions.Sdk;
using static Orbit.Extensions.Sdk.Tests.TestFixtures;

namespace Orbit.Extensions.Sdk.Tests;

public sealed class CompanionPeerTests
{
    [WindowsFact]
    public async Task EqualConfigurationsKeepSeparateHandlesAndStoppingOneLeavesTheOtherAlive()
    {
        var handler = new RecordingHandler();
        await using var host = new IndependentHost(handler);
        await host.AuthenticateAsync();
        var firstId = NewId();
        var secondId = NewId();
        await host.InstanceAsync("startSession", firstId);
        await host.InstanceAsync("startSession", secondId);
        await EventuallyAsync(() => handler.Sessions.Count == 2);
        var first = handler.Sessions.Single(session => session.SessionId == firstId);
        var second = handler.Sessions.Single(session => session.SessionId == secondId);
        Assert.NotSame(first, second);
        Assert.Equal(first.Settings["mode"], second.Settings["mode"]);
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, string>)first.Settings)["mode"] = "off");
        Assert.Equal("CompanionSession", first.ToString());
        await host.SendAsync(ExtensionWire.Message("stopSession", ("sessionId", firstId)));
        await EventuallyAsync(() => !first.IsActive);
        Assert.True(second.IsActive);
        Assert.False(first.ClearFace());
        Assert.False(first.SetFace(new CompanionFace("Late", 5)));
        Assert.True(second.SetFace(new CompanionFace("Live", 5)));
        using var face = await host.ReadDocumentAsync();
        Assert.Equal(secondId, face.RootElement.GetProperty("sessionId").GetString());
        await host.InstanceAsync("invoke", secondId, NewId());
        using var result = await host.ReadDocumentAsync();
        Assert.Equal("done", result.RootElement.GetProperty("outcome").GetString());
        Assert.Same(second, Assert.Single(handler.Invocations).Session);
    }

    [WindowsFact]
    public async Task PassiveContributionsPublishFiniteCleanFacesAndRefuseInvocations()
    {
        var handler = new RecordingHandler();
        await using var host = new IndependentHost(handler);
        await host.AuthenticateAsync();
        var sessionId = NewId();
        var settings = new Dictionary<string, string>();
        await host.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "startSession", sessionId, actionId = PassiveId, settings,
        }));
        await EventuallyAsync(() => handler.Sessions.Count == 1);
        var session = handler.Sessions.Single();
        Assert.Throws<ArgumentException>(() => session.SetFace(new CompanionFace("Forever", 0)));
        Assert.Throws<ArgumentException>(() => session.SetFace(new CompanionFace("Too long", 86401)));
        Assert.Throws<ArgumentException>(() => session.SetFace(new CompanionFace("Foreign glyph", 5) { Glyph = "A" }));
        Assert.Throws<ArgumentException>(() => session.SetFace(new CompanionFace("Foreign state", 5) { State = (CompanionFaceState)99 }));
        Assert.True(session.SetFace(new CompanionFace("A\nB\u202E", 5) { State = CompanionFaceState.On }));
        using (var publication = await host.ReadDocumentAsync())
        {
            Assert.Equal("face", publication.RootElement.GetProperty("type").GetString());
            Assert.Equal(sessionId, publication.RootElement.GetProperty("sessionId").GetString());
            var face = publication.RootElement.GetProperty("command").GetProperty("face");
            Assert.Equal("A B", face.GetProperty("line1").GetProperty("value").GetString());
            Assert.Equal("On", face.GetProperty("state").GetString());
            Assert.Equal(5, face.GetProperty("goodForSeconds").GetInt32());
        }
        var requestId = NewId();
        await host.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "invoke", sessionId, requestId, actionId = PassiveId, settings,
        }));
        using (var refusal = await host.ReadDocumentAsync())
            Assert.Equal("refused", refusal.RootElement.GetProperty("outcome").GetString());
        Assert.Empty(handler.Invocations);
        Assert.True(session.Fail(CompanionFailure.NeedsSetup));
        using (var failure = await host.ReadDocumentAsync())
            Assert.Equal("NeedsSetup", failure.RootElement.GetProperty("command").GetProperty("failure").GetString());
        Assert.True(session.ClearFace());
        using var cleared = await host.ReadDocumentAsync();
        Assert.Equal("clearFace", cleared.RootElement.GetProperty("command").GetProperty("$type").GetString());
    }

    [WindowsFact]
    public async Task ActionOnlyContributionUsesTheSameAuthenticatedSessionsWithoutFacePermission()
    {
        var handler = new RecordingHandler();
        var manifest = Manifest with { Actions = [Manifest.Actions[0] with { HasFace = false, Settings = [] }] };
        await using var host = new IndependentHost(handler, manifest);
        await host.AuthenticateAsync();
        var sessionId = NewId();
        var settings = new Dictionary<string, string>();
        await host.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "startSession", sessionId, actionId = ActionId, settings,
        }));
        await EventuallyAsync(() => handler.Sessions.Count == 1);
        var session = handler.Sessions.Single();
        Assert.Empty(session.Settings);
        Assert.False(session.SetFace(new CompanionFace("Not allowed", 5)));
        Assert.False(session.ClearFace());
        Assert.False(session.Fail(CompanionFailure.NoData));
        var requestId = NewId();
        await host.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "invoke", sessionId, requestId, actionId = ActionId, settings,
        }));
        using (var result = await host.ReadDocumentAsync())
        {
            Assert.Equal("result", result.RootElement.GetProperty("type").GetString());
            Assert.Equal(requestId, result.RootElement.GetProperty("requestId").GetString());
            Assert.Equal("done", result.RootElement.GetProperty("outcome").GetString());
        }
        Assert.Single(handler.Invocations);
        Assert.Same(session, handler.Invocations.Single().Session);
        await host.SendAsync(ExtensionWire.Message("stopSession", ("sessionId", sessionId)));
        await EventuallyAsync(() => !session.IsActive);
        await host.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = "invoke", sessionId, requestId = NewId(), actionId = ActionId, settings,
        }));
        using (var refused = await host.ReadDocumentAsync())
            Assert.Equal("refused", refused.RootElement.GetProperty("outcome").GetString());
        Assert.Single(handler.Invocations);
    }

    [WindowsFact]
    public async Task WrongServerProofNeverGetsClientProofOrCallsAuthorCode()
    {
        var handler = new RecordingHandler();
        await using var host = new IndependentHost(handler);
        await host.AuthenticateAsync(correctProof: false);
        Assert.Null((await host.ReadAsync()));
        Assert.Empty(handler.Sessions);
        Assert.Empty(handler.Invocations);
    }

    [WindowsFact]
    public async Task CallerMutationDoesNotChangeTheValidatedRunningCatalog()
    {
        var handler = new RecordingHandler();
        var actions = Manifest.Actions.ToList();
        var mutable = Manifest with { Actions = actions };
        await using var host = new IndependentHost(handler, mutable);
        actions.Clear(); // RunAsync snapshots before its first asynchronous boundary.
        await host.AuthenticateAsync();
        await host.InstanceAsync("startSession", NewId());
        await EventuallyAsync(() => handler.Sessions.Count == 1);
        Assert.Equal(ActionId, handler.Sessions.Single().ActionId);
    }

    [WindowsTheory]
    [InlineData("""{"type":"startSession","sessionId":"11111111111111111111111111111111","actionId":"example.sdk/set-state","settings":{"mode":"on","mode":"off"}}""")]
    [InlineData("""{"type":"startSession","sessionId":"11111111111111111111111111111111","actionId":"example.sdk/set-state","settings":{"mode":"undeclared"}}""")]
    [InlineData("""{"type":"startSession","sessionId":"11111111111111111111111111111111","actionId":"example.sdk/set-state","settings":{},"foreign":true}""")]
    [InlineData("""{"type":"startSession","sessionId":"11111111111111111111111111111111","actionId":"example.sdk/set-state","settings":{}}""")]
    [InlineData("""{"type":"startSession","type":"stopSession","sessionId":"11111111111111111111111111111111","actionId":"example.sdk/set-state","settings":{"mode":"on"}}""")]
    public async Task AmbiguousOrIncompleteHostConfigurationDisconnectsWithoutDispatch(string message)
    {
        var handler = new RecordingHandler();
        await using var host = new IndependentHost(handler);
        await host.AuthenticateAsync();
        await host.SendAsync(Encoding.UTF8.GetBytes(message));
        Assert.Null((await host.ReadAsync()));
        Assert.Empty(handler.Sessions);
    }

    [WindowsFact]
    public async Task DifferentConfigurationIsRefusedAndDuplicateRequestIsNeverReplayed()
    {
        var handler = new RecordingHandler();
        await using var host = new IndependentHost(handler);
        await host.AuthenticateAsync();
        var sessionId = NewId();
        await host.InstanceAsync("startSession", sessionId);
        await EventuallyAsync(() => handler.Sessions.Count == 1);
        await host.InstanceAsync("invoke", sessionId, NewId(), mode: "off");
        using (var refused = await host.ReadDocumentAsync())
            Assert.Equal("refused", refused.RootElement.GetProperty("outcome").GetString());
        Assert.Empty(handler.Invocations);
        var requestId = NewId();
        await host.InstanceAsync("invoke", sessionId, requestId);
        using (var done = await host.ReadDocumentAsync())
            Assert.Equal("done", done.RootElement.GetProperty("outcome").GetString());
        await host.InstanceAsync("invoke", sessionId, requestId);
        Assert.Null((await host.ReadAsync()));
        Assert.Single(handler.Invocations);
        await EventuallyAsync(() => !handler.Sessions.Single().IsActive);
    }

    [WindowsFact]
    public async Task BlockingAuthorCancellationCannotBlockReaderOrClientShutdown()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstId = NewId();
        var handler = new RecordingHandler
        {
            OnSession = async (session, token) =>
            {
                using var registration = token.Register(() =>
                {
                    if (session.SessionId != firstId) return;
                    entered.TrySetResult();
                    release.Wait();
                });
                if (session.SessionId == firstId) registered.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            },
        };
        await using var host = new IndependentHost(handler);
        try
        {
            await host.AuthenticateAsync();
            await host.InstanceAsync("startSession", firstId);
            await EventuallyAsync(() => handler.Sessions.Count == 1);
            await registered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await host.SendAsync(ExtensionWire.Message("stopSession", ("sessionId", firstId)));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(handler.Sessions.First().IsActive);
            var secondId = NewId();
            await host.InstanceAsync("startSession", secondId);
            await EventuallyAsync(() => handler.Sessions.Count == 2, seconds: 3);
            await host.InstanceAsync("invoke", secondId, NewId());
            using (var result = await host.ReadDocumentAsync())
                Assert.Equal("done", result.RootElement.GetProperty("outcome").GetString());
            await host.StopClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(handler.Sessions.Last().IsActive);
        }
        finally { release.Set(); }
    }

    [WindowsFact]
    public async Task RemoteCancelRetiresOnlyThatCallAndReaderStillAcceptsOtherCalls()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstRequest = NewId();
        var handler = new RecordingHandler
        {
            OnInvoke = async (invocation, token) =>
            {
                if (invocation.RequestId != firstRequest) return CompanionOutcome.Done;
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { cancelled.TrySetResult(); }
                return CompanionOutcome.Done;
            },
        };
        await using var host = new IndependentHost(handler);
        await host.AuthenticateAsync();
        var sessionId = NewId();
        await host.InstanceAsync("startSession", sessionId);
        await host.InstanceAsync("invoke", sessionId, firstRequest);
        await EventuallyAsync(() => handler.Invocations.Count == 1);
        await host.SendAsync(ExtensionWire.Message("cancel", ("requestId", firstRequest)));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var next = NewId();
        await host.InstanceAsync("invoke", sessionId, next);
        using var result = await host.ReadDocumentAsync();
        Assert.Equal(next, result.RootElement.GetProperty("requestId").GetString());
        Assert.Equal("done", result.RootElement.GetProperty("outcome").GetString());
        Assert.True(handler.Sessions.Single().IsActive);
    }

    [WindowsFact]
    public async Task InvocationDeadlineCancelsAuthorWorkWithoutAHostCancelMessage()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new RecordingHandler
        {
            OnInvoke = async (_, token) =>
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { cancelled.TrySetResult(); }
                return CompanionOutcome.Done;
            },
        };
        await using var host = new IndependentHost(handler);
        await host.AuthenticateAsync();
        var sessionId = NewId();
        await host.InstanceAsync("startSession", sessionId);
        await host.InstanceAsync("invoke", sessionId, NewId());
        await EventuallyAsync(() => handler.Invocations.Count == 1);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.True(handler.Sessions.Single().IsActive);
    }

    [WindowsFact]
    public async Task FullSessionAndInvokeBudgetsRefuseOverflowWithoutRunningExtraHandlers()
    {
        var handler = new RecordingHandler
        {
            OnInvoke = async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return CompanionOutcome.Done;
            },
        };
        await using var host = new IndependentHost(handler);
        await host.AuthenticateAsync();
        var sessions = Enumerable.Range(0, 64).Select(_ => NewId()).ToArray();
        foreach (var session in sessions) await host.InstanceAsync("startSession", session);
        await EventuallyAsync(() => handler.Sessions.Count == 64);
        for (var index = 0; index < 32; index++) await host.InstanceAsync("invoke", sessions[index], NewId());
        await EventuallyAsync(() => handler.Invocations.Count == 32);
        var overflow = NewId();
        await host.InstanceAsync("invoke", sessions[0], overflow);
        using (var refusal = await host.ReadDocumentAsync())
        {
            Assert.Equal(overflow, refusal.RootElement.GetProperty("requestId").GetString());
            Assert.Equal("refused", refusal.RootElement.GetProperty("outcome").GetString());
        }
        Assert.Equal(32, handler.Invocations.Count);
        await host.InstanceAsync("startSession", NewId());
        Assert.Null((await host.ReadAsync()));
        await EventuallyAsync(() => handler.Sessions.All(session => !session.IsActive));
        Assert.Equal(64, handler.Sessions.Count);
    }

    [WindowsFact]
    public async Task FaceFloodHasBoundedAdmissionAndOldSessionCannotPublishAfterDisconnect()
    {
        var handler = new RecordingHandler();
        await using var host = new IndependentHost(handler);
        await host.AuthenticateAsync();
        await host.InstanceAsync("startSession", NewId());
        await EventuallyAsync(() => handler.Sessions.Count == 1);
        var session = handler.Sessions.Single();
        var rejected = 0;
        for (var index = 0; index < 1024; index++)
            if (!session.SetFace(new CompanionFace("Live", 5))) rejected++;
        Assert.True(rejected > 0);
        Assert.True(session.IsActive); // Face pressure returns false; it does not revoke consent.
        await host.StopClientAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(session.IsActive);
        Assert.False(session.SetFace(new CompanionFace("Late", 5)));
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private sealed class IndependentHost : IAsyncDisposable
    {
        private readonly NamedPipeServerStream _pipe;
        private readonly byte[] _secret = RandomNumberGenerator.GetBytes(32);
        private readonly string _registrationId = NewId();
        private readonly ExtensionPairing _pairing;
        private readonly CancellationTokenSource _stop = new();
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(30));
        private readonly Task _client;

        internal IndependentHost(IContributionHandler handler, ExternalExtensionManifest? manifest = null)
        {
            var name = $"Orbit.Extensions.v2.sdk-test.0123456789abcdef.{_registrationId}";
            _pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly | PipeOptions.FirstPipeInstance);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                protocolVersion = 2, pipeName = name, registrationId = _registrationId,
                extensionId = ExtensionId, secret = Convert.ToBase64String(_secret),
            });
            _pairing = ExtensionPairing.Parse(bytes, ExtensionId, 2);
            _client = new CompanionClient().RunAsync(_pairing, manifest ?? Manifest, handler, _stop.Token);
        }

        internal async Task AuthenticateAsync(bool correctProof = true)
        {
            await _pipe.WaitForConnectionAsync(_deadline.Token);
            using var hello = await ReadDocumentAsync();
            Assert.Equal(2, hello.RootElement.GetProperty("protocolVersion").GetInt32());
            Assert.Equal(_registrationId, hello.RootElement.GetProperty("registrationId").GetString());
            var clientNonce = hello.RootElement.GetProperty("clientNonce").GetString()!;
            var serverNonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var challenge = JsonSerializer.SerializeToUtf8Bytes(new
            {
                type = "challenge", serverNonce,
                proof = correctProof ? Proof("server", clientNonce, serverNonce) : Convert.ToBase64String(new byte[32]),
            });
            // Challenge framing is deliberately fragmented, including its four-byte header.
            var header = BitConverter.GetBytes(challenge.Length);
            foreach (var value in header) await _pipe.WriteAsync(new[] { value }, _deadline.Token);
            await _pipe.WriteAsync(challenge.AsMemory(0, 3), _deadline.Token);
            await _pipe.WriteAsync(challenge.AsMemory(3), _deadline.Token);
            await _pipe.FlushAsync(_deadline.Token);
            if (!correctProof) return;
            using var response = await ReadDocumentAsync();
            Assert.Equal("authenticate", response.RootElement.GetProperty("type").GetString());
            Assert.Equal(Proof("client", clientNonce, serverNonce), response.RootElement.GetProperty("proof").GetString());
            await SendAsync(Encoding.UTF8.GetBytes("""{"type":"ready"}"""));
        }

        // Compute the transcript here, independently from the production Proof helper.
        private string Proof(string role, string clientNonce, string serverNonce) =>
            Convert.ToBase64String(HMACSHA256.HashData(_secret, Encoding.UTF8.GetBytes(
                $"Orbit.Extensions.v2\n{role}\n{_registrationId}\n{clientNonce}\n{serverNonce}")));

        internal Task InstanceAsync(string type, string sessionId, string? requestId = null, string mode = "on")
        {
            var message = new Dictionary<string, object>
            {
                ["type"] = type, ["sessionId"] = sessionId, ["actionId"] = ActionId,
                ["settings"] = new Dictionary<string, string> { ["mode"] = mode },
            };
            if (requestId is not null) message["requestId"] = requestId;
            return SendAsync(JsonSerializer.SerializeToUtf8Bytes(message));
        }

        internal Task SendAsync(byte[] message) => ExtensionWire.WriteAsync(_pipe, message, _deadline.Token);
        internal Task<byte[]?> ReadAsync() => ExtensionWire.ReadAsync(_pipe, _deadline.Token);
        internal async Task<JsonDocument> ReadDocumentAsync() =>
            JsonDocument.Parse(await ReadAsync() ?? throw new InvalidDataException("The test peer disconnected."));

        internal async Task StopClientAsync()
        {
            await _stop.CancelAsync();
            await _client.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public async ValueTask DisposeAsync()
        {
            try { await StopClientAsync(); }
            finally
            {
                await _pipe.DisposeAsync();
                _pairing.Dispose();
                _stop.Dispose();
                _deadline.Dispose();
                CryptographicOperations.ZeroMemory(_secret);
            }
        }
    }
}
