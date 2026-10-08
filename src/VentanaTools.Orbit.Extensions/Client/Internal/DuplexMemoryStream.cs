// SPDX-License-Identifier: Apache-2.0
// SPDX-FileCopyrightText: 2026 Ventana Tools LLC

namespace VentanaTools.Orbit.Extensions;

/// <summary>
/// One end of an in-memory, full-duplex byte stream: what one end writes, the other end reads.
/// The in-memory transport seam (the Testing package and the SDK's own tests) runs a real
/// <see cref="CompanionClient"/> over a pair of these instead of a named pipe.
/// </summary>
/// <remarks>
/// Disposing either end closes both directions: the peer's reads return 0 once buffered data is
/// consumed, and writes from either end throw <see cref="IOException"/>. Reads honour their
/// cancellation token. One reader and one writer per end may run at the same time.
/// </remarks>
internal sealed class DuplexMemoryStream : Stream
{
    private readonly ByteChannel _incoming;
    private readonly ByteChannel _outgoing;
    private int _disposed;

    private DuplexMemoryStream(ByteChannel incoming, ByteChannel outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Creates two connected ends.</summary>
    /// <returns>The two ends; bytes written to one are read from the other.</returns>
    public static (DuplexMemoryStream First, DuplexMemoryStream Second) CreatePair()
    {
        var forward = new ByteChannel();
        var backward = new ByteChannel();
        return (new DuplexMemoryStream(backward, forward), new DuplexMemoryStream(forward, backward));
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _incoming.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _outgoing.Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count) => _outgoing.Write(buffer.AsSpan(offset, count));

    public override void Flush()
    {
    }

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _incoming.Complete();
            _outgoing.Complete();
        }

        base.Dispose(disposing);
    }

    /// <summary>One direction: a queue of written segments and at most one waiting reader.</summary>
    private sealed class ByteChannel
    {
        private readonly object _gate = new();
        private readonly Queue<byte[]> _segments = new();
        private int _offset;
        private bool _completed;
        private TaskCompletionSource? _waiter;

        public void Write(ReadOnlySpan<byte> bytes)
        {
            TaskCompletionSource? wake;
            lock (_gate)
            {
                if (_completed)
                {
                    throw new IOException("The in-memory stream is closed.");
                }

                if (bytes.IsEmpty)
                {
                    return;
                }

                _segments.Enqueue(bytes.ToArray());
                wake = _waiter;
                _waiter = null;
            }

            wake?.TrySetResult();
        }

        public void Complete()
        {
            TaskCompletionSource? wake;
            lock (_gate)
            {
                _completed = true;
                wake = _waiter;
                _waiter = null;
            }

            wake?.TrySetResult();
        }

        public async ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken)
        {
            if (destination.IsEmpty)
            {
                return 0;
            }

            while (true)
            {
                Task wait;
                lock (_gate)
                {
                    if (_segments.Count > 0)
                    {
                        var segment = _segments.Peek();
                        var count = Math.Min(destination.Length, segment.Length - _offset);
                        segment.AsSpan(_offset, count).CopyTo(destination.Span);
                        _offset += count;
                        if (_offset == segment.Length)
                        {
                            _segments.Dequeue();
                            _offset = 0;
                        }

                        return count;
                    }

                    if (_completed)
                    {
                        return 0;
                    }

                    _waiter ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    wait = _waiter.Task;
                }

                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
