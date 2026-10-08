using VentanaTools.Orbit.Extensions;
using VentanaTools.Orbit.Extensions.Testing;
using Xunit;

public sealed class ExtensionTests
{
    [Fact]
    public void TheManifestIsValid() => ExtensionConformance.AssertManifestFileValid("extension.json");

    [Fact]
    public Task EveryContributionKeepsTheSessionContract() =>
        // Sessions only: the suite never runs the real action unless you list it.
        ExtensionConformance.AssertAuthoringContractFileAsync("extension.json", new TallyHandler());
}
