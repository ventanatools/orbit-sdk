// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Globalization;

namespace VentanaTools.Orbit.Extensions.Tool.Tests;

/// <summary>
/// The test assembly is also the program that the run and simulate tests start
/// (<c>dotnet &lt;this assembly&gt; …</c>): a real companion with <c>--companion</c>, a process that
/// prints a line and waits with <c>--sleep</c>, and one that exits at once with <c>--exit &lt;code&gt;</c>.
/// The test host never calls this entry point.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        switch (args)
        {
            case ["--exit", var code, ..]:
                return int.Parse(code, CultureInfo.InvariantCulture);
            case ["--sleep", ..]:
                Console.WriteLine("companion started");
                await Task.Delay(Timeout.Infinite).ConfigureAwait(false);
                return 0;
            case ["--companion", ..] when OperatingSystem.IsWindows():
                return await CompanionApp.RunAsync(args[1..], new TestCompanionHandler(), new CompanionAppOptions { WatchFiles = false })
                    .ConfigureAwait(false);
            default:
                return 0;
        }
    }
}

/// <summary>
/// The test companion: a widget whose face shows how often it was invoked (<c>ready</c> before
/// the first time), and an action whose <c>mode</c> setting decides between Done and Failed (Network).
/// </summary>
internal sealed class TestCompanionHandler : ContributionHandler
{
    private readonly Lock _gate = new();
    private readonly List<Session> _faces = [];
    private int _count;

    public override async Task RunSessionAsync(Session session, CancellationToken cancellationToken)
    {
        if ((session.Provides & Provides.Face) == 0)
        {
            return;
        }

        lock (_gate)
        {
            _faces.Add(session);
        }

        try
        {
            Publish(session, null);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _faces.Remove(session);
            }
        }
    }

    public override Task<InvokeResult> InvokeAsync(Invocation invocation, CancellationToken cancellationToken)
    {
        if (invocation.Session.Settings.TryGetValue("mode", out var mode) && mode == "fail")
        {
            return Task.FromResult(InvokeResult.Failed(Failure.Network));
        }

        Session[] sessions;
        int count;
        lock (_gate)
        {
            count = ++_count;
            sessions = [.. _faces];
        }

        foreach (var session in sessions)
        {
            Publish(session, count);
        }

        return Task.FromResult(InvokeResult.Done);
    }

    private static void Publish(Session session, int? count) => session.SetFace(new Face
    {
        Picture = FacePicture.Glyph(""),
        Line1 = count?.ToString(CultureInfo.InvariantCulture) ?? "ready",
        Line2 = "Test",
        State = FaceState.On,
        GoodFor = TimeSpan.FromMinutes(1),
    });
}
