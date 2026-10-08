// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.ComponentModel;
using System.Globalization;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VentanaTools.Orbit.Extensions.Wire;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tool.Tests;

/// <summary>
/// Runs simulate against the test companion (this assembly's <c>--companion</c> mode) over a real
/// named pipe. Windows only: the companion transport is a Windows named pipe.
/// </summary>
[Trait("Platform", "Windows")]
public sealed class SimulateTests
{
    private const int ErrorPipeBusy = 231;
    private const int FileAllAccess = 0x001F01FF; // FILE_ALL_ACCESS, the SDDL right FA

    // The tests that drive the handshake by hand aren't about its deadline: on a loaded runner the hello can
    // come more than the host's 5 seconds after the connection is accepted, which the host answers with
    // auth.timeout instead of a challenge.
    private static readonly TimeSpan TestHandshakeTimeout = TimeSpan.FromSeconds(30);

    private static string TestAssembly => typeof(SimulateTests).Assembly.Location;

    [WindowsFact]
    public void TheTemporaryPairingIsReadableByThisUserButNotByALowIntegrityProcess()
    {
        // Without a mandatory label a Low-integrity process of the same user could read the secret and pose as the host.
        using var registration = SimulationRegistration.Create("example-host", "example.tool-test");
        Assert.Contains("\"secret\"", File.ReadAllText(registration.PairingPath), StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(registration.Folder));
        Assert.Equal("refused", LowIntegrityToken.Run(() => Attempt(() => File.ReadAllBytes(registration.PairingPath).Length)));
        Assert.Equal("refused", LowIntegrityToken.Run(() => Attempt(() => Directory.GetFiles(registration.Folder).Length)));
        registration.Dispose();
        Assert.False(Directory.Exists(registration.Folder));
    }

    // A sandboxed process of the same user passes the pipe's one-entry access list. Under the default label (no write-up
    // only) it could open the one instance read-only and hold it through each handshake timeout, keeping the companion
    // out; the pipe's Medium no-read-up label refuses it every kind of open, so the companion still connects.
    [WindowsFact]
    public async Task TheSimulatedPipeRefusesALowIntegrityProcessAndRemoteClients_AndTheUserStillConnects()
    {
        var name = "ventana-tool-test-" + Guid.NewGuid().ToString("N");
        await using var server = SimulatedHost.CreatePipe(name);
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User!;

        foreach (var access in new[] { LowIntegrityToken.GenericRead, LowIntegrityToken.GenericRead | LowIntegrityToken.GenericWrite,
            LowIntegrityToken.GenericWrite })
        {
            var (handle, error) = LowIntegrityToken.OpenPipe(name, access);
            handle?.Dispose();
            Assert.True(handle is null, "a Low-integrity open with access 0x" + access.ToString("X8", CultureInfo.InvariantCulture) + " succeeded");
            Assert.Equal(LowIntegrityToken.ErrorAccessDenied, error);
        }

        // Windows reads the descriptor back as O:<user>D:P(A;;FA;;;<user>)S:AI(ML;;NWNRNX;;;ME), but writes a SID it has
        // an alias for as that alias (the built-in Administrator account, which GitHub's Windows runner uses, reads as
        // LA), so the owner and the one entry are compared as SIDs, not as text.
        var sddl = PipeFacts.Sddl(server.SafePipeHandle);
        var descriptor = new RawSecurityDescriptor(sddl);
        Assert.Equal(user, descriptor.Owner);
        Assert.True((descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0, sddl);
        var entry = Assert.IsType<CommonAce>(Assert.Single(descriptor.DiscretionaryAcl!.Cast<GenericAce>()));
        Assert.Equal(AceQualifier.AccessAllowed, entry.AceQualifier);
        Assert.Equal(user, entry.SecurityIdentifier);
        Assert.Equal(FileAllAccess, entry.AccessMask);
        var label = Regex.Match(sddl, @"S:[A-Z]*\(ML;;(?<rights>[A-Z]+);;;ME\)$");
        Assert.True(label.Success, sddl);
        Assert.Equal(["NR", "NW", "NX"], label.Groups["rights"].Value.Chunk(2).Select(pair => new string(pair)).Order(StringComparer.Ordinal));
        var flags = PipeFacts.Flags(server.SafePipeHandle);
        Assert.Equal(PipeFacts.ServerEnd | PipeFacts.RejectRemoteClients, flags & (PipeFacts.ServerEnd | PipeFacts.RejectRemoteClients));

        // The name stays the first and only instance's: a second server of it gets ERROR_PIPE_BUSY, an IOException.
        var second = Record.Exception(() => SimulatedHost.CreatePipe(name).Dispose());
        Assert.True(second is IOException { InnerException: Win32Exception { NativeErrorCode: ErrorPipeBusy } },
            second?.ToString() ?? "a second server took the name");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await accepted;
        Assert.True(server.IsConnected, "no refused open held the instance");
        AssertOwnedByTheUser(client);
    }

    [WindowsFact]
    public void ThePipesDescriptor_GrantsOnlyTheUser_AndCarriesTheMediumNoReadUpLabel()
    {
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        Assert.Equal("O:" + user.Value + "D:P(A;;FA;;;" + user.Value + ")S:(ML;;NRNWNX;;;ME)", SimulatedHost.PipeDescriptor(user));
    }

    // Disconnecting a named pipe discards what the companion has not read, so after an error frame the simulated host
    // waits for the companion to close before it readies its pipe's one instance again (contract §8.2). The wait has no
    // bound here, so only the companion's close can end it: the short probe can only find the instance taken, and the
    // frame read after it still arrives.
    [WindowsFact]
    public async Task AfterAnErrorFrame_TheSimulatedHostWaitsForTheCompanionToClose_SoAFrameReadLateStillArrives()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var registration = SimulationRegistration.Create("example-host", "example.tool-test");
        var transcript = new TranscriptLines();
        await using var host = new SimulatedHost(ToolTestManifest(), registration, transcript.Create())
        {
            ErrorFlushTimeout = Timeout.InfiniteTimeSpan,
            HandshakeTimeout = TestHandshakeTimeout,
        };
        host.Start();

        await using var late = await TryConnectAsync(registration.PipeName, TimeSpan.FromSeconds(10));
        Assert.True(late is not null, "the companion could not open the pipe");
        await WriteAsync(late, Hello(Guid.NewGuid().ToString("N")), timeout.Token);
        await transcript.WaitForAsync("disconnected", timeout.Token);

        await using (var probe = await TryConnectAsync(registration.PipeName, TimeSpan.FromMilliseconds(250)))
        {
            Assert.True(probe is null, "the host readied the pipe before the companion read its error frame");
        }

        var error = Assert.IsType<ErrorMessage>(await ReadAsync(late, ConnectionPhase.Handshake, timeout.Token));
        Assert.Equal(ReasonCode.AuthRegistrationMismatch, error.Code);
        await late.DisposeAsync();

        await using var next = await TryConnectAsync(registration.PipeName, TimeSpan.FromSeconds(10));
        Assert.True(next is not null, "once the companion closed, the host readied the pipe for the next one");
    }

    [WindowsFact]
    public async Task ACompanionThatKeepsItsEndOpenAfterAnErrorFrame_IsDisconnectedAfterTheErrorFlushTime()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var registration = SimulationRegistration.Create("example-host", "example.tool-test");
        await using var host = new SimulatedHost(ToolTestManifest(), registration, new TranscriptLines().Create())
        {
            HandshakeTimeout = TestHandshakeTimeout,
        };
        Assert.Equal(TimeSpan.FromSeconds(1), host.ErrorFlushTimeout);
        host.Start();

        await using var stubborn = await TryConnectAsync(registration.PipeName, TimeSpan.FromSeconds(10));
        Assert.True(stubborn is not null, "the companion could not open the pipe");
        await WriteAsync(stubborn, Hello(Guid.NewGuid().ToString("N")), timeout.Token);
        var error = Assert.IsType<ErrorMessage>(await ReadAsync(stubborn, ConnectionPhase.Handshake, timeout.Token));
        Assert.Equal(ReasonCode.AuthRegistrationMismatch, error.Code);

        // The companion never closes: the bound ends the wait and the host disconnects it.
        var after = await Record.ExceptionAsync(async () => Assert.Null(await ReadAsync(stubborn, ConnectionPhase.Handshake, timeout.Token)));
        Assert.True(after is null or IOException, after?.ToString());
        await using var next = await TryConnectAsync(registration.PipeName, TimeSpan.FromSeconds(10));
        Assert.True(next is not null, "the host readied the pipe for the next companion");
    }

    // A stopping host closes its pipe rather than disconnecting it, so the shutdown frame stays readable after
    // DisposeAsync returns.
    [WindowsFact]
    public async Task TheShutdownErrorFrame_StaysReadableAfterTheSimulatedHostStops()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var registration = SimulationRegistration.Create("example-host", "example.tool-test");
        var manifest = ToolTestManifest();
        var host = new SimulatedHost(manifest, registration, new TranscriptLines().Create())
        {
            HandshakeTimeout = TestHandshakeTimeout,
        };
        NamedPipeClientStream companion;
        try
        {
            host.Start();
            companion = await ConnectAndAuthenticateAsync(host, registration, manifest, timeout.Token);
        }
        catch
        {
            await host.DisposeAsync();
            throw;
        }

        await using (companion)
        {
            await host.DisposeAsync();

            var error = Assert.IsType<ErrorMessage>(await ReadAsync(companion, ConnectionPhase.Authenticated, timeout.Token));
            Assert.Equal(ReasonCode.HostShuttingDown, error.Code);
        }
    }

    private static string Attempt(Func<int> read)
    {
        try
        {
            return "read " + read();
        }
        catch (UnauthorizedAccessException)
        {
            return "refused";
        }
    }

    [WindowsFact]
    public async Task AScriptDrivesTheCompanionThroughSessionsInvocationsAndAReconnect()
    {
        using var project = new TempFolder();
        project.Write("extension.json", TestProject.Manifest("example-host"));
        project.Write("script.json", """
            [
              { "start": "example.tool-test/widget", "as": "w" },
              { "expectFace": "w", "within": "20s", "line1": "ready", "state": "On" },
              { "invoke": "w", "expect": "Done" },
              { "expectFace": "w", "within": "20s", "line1": "1" },
              { "start": "example.tool-test/action", "settings": { "mode": "fail" }, "as": "a" },
              { "invoke": "a", "expect": "Failed", "failure": "Network" },
              { "stop": "w" },
              { "disconnect": "host.reloaded" },
              { "start": "example.tool-test/widget", "as": "again" },
              { "expectFace": "again", "within": "20s" }
            ]
            """);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var run = await ToolHarness.RunAsync(project.Path,
            ["simulate", "--script", "script.json", "--json", "--", "dotnet", TestAssembly, "--companion"], null, null, timeout.Token);
        Assert.True(run.ExitCode == 0, run.ToString());

        var entries = run.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => JsonDocument.Parse(line).RootElement)
            .Select(entry => (From: entry.GetProperty("from").GetString(), Type: entry.GetProperty("type").GetString(), Entry: entry))
            .ToList();
        var types = entries.Select(entry => entry.From + " " + entry.Type).ToList();
        AssertInOrder(types,
            "Tool listening", "Tool started", "Companion hello", "Host challenge", "Companion authenticate", "Host ready",
            "Host startSession", "Companion setFace", "Host invoke", "Companion result", "Host startSession", "Host invoke",
            "Companion result", "Host stopSession", "Host error", "Tool disconnected", "Companion hello", "Host ready",
            "Host startSession", "Companion setFace", "Tool finished");
        var failed = entries.Where(entry => entry.Type == "result").Select(entry => entry.Entry.GetProperty("detail").GetString()).ToList();
        Assert.Contains("Failed (Network)", failed);
        Assert.Contains("Done", failed);
        Assert.DoesNotContain(entries, entry => entry.Entry.ToString().Contains("secret", StringComparison.OrdinalIgnoreCase)
            || entry.Entry.ToString().Contains("proof", StringComparison.OrdinalIgnoreCase));
        var registration = entries.First(entry => entry.Type == "listening").Entry.GetProperty("detail").GetString()!.Split(' ')[^1];
        Assert.DoesNotContain(Directory.EnumerateDirectories(Path.GetTempPath(), "ventana-simulate-*"),
            folder => Directory.EnumerateFiles(folder).Any(file => File.ReadAllText(file).Contains(registration, StringComparison.Ordinal)));
    }

    [WindowsFact]
    public async Task AFailedExpectationExitsOne()
    {
        using var project = new TempFolder();
        project.Write("extension.json", TestProject.Manifest("example-host"));
        project.Write("script.json", """
            [
              { "start": "example.tool-test/action", "as": "a" },
              { "invoke": "a", "expect": "Refused" }
            ]
            """);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var run = await ToolHarness.RunAsync(project.Path,
            ["simulate", "--script", "script.json", "--", "dotnet", TestAssembly, "--companion"], null, null, timeout.Token);
        Assert.Equal(1, run.ExitCode);
        Assert.Contains("expected Refused, got Done.", run.Error, StringComparison.Ordinal);
        Assert.Contains("expectationFailed", run.Out, StringComparison.Ordinal);
    }

    [WindowsFact]
    public async Task ThePromptStartsTheCompanionAndQuits()
    {
        using var project = new TempFolder();
        project.Write("extension.json", TestProject.Manifest("example-host"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var run = await ToolHarness.RunAsync(project.Path, ["simulate", "--", "dotnet", TestAssembly, "--sleep"], "faces\nbogus\nquit\n", null, timeout.Token);
        Assert.Equal(0, run.ExitCode);
        Assert.Contains("listening", run.Out, StringComparison.Ordinal);
        Assert.Contains("no sessions", run.Out, StringComparison.Ordinal);
        Assert.Contains("error: unknown command", run.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnInvalidScriptIsReportedBeforeAnythingStarts()
    {
        using var project = new TempFolder();
        project.Write("extension.json", TestProject.Manifest("example-host"));
        project.Write("script.json", """[ { "invoke": "s1", "expect": "Maybe" }, { "wait": 5 } ]""");
        var run = await ToolHarness.RunAsync(project.Path, "simulate", "--script", "script.json", "--", "ventana-never-started");
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(1, run.ExitCode);
            Golden.Match("simulate-invalid-script", run, project.Path);
        }
        else
        {
            Assert.Equal(5, run.ExitCode);
        }
    }

    [WindowsFact]
    public async Task ACompanionThatCannotStartIsFive()
    {
        using var project = new TempFolder();
        project.Write("extension.json", TestProject.Manifest("example-host"));
        var run = await ToolHarness.RunAsync(project.Path, "simulate", "--", "ventana-no-such-program-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(5, run.ExitCode);
    }

    private static void AssertInOrder(List<string> actual, params string[] expected)
    {
        var at = 0;
        foreach (var item in expected)
        {
            var found = actual.FindIndex(at, value => value == item);
            Assert.True(found >= 0, "Missing '" + item + "' after position " + at + " in:\n" + string.Join("\n", actual));
            at = found + 1;
        }
    }

    private static ExtensionManifest ToolTestManifest() =>
        ManifestReader.Read(Encoding.UTF8.GetBytes(TestProject.Manifest("example-host"))).Value
            ?? throw new InvalidOperationException("The test manifest does not read.");

    private static HelloMessage Hello(string registrationId, string? manifestHash = null) => new()
    {
        MinVersion = ProtocolVersions.Min,
        MaxVersion = ProtocolVersions.Max,
        RegistrationId = registrationId,
        ClientNonce = Handshake.NewNonce(),
        Capabilities = [],
        Client = new ClientInfo { Name = "tool-tests", Version = "1" },
        ManifestHash = manifestHash ?? ManifestWriter.ComputeHash(ToolTestManifest()),
    };

    // The companion's half of the handshake, over a raw client of the simulated host's pipe.
    private static async Task<NamedPipeClientStream> ConnectAndAuthenticateAsync(SimulatedHost host, SimulationRegistration registration,
        ExtensionManifest manifest, CancellationToken cancellationToken)
    {
        var client = await TryConnectAsync(registration.PipeName, TimeSpan.FromSeconds(10))
            ?? throw new TimeoutException("The companion could not open the pipe.");
        try
        {
            var hello = Hello(registration.RegistrationId, ManifestWriter.ComputeHash(manifest));
            await WriteAsync(client, hello, cancellationToken);
            var challenge = Assert.IsType<ChallengeMessage>(await ReadAsync(client, ConnectionPhase.Handshake, cancellationToken));
            var transcript = new HandshakeTranscript
            {
                HostId = challenge.Host.Id,
                HostVersion = challenge.Host.Version,
                RegistrationId = hello.RegistrationId,
                ClientNonce = hello.ClientNonce,
                ServerNonce = challenge.ServerNonce,
                Version = challenge.Version,
                MinVersion = hello.MinVersion,
                MaxVersion = hello.MaxVersion,
                ClientCapabilities = hello.Capabilities,
                HostCapabilities = challenge.Capabilities,
                ManifestHash = hello.ManifestHash,
            };
            Assert.True(Handshake.VerifyProof(registration.Secret, challenge.Proof, transcript, ProofRole.Server), "the host's proof does not verify");
            await WriteAsync(client, new AuthenticateMessage { Proof = Handshake.ComputeProof(registration.Secret, transcript, ProofRole.Client) },
                cancellationToken);
            Assert.IsType<ReadyMessage>(await ReadAsync(client, ConnectionPhase.PeerVerified, cancellationToken));
            Assert.True(await host.WaitForConnectionAsync(TimeSpan.FromSeconds(10), cancellationToken), "the host did not take the connection");
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    // A client of the pipe, which it checks is owned by the user, or null when no instance became free within the time.
    private static async Task<NamedPipeClientStream?> TryConnectAsync(string pipeName, TimeSpan within)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await client.ConnectAsync((int)within.TotalMilliseconds);
            AssertOwnedByTheUser(client);
            return client;
        }
        catch (TimeoutException)
        {
            await client.DisposeAsync();
            return null;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    // What a companion checks before it writes (contract §7.1): the pipe's owner, read through the client's handle, is the
    // user SID of its token. A test client does not use PipeOptions.CurrentUserOnly, which compares the owner with the
    // token's default owner instead: the Administrators group in an elevated process, as on GitHub's Windows runners,
    // where it refuses the simulated host's pipe that the user's own SID owns.
    private static void AssertOwnedByTheUser(NamedPipeClientStream client)
    {
        using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(identity.User, client.GetAccessControl().GetOwner(typeof(SecurityIdentifier)));
    }

    private static async Task WriteAsync(Stream stream, WireMessage message, CancellationToken cancellationToken) =>
        await Framing.WriteFrameAsync(stream, MessageWriter.Write(message), cancellationToken);

    // The host's next message, or null at the end of the stream.
    private static async Task<WireMessage?> ReadAsync(Stream stream, ConnectionPhase phase, CancellationToken cancellationToken)
    {
        var frame = await Framing.ReadFrameAsync(stream, Framing.MaxFrameBytes, TimeProvider.System, cancellationToken);
        if (frame is null)
        {
            return null;
        }

        var read = MessageReader.Read(frame, Sender.Host, phase);
        return read.Message ?? throw new InvalidDataException("The host's frame does not read: " + (read.Violation ?? read.Ignored)?.Value);
    }

    // The simulated host's transcript as JSON lines, for a test that waits on the tool's notes.
    private sealed class TranscriptLines : TextWriter
    {
        private readonly object _gate = new();
        private readonly StringBuilder _text = new();

        public override Encoding Encoding => Encoding.UTF8;

        public Transcript Create() => new(new ToolConsole
        {
            Out = this,
            Error = Null,
            In = TextReader.Null,
            WorkingDirectory = Path.GetTempPath(),
        }, json: true);

        public override void Write(char value)
        {
            lock (_gate)
            {
                _text.Append(value);
            }
        }

        public override void Write(string? value)
        {
            lock (_gate)
            {
                _text.Append(value);
            }
        }

        // Waits until the transcript has an entry of the type.
        public async Task WaitForAsync(string type, CancellationToken cancellationToken)
        {
            while (!Types().Contains(type, StringComparer.Ordinal))
            {
                await Task.Delay(10, cancellationToken);
            }
        }

        private List<string> Types()
        {
            string text;
            lock (_gate)
            {
                text = _text.ToString();
            }

            var types = new List<string>();
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                using var entry = JsonDocument.Parse(line);
                types.Add(entry.RootElement.GetProperty("type").GetString() ?? string.Empty);
            }

            return types;
        }
    }
}
