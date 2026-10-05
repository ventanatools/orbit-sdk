using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Orbit.Extensions.Protocol.Tests;

public sealed class ExtensionPairingTests
{
    private const string ExtensionId = "example.sdk";
    private const string RegistrationId = "0123456789abcdef0123456789abcdef";
    private static readonly byte[] Secret = Enumerable.Range(0, 32).Select(index => (byte)index).ToArray();
    private static readonly string ClientNonce = Convert.ToBase64String(Enumerable.Repeat((byte)11, 32).ToArray());
    private static readonly string ServerNonce = Convert.ToBase64String(Enumerable.Repeat((byte)22, 32).ToArray());

    private static string PairingJson() => JsonSerializer.Serialize(new
    {
        protocolVersion = 2,
        pipeName = $"Orbit.Extensions.v2.test.0123456789abcdef.{RegistrationId}",
        registrationId = RegistrationId,
        extensionId = ExtensionId,
        secret = Convert.ToBase64String(Secret),
    });

    [Fact]
    public void ProofAuthenticatesTheProtocolRoleRegistrationAndBothNonces()
    {
        using var pairing = ExtensionPairing.Parse(Encoding.UTF8.GetBytes(PairingJson()), ExtensionId, 2);
        var expected = Convert.ToBase64String(HMACSHA256.HashData(Secret, Encoding.UTF8.GetBytes(
            $"Orbit.Extensions.v2\nclient\n{RegistrationId}\n{ClientNonce}\n{ServerNonce}")));
        Assert.Equal(expected, pairing.CreateProof("client", ClientNonce, ServerNonce));
        Assert.True(pairing.VerifyProof(expected, "client", ClientNonce, ServerNonce));
        Assert.False(pairing.VerifyProof(expected, "server", ClientNonce, ServerNonce));
        Assert.False(pairing.VerifyProof(expected, "client", ServerNonce, ClientNonce));
        Assert.False(pairing.VerifyProof(null, "client", ClientNonce, ServerNonce));
        Assert.Equal("ExtensionPairing", pairing.ToString());
        Assert.DoesNotContain(Convert.ToBase64String(Secret), pairing.ToString());
    }

    [Theory]
    [InlineData("\"protocolVersion\":2", "\"protocolVersion\":1")]
    [InlineData("\"protocolVersion\":2", "\"protocolVersion\":3")]
    [InlineData("\"protocolVersion\":2", "\"protocolVersion\":2,\"protocolVersion\":2")]
    [InlineData("\"protocolVersion\":2", "\"protocolVersion\":2,\"entrypoint\":\"run.exe\"")]
    [InlineData("Orbit.Extensions.v2.test", "Orbit.Extensions.v1.test")]
    [InlineData("Orbit.Extensions.v2.test", "Other.Extensions.v2.test")]
    [InlineData("Orbit.Extensions.v2.test", "Orbit.Extensions.v2.TEST")]
    [InlineData("Orbit.Extensions.v2.test", "Orbit.Extensions.v2.test.extra")]
    [InlineData("example.sdk", "other.sdk")]
    [InlineData("0123456789abcdef0123456789abcdef", "0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcdef", "00000000000000000000000000000000")]
    [InlineData("0123456789abcdef.", "0123456789abcdeg.")]
    public void InvalidIdentityVersionsPipesAndAmbiguousFieldsProduceOnlyFixedDiagnostics(string before, string after)
    {
        var content = PairingJson().Replace(before, after, StringComparison.Ordinal);
        var error = Assert.Throws<InvalidDataException>(() => ExtensionPairing.Parse(Encoding.UTF8.GetBytes(content), ExtensionId, 2));
        Assert.Equal("Invalid extension pairing.", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(31)]
    [InlineData(33)]
    public void AKeyMustContainExactly32Bytes(int length)
    {
        var content = PairingJson().Replace(Convert.ToBase64String(Secret), Convert.ToBase64String(new byte[length]), StringComparison.Ordinal);
        Assert.Throws<InvalidDataException>(() => ExtensionPairing.Parse(Encoding.UTF8.GetBytes(content), ExtensionId));
    }

    [Fact]
    public void CallerCannotChangeExpectedIdentityOrProtocolAndInputSizeIsBounded()
    {
        var content = Encoding.UTF8.GetBytes(PairingJson());
        Assert.Throws<InvalidDataException>(() => ExtensionPairing.Parse(content, "other.sdk", 2));
        Assert.Throws<InvalidDataException>(() => ExtensionPairing.Parse(content, ExtensionId, 1));
        Assert.Throws<InvalidDataException>(() => ExtensionPairing.Parse(content, "clock", 2));
        Assert.Throws<InvalidDataException>(() => ExtensionPairing.Parse([], ExtensionId));
        Assert.Throws<InvalidDataException>(() => ExtensionPairing.Parse(new byte[ExtensionPairing.MaxBytes + 1], ExtensionId));
    }

    [Fact]
    public void InvalidProofParametersAndDisposedCredentialsCannotBeUsed()
    {
        var pairing = ExtensionPairing.Parse(Encoding.UTF8.GetBytes(PairingJson()), ExtensionId);
        Assert.Throws<ArgumentException>(() => pairing.CreateProof("other", ClientNonce, ServerNonce));
        Assert.Throws<ArgumentException>(() => pairing.CreateProof("client", "bad", ServerNonce));
        Assert.Throws<ArgumentException>(() => pairing.CreateProof("client", ClientNonce, "bad"));
        pairing.Dispose();
        pairing.Dispose();
        Assert.Throws<ObjectDisposedException>(() => pairing.CreateProof("client", ClientNonce, ServerNonce));
        Assert.Throws<ObjectDisposedException>(() => pairing.VerifyProof(null, "client", ClientNonce, ServerNonce));
    }
}
