using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using Orbit.Extensions.Protocol;
using Xunit;

namespace Orbit.Extensions.Sdk.Tests;

internal static class TestFixtures
{
    internal const string ExtensionId = "example.sdk";
    internal const string ActionId = "example.sdk/set-state";
    internal const string PassiveId = "example.sdk/status";
    internal const string ManifestJson = """
        {"schemaVersion":2,"id":"example.sdk","name":"SDK test","description":"Test companion.",
        "version":"0.1.0","hosts":["orbit"],"contributions":[
        {"id":"example.sdk/set-state","name":"Set state","description":"Change state.","glyph":"\uE7B3",
        "capabilities":["invoke","face"],"settings":[
        {"id":"mode","name":"Mode","default":"on","choices":[{"value":"on","name":"On"},{"value":"off","name":"Off"}]}]},
        {"id":"example.sdk/status","name":"Status","description":"Show state.","glyph":"\uE7B3",
        "capabilities":["face"],"settings":[]}]}
        """;

    internal static ExternalExtensionManifest Manifest =>
        ExternalExtensionManifestReader.Read(Encoding.UTF8.GetBytes(ManifestJson)).Manifest!;

    internal static async Task EventuallyAsync(Func<bool> condition, int seconds = 10)
    {
        var deadline = Stopwatch.StartNew();
        while (!condition())
        {
            if (deadline.Elapsed > TimeSpan.FromSeconds(seconds))
                throw new TimeoutException("SDK condition did not settle.");
            await Task.Delay(10);
        }
    }

    internal sealed class RecordingHandler : IContributionHandler
    {
        internal ConcurrentQueue<CompanionSession> Sessions { get; } = [];
        internal ConcurrentQueue<CompanionInvocation> Invocations { get; } = [];
        internal Func<CompanionSession, CancellationToken, Task>? OnSession { get; init; }
        internal Func<CompanionInvocation, CancellationToken, Task<CompanionOutcome>>? OnInvoke { get; init; }

        public async Task RunSessionAsync(CompanionSession session, CancellationToken cancellationToken)
        {
            Sessions.Enqueue(session);
            if (OnSession is { } callback) await callback(session, cancellationToken);
            else await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public Task<CompanionOutcome> InvokeAsync(CompanionInvocation invocation, CancellationToken cancellationToken)
        {
            Invocations.Enqueue(invocation);
            cancellationToken.ThrowIfCancellationRequested();
            return OnInvoke?.Invoke(invocation, cancellationToken) ?? Task.FromResult(CompanionOutcome.Done);
        }
    }
}

/// <summary>Transport cases require Windows; validators and public API tests stay portable.</summary>
public sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Orbit companion named pipes require Windows.";
    }
}

public sealed class WindowsTheoryAttribute : TheoryAttribute
{
    public WindowsTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Orbit companion named pipes require Windows.";
    }
}
