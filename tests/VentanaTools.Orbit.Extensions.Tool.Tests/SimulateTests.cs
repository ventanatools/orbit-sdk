// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Globalization;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace VentanaTools.Orbit.Extensions.Tool.Tests;

/// <summary>
/// Runs simulate against the test companion (this assembly's <c>--companion</c> mode) over a real
/// named pipe. Windows only: the companion transport is a Windows named pipe.
/// </summary>
[Trait("Platform", "Windows")]
public sealed class SimulateTests
{
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
        var user = identity.User!.Value;

        foreach (var access in new[] { LowIntegrityToken.GenericRead, LowIntegrityToken.GenericRead | LowIntegrityToken.GenericWrite,
            LowIntegrityToken.GenericWrite })
        {
            var (handle, error) = LowIntegrityToken.OpenPipe(name, access);
            handle?.Dispose();
            Assert.True(handle is null, "a Low-integrity open with access 0x" + access.ToString("X8", CultureInfo.InvariantCulture) + " succeeded");
            Assert.Equal(LowIntegrityToken.ErrorAccessDenied, error);
        }

        // Windows reads the descriptor back as O:<user>D:P(A;;FA;;;<user>)S:AI(ML;;NWNRNX;;;ME).
        var sddl = PipeFacts.Sddl(server.SafePipeHandle);
        Assert.StartsWith("O:" + user + "D:P(A;;FA;;;" + user + ")S:", sddl, StringComparison.Ordinal);
        var label = Regex.Match(sddl, @"S:[A-Z]*\(ML;;(?<rights>[A-Z]+);;;ME\)$");
        Assert.True(label.Success, sddl);
        Assert.Equal(["NR", "NW", "NX"], label.Groups["rights"].Value.Chunk(2).Select(pair => new string(pair)).Order(StringComparer.Ordinal));
        var flags = PipeFacts.Flags(server.SafePipeHandle);
        Assert.Equal(PipeFacts.ServerEnd | PipeFacts.RejectRemoteClients, flags & (PipeFacts.ServerEnd | PipeFacts.RejectRemoteClients));

        // The name stays the first and only instance's (Windows says ERROR_PIPE_BUSY or ERROR_ACCESS_DENIED).
        var second = Record.Exception(() => SimulatedHost.CreatePipe(name).Dispose());
        Assert.True(second is IOException or UnauthorizedAccessException, second?.ToString() ?? "a second server took the name");

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var accepted = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await accepted;
        Assert.True(server.IsConnected, "no refused open held the instance");
    }

    [WindowsFact]
    public void ThePipesDescriptor_GrantsOnlyTheUser_AndCarriesTheMediumNoReadUpLabel()
    {
        var user = new SecurityIdentifier("S-1-5-21-1-2-3-1001");
        Assert.Equal("O:" + user.Value + "D:P(A;;FA;;;" + user.Value + ")S:(ML;;NRNWNX;;;ME)", SimulatedHost.PipeDescriptor(user));
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
}
