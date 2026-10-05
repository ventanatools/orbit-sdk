using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace Orbit.Extensions.Protocol.Tests;

public sealed class ExtensionWireTests
{
    [Fact]
    public async Task FragmentedLittleEndianFramesRoundTripAndCleanEofIsDistinct()
    {
        var content = ExtensionWire.Message("result", ("requestId", "hello"), ("outcome", "done"));
        using var output = new MemoryStream();
        await ExtensionWire.WriteAsync(output, content, CancellationToken.None);
        Assert.Equal(content.Length, BinaryPrimitives.ReadInt32LittleEndian(output.ToArray()));
        using var fragmented = new FragmentedStream(output.ToArray());
        Assert.Equal(content, await ExtensionWire.ReadAsync(fragmented, CancellationToken.None));
        Assert.Null(await ExtensionWire.ReadAsync(fragmented, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65537)]
    [InlineData(-1)]
    public async Task InvalidDeclaredLengthIsRejectedBeforeBodyAllocation(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var input = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => ExtensionWire.ReadAsync(input, CancellationToken.None));
    }

    [Fact]
    public async Task TruncatedHeadersBodiesAndMalformedUtf8AreRejected()
    {
        using var header = new MemoryStream([1, 0]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => ExtensionWire.ReadAsync(header, CancellationToken.None));
        using var body = new MemoryStream([2, 0, 0, 0, 65]);
        await Assert.ThrowsAsync<EndOfStreamException>(() => ExtensionWire.ReadAsync(body, CancellationToken.None));
        using var invalid = new MemoryStream([1, 0, 0, 0, 255]);
        await Assert.ThrowsAsync<DecoderFallbackException>(() => ExtensionWire.ReadAsync(invalid, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65537)]
    public async Task InvalidOutgoingLengthsNeverWriteAHeader(int length)
    {
        using var output = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => ExtensionWire.WriteAsync(output, new byte[length], CancellationToken.None));
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData("{\"type\":\"ready\",\"type\":\"ready\"}")]
    [InlineData("{\"type\":\"ready\",\"extra\":true}")]
    [InlineData("{\"Type\":\"ready\"}")]
    [InlineData("{\"type\":3}")]
    [InlineData("{\"type\":\"other\"}")]
    [InlineData("[]")]
    public void MessagesRejectUnknownDuplicateMissingAndMistypedFields(string text)
    {
        Assert.Throws<InvalidDataException>(() => ExtensionWire.ReadMessage(Encoding.UTF8.GetBytes(text), "ready"));
    }

    [Fact]
    public void ProofAndNonceEncodingIsCanonicalAndVersionBound()
    {
        var valid = Convert.ToBase64String(new byte[32]);
        Assert.True(ExtensionWire.TryBytes(valid, out var bytes));
        Assert.Equal(32, bytes.Length);
        Assert.False(ExtensionWire.TryBytes(valid.TrimEnd('='), out _));
        Assert.False(ExtensionWire.TryBytes(Convert.ToBase64String(new byte[31]), out _));
        Assert.False(ExtensionWire.MatchesProof(valid, Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray())));
        Assert.Throws<ArgumentOutOfRangeException>(() => ExtensionWire.Proof(new byte[32], "client", "id", valid, valid, 1));
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}
