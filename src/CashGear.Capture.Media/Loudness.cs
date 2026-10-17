using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media;

/// <summary>A sound track's loudness (EBU R128) as FFmpeg's <c>loudnorm</c> measures it in its first pass.</summary>
/// <param name="IntegratedLufs">The whole track's loudness; −∞ for silence.</param>
/// <param name="TruePeakDb">Its loudest true peak (dBTP).</param>
/// <param name="RangeLu">How much its loudness varies (LU).</param>
/// <param name="ThresholdLufs">The gate below which quiet passages don't count.</param>
/// <param name="TargetOffsetLu">The correction loudnorm wants for the second pass.</param>
public sealed record LoudnessMeasurement(double IntegratedLufs, double TruePeakDb, double RangeLu, double ThresholdLufs, double TargetOffsetLu);

/// <summary>
/// Evens out a recording's sound to −16 LUFS (the usual level for speech online), with FFmpeg's <c>loudnorm</c> in two
/// passes: <see cref="Measure"/> reads each sound track through it once, then <see cref="Remuxer"/> re-encodes each
/// track through it with the measured values (a <see cref="LoudnessTrack"/>), while the video is copied as it is.
/// With the measurements, loudnorm can usually just turn the whole track up or down (linear mode); when that would push
/// peaks over <see cref="TargetTruePeakDb"/>, it limits them as it goes.
/// </summary>
public static unsafe class Loudness
{
    public const double TargetLufs = -16;
    public const double TargetTruePeakDb = -1.5;

    /// <summary>The loudness range allowed before loudnorm compresses: wide, so speech with pauses is only turned up or down.</summary>
    public const double TargetRangeLu = 20;

    /// <summary>A track quieter than this (silence, or a muted source) is left as it is rather than turned up.</summary>
    public const double QuietestLufs = -60;

    /// <summary>Measures every sound track of <paramref name="path"/>, by stream index.</summary>
    public static IReadOnlyDictionary<int, LoudnessMeasurement> Measure(string path, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        AVFormatContext* input = null;
        var packet = ffmpeg.av_packet_alloc();
        var meters = new Dictionary<int, Meter>();
        try
        {
            FfmpegException.Check(ffmpeg.avformat_open_input(&input, path, null, null), $"Opening {path}");
            FfmpegException.Check(ffmpeg.avformat_find_stream_info(input, null), "Reading the recording");
            for (var i = 0; i < input->nb_streams; i++)
            {
                if (input->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
                {
                    meters[i] = new Meter(input->streams[i]);
                }
            }

            var size = Math.Max(1, ffmpeg.avio_size(input->pb));
            var count = 0;
            while (meters.Count > 0 && ffmpeg.av_read_frame(input, packet) >= 0)
            {
                ct.ThrowIfCancellationRequested();
                if (meters.TryGetValue(packet->stream_index, out var meter) && (packet->flags & ffmpeg.AV_PKT_FLAG_CORRUPT) == 0)
                {
                    meter.Send(packet);
                }

                ffmpeg.av_packet_unref(packet);
                if (++count % 256 == 0)
                {
                    progress?.Report(Math.Clamp((double)ffmpeg.avio_tell(input->pb) / size, 0, 1));
                }
            }

            var results = new Dictionary<int, LoudnessMeasurement>();
            foreach (var (index, meter) in meters)
            {
                if (meter.Finish() is { } measured)
                {
                    results[index] = measured;
                }
            }

            progress?.Report(1);
            return results;
        }
        finally
        {
            foreach (var meter in meters.Values)
            {
                meter.Dispose();
            }

            ffmpeg.av_packet_free(&packet);
            ffmpeg.avformat_close_input(&input);
        }
    }

    /// <summary>Whether a measured track gets normalized (not silence).</summary>
    public static bool Applies(LoudnessMeasurement measured) =>
        measured is not null && double.IsFinite(measured.IntegratedLufs) && measured.IntegratedLufs > QuietestLufs;

    /// <summary>The second pass's filters: loudnorm with the measurements, back to 48 kHz stereo float planar.</summary>
    internal static string SecondPass(LoudnessMeasurement m) => string.Create(
        CultureInfo.InvariantCulture,
        $"loudnorm=I={TargetLufs}:TP={TargetTruePeakDb}:LRA={TargetRangeLu}:measured_I={m.IntegratedLufs:0.00}:measured_TP={m.TruePeakDb:0.00}:measured_LRA={m.RangeLu:0.00}:measured_thresh={m.ThresholdLufs:0.00}:offset={m.TargetOffsetLu:0.00}:linear=true:print_format=none,aresample={AudioEncoder.SampleRate},aformat=sample_fmts=fltp:channel_layouts=stereo");

    private static string FirstPass => string.Create(
        CultureInfo.InvariantCulture, $"loudnorm=I={TargetLufs}:TP={TargetTruePeakDb}:LRA={TargetRangeLu}:print_format=json");

    /// <summary>loudnorm's JSON report ("input_i" : "-23.54", …); null if there is none.</summary>
    internal static LoudnessMeasurement? Parse(string log)
    {
        var start = log.IndexOf('{', StringComparison.Ordinal);
        var end = log.LastIndexOf('}');
        if (start < 0 || end < start)
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(log[start..(end + 1)]);
            var root = json.RootElement;
            return new LoudnessMeasurement(Number(root, "input_i"), Number(root, "input_tp"), Number(root, "input_lra"), Number(root, "input_thresh"), Number(root, "target_offset"));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }

        static double Number(JsonElement root, string name)
        {
            var text = root.GetProperty(name).GetString() ?? string.Empty;
            return text.Trim().ToLowerInvariant() switch
            {
                "-inf" => double.NegativeInfinity,
                "inf" or "+inf" => double.PositiveInfinity,
                var value => double.Parse(value, CultureInfo.InvariantCulture),
            };
        }
    }

    /// <summary>One sound track decoded through loudnorm's first pass.</summary>
    private sealed class Meter : IDisposable
    {
        private readonly AudioDecoder _decoder;
        private readonly AudioFilter _filter;

        public Meter(AVStream* stream)
        {
            _decoder = new AudioDecoder(stream);
            _filter = new AudioFilter(_decoder.Context, FirstPass, frameSize: 0);
        }

        public void Send(AVPacket* packet) => _decoder.Send(packet, frame => _filter.Send(frame, _ => { }));

        /// <summary>Flushes the track and reads loudnorm's report, which it logs when its graph is freed.</summary>
        public LoudnessMeasurement? Finish()
        {
            _decoder.Send(null, frame => _filter.Send(frame, _ => { }));
            _filter.Send(null, _ => { });
            using var log = FfmpegLog.Capture(ffmpeg.AV_LOG_INFO);
            _filter.Dispose();
            return Parse(string.Join("\n", log.Messages));
        }

        public void Dispose()
        {
            _filter.Dispose();
            _decoder.Dispose();
        }
    }
}

/// <summary>
/// One sound track re-encoded to −16 LUFS (the second pass): its packets are decoded, put through loudnorm with the
/// track's measurements, and encoded again to AAC like the recording's own sound (<see cref="AudioEncoder"/>). The
/// output starts at the time of the first sound decoded and then counts the samples that come out, which are exactly
/// as many as went in, so the track keeps its place against the video.
/// </summary>
public sealed unsafe class LoudnessTrack : IDisposable
{
    private readonly AudioDecoder _decoder;
    private readonly AudioFilter _filter;
    private readonly float[] _interleaved;
    private long? _start;
    private long _produced;

    public LoudnessTrack(AVStream* stream, LoudnessMeasurement measured, int outputStream)
    {
        ArgumentNullException.ThrowIfNull(measured);
        _decoder = new AudioDecoder(stream);
        Encoder = AudioEncoder.Open(outputStream);
        _filter = new AudioFilter(_decoder.Context, Loudness.SecondPass(measured), Encoder.FrameSize);
        _interleaved = new float[Encoder.FrameSize * AudioEncoder.Channels];
        Measured = measured;
    }

    public AudioEncoder Encoder { get; }

    public LoudnessMeasurement Measured { get; }

    /// <summary>Re-encodes one packet of the track (its timestamps already in the output's terms); encoded packets go to <paramref name="output"/>.</summary>
    public void Send(AVPacket* packet, List<EncodedPacket> output) =>
        _decoder.Send(packet, frame => Filter(frame, output));

    /// <summary>Everything still held by the decoder, the filter and the encoder.</summary>
    public void Flush(List<EncodedPacket> output)
    {
        _decoder.Send(null, frame => Filter(frame, output));
        _filter.Send(null, filtered => Encode(filtered, output));
        Encoder.Flush(output);
    }

    public void Dispose()
    {
        _filter.Dispose();
        _decoder.Dispose();
        Encoder.Dispose();
    }

    private void Filter(AVFrame* frame, List<EncodedPacket> output)
    {
        if (_start is null && frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE)
        {
            _start = ffmpeg.av_rescale_q(frame->best_effort_timestamp, _decoder.TimeBase, new AVRational { num = 1, den = AudioEncoder.SampleRate });
        }

        _filter.Send(frame, filtered => Encode(filtered, output));
    }

    private void Encode(AVFrame* frame, List<EncodedPacket> output)
    {
        var samples = frame->nb_samples;
        var left = new ReadOnlySpan<float>(frame->data[0], samples);
        var right = new ReadOnlySpan<float>(frame->data[1], samples);
        for (var i = 0; i < samples; i++)
        {
            _interleaved[2 * i] = left[i];
            _interleaved[(2 * i) + 1] = right[i];
        }

        Encoder.Encode(_interleaved.AsSpan(0, samples * AudioEncoder.Channels), (_start ?? 0) + _produced, output);
        _produced += samples;
    }
}

/// <summary>Decodes one sound stream, a packet at a time.</summary>
internal sealed unsafe class AudioDecoder : IDisposable
{
    private AVCodecContext* _context;
    private AVFrame* _frame;

    public AudioDecoder(AVStream* stream)
    {
        var codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
        if (codec is null)
        {
            throw new FfmpegException($"No decoder for the sound track ({ffmpeg.avcodec_get_name(stream->codecpar->codec_id)}).");
        }

        _context = ffmpeg.avcodec_alloc_context3(codec);
        _frame = ffmpeg.av_frame_alloc();
        FfmpegException.Check(ffmpeg.avcodec_parameters_to_context(_context, stream->codecpar), "Setting up the sound decoder");
        _context->pkt_timebase = stream->time_base;
        TimeBase = stream->time_base;
        FfmpegException.Check(ffmpeg.avcodec_open2(_context, codec, null), "Opening the sound decoder");
    }

    public delegate void FrameHandler(AVFrame* frame);

    public AVCodecContext* Context => _context;

    public AVRational TimeBase { get; }

    /// <summary>Decodes <paramref name="packet"/> (null: the end) and hands on each frame that comes out.</summary>
    public void Send(AVPacket* packet, FrameHandler onFrame)
    {
        var sent = ffmpeg.avcodec_send_packet(_context, packet);
        if (sent < 0 && sent != ffmpeg.AVERROR_EOF && sent != ffmpeg.AVERROR_INVALIDDATA)
        {
            FfmpegException.Check(sent, "Decoding sound");
        }

        while (ffmpeg.avcodec_receive_frame(_context, _frame) == 0)
        {
            onFrame(_frame);
            ffmpeg.av_frame_unref(_frame);
        }
    }

    public void Dispose()
    {
        var frame = _frame;
        var context = _context;
        _frame = null;
        _context = null;
        ffmpeg.av_frame_free(&frame);
        ffmpeg.avcodec_free_context(&context);
    }
}

/// <summary>An FFmpeg filter graph for sound: frames in at the decoder's format, frames out of <paramref name="frameSize"/> samples (0: as they come).</summary>
internal sealed unsafe class AudioFilter : IDisposable
{
    private AVFilterGraph* _graph;
    private AVFilterContext* _source;
    private AVFilterContext* _sink;
    private AVFrame* _out;
    private long _next;

    public AudioFilter(AVCodecContext* decoder, string description, int frameSize)
    {
        _graph = ffmpeg.avfilter_graph_alloc();
        _out = ffmpeg.av_frame_alloc();
        try
        {
            var layout = stackalloc byte[64];
            ffmpeg.av_channel_layout_describe(&decoder->ch_layout, layout, 64);
            var args = string.Create(
                CultureInfo.InvariantCulture,
                $"time_base=1/{decoder->sample_rate}:sample_rate={decoder->sample_rate}:sample_fmt={ffmpeg.av_get_sample_fmt_name(decoder->sample_fmt)}:channel_layout={Marshal.PtrToStringUTF8((nint)layout)}");
            AVFilterContext* source = null;
            AVFilterContext* sink = null;
            using (var log = FfmpegLog.Capture())
            {
                Check(ffmpeg.avfilter_graph_create_filter(&source, ffmpeg.avfilter_get_by_name("abuffer"), "in", args, null, _graph), "Creating the sound filter's input", log);
                Check(ffmpeg.avfilter_graph_create_filter(&sink, ffmpeg.avfilter_get_by_name("abuffersink"), "out", null, null, _graph), "Creating the sound filter's output", log);
                var outputs = ffmpeg.avfilter_inout_alloc();
                var inputs = ffmpeg.avfilter_inout_alloc();
                outputs->name = ffmpeg.av_strdup("in");
                outputs->filter_ctx = source;
                inputs->name = ffmpeg.av_strdup("out");
                inputs->filter_ctx = sink;
                var parsed = ffmpeg.avfilter_graph_parse_ptr(_graph, description, &inputs, &outputs, null);
                ffmpeg.avfilter_inout_free(&inputs);
                ffmpeg.avfilter_inout_free(&outputs);
                Check(parsed, $"Setting up the sound filter ({description})", log);
                Check(ffmpeg.avfilter_graph_config(_graph, null), "Starting the sound filter", log);
            }

            if (frameSize > 0)
            {
                ffmpeg.av_buffersink_set_frame_size(sink, (uint)frameSize);
            }

            _source = source;
            _sink = sink;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Filters <paramref name="frame"/> (null: the end) and hands on each frame that comes out.</summary>
    public void Send(AVFrame* frame, AudioDecoder.FrameHandler onFrame)
    {
        if (_graph is null)
        {
            return;
        }

        if (frame is not null)
        {
            frame->pts = _next; // the samples in a row: the filter's own time
            _next += frame->nb_samples;
        }

        FfmpegException.Check(ffmpeg.av_buffersrc_add_frame_flags(_source, frame, (int)AvBuffersrcFlag.AV_BUFFERSRC_FLAG_KEEP_REF), "Filtering sound");
        while (ffmpeg.av_buffersink_get_frame(_sink, _out) >= 0)
        {
            onFrame(_out);
            ffmpeg.av_frame_unref(_out);
        }
    }

    public void Dispose()
    {
        var graph = _graph;
        var frame = _out;
        _graph = null;
        _out = null;
        _source = null;
        _sink = null;
        ffmpeg.avfilter_graph_free(&graph);
        ffmpeg.av_frame_free(&frame);
    }

    private static void Check(int result, string what, FfmpegLog.Scope log)
    {
        if (result < 0)
        {
            throw new FfmpegException(what, result, log.Summary());
        }
    }
}
