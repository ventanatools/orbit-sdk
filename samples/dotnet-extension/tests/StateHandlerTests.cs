// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using VentanaTools.Orbit.Extensions;
using VentanaTools.Orbit.Extensions.Testing;
using Xunit;

namespace DotnetExtensionSample.Tests;

public sealed class StateHandlerTests
{
    private static async Task<ExtensionManifest> ManifestAsync() =>
        (await ManifestReader.ReadFileAsync("extension.json")).Value ?? throw new InvalidOperationException("extension.json is invalid.");

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            await Task.Delay(5, timeout.Token);
        }
    }

    [Fact]
    public void TheStateChangesOnlyWhenSetToTheOtherValue()
    {
        var state = new SampleState();
        var (on, changed) = state.Read();
        Assert.False(on);
        Assert.False(state.Set(false));
        Assert.False(changed.IsCompleted);
        Assert.True(state.Set(true));
        Assert.True(changed.IsCompleted);
        Assert.True(state.Read().On);
    }

    [Fact]
    public void FacesShowTheValueTheWayEachPlacementChose()
    {
        var words = StateHandler.CreateFace(StateHandler.StatusId, new Dictionary<string, string> { ["display"] = "words" }, on: true);
        Assert.Equal(new TextLine { Text = "On" }, words.Line1);
        Assert.Equal(FaceState.On, words.State);
        Assert.True(words.Renew);
        var number = StateHandler.CreateFace(StateHandler.StatusId, new Dictionary<string, string> { ["display"] = "number" }, on: false);
        Assert.Equal(new TextLine { Text = "0" }, number.Line1);
        var action = StateHandler.CreateFace(StateHandler.SetStateId, new Dictionary<string, string> { ["mode"] = "off" }, on: true);
        Assert.Equal(new TextLine { Text = "Sets off" }, action.Line2);
    }

    [Fact]
    public async Task ASessionPublishesOnChangeAndNotOnATimer()
    {
        var handler = new StateHandler();
        using var status = TestSessions.FromManifest(await ManifestAsync(), StateHandler.StatusId);
        var running = status.RunAsync(handler);
        await UntilAsync(() => status.Publications.Count == 1);
        Assert.Equal(new TextLine { Text = "Off" }, status.LastFace!.Line1);

        using var turnOn = TestSessions.FromManifest(await ManifestAsync(), StateHandler.SetStateId);
        Assert.Equal(InvokeResult.Done, await handler.InvokeAsync(TestInvocations.Create(turnOn), CancellationToken.None));
        await UntilAsync(() => status.Publications.Count == 2);
        Assert.Equal(new TextLine { Text = "On" }, status.LastFace!.Line1);

        // Setting the same value again changes nothing, so nothing is published.
        Assert.Equal(InvokeResult.Done, await handler.InvokeAsync(TestInvocations.Create(turnOn), CancellationToken.None));
        await Task.Delay(50);
        Assert.Equal(2, status.Publications.Count);
        status.Stop();
        await running;
        Assert.DoesNotContain(status.Publications, publication => publication.Kind == PublicationKind.AfterStop);
    }

    [Fact]
    public async Task OnlyTheActionIsInvokedAndOnlyWhileItsSessionRuns()
    {
        var handler = new StateHandler();
        using var status = TestSessions.FromManifest(await ManifestAsync(), StateHandler.StatusId);
        Assert.Equal(InvokeResult.Unsupported, await handler.InvokeAsync(TestInvocations.Create(status), CancellationToken.None));
        using var stopped = TestSessions.FromManifest(await ManifestAsync(), StateHandler.SetStateId);
        stopped.Stop();
        Assert.Equal(InvokeResult.Refused, await handler.InvokeAsync(TestInvocations.Create(stopped), CancellationToken.None));
        Assert.False(handler.State.Read().On);
    }

    [Fact]
    public async Task ARealClientSetsTheSharedState()
    {
        var handler = new StateHandler();
        await using var host = await CompanionTestHost.StartAsync(await ManifestAsync(), handler);
        var status = await host.StartSessionAsync(StateHandler.StatusId, new Dictionary<string, string> { ["display"] = "number" });
        var turnOn = await host.StartSessionAsync(StateHandler.SetStateId, new Dictionary<string, string> { ["mode"] = "on" });
        Assert.Equal(new TextLine { Text = "0" }, (await host.WaitForFaceAsync(status, TimeSpan.FromSeconds(5))).Face.Line1);
        Assert.Equal(TestInvokeOutcomeKind.Done, (await host.InvokeAsync(turnOn)).Kind);
        Assert.Equal(new TextLine { Text = "1" }, (await host.WaitForFaceAsync(status, TimeSpan.FromSeconds(5))).Face.Line1);
        Assert.Empty(host.Faults);
    }
}

/// <summary>The SDK's contract suite over every contribution and setting combination.</summary>
public sealed class StateContract : ContributionContractSuite
{
    protected override ExtensionManifest Manifest =>
        ManifestReader.ReadFileAsync("extension.json").GetAwaiter().GetResult().Value ?? throw new InvalidOperationException("extension.json is invalid.");

    protected override IContributionHandler CreateHandler() => new StateHandler();

    protected override IEnumerable<string> InvokeContributions => [StateHandler.SetStateId];
}

public sealed class ContractTests
{
    [Fact]
    public void TheManifestIsValid() => ExtensionConformance.AssertManifestFileValid("extension.json");

    [Fact]
    public Task EveryContributionKeepsTheContract() => new StateContract().AssertAllAsync();
}
