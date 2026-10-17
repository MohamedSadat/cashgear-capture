using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media;

/// <summary>
/// One compressed packet on its way from an encoder to the writer, which owns it from then on (and disposes it).
/// Timestamps are in the encoder's time base until the writer rescales them for the file.
/// </summary>
public sealed unsafe class EncodedPacket : IDisposable
{
    private AVPacket* _packet;

    /// <summary>Takes over <paramref name="source"/>'s data, leaving it empty for the encoder's next packet.</summary>
    internal EncodedPacket(AVPacket* source, int stream, AVRational timeBase)
    {
        _packet = ffmpeg.av_packet_alloc();
        ffmpeg.av_packet_move_ref(_packet, source);
        Stream = stream;
        TimeBase = timeBase;
    }

    /// <summary>Which of the recording's streams it belongs to: 0 is the video, 1 and up the sound tracks.</summary>
    public int Stream { get; }

    internal AVRational TimeBase { get; }

    internal AVPacket* Packet => _packet;

    public long Pts => _packet->pts;

    public long Duration => _packet->duration;

    public int Size => _packet->size;

    public bool IsKey => (_packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0;

    /// <summary>The presentation time in seconds.</summary>
    public double Seconds => _packet->pts * ffmpeg.av_q2d(TimeBase);

    public void Dispose()
    {
        var packet = _packet;
        if (packet is not null)
        {
            _packet = null;
            ffmpeg.av_packet_free(&packet);
        }
    }
}
