using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media;

/// <summary>
/// Reads one sound stream of a file as 48 kHz stereo float (interleaved L, R), whatever its own format: the video
/// editor's sound. Samples are placed by the stream's own times, so a stream that starts late, or a gap in it, reads
/// as silence first, and <see cref="Seek"/> is exact to the sample. One thread at a time.
/// </summary>
public sealed unsafe class AudioStreamReader : IDisposable
{
    public const int SampleRate = AudioEncoder.SampleRate;
    public const int Channels = AudioEncoder.Channels;

    /// <summary>How far before a seek's time decoding starts, so the decoder has settled by then.</summary>
    private const double PreRoll = 0.2;

    private readonly int _stream;
    private readonly double _timeBase;
    private AVFormatContext* _input;
    private AVCodecContext* _context;
    private AVPacket* _packet;
    private AVFrame* _frame;
    private SwrContext* _resampler;
    private (int Rate, int Format, ulong Mask, int Count) _resamplerShape;
    private float[] _pending = new float[8192];
    private int _pendingOffset;
    private int _pendingCount;
    private long _pendingStart;
    private bool _anchored;
    private long _position;
    private bool _flushed;
    private bool _drained;
    private float[] _converted = [];

    /// <param name="streamIndex">The stream among all the file's streams; -1 for the file's main sound.</param>
    /// <exception cref="FfmpegException">The file can't be opened, has no such sound, or can't be decoded.</exception>
    public AudioStreamReader(string path, int streamIndex = -1)
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
            _stream = streamIndex >= 0 ? streamIndex : ffmpeg.av_find_best_stream(input, AVMediaType.AVMEDIA_TYPE_AUDIO, -1, -1, null, 0);
            if (_stream < 0 || _stream >= input->nb_streams || input->streams[_stream]->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_AUDIO)
            {
                throw new FfmpegException($"{Path.GetFileName(path)} has no sound stream {streamIndex}.");
            }

            for (var i = 0; i < input->nb_streams; i++)
            {
                if (i != _stream)
                {
                    input->streams[i]->discard = AVDiscard.AVDISCARD_ALL;
                }
            }

            var stream = input->streams[_stream];
            var codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (codec is null)
            {
                throw new FfmpegException($"No decoder for the sound ({ffmpeg.avcodec_get_name(stream->codecpar->codec_id)}).");
            }

            _context = ffmpeg.avcodec_alloc_context3(codec);
            FfmpegException.Check(ffmpeg.avcodec_parameters_to_context(_context, stream->codecpar), "Setting up the sound decoder");
            _context->pkt_timebase = stream->time_base;
            FfmpegException.Check(ffmpeg.avcodec_open2(_context, codec, null), "Opening the sound decoder");
            _packet = ffmpeg.av_packet_alloc();
            _frame = ffmpeg.av_frame_alloc();
            _timeBase = ffmpeg.av_q2d(stream->time_base);
            Duration = stream->duration != ffmpeg.AV_NOPTS_VALUE ? stream->duration * _timeBase
                : input->duration != ffmpeg.AV_NOPTS_VALUE ? input->duration / (double)ffmpeg.AV_TIME_BASE : 0;
            Start = stream->start_time == ffmpeg.AV_NOPTS_VALUE ? 0 : stream->start_time * _timeBase;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>The stream's length in seconds (0 when the file doesn't say).</summary>
    public double Duration { get; }

    /// <summary>The first sample's time.</summary>
    public double Start { get; }

    /// <summary>Where the next <see cref="Read"/> starts, in seconds.</summary>
    public double Position => (double)_position / SampleRate;

    /// <summary>Goes to <paramref name="time"/> (seconds on the stream's clock): the next <see cref="Read"/> starts exactly there.</summary>
    public void Seek(double time)
    {
        _position = (long)Math.Round(time * SampleRate);
        _pendingOffset = 0;
        _pendingCount = 0;
        _anchored = false;
        var target = (long)Math.Floor((time - PreRoll) / _timeBase);
        if (time - PreRoll <= Start || ffmpeg.avformat_seek_file(_input, _stream, long.MinValue, target, target, 0) < 0)
        {
            FfmpegException.Check(ffmpeg.av_seek_frame(_input, _stream, long.MinValue, ffmpeg.AVSEEK_FLAG_BACKWARD), "Seeking in the sound");
        }

        ffmpeg.avcodec_flush_buffers(_context);
        FreeResampler();
        _flushed = false;
        _drained = false;
    }

    /// <summary>
    /// Fills <paramref name="interleaved"/> with the next samples (pairs of L, R); returns how many pairs were
    /// written, fewer than asked only at the end of the stream.
    /// </summary>
    public int Read(Span<float> interleaved)
    {
        var wanted = interleaved.Length / Channels;
        var done = 0;
        while (done < wanted)
        {
            if (_pendingCount == 0)
            {
                if (!Fill())
                {
                    break;
                }

                continue;
            }

            if (_position < _pendingStart)
            {
                var gap = (int)Math.Min(_pendingStart - _position, wanted - done);
                interleaved.Slice(done * Channels, gap * Channels).Clear();
                done += gap;
                _position += gap;
                continue;
            }

            if (_position > _pendingStart)
            {
                var drop = (int)Math.Min(_position - _pendingStart, _pendingCount);
                Consume(drop);
                continue;
            }

            var take = Math.Min(_pendingCount, wanted - done);
            _pending.AsSpan(_pendingOffset * Channels, take * Channels).CopyTo(interleaved.Slice(done * Channels));
            Consume(take);
            done += take;
            _position += take;
        }

        return done;
    }

    public void Dispose()
    {
        FreeResampler();
        var packet = _packet;
        var frame = _frame;
        var context = _context;
        var input = _input;
        _packet = null;
        _frame = null;
        _context = null;
        _input = null;
        ffmpeg.av_packet_free(&packet);
        ffmpeg.av_frame_free(&frame);
        ffmpeg.avcodec_free_context(&context);
        ffmpeg.avformat_close_input(&input);
    }

    private void Consume(int count)
    {
        _pendingOffset += count;
        _pendingCount -= count;
        _pendingStart += count;
        if (_pendingCount == 0)
        {
            _pendingOffset = 0;
        }
    }

    /// <summary>Decodes and converts the next frame of sound into the pending samples; false at the end.</summary>
    private bool Fill()
    {
        while (true)
        {
            if (_drained)
            {
                return false;
            }

            var received = ffmpeg.avcodec_receive_frame(_context, _frame);
            if (received == 0)
            {
                try
                {
                    Convert(_frame);
                }
                finally
                {
                    ffmpeg.av_frame_unref(_frame);
                }

                if (_pendingCount > 0)
                {
                    return true;
                }

                continue;
            }

            if (received == ffmpeg.AVERROR_EOF || _flushed)
            {
                // What the resampler still holds.
                _drained = true;
                if (_resampler is not null)
                {
                    Convert(null);
                }

                return _pendingCount > 0;
            }

            if (received != -ffmpeg.EAGAIN)
            {
                FfmpegException.Check(received, "Decoding the sound");
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
                    FfmpegException.Check(sent, "Decoding the sound");
                }

                break;
            }
        }
    }

    /// <summary>Resamples <paramref name="frame"/> (null: what the resampler still holds) onto the end of the pending samples.</summary>
    private void Convert(AVFrame* frame)
    {
        if (frame is not null)
        {
            var shape = (frame->sample_rate, frame->format, frame->ch_layout.u.mask, frame->ch_layout.nb_channels);
            if (_resampler is null || shape != _resamplerShape)
            {
                FreeResampler();
                SwrContext* resampler = null;
                AVChannelLayout stereo;
                ffmpeg.av_channel_layout_default(&stereo, Channels);
                FfmpegException.Check(
                    ffmpeg.swr_alloc_set_opts2(&resampler, &stereo, AVSampleFormat.AV_SAMPLE_FMT_FLT, SampleRate, &frame->ch_layout, (AVSampleFormat)frame->format, frame->sample_rate, 0, null),
                    "Setting up the sound resampler");
                FfmpegException.Check(ffmpeg.swr_init(resampler), "Starting the sound resampler");
                _resampler = resampler;
                _resamplerShape = shape;
            }

            if (!_anchored)
            {
                var pts = frame->best_effort_timestamp != ffmpeg.AV_NOPTS_VALUE ? frame->best_effort_timestamp : frame->pts;
                _pendingStart = pts == ffmpeg.AV_NOPTS_VALUE ? _position : (long)Math.Round(pts * _timeBase * SampleRate);
                _anchored = true;
            }
        }

        var room = ffmpeg.swr_get_out_samples(_resampler, frame is null ? 0 : frame->nb_samples);
        if (room <= 0)
        {
            return;
        }

        if (_converted.Length < room * Channels)
        {
            _converted = new float[room * Channels];
        }

        int produced;
        fixed (float* output = _converted)
        {
            var planes = (byte*)output;
            produced = ffmpeg.swr_convert(_resampler, &planes, room, frame is null ? null : (byte**)frame->extended_data, frame is null ? 0 : frame->nb_samples);
        }

        FfmpegException.Check(produced, "Resampling the sound");
        Append(_converted.AsSpan(0, produced * Channels));
    }

    private void Append(ReadOnlySpan<float> samples)
    {
        var end = (_pendingOffset + _pendingCount) * Channels;
        if (end + samples.Length > _pending.Length)
        {
            // Move what is left to the front, and grow if that isn't enough.
            var left = _pending.AsSpan(_pendingOffset * Channels, _pendingCount * Channels);
            var target = _pending.Length >= left.Length + samples.Length ? _pending : new float[Math.Max(_pending.Length * 2, left.Length + samples.Length)];
            left.CopyTo(target);
            _pending = target;
            _pendingOffset = 0;
            end = _pendingCount * Channels;
        }

        samples.CopyTo(_pending.AsSpan(end));
        _pendingCount += samples.Length / Channels;
    }

    private void FreeResampler()
    {
        var resampler = _resampler;
        _resampler = null;
        if (resampler is not null)
        {
            ffmpeg.swr_free(&resampler);
        }
    }
}
