// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace VentanaTools.Orbit.Extensions.Hosting.Tests;

/// <summary>
/// The test assembly is also a Generic Host companion that a test starts as a child process
/// (<c>dotnet &lt;this assembly&gt; --companion &lt;manifest&gt; &lt;pairing&gt;</c>), so the process's
/// own exit code is checked, not only the add-on's test hook. The test host never calls this entry point.
/// </summary>
internal static class Program
{
    // No return value: the process exits with Environment.ExitCode, which the add-on sets.
    public static async Task Main(string[] args)
    {
        if (args is not ["--companion", var manifest, var pairing])
        {
            return;
        }

        // As an application builds its host, without logging providers, so a test writes nothing to the event log.
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = [] });
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new Clock());
        builder.Services.AddCompanion<ClockWidget>(options =>
        {
            options.Arguments = [];
            options.ManifestPath = manifest;
            options.PairingPath = pairing;
            options.WatchFiles = false;
        });
        using var host = builder.Build();
        await host.RunAsync();
    }
}
