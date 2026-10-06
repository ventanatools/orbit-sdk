// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Text;
using System.Text.Json;
using VentanaTools.Orbit.Extensions.Wire;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tests.Client;

/// <summary>Every <c>fixtures/wire/v3</c> vector that a companion receives, run through the real client.</summary>
public sealed class WireVectorTests
{
    public static TheoryData<string, bool> SdkHandshakeCases()
    {
        var data = new TheoryData<string, bool>();
        using var document = Fixtures.Json("wire/v3/handshake.json");
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            if (item.GetProperty("sdk").ValueKind == JsonValueKind.Object)
            {
                data.Add(item.GetProperty("name").GetString()!, true);
                data.Add(item.GetProperty("name").GetString()!, false);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SdkHandshakeCases))]
    public async Task EveryScriptedHandshakeLeavesTheSdkInItsExpectedState(string name, bool verified)
    {
        using var document = Fixtures.Json("wire/v3/handshake.json");
        var item = document.RootElement.GetProperty("cases").EnumerateArray().Single(entry => entry.GetProperty("name").GetString() == name);
        var expected = item.GetProperty("sdk").GetProperty(verified ? "verified" : "unverified");
        var challengeValid = item.TryGetProperty("proofs", out var proofs) && proofs.GetProperty("challengeValid").GetBoolean();
        var clock = new TestClock();
        await using var harness = new ClientHarness(new TestHandler(), options: new CompanionClientOptions { TimeProvider = clock }, serverVerified: verified);
        harness.Start();
        var peer = await harness.NextPeerAsync();
        await peer.ReadHelloAsync();
        ChallengeMessage? challenge = null;
        foreach (var step in item.GetProperty("steps").EnumerateArray().Skip(1))
        {
            var frame = step.GetProperty("frame").GetString()!;
            if (step.TryGetProperty("atMs", out var at) && at.GetInt64() > (long)clock.Elapsed.TotalMilliseconds)
            {
                // The SDK pings after 30 idle seconds too; its pong must be read before the clock moves on.
                await ClientHarness.WaitForAsync(() => harness.Observer.FramesProcessed.Count(type => type == "pong") >= peer.PingsReceived.Count,
                    "pongs processed");
                await clock.AdvanceAsync(TimeSpan.FromMilliseconds(at.GetInt64()) - clock.Elapsed, TimeSpan.FromSeconds(5));
            }

            if (step.GetProperty("from").GetString() == "Companion")
            {
                var read = await peer.ReadAsync(Enum.Parse<ConnectionPhase>(step.GetProperty("phase").GetString()!));
                Assert.Equal(JsonDocument.Parse(frame).RootElement.GetProperty("type").GetString(), read?.Type);
                continue;
            }

            var parsed = MessageReader.Read(Encoding.UTF8.GetBytes(frame), Sender.Host, Enum.Parse<ConnectionPhase>(step.GetProperty("phase").GetString()!));
            if (parsed.Message is ChallengeMessage fixture && challengeValid)
            {
                // The SDK's hello carries its own nonce and manifest hash, so a valid proof is recomputed over it.
                challenge = peer.BuildChallenge(new HostScript
                {
                    Version = fixture.Version,
                    HostId = fixture.Host.Id,
                    HostVersion = fixture.Host.Version,
                    Capabilities = fixture.Capabilities,
                });
                await peer.SendAsync(challenge);
                continue;
            }

            await peer.SendJsonAsync(frame);
        }

        var state = Enum.Parse<ConnectionState>(expected.GetProperty("state").GetString()!);
        if (state == ConnectionState.Connected)
        {
            await ClientHarness.WaitForAsync(() => harness.Client.State == ConnectionState.Connected, "connected");
            await Task.Delay(50);
            Assert.Equal(ConnectionState.Connected, harness.Client.State);
            return;
        }

        var status = await harness.WaitForStatusAsync(entry => entry.State == state, state.ToString());
        Assert.Equal(expected.GetProperty("reason").GetString(), status.Reason?.Value);
        if (expected.TryGetProperty("retryAtMostMs", out var most))
        {
            Assert.NotNull(status.RetryIn);
            Assert.InRange(status.RetryIn!.Value.TotalMilliseconds, 0, most.GetInt64());
        }
    }

    public static TheoryData<string> HostInvalidCases()
    {
        var data = new TheoryData<string>();
        using var document = Fixtures.Json("wire/v3/messages-invalid.json");
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray().Where(entry => entry.GetProperty("sender").GetString() == "Host"))
        {
            data.Add(item.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(HostInvalidCases))]
    public async Task EveryInvalidHostFrameClosesWithItsCode(string name)
    {
        using var document = Fixtures.Json("wire/v3/messages-invalid.json");
        var item = document.RootElement.GetProperty("cases").EnumerateArray().Single(entry => entry.GetProperty("name").GetString() == name);
        var expected = item.GetProperty("expect").GetString();
        await using var harness = new ClientHarness(new TestHandler());
        harness.Start();
        var peer = await harness.NextPeerAsync();
        await peer.ReadHelloAsync();
        var phase = item.GetProperty("phase").GetString();
        if (phase != "Handshake")
        {
            await peer.SendAsync(peer.BuildChallenge(new HostScript()));
            Assert.IsType<AuthenticateMessage>(await peer.ReadAsync(ConnectionPhase.Handshake));
        }

        if (phase == "Authenticated")
        {
            await peer.SendAsync(ScriptedPeer.BuildReady(new HostScript()));
            await ClientHarness.WaitForAsync(() => harness.Client.State == ConnectionState.Connected, "connected");
        }

        await peer.SendBodyAsync(FrameOf(item));
        Assert.Equal(expected, (await peer.ReadCloseAsync())?.Value);
        var status = await harness.WaitForStatusAsync(entry => entry.State is ConnectionState.Waiting or ConnectionState.Stopped, "closed");
        Assert.Equal(expected, status.Reason?.Value);
    }

    public static TheoryData<string> HostIgnoredCases()
    {
        var data = new TheoryData<string>();
        using var document = Fixtures.Json("wire/v3/messages-ignored.json");
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray().Where(entry => entry.GetProperty("sender").GetString() == "Host"))
        {
            data.Add(item.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(HostIgnoredCases))]
    public async Task UnknownMembersTypesAndCodesFromTheHostAreAcceptedOrIgnored(string name)
    {
        using var document = Fixtures.Json("wire/v3/messages-ignored.json");
        var item = document.RootElement.GetProperty("cases").EnumerateArray().Single(entry => entry.GetProperty("name").GetString() == name);
        var frame = item.GetProperty("frame").GetString()!;
        var unknownCode = item.TryGetProperty("unknownCode", out var code) ? code.GetString() : null;
        await using var harness = new ClientHarness(new TestHandler(), serverVerified: false);
        harness.Start();
        var peer = await harness.NextPeerAsync();
        await peer.ReadHelloAsync();
        var phase = item.GetProperty("phase").GetString();
        var parsed = MessageReader.Read(Encoding.UTF8.GetBytes(frame), Sender.Host, Enum.Parse<ConnectionPhase>(phase!));
        if (phase == "Handshake" && parsed.Message is ChallengeMessage fixture)
        {
            var script = new HostScript { Capabilities = fixture.Capabilities };
            await peer.SendAsync(peer.BuildChallenge(script));
            Assert.IsType<AuthenticateMessage>(await peer.ReadAsync(ConnectionPhase.Handshake));
            await peer.SendAsync(ScriptedPeer.BuildReady(script));
            await ClientHarness.WaitForAsync(() => harness.Client.State == ConnectionState.Connected, "connected");
            return;
        }

        if (phase != "Handshake")
        {
            await peer.SendAsync(peer.BuildChallenge(new HostScript()));
            Assert.IsType<AuthenticateMessage>(await peer.ReadAsync(ConnectionPhase.Handshake));
        }

        if (phase == "Authenticated")
        {
            await peer.SendAsync(ScriptedPeer.BuildReady(new HostScript()));
            await ClientHarness.WaitForAsync(() => harness.Client.State == ConnectionState.Connected, "connected");
        }

        await peer.SendJsonAsync(frame);
        if (unknownCode is not null)
        {
            var status = await harness.WaitForStatusAsync(entry => entry.State == ConnectionState.Waiting, "waiting");
            Assert.Equal(unknownCode, status.Reason?.Value);
            Assert.False(status.Reason!.Value.IsKnown);
            Assert.InRange(status.RetryIn!.Value, TimeSpan.Zero, new CompanionClientOptions().MaxRetryDelay);
            return;
        }

        if (phase == "PeerVerified")
        {
            await ClientHarness.WaitForAsync(() => harness.Client.State == ConnectionState.Connected, "connected");
        }
        else
        {
            // A host ping is the barrier: its pong proves the frame was read and the connection kept.
            var barrier = parsed.Message is PingMessage ping ? ping.Id : 99;
            if (parsed.Message is not PingMessage)
            {
                await peer.SendAsync(new PingMessage { Id = barrier });
            }

            Assert.Equal(barrier, (await peer.ReadAsync<PongMessage>()).Id);
        }

        await Task.Delay(50);
        Assert.Equal(ConnectionState.Connected, harness.Client.State);
    }

    public static TheoryData<string> SessionCases()
    {
        var data = new TheoryData<string>();
        using var document = Fixtures.Json("wire/v3/sessions.json");
        foreach (var item in document.RootElement.GetProperty("cases").EnumerateArray())
        {
            data.Add(item.GetProperty("name").GetString()!);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(SessionCases))]
    public async Task StartSessionSettingsAreCheckedAsAMapAndRefusedWithTheirCode(string name)
    {
        using var document = Fixtures.Json("wire/v3/sessions.json");
        var item = document.RootElement.GetProperty("cases").EnumerateArray().Single(entry => entry.GetProperty("name").GetString() == name);
        var settings = item.GetProperty("settings").EnumerateObject().ToDictionary(member => member.Name, member => member.Value.GetString()!);
        var handler = new TestHandler();
        await using var harness = new ClientHarness(handler);
        harness.Start();
        var peer = await harness.ConnectAsync();
        var sessionId = ScriptedPeer.NewId();
        await peer.SendAsync(new StartSessionMessage { SessionId = sessionId, ContributionId = item.GetProperty("contributionId").GetString()!, Settings = settings });
        var expected = item.GetProperty("code").GetString();
        if (expected is null)
        {
            await ClientHarness.WaitForAsync(() => handler.Sessions.Any(session => session.Id == sessionId), "session");
            Assert.Equal(settings, handler.Sessions.Single().Settings);
            return;
        }

        var refused = await peer.ReadAsync<SessionRefusedMessage>();
        Assert.Equal(sessionId, refused.SessionId);
        Assert.Equal(expected, refused.Code.Value);
        Assert.Empty(handler.Sessions);
        Assert.Equal(ConnectionState.Connected, harness.Client.State);
    }

    internal static byte[] FrameOf(JsonElement item)
    {
        if (item.TryGetProperty("frame", out var text))
        {
            return Encoding.UTF8.GetBytes(text.GetString()!);
        }

        if (item.TryGetProperty("frameBase64", out var base64))
        {
            return Convert.FromBase64String(base64.GetString()!);
        }

        var repeat = item.GetProperty("frameRepeat");
        var prefix = Encoding.UTF8.GetBytes(repeat.GetProperty("prefix").GetString()!);
        var fill = Encoding.UTF8.GetBytes(repeat.GetProperty("fill").GetString()!);
        var suffix = Encoding.UTF8.GetBytes(repeat.GetProperty("suffix").GetString()!);
        var length = repeat.GetProperty("length").GetInt32();
        var body = new byte[length];
        prefix.CopyTo(body, 0);
        for (var index = prefix.Length; index < length - suffix.Length; index++)
        {
            body[index] = fill[(index - prefix.Length) % fill.Length];
        }

        suffix.CopyTo(body, length - suffix.Length);
        return body;
    }
}
