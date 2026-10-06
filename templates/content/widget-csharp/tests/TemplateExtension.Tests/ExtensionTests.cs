using VentanaTools.Orbit.Extensions;
using VentanaTools.Orbit.Extensions.Testing;
using Xunit;

public sealed class ExtensionTests
{
    [Fact]
    public void TheManifestIsValid() => ExtensionConformance.AssertManifestFileValid("extension.json");

    [Fact]
    public async Task EveryContributionKeepsTheSessionContract()
    {
        // Sessions only: the suite never runs the real action unless you list it.
        var manifest = (await ManifestReader.ReadFileAsync("extension.json")).Value!;
        await ExtensionConformance.AssertAuthoringContractAsync(manifest, new TallyHandler());
    }
}
