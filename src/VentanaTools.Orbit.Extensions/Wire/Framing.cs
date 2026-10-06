// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

using System.Buffers.Binary;

namespace VentanaTools.Orbit.Extensions.Wire;

/// <summary>The protocol versions this library implements (contract §7.3) and the transcript label.</summary>
public static class ProtocolVersions
{
    /// <summary>The lowest protocol version implemented.</summary>
    public const int Min = 3;

    /// <summary>The highest protocol version implemented.</summary>
    public const int Max = 3;

    /// <summary>The first line of every proof transcript, and the transport generation (contract §7.3.4).</summary>
    public const string Label = "Ventana.Extensions.v3";
}

/// <summary>
/// Thrown when a frame breaks the framing rules (contract §7.2). <see cref="Code"/> is the
/// reason code to close with.
/// </summary>
public sealed class FrameException : IOException
{
    internal FrameException(ReasonCode code)
        : base("The frame breaks the framing rules: " + code.Value + ".")
    {
        Code = code;
    }

    /// <summary>The reason code: <c>frame.too-large</c> or <c>frame.timeout</c>.</summary>
    public ReasonCode Code { get; }
}

/// <summary>
/// Frames: a 4-byte unsigned little-endian length, then that many bytes of UTF-8 JSON
/// (contract §7.2).
/// </summary>
/// <remarks>
/// Waiting between frames is unbounded; once the first byte of a frame arrives, the whole
/// frame must arrive within <see cref="PartialFrameTimeout"/>. Until <c>ready</c> has been sent
/// or received, callers pass <see cref="MaxHandshakeFrameBytes"/> as the limit, and the effective
/// <c>limits.maxFrameBytes</c> after. A sender that cannot finish writing a frame within 5 seconds
/// must close the connection (<c>frame.write-timeout</c>); callers enforce that around
/// <see cref="WriteFrameAsync"/>. Neither member is safe to call concurrently on one stream.
/// </remarks>
public static class Framing
{
    /// <summary>The largest frame body, in bytes.</summary>
    public const int MaxFrameBytes = 65_536;

    /// <summary>The largest frame body before <c>ready</c>, in bytes.</summary>
    public const int MaxHandshakeFrameBytes = 8_192;

    /// <summary>How long a started frame may take to arrive: 5 seconds.</summary>
    public static readonly TimeSpan PartialFrameTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Reads one frame's body. Returns null on a clean end of stream before a frame starts.
    /// </summary>
    /// <param name="stream">The connection.</param>
    /// <param name="maxFrameBytes">The largest body allowed now, 1 to <see cref="MaxFrameBytes"/>.</param>
    /// <param name="time">The clock that measures <see cref="PartialFrameTimeout"/>.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>The body, or null at a clean end of stream.</returns>
    /// <exception cref="FrameException">The length is 0 or over the limit (<c>frame.too-large</c>), or a started frame did not finish in time (<c>frame.timeout</c>).</exception>
    /// <exception cref="EndOfStreamException">The stream ended inside a frame.</exception>
    public static async ValueTask<byte[]?> ReadFrameAsync(Stream stream, int maxFrameBytes, TimeProvider time,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(time);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxFrameBytes, MaxFrameBytes);

        var header = new byte[4];
        if (await stream.ReadAsync(header.AsMemory(0, 1), cancellationToken).ConfigureAwait(false) == 0)
        {
            return null;
        }

        using var deadline = new CancellationTokenSource(PartialFrameTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await stream.ReadExactlyAsync(header.AsMemory(1), linked.Token).ConfigureAwait(false);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (length == 0 || length > (uint)maxFrameBytes)
            {
                throw new FrameException(ReasonCode.FrameTooLarge);
            }

            var body = new byte[(int)length];
            await stream.ReadExactlyAsync(body, linked.Token).ConfigureAwait(false);
            return body;
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new FrameException(ReasonCode.FrameTimeout);
        }
    }

    /// <summary>Writes one frame: the length prefix and the body in a single write, then flushes.</summary>
    /// <param name="stream">The connection.</param>
    /// <param name="frame">The body, 1 to <see cref="MaxFrameBytes"/> bytes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    /// <returns>A task that completes when the frame is flushed.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The body is empty or larger than <see cref="MaxFrameBytes"/>.</exception>
    public static async ValueTask WriteFrameAsync(Stream stream, ReadOnlyMemory<byte> frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfLessThan(frame.Length, 1, nameof(frame));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(frame.Length, MaxFrameBytes, nameof(frame));

        var buffer = new byte[4 + frame.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, (uint)frame.Length);
        frame.CopyTo(buffer.AsMemory(4));
        await stream.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
