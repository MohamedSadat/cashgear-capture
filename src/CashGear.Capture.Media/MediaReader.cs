using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media;

/// <param name="Index">Index among all the file's streams.</param>
/// <param name="Start">The first packet's time in seconds.</param>
public sealed record MediaStreamInfo(int Index, bool IsVideo, string Codec, string? Title, bool IsDefault, double Start, double Duration, int Width, int Height);

/// <summary>Receives decoded sound mixed to mono, starting at <paramref name="time"/> seconds.</summary>
public delegate void MonoSoundHandler(double time, int sampleRate, ReadOnlySpan<float> samples);

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
        FfmpegException.Check(ffmpeg.avformat_open_input(&input, path, null, null), $"Opening {path}");
        _input = input;
        FfmpegException.Check(ffmpeg.avformat_find_stream_info(input, null), $"Reading {path}");
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

            var timeBase = ffmpeg.av_q2d(stream->time_base);
            streams.Add(new MediaStreamInfo(
                (int)i,
                parameters->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO,
                ffmpeg.avcodec_get_name(parameters->codec_id),
                title is null ? null : Marshal.PtrToStringUTF8((nint)title->value),
                (stream->disposition & ffmpeg.AV_DISPOSITION_DEFAULT) != 0,
                stream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->start_time * timeBase,
                stream->duration == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->duration * timeBase,
                parameters->width,
                parameters->height));
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
            FfmpegException.Check(ffmpeg.avcodec_parameters_to_context(context, stream->codecpar), "Setting up the decoder");
            context->pkt_timebase = stream->time_base;
            FfmpegException.Check(ffmpeg.avcodec_open2(context, codec, null), "Opening the decoder");
            FfmpegException.Check(ffmpeg.av_seek_frame(_input, -1, long.MinValue, ffmpeg.AVSEEK_FLAG_BACKWARD), "Rewinding");
            while (ffmpeg.av_read_frame(_input, packet) >= 0)
            {
                if (packet->stream_index == streamIndex)
                {
                    FfmpegException.Check(ffmpeg.avcodec_send_packet(context, packet), "Decoding");
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
