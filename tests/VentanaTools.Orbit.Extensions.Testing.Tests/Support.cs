// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Diagnostics;

namespace VentanaTools.Orbit.Extensions.Testing.Tests;

/// <summary>Manifests for the test-kit tests. Every one names only the test host id.</summary>
internal static class Manifests
{
    public const string Host = "example-host";
    public const string Widget = "example.kit/widget";
    public const string Action = "example.kit/action";
    public const string Both = "example.kit/both";

    public static ExtensionManifest Kit(params Contribution[] contributions) => new()
    {
        Id = "example.kit",
        Name = "Kit",
        Description = "Test-kit tests.",
        Version = "1.0.0",
        Hosts = [Host],
        Contributions = contributions.Length > 0 ? contributions : [Widget1(), Action1()],
    };

    public static Contribution Widget1(string id = Widget) => new()
    {
        Id = id,
        Name = "Widget",
        Description = "Shows a face.",
        Glyph = "\uE916",
        Provides = Provides.Face,
        Settings =
        [
            new Setting
            {
                Id = "size",
                Name = "Size",
                Default = "small",
                Choices = [new SettingChoice { Value = "small", Name = "Small" }, new SettingChoice { Value = "large", Name = "Large" }],
            },
        ],
    };

    public static Contribution Action1(string id = Action, Provides provides = Provides.Invoke) => new()
    {
        Id = id,
        Name = "Action",
        Description = "Runs something.",
        Glyph = "\uE916",
        Provides = provides,
    };

    public static async Task WaitForAsync(Func<bool> condition, string what, int seconds = 10)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(seconds))
            {
                throw new TimeoutException("Timed out waiting for: " + what);
            }

            await Task.Delay(5);
        }
    }
}

/// <summary>A widget that publishes the time with its size setting and ends when cancelled.</summary>
internal sealed class GoodWidget : ContributionHandler
{
    public override async Task RunSessionAsync(Session session, CancellationToken cancellationToken)
    {
        if ((session.Provides & Provides.Face) == 0)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), session.Time);
        do
        {
            session.SetFace(new Face { Line1 = session.Settings.GetValueOrDefault("size", "-"), GoodFor = TimeSpan.FromSeconds(60) });
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }
}

/// <summary>A handler whose parts each test sets.</summary>
internal sealed class ScriptedHandler : IContributionHandler
{
    private int _invocations;

    public int Invocations => Volatile.Read(ref _invocations);

    public Func<Session, CancellationToken, Task>? OnSession { get; init; }

    public Func<Invocation, CancellationToken, Task<InvokeResult>>? OnInvoke { get; init; }

    public Task RunSessionAsync(Session session, CancellationToken cancellationToken) =>
        OnSession?.Invoke(session, cancellationToken) ?? Task.CompletedTask;

    public Task<InvokeResult> InvokeAsync(Invocation invocation, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _invocations);
        return OnInvoke?.Invoke(invocation, cancellationToken) ?? Task.FromResult(InvokeResult.Done);
    }
}
