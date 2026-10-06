// SPDX-License-Identifier: MIT-0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using VentanaTools.Orbit.Extensions;
using VentanaTools.Orbit.Extensions.Testing;
using Xunit;

namespace CountdownExtensionSample.Tests;

/// <summary>
/// The SDK's contract suite for every contribution and setting combination: sessions are accepted,
/// faces arrive with finite lifetimes, nothing is published after stop, and handlers end promptly.
/// The timer's real action is cheap and local, so its invocations run too.
/// </summary>
public sealed class CountdownContract : ContributionContractSuite
{
    protected override ExtensionManifest Manifest =>
        ManifestReader.ReadFileAsync("extension.json").GetAwaiter().GetResult().Value ?? throw new InvalidOperationException("extension.json is invalid.");

    protected override IContributionHandler CreateHandler() => new CountdownHandler();

    protected override IEnumerable<string> InvokeContributions => [CountdownChoices.TimerId];
}

public sealed class ContractTests
{
    [Fact]
    public void TheManifestIsValid() => ExtensionConformance.AssertManifestFileValid("extension.json");

    [Fact]
    public Task EveryContributionKeepsTheContract() => new CountdownContract().AssertAllAsync();
}
