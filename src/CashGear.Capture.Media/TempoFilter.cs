using System.Globalization;
using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media;

/// <summary>Receives sound as 48 kHz stereo float, interleaved.</summary>
public delegate void SampleSink(ReadOnlySpan<float> interleaved);

/// <summary>
/// Plays sound faster or slower without changing its pitch, with FFmpeg's <c>atempo</c> filter: 48 kHz stereo float
/// in, about <c>1 / tempo</c> times as many samples out. atempo takes 0.5 to 100 at once, so slower tempos are
/// chained (0.25 is 0.5 twice). One thread at a time.
/// </summary>
public sealed unsafe class TempoFilter : IDisposable
{
    public const double Slowest = 0.0625;
    public const double Fastest = 100;

    private AVFilterGraph* _graph;
    private AVFilterContext* _source;
    private AVFilterContext* _sink;
    private AVFrame* _in;
    private AVFrame* _out;
    private long _next;

    /// <exception cref="ArgumentOutOfRangeException">Not between <see cref="Slowest"/> and <see cref="Fastest"/>.</exception>
    public TempoFilter(double tempo)
    {
        if (!(tempo >= Slowest && tempo <= Fastest))
        {
            throw new ArgumentOutOfRangeException(nameof(tempo), tempo, $"atempo takes {Slowest} to {Fastest}.");
        }

        Tempo = tempo;
        _graph = ffmpeg.avfilter_graph_alloc();
        _in = ffmpeg.av_frame_alloc();
        _out = ffmpeg.av_frame_alloc();
        try
        {
            var args = string.Create(CultureInfo.InvariantCulture, $"time_base=1/{AudioEncoder.SampleRate}:sample_rate={AudioEncoder.SampleRate}:sample_fmt=flt:channel_layout=stereo");
            AVFilterContext* source = null;
            AVFilterContext* sink = null;
            using var log = FfmpegLog.Capture();
            Check(ffmpeg.avfilter_graph_create_filter(&source, ffmpeg.avfilter_get_by_name("abuffer"), "in", args, null, _graph), "Creating the tempo filter's input", log);
            Check(ffmpeg.avfilter_graph_create_filter(&sink, ffmpeg.avfilter_get_by_name("abuffersink"), "out", null, null, _graph), "Creating the tempo filter's output", log);
            var outputs = ffmpeg.avfilter_inout_alloc();
            var inputs = ffmpeg.avfilter_inout_alloc();
            outputs->name = ffmpeg.av_strdup("in");
            outputs->filter_ctx = source;
            inputs->name = ffmpeg.av_strdup("out");
            inputs->filter_ctx = sink;
            var description = Description(tempo);
            var parsed = ffmpeg.avfilter_graph_parse_ptr(_graph, description, &inputs, &outputs, null);
            ffmpeg.avfilter_inout_free(&inputs);
            ffmpeg.avfilter_inout_free(&outputs);
            Check(parsed, $"Setting up the tempo filter ({description})", log);
            Check(ffmpeg.avfilter_graph_config(_graph, null), "Starting the tempo filter", log);
            _source = source;
            _sink = sink;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public double Tempo { get; }

    /// <summary>The filters for a tempo: atempo (chained below 0.5), back to stereo float.</summary>
    internal static string Description(double tempo)
    {
        var parts = new List<string>();
        while (tempo < 0.5)
        {
            parts.Add("atempo=0.5");
            tempo /= 0.5;
        }

        parts.Add(string.Create(CultureInfo.InvariantCulture, $"atempo={tempo:0.######}"));
        parts.Add(string.Create(CultureInfo.InvariantCulture, $"aformat=sample_fmts=flt:channel_layouts=stereo:sample_rates={AudioEncoder.SampleRate}"));
        return string.Join(",", parts);
    }

    /// <summary>Feeds <paramref name="interleaved"/> in; whatever comes out goes to <paramref name="output"/>.</summary>
    public void Push(ReadOnlySpan<float> interleaved, SampleSink output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var samples = interleaved.Length / AudioEncoder.Channels;
        if (samples == 0 || _graph is null)
        {
            return;
        }

        _in->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLT;
        _in->sample_rate = AudioEncoder.SampleRate;
        _in->nb_samples = samples;
        ffmpeg.av_channel_layout_default(&_in->ch_layout, AudioEncoder.Channels);
        FfmpegException.Check(ffmpeg.av_frame_get_buffer(_in, 0), "Allocating sound for the tempo filter");
        interleaved[..(samples * AudioEncoder.Channels)].CopyTo(new Span<float>(_in->data[0], samples * AudioEncoder.Channels));
        _in->pts = _next;
        _next += samples;
        var added = ffmpeg.av_buffersrc_add_frame_flags(_source, _in, 0); // takes the frame's buffers
        ffmpeg.av_frame_unref(_in);
        FfmpegException.Check(added, "Changing the sound's tempo");
        Drain(output);
    }

    /// <summary>The end of the sound: everything the filter still holds goes to <paramref name="output"/>.</summary>
    public void Flush(SampleSink output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (_graph is null)
        {
            return;
        }

        FfmpegException.Check(ffmpeg.av_buffersrc_add_frame_flags(_source, null, 0), "Changing the sound's tempo");
        Drain(output);
    }

    public void Dispose()
    {
        var graph = _graph;
        var input = _in;
        var output = _out;
        _graph = null;
        _in = null;
        _out = null;
        _source = null;
        _sink = null;
        ffmpeg.avfilter_graph_free(&graph);
        ffmpeg.av_frame_free(&input);
        ffmpeg.av_frame_free(&output);
    }

    private void Drain(SampleSink output)
    {
        while (ffmpeg.av_buffersink_get_frame(_sink, _out) >= 0)
        {
            try
            {
                output(new ReadOnlySpan<float>(_out->data[0], _out->nb_samples * AudioEncoder.Channels));
            }
            finally
            {
                ffmpeg.av_frame_unref(_out);
            }
        }
    }

    private static void Check(int result, string what, FfmpegLog.Scope log)
    {
        if (result < 0)
        {
            throw new FfmpegException(what, result, log.Summary());
        }
    }
}
