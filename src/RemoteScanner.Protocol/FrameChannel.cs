using System.Threading.Channels;

namespace RemoteScanner.Protocol;

/// <summary>
/// Framed message transport over any duplex <see cref="Stream"/> (named pipe on both hosts,
/// or the WTS virtual-file handle on the server). Reads are serialised on a single pump task;
/// writes are serialised behind a semaphore so concurrent senders cannot interleave a header
/// with someone else's payload.
/// </summary>
public sealed class FrameChannel : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly bool _ownsStream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Channel<Frame> _inbound =
        Channel.CreateBounded<Frame>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true,
        });

    private readonly CancellationTokenSource _shutdown = new();
    private Task? _readPump;
    private int _disposed;

    public FrameChannel(Stream stream, bool ownsStream = true)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _ownsStream = ownsStream;
    }

    /// <summary>Set when the read pump ends, carrying the reason if it was not a clean EOF.</summary>
    public Exception? Fault { get; private set; }

    public bool IsFaulted => Fault is not null;

    public event Action<Exception?>? Closed;

    public void Start()
    {
        if (_readPump is not null) throw new InvalidOperationException("Channel already started.");
        _readPump = Task.Run(() => ReadPumpAsync(_shutdown.Token));
    }

    public ChannelReader<Frame> Reader => _inbound.Reader;

    public async Task<Frame?> ReadAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _inbound.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    public Task SendAsync(IMessage message, uint streamId, CancellationToken cancellationToken)
    {
        var writer = new PayloadWriter();
        message.Write(writer);
        return SendRawAsync(message.Type, streamId, writer.AsMemory(), cancellationToken);
    }

    public async Task SendRawAsync(
        MessageType type, uint streamId, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var frame = new byte[Wire.HeaderSize + payload.Length];
        FrameCodec.WriteHeader(frame, type, streamId, payload.Length);
        payload.Span.CopyTo(frame.AsSpan(Wire.HeaderSize));

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadPumpAsync(CancellationToken cancellationToken)
    {
        var header = new byte[Wire.HeaderSize];
        Exception? fault = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await ReadExactAsync(header, cancellationToken).ConfigureAwait(false))
                    break; // clean EOF: peer closed

                int payloadLength = FrameCodec.ParseHeader(header, out var type, out uint streamId);

                byte[] payload = payloadLength == 0 ? Array.Empty<byte>() : new byte[payloadLength];
                if (payloadLength > 0 && !await ReadExactAsync(payload, cancellationToken).ConfigureAwait(false))
                    throw new ProtocolException($"Stream ended mid-frame; expected {payloadLength} payload bytes.");

                await _inbound.Writer.WriteAsync(new Frame(type, streamId, payload), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            fault = ex;
        }

        Fault = fault;
        _inbound.Writer.TryComplete(fault);
        Closed?.Invoke(fault);
    }

    private async Task<bool> ReadExactAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await _stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                // EOF at a frame boundary is clean; anywhere else the caller treats it as truncation.
                return offset != 0
                    ? throw new ProtocolException($"Stream ended after {offset} of {buffer.Length} bytes.")
                    : false;
            }
            offset += read;
        }
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _shutdown.Cancel();
        _inbound.Writer.TryComplete();

        if (_readPump is not null)
        {
            try { await _readPump.ConfigureAwait(false); } catch { /* shutdown races are expected */ }
        }

        if (_ownsStream)
        {
            try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { /* already gone */ }
        }

        _shutdown.Dispose();
        _writeLock.Dispose();
    }
}

/// <summary>
/// Credit-based backpressure. The sender must acquire a credit before each data frame; the
/// receiver returns credit as it drains. Without this a fast ADF scanner will queue hundreds
/// of megabytes into the RDP send buffer and the whole remote session becomes unresponsive.
/// </summary>
public sealed class FlowController : IDisposable
{
    private readonly SemaphoreSlim _credits;
    private int _consumedSinceRefill;

    public FlowController(int window = Wire.DefaultCreditWindow)
    {
        Window = window;
        _credits = new SemaphoreSlim(window, int.MaxValue);
    }

    public int Window { get; }

    /// <summary>Blocks the sender until the receiver has room. Honours cancellation immediately.</summary>
    public Task AcquireAsync(CancellationToken cancellationToken) => _credits.WaitAsync(cancellationToken);

    public void Grant(int frames)
    {
        if (frames > 0) _credits.Release(frames);
    }

    /// <summary>
    /// Called by the receiver for each frame it has finished with. Returns the number of
    /// credits to send back, or 0 when it is not yet worth a round trip.
    /// </summary>
    public int Consume()
    {
        if (++_consumedSinceRefill < Wire.CreditRefillThreshold) return 0;
        int refill = _consumedSinceRefill;
        _consumedSinceRefill = 0;
        return refill;
    }

    public void Dispose() => _credits.Dispose();
}
