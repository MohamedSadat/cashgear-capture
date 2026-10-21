using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media;

/// <summary>
/// One sound track's encoder: FFmpeg's own AAC (the recordings') by default, or another codec for exports (Opus for
/// WebM, MP3). 48 kHz stereo, fed interleaved float samples <see cref="FrameSize"/> at a time. Timestamps are sample
/// positions (time base 1/48000).
/// </summary>
public sealed unsafe class AudioEncoder : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int DefaultBitRate = 160_000;

    private AVCodecContext* _context;
    private AVFrame* _frame;
    private AVPacket* _packet;

    private AudioEncoder(AVCodecContext* context, int stream)
    {
        _context = context;
        Stream = stream;
        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        _frame->format = (int)context->sample_fmt;
        _frame->sample_rate = SampleRate;
        _frame->nb_samples = context->frame_size;
        FfmpegException.Check(ffmpeg.av_channel_layout_copy(&_frame->ch_layout, &context->ch_layout), "Setting up a sound frame");
        FfmpegException.Check(ffmpeg.av_frame_get_buffer(_frame, 0), "Allocating a sound frame");
    }

    public int Stream { get; }

    /// <summary>Samples per channel in each frame the encoder takes (1024 for AAC); only the last may be shorter.</summary>
    public int FrameSize => _context->frame_size;

    internal AVCodecContext* Context => _context;

    /// <summary>FFmpeg's names of the codecs exports use.</summary>
    public const string Aac = "aac";
    public const string Opus = "libopus";
    public const string Mp3 = "libmp3lame";

    /// <param name="codec">FFmpeg's encoder: <see cref="Aac"/>, <see cref="Opus"/> or <see cref="Mp3"/>.</param>
    public static AudioEncoder Open(int stream, int bitRate = DefaultBitRate, string codec = Aac)
    {
        var encoder = ffmpeg.avcodec_find_encoder_by_name(codec);
        if (encoder is null)
        {
            throw new FfmpegException($"The {codec} encoder isn't in this FFmpeg build.");
        }

        var context = ffmpeg.avcodec_alloc_context3(encoder);

        // libopus takes interleaved float; AAC and LAME planar.
        context->sample_fmt = codec == Opus ? AVSampleFormat.AV_SAMPLE_FMT_FLT : AVSampleFormat.AV_SAMPLE_FMT_FLTP;
        context->sample_rate = SampleRate;
        ffmpeg.av_channel_layout_default(&context->ch_layout, Channels);
        context->bit_rate = bitRate;
        context->time_base = new AVRational { num = 1, den = SampleRate };
        context->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        using var log = FfmpegLog.Capture();
        var result = ffmpeg.avcodec_open2(context, encoder, null);
        if (result < 0)
        {
            ffmpeg.avcodec_free_context(&context);
            throw new FfmpegException($"Opening the {codec} encoder", result, log.Summary());
        }

        return new AudioEncoder(context, stream);
    }

    /// <summary>Encodes up to <see cref="FrameSize"/> stereo samples (interleaved L, R) starting at sample <paramref name="pts"/>.</summary>
    public void Encode(ReadOnlySpan<float> interleaved, long pts, List<EncodedPacket> output)
    {
        var samples = interleaved.Length / Channels;
        if (samples == 0 || samples > FrameSize || interleaved.Length % Channels != 0)
        {
            throw new ArgumentException($"Expected 1 to {FrameSize} stereo samples, got {interleaved.Length} values.", nameof(interleaved));
        }

        FfmpegException.Check(ffmpeg.av_frame_make_writable(_frame), "Preparing a sound frame");
        _frame->nb_samples = samples;
        _frame->pts = pts;
        if (_context->sample_fmt == AVSampleFormat.AV_SAMPLE_FMT_FLT)
        {
            interleaved.CopyTo(new Span<float>(_frame->data[0], samples * Channels));
            Send(_frame, output);
            return;
        }

        var left = new Span<float>(_frame->data[0], samples);
        var right = new Span<float>(_frame->data[1], samples);
        for (var i = 0; i < samples; i++)
        {
            left[i] = interleaved[2 * i];
            right[i] = interleaved[(2 * i) + 1];
        }

        Send(_frame, output);
    }

    public void Flush(List<EncodedPacket> output) => Send(null, output);

    private void Send(AVFrame* frame, List<EncodedPacket> output)
    {
        var result = ffmpeg.avcodec_send_frame(_context, frame);
        if (result == -ffmpeg.EAGAIN)
        {
            Receive(output);
            result = ffmpeg.avcodec_send_frame(_context, frame);
        }

        if (result < 0 && !(frame is null && result == ffmpeg.AVERROR_EOF))
        {
            throw new FfmpegException("Encoding sound", result);
        }

        Receive(output);
    }

    private void Receive(List<EncodedPacket> output)
    {
        while (true)
        {
            var result = ffmpeg.avcodec_receive_packet(_context, _packet);
            if (result == -ffmpeg.EAGAIN || result == ffmpeg.AVERROR_EOF)
            {
                return;
            }

            FfmpegException.Check(result, "Encoding sound");
            output.Add(new EncodedPacket(_packet, Stream, _context->time_base));
        }
    }

    public void Dispose()
    {
        var frame = _frame;
        var packet = _packet;
        var context = _context;
        _frame = null;
        _packet = null;
        _context = null;
        ffmpeg.av_frame_free(&frame);
        ffmpeg.av_packet_free(&packet);
        ffmpeg.avcodec_free_context(&context);
    }
}
