using System.Runtime.InteropServices;
using CashGear.Capture.Media.Resources;
using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media;

/// <param name="Index">Index among all the file's streams.</param>
/// <param name="Start">The first packet's time in seconds.</param>
/// <param name="FrameRate">A video's frames per second (its average, or the stream's base rate), else 0.</param>
/// <param name="SampleRate">A sound stream's samples per second, else 0.</param>
/// <param name="Channels">A sound stream's channels, else 0.</param>
public sealed record MediaStreamInfo(
    int Index,
    bool IsVideo,
    string Codec,
    string? Title,
    bool IsDefault,
    double Start,
    double Duration,
    int Width,
    int Height,
    double FrameRate = 0,
    int SampleRate = 0,
    int Channels = 0);

/// <summary>Receives decoded sound mixed to mono, starting at <paramref name="time"/> seconds.</summary>
public delegate void MonoSoundHandler(double time, int sampleRate, ReadOnlySpan<float> samples);

/// <summary>Receives a decoded picture as top-down BGRA, shown at <paramref name="time"/> seconds.</summary>
public delegate void BgraFrameHandler(double time, int width, int height, ReadOnlySpan<byte> bgra);

/// <summary>
/// Turns decoded pictures (any size and pixel format; BT.709 limited range, as the recorder writes) into top-down
/// BGRA. Reuses its buffer and swscale context while the pictures keep their size and format.
/// </summary>
public sealed unsafe class BgraConverter : IDisposable
{
    private readonly byte*[] _planes = new byte*[4];
    private readonly int[] _strides = new int[4];
    private SwsContext* _context;
    private (int Width, int Height, int Format) _shape;
    private byte[] _buffer = [];

    public ReadOnlySpan<byte> Convert(AVFrame* frame)
    {
        var shape = (frame->width, frame->height, frame->format);
        if (_context is null || shape != _shape)
        {
            ffmpeg.sws_freeContext(_context);
            _context = ffmpeg.sws_getContext(
                frame->width, frame->height, (AVPixelFormat)frame->format,
                frame->width, frame->height, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)SwsFlags.SWS_POINT, null, null, null);
            if (_context is null)
            {
                throw new FfmpegException(MediaStrings.CantConvertToBgra((AVPixelFormat)frame->format));
            }

            var yuv = *(int4*)ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_ITU709);
            var rgb = *(int4*)ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_DEFAULT);
            ffmpeg.sws_setColorspaceDetails(_context, in yuv, 0, in rgb, 1, 0, 1 << 16, 1 << 16);
            _shape = shape;
            _buffer = new byte[frame->width * frame->height * 4];
        }

        fixed (byte* destination = _buffer)
        {
            _planes[0] = destination;
            _strides[0] = frame->width * 4;
            var source = new byte*[8];
            var sourceStrides = new int[8];
            for (uint i = 0; i < 8; i++)
            {
                source[i] = frame->data[i];
                sourceStrides[i] = frame->linesize[i];
            }

            ffmpeg.sws_scale(_context, source, sourceStrides, 0, frame->height, _planes, _strides);
        }

        return _buffer;
    }

    public void Dispose()
    {
        var context = _context;
        _context = null;
        ffmpeg.sws_freeContext(context);
    }
}

/// <summary>
/// Reads a recording back by decoding it: what the tests and the sync test check a file with. Not used while
/// recording.
/// </summary>
public sealed unsafe class MediaReader : IDisposable
{
    private AVFormatContext* _input;

    public MediaReader(string path)
    {
        AVFormatContext* input = null;
        FfmpegException.Check(ffmpeg.avformat_open_input(&input, path, null, null), MediaStrings.Opening(path));
        _input = input;
        FfmpegException.Check(ffmpeg.avformat_find_stream_info(input, null), MediaStrings.Reading(path));
        var streams = new List<MediaStreamInfo>();
        for (var i = 0; i < input->nb_streams; i++)
        {
            var stream = input->streams[i];
            var parameters = stream->codecpar;
            if (parameters->codec_type is not (AVMediaType.AVMEDIA_TYPE_VIDEO or AVMediaType.AVMEDIA_TYPE_AUDIO))
            {
                continue;
            }

            var title = ffmpeg.av_dict_get(stream->metadata, "title", null, 0);
            if (title is null)
            {
                title = ffmpeg.av_dict_get(stream->metadata, "name", null, 0); // how MP4 track names come back
            }

            if ((stream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0)
            {
                continue; // cover art in a music file
            }

            var timeBase = ffmpeg.av_q2d(stream->time_base);
            var rate = stream->avg_frame_rate.num > 0 ? stream->avg_frame_rate : stream->r_frame_rate;
            var isVideo = parameters->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO;
            streams.Add(new MediaStreamInfo(
                (int)i,
                isVideo,
                ffmpeg.avcodec_get_name(parameters->codec_id),
                title is null ? null : Marshal.PtrToStringUTF8((nint)title->value),
                (stream->disposition & ffmpeg.AV_DISPOSITION_DEFAULT) != 0,
                stream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->start_time * timeBase,
                stream->duration == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->duration * timeBase,
                parameters->width,
                parameters->height,
                isVideo && rate.num > 0 && rate.den > 0 ? ffmpeg.av_q2d(rate) : 0,
                isVideo ? 0 : parameters->sample_rate,
                isVideo ? 0 : parameters->ch_layout.nb_channels));
        }

        Streams = streams;
        Duration = input->duration == ffmpeg.AV_NOPTS_VALUE ? 0 : input->duration / (double)ffmpeg.AV_TIME_BASE;
    }

    public IReadOnlyList<MediaStreamInfo> Streams { get; }

    public IEnumerable<MediaStreamInfo> AudioStreams => Streams.Where(s => !s.IsVideo);

    public MediaStreamInfo Video => Streams.First(s => s.IsVideo);

    /// <summary>The container's duration in seconds.</summary>
    public double Duration { get; }

    /// <summary>
    /// Decodes the whole video: each picture's time in seconds and the mean luma (0–255) of a rectangle of it (the
    /// whole picture by default).
    /// </summary>
    public List<(double Time, double Luma)> VideoLuma(int x = 0, int y = 0, int width = 0, int height = 0)
    {
        var result = new List<(double, double)>();
        Decode(Video.Index, frame =>
        {
            var w = width > 0 ? width : frame->width - x;
            var h = height > 0 ? height : frame->height - y;
            long sum = 0;
            for (var row = y; row < y + h; row++)
            {
                var line = frame->data[0] + (row * frame->linesize[0]);
                for (var column = x; column < x + w; column++)
                {
                    sum += line[column];
                }
            }

            result.Add((frame->best_effort_timestamp * TimeBase(Video.Index), (double)sum / (w * h)));
        });
        return result;
    }

    /// <summary>
    /// Decodes a video stream (the first by default) and hands each picture over as top-down BGRA rows of
    /// <c>width * 4</c> bytes, with its time in seconds. The buffer is reused: copy what you keep.
    /// </summary>
    public void ReadVideo(BgraFrameHandler onFrame, int streamIndex = -1)
    {
        ArgumentNullException.ThrowIfNull(onFrame);
        var index = streamIndex >= 0 ? streamIndex : Video.Index;
        using var converter = new BgraConverter();
        Decode(index, frame =>
        {
            var bgra = converter.Convert(frame);
            onFrame(frame->best_effort_timestamp * TimeBase(index), frame->width, frame->height, bgra);
        });
    }

    /// <summary>Decodes a sound stream to mono (the channels averaged), with the time of its first sample in seconds.</summary>
    public (double Start, int SampleRate, float[] Samples) Audio(int streamIndex)
    {
        var samples = new List<float>();
        double? start = null;
        var rate = 0;
        ReadAudio(streamIndex, (time, sampleRate, chunk) =>
        {
            rate = sampleRate;
            start ??= time;
            foreach (var sample in chunk)
            {
                samples.Add(sample);
            }
        });
        return (start ?? 0, rate, samples.ToArray());
    }

    /// <summary>Decodes a sound stream to mono a frame at a time (for files too long to hold in memory).</summary>
    public void ReadAudio(int streamIndex, MonoSoundHandler onSound)
    {
        ArgumentNullException.ThrowIfNull(onSound);
        var mono = Array.Empty<float>();
        Decode(streamIndex, frame =>
        {
            var channels = frame->ch_layout.nb_channels;
            if (mono.Length < frame->nb_samples)
            {
                mono = new float[frame->nb_samples];
            }

            for (var i = 0; i < frame->nb_samples; i++)
            {
                float sum = 0;
                for (var c = 0u; c < channels; c++)
                {
                    sum += frame->format == (int)AVSampleFormat.AV_SAMPLE_FMT_FLTP
                        ? ((float*)frame->data[c])[i]
                        : ((float*)frame->data[0])[(i * channels) + c];
                }

                mono[i] = sum / channels;
            }

            onSound(frame->best_effort_timestamp * TimeBase(streamIndex), frame->sample_rate, mono.AsSpan(0, frame->nb_samples));
        });
    }

    private double TimeBase(int stream) => ffmpeg.av_q2d(_input->streams[stream]->time_base);

    private delegate void FrameHandler(AVFrame* frame);

    private void Decode(int streamIndex, FrameHandler onFrame)
    {
        var stream = _input->streams[streamIndex];
        var codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        var context = ffmpeg.avcodec_alloc_context3(codec);
        var packet = ffmpeg.av_packet_alloc();
        var frame = ffmpeg.av_frame_alloc();
        try
        {
            FfmpegException.Check(ffmpeg.avcodec_parameters_to_context(context, stream->codecpar), MediaStrings.SettingUpDecoder);
            context->pkt_timebase = stream->time_base;
            FfmpegException.Check(ffmpeg.avcodec_open2(context, codec, null), MediaStrings.OpeningDecoder);
            FfmpegException.Check(ffmpeg.av_seek_frame(_input, -1, long.MinValue, ffmpeg.AVSEEK_FLAG_BACKWARD), MediaStrings.Rewinding);
            while (ffmpeg.av_read_frame(_input, packet) >= 0)
            {
                if (packet->stream_index == streamIndex)
                {
                    FfmpegException.Check(ffmpeg.avcodec_send_packet(context, packet), MediaStrings.Decoding);
                    Drain(context, frame, onFrame);
                }

                ffmpeg.av_packet_unref(packet);
            }

            ffmpeg.avcodec_send_packet(context, null);
            Drain(context, frame, onFrame);
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
            ffmpeg.avcodec_free_context(&context);
        }
    }

    private static void Drain(AVCodecContext* context, AVFrame* frame, FrameHandler onFrame)
    {
        while (ffmpeg.avcodec_receive_frame(context, frame) == 0)
        {
            onFrame(frame);
            ffmpeg.av_frame_unref(frame);
        }
    }

    public void Dispose()
    {
        var input = _input;
        _input = null;
        ffmpeg.avformat_close_input(&input);
    }
}
