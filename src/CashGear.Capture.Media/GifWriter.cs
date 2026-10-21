using System.ComponentModel;
using System.Globalization;
using CashGear.Capture.Media.Resources;
using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media;

/// <summary>How a GIF spreads its few colours over the rest.</summary>
public enum GifDither
{
    /// <summary>A fine, steady pattern (Bayer): smaller files, nothing crawls between frames. Best for screens.</summary>
    Pattern,

    /// <summary>Error diffusion (Sierra-2-4A): smoother gradients and photos, larger files.</summary>
    Diffusion,

    /// <summary>The nearest colour only: flat areas stay flat, gradients band.</summary>
    None,
}

/// <param name="Colors">At most 256 (one is kept for "unchanged since the last frame").</param>
/// <param name="Loop">0 plays over and over, -1 once, n plays n more times.</param>
public sealed record GifSettings(int Colors = 256, GifDither Dither = GifDither.Pattern, int Loop = 0);

/// <summary>
/// Writes an animated GIF with FFmpeg in two passes over the same pictures, as its best palette needs: first every
/// picture is shown to <c>palettegen</c>, which picks the colours; then each is mapped to them by <c>paletteuse</c>
/// (dithered, only the area that changed) and encoded by the <c>gif</c> encoder, which leaves what didn't change
/// transparent. Top-down BGRA in, of the writer's size; one thread at a time.
/// </summary>
public sealed unsafe class GifWriter : IDisposable
{
    /// <summary>AV_BUFFERSRC_FLAG_KEEP_REF: the source copies the frame, which stays ours.</summary>
    private const int KeepReference = 8;

    private readonly string _path;
    private readonly int _width;
    private readonly int _height;
    private readonly int _fps;
    private readonly GifSettings _settings;
    private AVFilterGraph* _learning;
    private AVFilterContext* _learnIn;
    private AVFilterContext* _learnOut;
    private AVFilterGraph* _mapping;
    private AVFilterContext* _mapIn;
    private AVFilterContext* _mapOut;
    private AVFrame* _frame;
    private AVFrame* _palette;
    private AVCodecContext* _encoder;
    private AVFormatContext* _output;
    private AVPacket* _packet;
    private long _learned;
    private long _written;

    public GifWriter(string path, int width, int height, int fps, GifSettings settings)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(fps, 1);
        _path = path;
        _width = width;
        _height = height;
        _fps = fps;
        _settings = settings;
        _frame = ffmpeg.av_frame_alloc();
        _packet = ffmpeg.av_packet_alloc();
        try
        {
            var colors = Math.Clamp(settings.Colors, 4, 256);
            _learning = Graph(string.Create(CultureInfo.InvariantCulture, $"[in]palettegen=max_colors={colors}:stats_mode=full[out]"), withPalette: false, out _learnIn, out _learnOut, out _);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public int Width => _width;

    public int Height => _height;

    /// <summary>The first pass: a picture for the palette.</summary>
    public void Learn(ReadOnlySpan<byte> bgra, int stride)
    {
        if (_learning is null)
        {
            throw new InvalidOperationException("The palette is made already.");
        }

        Push(_learnIn, bgra, stride, _learned++, MediaStrings.ChoosingGifColours);
    }

    /// <summary>The end of the first pass: the palette is made, and the file started.</summary>
    public void EndLearning()
    {
        if (_learning is null)
        {
            throw new InvalidOperationException("The palette is made already.");
        }

        FfmpegException.Check(ffmpeg.av_buffersrc_add_frame_flags(_learnIn, null, 0), MediaStrings.ChoosingGifColours);
        _palette = ffmpeg.av_frame_alloc();
        FfmpegException.Check(ffmpeg.av_buffersink_get_frame(_learnOut, _palette), MediaStrings.ChoosingGifColours);
        var learning = _learning;
        ffmpeg.avfilter_graph_free(&learning);
        _learning = null;
        _learnIn = null;
        _learnOut = null;

        var dither = _settings.Dither switch
        {
            GifDither.Diffusion => "sierra2_4a",
            GifDither.None => "none",
            _ => "bayer:bayer_scale=3",
        };
        _mapping = Graph($"[in][palette]paletteuse=dither={dither}:diff_mode=rectangle[out]", withPalette: true, out _mapIn, out _mapOut, out var paletteIn);
        _palette->pts = 0;
        using (var log = FfmpegLog.Capture())
        {
            Check(ffmpeg.av_buffersrc_add_frame_flags(paletteIn, _palette, KeepReference), MediaStrings.GivingGifColours, log);
            Check(ffmpeg.av_buffersrc_add_frame_flags(paletteIn, null, 0), MediaStrings.GivingGifColours, log);
        }

        OpenEncoder();
    }

    /// <summary>The second pass: the same pictures again, in the same order, written.</summary>
    public void Write(ReadOnlySpan<byte> bgra, int stride)
    {
        if (_mapping is null)
        {
            throw new InvalidOperationException("The palette isn't made yet (EndLearning).");
        }

        Push(_mapIn, bgra, stride, _written++, MediaStrings.WritingGif);
        Drain();
    }

    /// <summary>Writes what is left and the file's end; returns its size in bytes.</summary>
    public long Finish()
    {
        if (_mapping is null)
        {
            throw new InvalidOperationException("The palette isn't made yet (EndLearning).");
        }

        FfmpegException.Check(ffmpeg.av_buffersrc_add_frame_flags(_mapIn, null, 0), MediaStrings.WritingGif);
        Drain();
        Encode(null);
        FfmpegException.Check(ffmpeg.av_write_trailer(_output), MediaStrings.FinishingGif);
        ffmpeg.avio_closep(&_output->pb);
        return new FileInfo(_path).Length;
    }

    public void Dispose()
    {
        var learning = _learning;
        var mapping = _mapping;
        var frame = _frame;
        var palette = _palette;
        var encoder = _encoder;
        var packet = _packet;
        _learning = null;
        _mapping = null;
        _frame = null;
        _palette = null;
        _encoder = null;
        _packet = null;
        ffmpeg.avfilter_graph_free(&learning);
        ffmpeg.avfilter_graph_free(&mapping);
        ffmpeg.av_frame_free(&frame);
        ffmpeg.av_frame_free(&palette);
        ffmpeg.avcodec_free_context(&encoder);
        ffmpeg.av_packet_free(&packet);
        if (_output is not null)
        {
            ffmpeg.avio_closep(&_output->pb);
            ffmpeg.avformat_free_context(_output);
            _output = null;
        }
    }

    /// <summary>A filter graph from the BGRA source "in" (and the palette source "palette") to the sink "out".</summary>
    private AVFilterGraph* Graph([Localizable(false)] string description, bool withPalette, out AVFilterContext* input, out AVFilterContext* output, out AVFilterContext* paletteIn)
    {
        var graph = ffmpeg.avfilter_graph_alloc();
        input = null;
        output = null;
        paletteIn = null;
        try
        {
            using var log = FfmpegLog.Capture();
            AVFilterContext* source = null;
            AVFilterContext* palette = null;
            AVFilterContext* sink = null;
            var args = string.Create(CultureInfo.InvariantCulture, $"video_size={_width}x{_height}:pix_fmt={(int)AVPixelFormat.AV_PIX_FMT_BGRA}:time_base=1/{_fps}:pixel_aspect=1/1");
            Check(ffmpeg.avfilter_graph_create_filter(&source, ffmpeg.avfilter_get_by_name("buffer"), "in", args, null, graph), MediaStrings.CreatingGifInput, log);
            Check(ffmpeg.avfilter_graph_create_filter(&sink, ffmpeg.avfilter_get_by_name("buffersink"), "out", null, null, graph), MediaStrings.CreatingGifOutput, log);
            var outputs = ffmpeg.avfilter_inout_alloc();
            outputs->name = ffmpeg.av_strdup("in");
            outputs->filter_ctx = source;
            if (withPalette)
            {
                var paletteArgs = string.Create(CultureInfo.InvariantCulture, $"video_size=16x16:pix_fmt={(int)AVPixelFormat.AV_PIX_FMT_BGRA}:time_base=1/{_fps}:pixel_aspect=1/1");
                Check(ffmpeg.avfilter_graph_create_filter(&palette, ffmpeg.avfilter_get_by_name("buffer"), "palette", paletteArgs, null, graph), MediaStrings.CreatingGifPaletteInput, log);
                var second = ffmpeg.avfilter_inout_alloc();
                second->name = ffmpeg.av_strdup("palette");
                second->filter_ctx = palette;
                outputs->next = second;
            }

            var inputs = ffmpeg.avfilter_inout_alloc();
            inputs->name = ffmpeg.av_strdup("out");
            inputs->filter_ctx = sink;
            var parsed = ffmpeg.avfilter_graph_parse_ptr(graph, description, &inputs, &outputs, null);
            ffmpeg.avfilter_inout_free(&inputs);
            ffmpeg.avfilter_inout_free(&outputs);
            Check(parsed, MediaStrings.SettingUpGifFilters(description), log);
            Check(ffmpeg.avfilter_graph_config(graph, null), MediaStrings.StartingGifFilters, log);
            input = source;
            output = sink;
            paletteIn = palette;
            return graph;
        }
        catch
        {
            ffmpeg.avfilter_graph_free(&graph);
            throw;
        }
    }

    private void Push(AVFilterContext* source, ReadOnlySpan<byte> bgra, int stride, long pts, [Localizable(true)] string what)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(bgra.Length, stride * _height, nameof(bgra));
        _frame->format = (int)AVPixelFormat.AV_PIX_FMT_BGRA;
        _frame->width = _width;
        _frame->height = _height;
        FfmpegException.Check(ffmpeg.av_frame_get_buffer(_frame, 0), what);
        for (var y = 0; y < _height; y++)
        {
            bgra.Slice(y * stride, _width * 4).CopyTo(new Span<byte>(_frame->data[0] + (y * _frame->linesize[0]), _width * 4));
        }

        _frame->pts = pts;
        _frame->duration = 1;
        var added = ffmpeg.av_buffersrc_add_frame_flags(source, _frame, 0); // takes the frame's buffers
        ffmpeg.av_frame_unref(_frame);
        FfmpegException.Check(added, what);
    }

    /// <summary>The gif encoder and muxer: PAL8 at the writer's rate, looping as asked.</summary>
    private void OpenEncoder()
    {
        var codec = ffmpeg.avcodec_find_encoder_by_name("gif");
        if (codec is null)
        {
            throw new FfmpegException(MediaStrings.NoGifEncoder);
        }

        _encoder = ffmpeg.avcodec_alloc_context3(codec);
        _encoder->width = _width;
        _encoder->height = _height;
        _encoder->pix_fmt = AVPixelFormat.AV_PIX_FMT_PAL8;
        _encoder->time_base = new AVRational { num = 1, den = _fps };
        _encoder->framerate = new AVRational { num = _fps, den = 1 };
        FfmpegException.Check(ffmpeg.avcodec_open2(_encoder, codec, null), MediaStrings.OpeningGifEncoder);

        AVFormatContext* output = null;
        FfmpegException.Check(ffmpeg.avformat_alloc_output_context2(&output, null, "gif", _path), MediaStrings.CreatingGif);
        _output = output;
        var stream = ffmpeg.avformat_new_stream(output, null);
        FfmpegException.Check(ffmpeg.avcodec_parameters_from_context(stream->codecpar, _encoder), MediaStrings.CopyingGifSettings);
        stream->time_base = _encoder->time_base;
        FfmpegException.Check(ffmpeg.avio_open(&output->pb, _path, ffmpeg.AVIO_FLAG_WRITE), MediaStrings.CreatingGifFile);
        AVDictionary* options = null;
        ffmpeg.av_dict_set(&options, "loop", _settings.Loop.ToString(CultureInfo.InvariantCulture), 0);
        try
        {
            FfmpegException.Check(ffmpeg.avformat_write_header(output, &options), MediaStrings.StartingGif);
        }
        finally
        {
            ffmpeg.av_dict_free(&options);
        }
    }

    /// <summary>Encodes every picture paletteuse has ready.</summary>
    private void Drain()
    {
        while (ffmpeg.av_buffersink_get_frame(_mapOut, _frame) >= 0)
        {
            try
            {
                Encode(_frame);
            }
            finally
            {
                ffmpeg.av_frame_unref(_frame);
            }
        }
    }

    private void Encode(AVFrame* frame)
    {
        FfmpegException.Check(ffmpeg.avcodec_send_frame(_encoder, frame), MediaStrings.EncodingGif);
        while (true)
        {
            var received = ffmpeg.avcodec_receive_packet(_encoder, _packet);
            if (received == ffmpeg.AVERROR(ffmpeg.EAGAIN) || received == ffmpeg.AVERROR_EOF)
            {
                return;
            }

            FfmpegException.Check(received, MediaStrings.EncodingGif);
            _packet->stream_index = 0;
            if (_packet->duration <= 0)
            {
                _packet->duration = 1; // the last frame lasts as long as the others
            }

            ffmpeg.av_packet_rescale_ts(_packet, _encoder->time_base, _output->streams[0]->time_base);
            FfmpegException.Check(ffmpeg.av_interleaved_write_frame(_output, _packet), MediaStrings.WritingGif);
        }
    }

    private static void Check(int result, [Localizable(true)] string what, FfmpegLog.Scope log)
    {
        if (result < 0)
        {
            throw new FfmpegException(what, result, log.Summary());
        }
    }
}
