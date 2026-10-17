using System.Text.RegularExpressions;
using CashGear.Capture.Core.Recording;
using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media;

/// <summary>
/// One FFmpeg video encoder, used by one thread at a time. Frames are NV12 (planar YUV 4:2:0 for OpenH264) in
/// BT.709 limited range, the encoder's time base is one frame, a keyframe comes every two seconds and there are no
/// B-frames, so packets come out in order with their frame's timestamp.
/// </summary>
public sealed unsafe partial class VideoEncoder : IDisposable
{
    private AVCodecContext* _context;
    private AVFrame* _frame;
    private AVPacket* _packet;

    private VideoEncoder(EncoderCandidate candidate, VideoFormat format, int stream, AVCodecContext* context, string? detail)
    {
        Candidate = candidate;
        Format = format;
        Stream = stream;
        _context = context;
        Detail = detail;
        _packet = ffmpeg.av_packet_alloc();
        _frame = ffmpeg.av_frame_alloc();
        _frame->format = (int)context->pix_fmt;
        _frame->width = format.Width;
        _frame->height = format.Height;
        _frame->colorspace = context->colorspace;
        _frame->color_primaries = context->color_primaries;
        _frame->color_trc = context->color_trc;
        _frame->color_range = context->color_range;
        FfmpegException.Check(ffmpeg.av_frame_get_buffer(_frame, 0), "Allocating a video frame");
    }

    public EncoderCandidate Candidate { get; }

    public VideoFormat Format { get; }

    /// <summary>The stream its packets are for (see <see cref="EncodedPacket.Stream"/>).</summary>
    public int Stream { get; }

    /// <summary>Which device encodes, when FFmpeg says (Media Foundation names its encoder, e.g. "NVIDIA H.264 Encoder MFT").</summary>
    public string? Detail { get; }

    public AVPixelFormat PixelFormat => _context->pix_fmt;

    internal AVCodecContext* Context => _context;

    /// <summary>Opens the encoder; throws <see cref="FfmpegException"/> (with FFmpeg's explanation) if it won't open.</summary>
    public static VideoEncoder Open(
        EncoderCandidate candidate, VideoFormat format, IReadOnlyList<KeyValuePair<string, string>> options, int stream = 0)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(options);
        if (format.Width < 2 || format.Height < 2 || format.Width % 2 != 0 || format.Height % 2 != 0 || format.Fps <= 0)
        {
            throw new ArgumentException($"Not an encodable size: {format}.", nameof(format));
        }

        var codec = ffmpeg.avcodec_find_encoder_by_name(candidate.FfmpegName);
        if (codec is null)
        {
            throw new FfmpegException($"{candidate.FfmpegName} isn't in this FFmpeg build.");
        }

        var context = ffmpeg.avcodec_alloc_context3(codec);
        context->width = format.Width;
        context->height = format.Height;
        context->time_base = new AVRational { num = 1, den = format.Fps };
        context->framerate = new AVRational { num = format.Fps, den = 1 };
        context->pix_fmt = EncoderPresets.UsesPlanarYuv(candidate) ? AVPixelFormat.AV_PIX_FMT_YUV420P : AVPixelFormat.AV_PIX_FMT_NV12;
        context->gop_size = format.Fps * 2;
        context->max_b_frames = 0;
        context->colorspace = AVColorSpace.AVCOL_SPC_BT709;
        context->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
        context->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
        context->color_range = AVColorRange.AVCOL_RANGE_MPEG;
        context->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

        AVDictionary* dictionary = null;
        foreach (var (name, value) in options)
        {
            ffmpeg.av_dict_set(&dictionary, name, value, 0);
        }

        using var log = FfmpegLog.Capture(ffmpeg.AV_LOG_INFO);
        var result = ffmpeg.avcodec_open2(context, codec, &dictionary);
        var unused = Keys(dictionary);
        ffmpeg.av_dict_free(&dictionary);
        if (result < 0)
        {
            ffmpeg.avcodec_free_context(&context);
            throw new FfmpegException($"Opening {candidate.FfmpegName}", result, log.Summary());
        }

        if (unused.Count > 0)
        {
            ffmpeg.avcodec_free_context(&context);
            throw new FfmpegException($"{candidate.FfmpegName} doesn't know the option(s) {string.Join(", ", unused)}.");
        }

        var detail = log.Messages.Select(m => MftName().Match(m)).FirstOrDefault(m => m.Success)?.Groups[1].Value;
        try
        {
            return new VideoEncoder(candidate, format, stream, context, detail);
        }
        catch
        {
            ffmpeg.avcodec_free_context(&context);
            throw;
        }
    }

    /// <summary>
    /// The frame to fill before <see cref="Encode"/>: the encoder's size and pixel format, safe to write (if the
    /// encoder still holds the previous picture, this one gets fresh buffers).
    /// </summary>
    public AVFrame* NextFrame()
    {
        FfmpegException.Check(ffmpeg.av_frame_make_writable(_frame), "Preparing a video frame");
        return _frame;
    }

    /// <summary>Encodes the frame from <see cref="NextFrame"/> as frame number <paramref name="pts"/>; finished packets go to <paramref name="output"/>.</summary>
    public void Encode(long pts, List<EncodedPacket> output)
    {
        _frame->pts = pts;
        Send(_frame, output);
    }

    /// <summary>Ends the stream: everything the encoder still holds goes to <paramref name="output"/>.</summary>
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
            throw new FfmpegException($"Encoding with {Candidate.FfmpegName}", result);
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

            FfmpegException.Check(result, $"Encoding with {Candidate.FfmpegName}");
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

    internal static List<string> Keys(AVDictionary* dictionary)
    {
        var keys = new List<string>();
        AVDictionaryEntry* entry = null;
        while ((entry = ffmpeg.av_dict_get(dictionary, "", entry, ffmpeg.AV_DICT_IGNORE_SUFFIX)) is not null)
        {
            keys.Add(System.Runtime.InteropServices.Marshal.PtrToStringUTF8((nint)entry->key) ?? "");
        }

        return keys;
    }

    [GeneratedRegex(@"MFT name: '([^']+)'")]
    private static partial Regex MftName();
}
