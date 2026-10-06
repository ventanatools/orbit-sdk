// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Security.Cryptography;
using VentanaTools.Orbit.Extensions;

namespace CountdownExtensionSample;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!OperatingSystem.IsWindows())
            return Failure("This sample uses Orbit's Windows named-pipe companion transport.");
        if (args.Length != 4 || args[0] != "--manifest" || args[2] != "--pairing")
            return Failure("Usage: CountdownExtensionSample --manifest <extension.json> --pairing <private pairing-file path>");

        using var stopping = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; stopping.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var manifestBytes = await ReadBoundedAsync(args[1], ExternalExtensionManifestReader.MaxBytes, stopping.Token);
            var manifest = ExternalExtensionManifestReader.Read(manifestBytes).Manifest;
            if (manifest is not { ManifestVersion: 2, Id: CountdownChoices.ExtensionId })
                return Failure("Choose this sample's valid schema-2 extension.json manifest.");

            using var pairing = ReadPairing(await ReadBoundedAsync(args[3], ExtensionPairing.MaxBytes, stopping.Token));
            Console.WriteLine("Countdown starts ready at 5:00. State exists only in this process; there is no alarm sound.");
            Console.WriteLine("Waiting for the enabled Orbit host. Press Ctrl+C to stop.");
            var handler = new CountdownHandler(new Countdown(TimeProvider.System));
            await new CompanionClient().RunAsync(pairing, manifest, handler, stopping.Token);
            return 0;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            Console.WriteLine("Stopped.");
            return 0;
        }
        catch (Exception)
        {
            // Never print pairing contents, file paths or exception prose.
            return Failure("Could not run the sample. Check its manifest, current pairing file and supported Orbit build.");
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

    private static ExtensionPairing ReadPairing(byte[] bytes)
    {
        try { return ExtensionPairing.Parse(bytes, CountdownChoices.ExtensionId, expectedProtocolVersion: 2); }
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
