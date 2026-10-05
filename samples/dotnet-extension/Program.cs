using System.Security.Cryptography;
using Orbit.Extensions.Protocol;
using Orbit.Extensions.Sdk;

namespace DotnetExtensionSample;

internal static class Program
{
    private const string ExtensionId = "example.dotnet-state";
    private const int MaxPairingBytes = 4096;

    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
            return Failure("This sample uses Orbit's Windows named-pipe companion transport.");
        if (args.Length != 4 || args[0] != "--manifest" || args[2] != "--pairing")
            return Failure("Usage: DotnetExtensionSample --manifest <extension.json> --pairing <private pairing-file path>");

        using var stopping = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stopping.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var manifestBytes = await ReadBoundedAsync(args[1], ExternalExtensionManifestReader.MaxBytes, stopping.Token);
            var manifest = ExternalExtensionManifestReader.Read(manifestBytes).Manifest;
            if (manifest is not { ManifestVersion: 2, Id: ExtensionId })
                return Failure("Choose this sample's valid schema-2 extension.json manifest.");

            using var pairing = ReadPairing(await ReadBoundedAsync(args[3], MaxPairingBytes, stopping.Token));
            Console.WriteLine("Sample state starts Off and exists only in memory.");
            Console.WriteLine("Waiting for the enabled Orbit host. Press Ctrl+C to stop.");
            await new CompanionClient().RunAsync(pairing, manifest, new StateHandler(), stopping.Token);
            return 0;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            Console.WriteLine("Stopped.");
            return 0;
        }
        catch (Exception)
        {
            // File paths, pairing contents and exception messages never reach the console.
            return Failure("Could not run the sample. Check its manifest, current pairing file and supported Orbit build.");
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static ExtensionPairing ReadPairing(byte[] bytes)
    {
        try { return ExtensionPairing.Parse(bytes, ExtensionId, expectedProtocolVersion: 2); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static async Task<byte[]> ReadBoundedAsync(string path, int maximum, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 4096, options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length is 0 || file.Length > maximum) throw new InvalidDataException();
        var buffer = new byte[maximum + 1];
        try
        {
            var count = 0;
            while (count < buffer.Length)
            {
                var read = await file.ReadAsync(buffer.AsMemory(count), cancellationToken);
                if (read == 0) break;
                count += read;
            }
            // Check the bytes read too: the file can change after the initial length check.
            if (count == 0 || count > maximum) throw new InvalidDataException();
            return buffer[..count];
        }
        finally { CryptographicOperations.ZeroMemory(buffer); }
    }

    private static int Failure(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }
}

internal sealed class StateHandler : IContributionHandler
{
    private const string SetState = "example.dotnet-state/set-state";
    private const string Status = "example.dotnet-state/status";
    private int _isOn;
    private int _announced;

    public async Task RunSessionAsync(CompanionSession session, CancellationToken cancellationToken)
    {
        if (!ValidSettings(session))
        {
            session.Fail(CompanionFailure.NeedsSetup);
            return;
        }
        if (Interlocked.Exchange(ref _announced, 1) == 0)
            Console.WriteLine("A sample session is active.");

        while (session.IsActive)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var on = Volatile.Read(ref _isOn) != 0;
            var number = session.ActionId == Status && session.Settings["display"] == "number";
            var line = number ? (on ? "1" : "0") : (on ? "On" : "Off");
            var face = new CompanionFace(line, 5)
            {
                State = on ? CompanionFaceState.On : CompanionFaceState.Off,
                Detail = on ? "The sample's in-memory state is on." : "The sample's in-memory state is off.",
                Line2 = session.ActionId == SetState
                    ? session.Settings["mode"] == "on" ? "Sets on" : "Sets off"
                    : "Memory only",
            };
            if (!session.SetFace(face)) return;
            // Renew finite state while this session exists; cancellation ends this loop.
            // A successful pick is reflected in every session at its next renewal.
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }
    }

    public Task<CompanionOutcome> InvokeAsync(CompanionInvocation invocation, CancellationToken cancellationToken)
    {
        var session = invocation.Session;
        if (session.ActionId != SetState) return Task.FromResult(CompanionOutcome.Unsupported);
        if (!ValidSettings(session)) return Task.FromResult(CompanionOutcome.Refused);
        var next = session.Settings["mode"] == "on" ? 1 : 0;
        // Real adapters must repeat these checks immediately before their external mutation.
        cancellationToken.ThrowIfCancellationRequested();
        if (!session.IsActive) return Task.FromResult(CompanionOutcome.Refused);
        Interlocked.Exchange(ref _isOn, next);
        Console.WriteLine(next == 1 ? "Sample state: On." : "Sample state: Off.");
        return Task.FromResult(CompanionOutcome.Done);
    }

    private static bool ValidSettings(CompanionSession session) => session.ActionId switch
    {
        SetState => session.Settings.TryGetValue("mode", out var mode) && mode is "on" or "off",
        Status => session.Settings.TryGetValue("display", out var display) && display is "words" or "number",
        _ => false,
    };
}
