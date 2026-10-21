using System.Collections.Concurrent;
using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media;

/// <summary>
/// One compressed packet on its way from an encoder to the writer, which owns it from then on (and disposes it).
/// Timestamps are in the encoder's time base until the writer rescales them for the file.
/// </summary>
/// <remarks>
/// Packets are pooled: disposing one gives its (emptied) <c>AVPacket</c> back for the next, so a recording doesn't
/// allocate a packet per frame. Dispose a packet once, and don't touch it afterwards.
/// </remarks>
public sealed unsafe class EncodedPacket : IDisposable
{
    /// <summary>More than a recording's packets in flight at once (the writer warns past 600 waiting).</summary>
    private const int MaxPooled = 1024;

    private static readonly ConcurrentQueue<EncodedPacket> Pool = new();
    private static int _pooled;

    private readonly AVPacket* _packet;
    private int _disposed;

    private EncodedPacket() => _packet = ffmpeg.av_packet_alloc();

    /// <summary>Which of the recording's streams it belongs to: 0 is the video, 1 and up the sound tracks.</summary>
    public int Stream { get; private set; }

    internal AVRational TimeBase { get; private set; }

    internal AVPacket* Packet => _packet;

    public long Pts => _packet->pts;

    public long Duration => _packet->duration;

    public int Size => _packet->size;

    public bool IsKey => (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;

    /// <summary>The presentation time in seconds.</summary>
    public double Seconds => _packet->pts * ffmpeg.av_q2d(TimeBase);

    /// <summary>Packets waiting in the pool (tests).</summary>
    internal static int Pooled => Volatile.Read(ref _pooled);

    /// <summary>A packet that takes over <paramref name="source"/>'s data, leaving it empty for the encoder's next packet.</summary>
    internal static EncodedPacket Take(AVPacket* source, int stream, AVRational timeBase)
    {
        if (Pool.TryDequeue(out var packet))
        {
            Interlocked.Decrement(ref _pooled);
            packet._disposed = 0;
        }
        else
        {
            packet = new EncodedPacket();
        }

        ffmpeg.av_packet_move_ref(packet._packet, source);
        packet.Stream = stream;
        packet.TimeBase = timeBase;
        return packet;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        ffmpeg.av_packet_unref(_packet);
        if (Interlocked.Increment(ref _pooled) <= MaxPooled)
        {
            Pool.Enqueue(this);
            return;
        }

        Interlocked.Decrement(ref _pooled);
        var packet = _packet;
        ffmpeg.av_packet_free(&packet);
    }
}
