using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media;

/// <summary>How a <see cref="VideoStreamReader"/> decodes.</summary>
public enum VideoDecoding
{
    /// <summary>On the graphics card (Direct3D 11 video acceleration) when it can, else in software.</summary>
    Hardware,

    Software,
}

/// <summary>
/// Reads one video stream of a file picture by picture, for the video editor: frame-exact seeking (back to the
/// keyframe before the time, then decoded forward) and each picture as top-down BGRA at any size. With
/// <see cref="VideoDecoding.Hardware"/> the pictures are decoded by the graphics card through one Direct3D 11 device
/// shared by every reader, then copied back to memory; FFmpeg falls back to software by itself when the card can't
/// decode the stream. Times are the stream's own, in seconds. One thread at a time.
/// </summary>
public sealed unsafe class VideoStreamReader : IDisposable
{
    /// <summary>A picture counts as shown at a time this close after its own (rounding of times in seconds).</summary>
    private const double Tolerance = 1e-6;

    private static readonly Lock DeviceGate = new();
    private static AVBufferRef* _sharedDevice;
    private static string? _deviceProblem;
    private static bool _deviceTried;

    private readonly Queue<nint> _ready = new();
    private readonly byte*[] _sourcePlanes = new byte*[4];
    private readonly int[] _sourceStrides = new int[4];
    private readonly byte*[] _planes = new byte*[4];
    private readonly int[] _strides = new int[4];
    private readonly int _stream;
    private readonly double _timeBase;
    private AVFormatContext* _input;
    private AVCodecContext* _context;
    private AVPacket* _packet;
    private AVFrame* _decoded;
    private AVFrame* _transfer;
    private SwsContext* _scaler;
    private (int Width, int Height, int Format, int OutWidth, int OutHeight, int Space, int Range) _scalerShape;
    private bool _flushed;

    /// <param name="streamIndex">The stream among all the file's streams; -1 for the file's main video.</param>
    /// <param name="keyframesOnly">Decode only keyframes (thumbnails): every other picture is skipped.</param>
    /// <exception cref="FfmpegException">The file can't be opened, has no such video, or can't be decoded.</exception>
    public VideoStreamReader(string path, int streamIndex = -1, VideoDecoding decoding = VideoDecoding.Hardware, bool keyframesOnly = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        AVFormatContext* input = null;
        using (var log = FfmpegLog.Capture())
        {
            var opened = ffmpeg.avformat_open_input(&input, path, null, null);
            if (opened < 0)
            {
                throw new FfmpegException($"Opening {Path.GetFileName(path)}", opened, log.Summary());
            }
        }

        _input = input;
        try
        {
            FfmpegException.Check(ffmpeg.avformat_find_stream_info(input, null), $"Reading {Path.GetFileName(path)}");
            _stream = streamIndex >= 0 ? streamIndex : ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            if (_stream < 0 || _stream >= input->nb_streams || input->streams[_stream]->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                throw new FfmpegException($"{Path.GetFileName(path)} has no video stream {streamIndex}.");
            }

            for (var i = 0; i < input->nb_streams; i++)
            {
                if (i != _stream)
                {
                    input->streams[i]->discard = AVDiscard.AVDISCARD_ALL; // read past the other streams' packets cheaply
                }
            }

            var stream = input->streams[_stream];
            var codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (codec is null)
            {
                throw new FfmpegException($"No decoder for {ffmpeg.avcodec_get_name(stream->codecpar->codec_id)}.");
            }

            _context = ffmpeg.avcodec_alloc_context3(codec);
            FfmpegException.Check(ffmpeg.avcodec_parameters_to_context(_context, stream->codecpar), "Setting up the video decoder");
            _context->pkt_timebase = stream->time_base;
            if (keyframesOnly)
            {
                _context->skip_frame = AVDiscard.AVDISCARD_NONKEY;
            }

            if (decoding == VideoDecoding.Hardware && SupportsD3D11(codec) && SharedDevice() is { } device)
            {
                _context->hw_device_ctx = ffmpeg.av_buffer_ref((AVBufferRef*)device);
                HardwareRequested = true;
            }
            else
            {
                _context->thread_count = 0; // as many as the processors
            }

            FfmpegException.Check(ffmpeg.avcodec_open2(_context, codec, null), "Opening the video decoder");
            _packet = ffmpeg.av_packet_alloc();
            _decoded = ffmpeg.av_frame_alloc();
            _transfer = ffmpeg.av_frame_alloc();
            _timeBase = ffmpeg.av_q2d(stream->time_base);
            Width = stream->codecpar->width;
            Height = stream->codecpar->height;
            var rate = stream->avg_frame_rate.num > 0 ? stream->avg_frame_rate : stream->r_frame_rate;
            FrameRate = rate.num > 0 && rate.den > 0 ? ffmpeg.av_q2d(rate) : 30;
            Start = stream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->start_time * _timeBase;
            Duration = stream->duration != ffmpeg.AV_NOPTS_VALUE ? stream->duration * _timeBase
                : input->duration != ffmpeg.AV_NOPTS_VALUE ? input->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            Codec = ffmpeg.avcodec_get_name(stream->codecpar->codec_id);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>Why no picture can be decoded on the graphics card here (no Direct3D 11 video device), or null.</summary>
    public static string? HardwareProblem
    {
        get
        {
            SharedDevice();
            return _deviceProblem;
        }
    }

    public int Width { get; }

    public int Height { get; }

    public double FrameRate { get; }

    /// <summary>The first picture's time.</summary>
    public double Start { get; }

    public double Duration { get; }

    public string Codec { get; }

    /// <summary>The graphics card was asked to decode (it may still have declined; see <see cref="IsHardware"/>).</summary>
    public bool HardwareRequested { get; }

    /// <summary>Pictures have come from the graphics card.</summary>
    public bool IsHardware { get; private set; }

    /// <summary>
    /// Goes to <paramref name="time"/>: the next <see cref="Read"/> gives the picture shown then (the last one whose
    /// time isn't after it; the first picture for a time before it), and the pictures after it in turn.
    /// </summary>
    public void Seek(double time)
    {
        ClearReady();
        var target = (long)Math.Floor((time + Tolerance) / _timeBase);
        if (ffmpeg.avformat_seek_file(_input, _stream, long.MinValue, target, target, 0) < 0)
        {
            FfmpegException.Check(ffmpeg.av_seek_frame(_input, _stream, long.MinValue, ffmpeg.AVSEEK_FLAG_BACKWARD), "Seeking in the video");
        }

        ffmpeg.avcodec_flush_buffers(_context);
        _flushed = false;
        AVFrame* held = null;
        while (DecodeNext())
        {
            if (TimeOf(_decoded) <= time + Tolerance)
            {
                Free(held);
                held = Take(_decoded);
                continue;
            }

            if (held is null)
            {
                held = Take(_decoded); // the time is before the first picture: that one
                break;
            }

            _ready.Enqueue((nint)held);
            _ready.Enqueue((nint)Take(_decoded));
            return;
        }

        if (held is not null)
        {
            _ready.Enqueue((nint)held);
        }
    }

    /// <summary>
    /// The next picture in display order, written to <paramref name="bgra"/> as <paramref name="width"/> ×
    /// <paramref name="height"/> top-down BGRA (scaled if that isn't the video's size); its time, or null at the end.
    /// </summary>
    public double? Read(Span<byte> bgra, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(bgra.Length, width * height * 4, nameof(bgra));
        if (_ready.TryDequeue(out var queued))
        {
            var frame = (AVFrame*)queued;
            try
            {
                var time = TimeOf(frame);
                Convert(frame, bgra, width, height);
                return time;
            }
            finally
            {
                Free(frame);
            }
        }

        if (!DecodeNext())
        {
            return null;
        }

        try
        {
            var time = TimeOf(_decoded);
            Convert(_decoded, bgra, width, height);
            return time;
        }
        finally
        {
            ffmpeg.av_frame_unref(_decoded);
        }
    }

    /// <summary>The next picture's time without converting it (null at the end).</summary>
    public double? Skip()
    {
        if (_ready.TryDequeue(out var queued))
        {
            var frame = (AVFrame*)queued;
            var time = TimeOf(frame);
            Free(frame);
            return time;
        }

        if (!DecodeNext())
        {
            return null;
        }

        var next = TimeOf(_decoded);
        ffmpeg.av_frame_unref(_decoded);
        return next;
    }

    public void Dispose()
    {
        ClearReady();
        var packet = _packet;
        var decoded = _decoded;
        var transfer = _transfer;
        var context = _context;
        var input = _input;
        _packet = null;
        _decoded = null;
        _transfer = null;
        _context = null;
        _input = null;
        ffmpeg.av_packet_free(&packet);
        ffmpeg.av_frame_free(&decoded);
        ffmpeg.av_frame_free(&transfer);
        ffmpeg.avcodec_free_context(&context);
        ffmpeg.avformat_close_input(&input);
        ffmpeg.sws_freeContext(_scaler);
        _scaler = null;
    }

    /// <summary>The shared Direct3D 11 video device, made the first time it is needed; null when there is none.</summary>
    private static nint? SharedDevice()
    {
        lock (DeviceGate)
        {
            if (!_deviceTried)
            {
                _deviceTried = true;
                AVBufferRef* device = null;
                using var log = FfmpegLog.Capture();
                var result = ffmpeg.av_hwdevice_ctx_create(&device, AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA, null, null, 0);
                if (result < 0)
                {
                    _deviceProblem = new FfmpegException("Creating a Direct3D 11 video device", result, log.Summary()).Message;
                }
                else
                {
                    _sharedDevice = device;
                }
            }

            return _sharedDevice is null ? null : (nint)_sharedDevice;
        }
    }

    private static bool SupportsD3D11(AVCodec* codec)
    {
        for (var i = 0; ; i++)
        {
            var config = ffmpeg.avcodec_get_hw_config(codec, i);
            if (config is null)
            {
                return false;
            }

            if ((config->methods & (int)AvCodecHwConfigMethod.AV_CODEC_HW_CONFIG_METHOD_HW_DEVICE_CTX) != 0 && config->device_type == AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA)
            {
                return true;
            }
        }
    }

    private static AVFrame* Take(AVFrame* frame)
    {
        var copy = ffmpeg.av_frame_alloc();
        ffmpeg.av_frame_move_ref(copy, frame);
        return copy;
    }

    private static void Free(AVFrame* frame)
    {
        if (frame is not null)
        {
            ffmpeg.av_frame_free(&frame);
        }
    }

    private double TimeOf(AVFrame* frame)
    {
        var pts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
        return pts == ffmpeg.AV_NOPTS_VALUE ? 0 : pts * _timeBase;
    }

    private void ClearReady()
    {
        while (_ready.TryDequeue(out var queued))
        {
            Free((AVFrame*)queued);
        }
    }

    /// <summary>The next decoded picture into <c>_decoded</c>; false at the end.</summary>
    private bool DecodeNext()
    {
        while (true)
        {
            var received = ffmpeg.avcodec_receive_frame(_context, _decoded);
            if (received == 0)
            {
                return true;
            }

            if (received == ffmpeg.AVERROR_EOF || _flushed)
            {
                return false;
            }

            if (received != -ffmpeg.EAGAIN)
            {
                FfmpegException.Check(received, "Decoding the video");
            }

            while (true)
            {
                if (ffmpeg.av_read_frame(_input, _packet) < 0)
                {
                    ffmpeg.avcodec_send_packet(_context, null);
                    _flushed = true;
                    break;
                }

                if (_packet->stream_index != _stream)
                {
                    ffmpeg.av_packet_unref(_packet);
                    continue;
                }

                var sent = ffmpeg.avcodec_send_packet(_context, _packet);
                ffmpeg.av_packet_unref(_packet);
                if (sent < 0 && sent != ffmpeg.AVERROR_INVALIDDATA)
                {
                    FfmpegException.Check(sent, "Decoding the video");
                }

                break;
            }
        }
    }

    private void Convert(AVFrame* frame, Span<byte> bgra, int width, int height)
    {
        var source = frame;
        if (frame->format == (int)AVPixelFormat.AV_PIX_FMT_D3D11)
        {
            if (_transfer->width != frame->width || _transfer->height != frame->height)
            {
                ffmpeg.av_frame_unref(_transfer);
            }

            FfmpegException.Check(ffmpeg.av_hwframe_transfer_data(_transfer, frame, 0), "Copying a picture from the graphics card");
            source = _transfer;
            IsHardware = true;
        }

        var space = (int)source->colorspace;
        var range = (int)source->color_range;
        var shape = (source->width, source->height, source->format, width, height, space, range);
        if (_scaler is null || shape != _scalerShape)
        {
            ffmpeg.sws_freeContext(_scaler);
            var scaling = source->width != width || source->height != height;
            _scaler = ffmpeg.sws_getContext(
                source->width, source->height, (AVPixelFormat)source->format,
                width, height, AVPixelFormat.AV_PIX_FMT_BGRA,
                (int)(scaling ? SwsFlags.SWS_BILINEAR : SwsFlags.SWS_POINT), null, null, null);
            if (_scaler is null)
            {
                throw new FfmpegException($"swscale can't convert {(AVPixelFormat)source->format} to BGRA.");
            }

            // The recordings are BT.709 limited range; other videos say what they are (unsaid: 709 for HD, else 601).
            var coefficients = (AVColorSpace)space switch
            {
                AVColorSpace.AVCOL_SPC_BT709 => ffmpeg.SWS_CS_ITU709,
                AVColorSpace.AVCOL_SPC_BT470BG or AVColorSpace.AVCOL_SPC_SMPTE170M => ffmpeg.SWS_CS_ITU601,
                _ => source->height >= 720 ? ffmpeg.SWS_CS_ITU709 : ffmpeg.SWS_CS_ITU601,
            };
            var yuv = *(int4*)ffmpeg.sws_getCoefficients(coefficients);
            var rgb = *(int4*)ffmpeg.sws_getCoefficients(ffmpeg.SWS_CS_DEFAULT);
            var fullRange = (AVColorRange)range == AVColorRange.AVCOL_RANGE_JPEG ? 1 : 0;
            ffmpeg.sws_setColorspaceDetails(_scaler, in yuv, fullRange, in rgb, 1, 0, 1 << 16, 1 << 16);
            _scalerShape = shape;
        }

        fixed (byte* destination = bgra)
        {
            for (uint i = 0; i < 4; i++)
            {
                _sourcePlanes[i] = source->data[i];
                _sourceStrides[i] = source->linesize[i];
                _planes[i] = null;
                _strides[i] = 0;
            }

            _planes[0] = destination;
            _strides[0] = width * 4;
            var rows = ffmpeg.sws_scale(_scaler, _sourcePlanes, _sourceStrides, 0, source->height, _planes, _strides);
            if (rows != height)
            {
                throw new FfmpegException("Converting a picture", rows < 0 ? rows : ffmpeg.AVERROR_UNKNOWN);
            }
        }
    }
}
