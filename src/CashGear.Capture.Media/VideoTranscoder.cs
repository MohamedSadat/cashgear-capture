using CashGear.Capture.Core.Recording;
using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media;

/// <summary>A decoded picture of another video stream (the webcam's): top-down BGRA; a new <see cref="Version"/> means new pixels.</summary>
public sealed record DecodedPicture(int Width, int Height, byte[] Pixels, long Version);

/// <summary>
/// Changes a picture of the video being transcoded in place: top-down BGRA rows of <c>width * 4</c> bytes, shown
/// at <paramref name="time"/> seconds. <paramref name="other"/> is the newest picture of the other video stream (the
/// webcam) shown by then, if the file has one.
/// </summary>
public delegate void TranscodeFrame(double time, Span<byte> bgra, int width, int height, DecodedPicture? other);

/// <summary>What transcoding wrote.</summary>
public sealed record TranscodeResult(string Path, TimeSpan Duration, long Frames, long Bytes);

/// <summary>
/// Makes a new MP4 from a recording with its main video stream decoded, changed picture by picture
/// (<see cref="TranscodeFrame"/>) and encoded again; every other stream (the sound tracks, the webcam's track) is
/// copied as it is. The other video stream is decoded too, so each picture can be given the webcam picture shown at
/// its time. Pictures keep their times (the recordings are constant frame rate). Like <see cref="Remuxer"/>, the
/// file is written as <c>…​.partial</c>, renamed at the end, and has its index at the front (<c>+faststart</c>).
/// </summary>
public static unsafe class VideoTranscoder
{
    /// <param name="openEncoder">Opens an encoder for the video's size and frame rate.</param>
    public static TranscodeResult Transcode(
        string source,
        string destination,
        Func<VideoFormat, VideoEncoder> openEncoder,
        TranscodeFrame process,
        IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(source);
        ArgumentException.ThrowIfNullOrEmpty(destination);
        ArgumentNullException.ThrowIfNull(openEncoder);
        ArgumentNullException.ThrowIfNull(process);
        using var job = new Job(destination + ".partial", process);
        return job.Run(source, destination, openEncoder, progress, ct);
    }

    /// <summary>One transcoding: the files, decoders, encoder, and the packets that wait for the output to start.</summary>
    private sealed class Job(string partial, TranscodeFrame process) : IDisposable
    {
        private readonly List<(int Stream, nint Packet)> _waiting = [];
        private readonly List<EncodedPacket> _encoded = [];
        private readonly List<(double Time, DecodedPicture Picture)> _pictures = [];
        private AVFormatContext* _input;
        private AVFormatContext* _output;
        private AVPacket* _packet = ffmpeg.av_packet_alloc();
        private AVFrame* _frame = ffmpeg.av_frame_alloc();
        private Decoder? _main;
        private Decoder? _other;
        private VideoEncoder? _encoder;
        private FrameConverter? _converter;
        private int[]? _map;
        private byte[] _bgra = [];
        private int _mainIndex;
        private int _otherIndex = -1;
        private int _width;
        private int _height;
        private int _fps;
        private long _version;
        private long _frames;
        private double _end;

        public TranscodeResult Run(string source, string destination, Func<VideoFormat, VideoEncoder> openEncoder, IProgress<double>? progress, CancellationToken ct)
        {
            Open(source);
            _encoder = openEncoder(new VideoFormat(_width, _height, _fps));
            _converter = new FrameConverter(_width, _height, _encoder.PixelFormat);
            var size = Math.Max(1, ffmpeg.avio_size(_input->pb));
            var count = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (ffmpeg.av_read_frame(_input, _packet) < 0)
                {
                    break;
                }

                var index = _packet->stream_index;
                if (index == _mainIndex)
                {
                    _main!.Send(_packet, _frame, Picture);
                }
                else if (index == _otherIndex)
                {
                    _other!.Send(_packet, _frame, OtherPicture);
                    Write(index, _packet, _input->streams[index]->time_base);
                }
                else if (_input->streams[index]->codecpar->codec_type is AVMediaType.AVMEDIA_TYPE_AUDIO or AVMediaType.AVMEDIA_TYPE_VIDEO)
                {
                    Write(index, _packet, _input->streams[index]->time_base);
                }

                ffmpeg.av_packet_unref(_packet);
                if (++count % 64 == 0)
                {
                    progress?.Report(Math.Clamp((double)ffmpeg.avio_tell(_input->pb) / size, 0, 1));
                }
            }

            _other?.Send(null, _frame, OtherPicture);
            _main!.Send(null, _frame, Picture);
            _encoder.Flush(_encoded);
            WriteEncoded();
            if (_map is null)
            {
                throw new FfmpegException("The encoder produced no video.");
            }

            FfmpegException.Check(ffmpeg.av_write_trailer(_output), "Finishing the MP4");
            ffmpeg.avio_closep(&_output->pb);
            var bytes = new FileInfo(partial).Length;
            File.Move(partial, destination, overwrite: false);
            progress?.Report(1);
            return new TranscodeResult(destination, TimeSpan.FromSeconds(_end), _frames, bytes);
        }

        public void Dispose()
        {
            _encoded.ForEach(p => p.Dispose());
            foreach (var (_, p) in _waiting)
            {
                var held = (AVPacket*)p;
                ffmpeg.av_packet_free(&held);
            }

            _converter?.Dispose();
            _encoder?.Dispose();
            _main?.Dispose();
            _other?.Dispose();
            var frame = _frame;
            var packet = _packet;
            ffmpeg.av_frame_free(&frame);
            ffmpeg.av_packet_free(&packet);
            _frame = null;
            _packet = null;
            if (_output is not null)
            {
                ffmpeg.avio_closep(&_output->pb);
                ffmpeg.avformat_free_context(_output);
                _output = null;
            }

            var input = _input;
            ffmpeg.avformat_close_input(&input);
            _input = null;
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }

        private void Open(string source)
        {
            AVFormatContext* input = null;
            using (var log = FfmpegLog.Capture())
            {
                var opened = ffmpeg.avformat_open_input(&input, source, null, null);
                if (opened < 0)
                {
                    throw new FfmpegException($"Opening {source}", opened, log.Summary());
                }
            }

            _input = input;
            FfmpegException.Check(ffmpeg.avformat_find_stream_info(input, null), "Reading the recording");
            _mainIndex = MainVideo(input);
            if (_mainIndex < 0)
            {
                throw new FfmpegException($"{Path.GetFileName(source)} has no video.");
            }

            _otherIndex = OtherVideo(input, _mainIndex);
            _main = new Decoder(input->streams[_mainIndex]);
            _other = _otherIndex >= 0 ? new Decoder(input->streams[_otherIndex]) : null;
            var stream = input->streams[_mainIndex];
            var rate = stream->avg_frame_rate.num > 0 ? stream->avg_frame_rate : stream->r_frame_rate;
            _fps = Math.Max(1, (int)Math.Round(ffmpeg.av_q2d(rate)));
            _width = stream->codecpar->width;
            _height = stream->codecpar->height;
            _bgra = new byte[_width * _height * 4];
        }

        /// <summary>A picture of the main video: changed, encoded again, written.</summary>
        private void Picture(AVFrame* decoded)
        {
            var time = decoded->best_effort_timestamp * ffmpeg.av_q2d(_input->streams[_mainIndex]->time_base);
            _main!.Converter.Convert(decoded).CopyTo(_bgra);

            // The other video's newest picture shown by then (or its first, before it starts).
            DecodedPicture? shown = null;
            foreach (var (at, picture) in _pictures)
            {
                if (at <= time + 1e-6 || shown is null)
                {
                    shown = picture;
                }
            }

            process(time, _bgra, _width, _height, shown);
            _pictures.RemoveAll(p => p.Time < time && !ReferenceEquals(p.Picture, shown));
            _converter!.Convert(_bgra, _width * 4, _encoder!.NextFrame());
            _encoder.Encode((long)Math.Round(time * _fps), _encoded);
            _frames++;
            WriteEncoded();
        }

        private void OtherPicture(AVFrame* decoded)
        {
            var time = decoded->best_effort_timestamp * ffmpeg.av_q2d(_input->streams[_otherIndex]->time_base);
            var pixels = _other!.Converter.Convert(decoded).ToArray();
            _pictures.Add((time, new DecodedPicture(decoded->width, decoded->height, pixels, ++_version)));
        }

        /// <summary>Writes the encoder's packets, starting the output with the first (its codec headers come with it).</summary>
        private void WriteEncoded()
        {
            foreach (var encoded in _encoded)
            {
                if (_map is null)
                {
                    _output = Start(_input, _mainIndex, _encoder!, partial, out var map);
                    _map = map;
                    foreach (var (stream, held) in _waiting)
                    {
                        var p = (AVPacket*)held;
                        Write(stream, p, _input->streams[stream]->time_base);
                        ffmpeg.av_packet_free(&p);
                    }

                    _waiting.Clear();
                }

                // The encoder's time base is one frame; without a duration the last picture would fall outside the MP4's edit list.
                if (encoded.Packet->duration <= 0)
                {
                    encoded.Packet->duration = 1;
                }

                Write(_mainIndex, encoded.Packet, encoded.TimeBase);
            }

            _encoded.ForEach(p => p.Dispose());
            _encoded.Clear();
        }

        /// <summary>Writes a packet of an input stream to its output stream; before the output starts, it waits.</summary>
        private void Write(int inputStream, AVPacket* packet, AVRational timeBase)
        {
            if (_map is null)
            {
                _waiting.Add((inputStream, (nint)ffmpeg.av_packet_clone(packet)));
                return;
            }

            var to = _output->streams[_map[inputStream]];
            packet->stream_index = to->index;
            packet->pos = -1;
            ffmpeg.av_packet_rescale_ts(packet, timeBase, to->time_base);
            if (packet->pts != ffmpeg.AV_NOPTS_VALUE)
            {
                _end = Math.Max(_end, (packet->pts + packet->duration) * ffmpeg.av_q2d(to->time_base));
            }

            FfmpegException.Check(ffmpeg.av_interleaved_write_frame(_output, packet), "Writing the MP4");
        }
    }

    /// <summary>The video shown by default (the screen), or the first video stream.</summary>
    private static int MainVideo(AVFormatContext* input)
    {
        var first = -1;
        for (var i = 0; i < input->nb_streams; i++)
        {
            var stream = input->streams[i];
            if (stream->codecpar->codec_type != AVMediaType.AVMEDIA_TYPE_VIDEO || (stream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) != 0)
            {
                continue;
            }

            if ((stream->disposition & ffmpeg.AV_DISPOSITION_DEFAULT) != 0)
            {
                return (int)i;
            }

            first = first < 0 ? (int)i : first;
        }

        return first;
    }

    private static int OtherVideo(AVFormatContext* input, int main)
    {
        for (var i = 0; i < input->nb_streams; i++)
        {
            var stream = input->streams[i];
            if (i != main && stream->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO && (stream->disposition & ffmpeg.AV_DISPOSITION_ATTACHED_PIC) == 0)
            {
                return (int)i;
            }
        }

        return -1;
    }

    /// <summary>Creates the output with a stream for each input stream (the main video from the encoder) and writes its header.</summary>
    private static AVFormatContext* Start(AVFormatContext* input, int mainIndex, VideoEncoder encoder, string path, out int[] map)
    {
        AVFormatContext* output = null;
        FfmpegException.Check(ffmpeg.avformat_alloc_output_context2(&output, null, "mp4", path), "Creating the MP4");
        map = new int[input->nb_streams];
        using var setup = StreamSetup.From(encoder);
        for (var i = 0; i < input->nb_streams; i++)
        {
            var from = input->streams[i];
            if (from->codecpar->codec_type is not (AVMediaType.AVMEDIA_TYPE_VIDEO or AVMediaType.AVMEDIA_TYPE_AUDIO))
            {
                map[i] = -1;
                continue;
            }

            var to = ffmpeg.avformat_new_stream(output, null);
            if (i == mainIndex)
            {
                FfmpegException.Check(ffmpeg.avcodec_parameters_copy(to->codecpar, setup.Parameters), "Copying codec settings");
                to->time_base = setup.TimeBase;
            }
            else
            {
                FfmpegException.Check(ffmpeg.avcodec_parameters_copy(to->codecpar, from->codecpar), "Copying codec settings");
                to->time_base = from->time_base;
            }

            to->codecpar->codec_tag = 0;
            to->disposition = from->disposition;
            ffmpeg.av_dict_copy(&to->metadata, from->metadata, 0);
            map[i] = to->index;
        }

        FfmpegException.Check(ffmpeg.avio_open(&output->pb, path, ffmpeg.AVIO_FLAG_WRITE), $"Creating {path}");
        AVDictionary* options = null;
        ffmpeg.av_dict_set(&options, "movflags", "+faststart", 0);
        var header = ffmpeg.avformat_write_header(output, &options);
        ffmpeg.av_dict_free(&options);
        FfmpegException.Check(header, "Starting the MP4");
        return output;
    }

    /// <summary>A stream's decoder, handing each decoded picture over.</summary>
    private sealed class Decoder : IDisposable
    {
        private AVCodecContext* _context;

        public Decoder(AVStream* stream)
        {
            var codec = ffmpeg.avcodec_find_decoder(stream->codecpar->codec_id);
            if (codec is null)
            {
                throw new FfmpegException($"No decoder for {ffmpeg.avcodec_get_name(stream->codecpar->codec_id)}.");
            }

            _context = ffmpeg.avcodec_alloc_context3(codec);
            FfmpegException.Check(ffmpeg.avcodec_parameters_to_context(_context, stream->codecpar), "Setting up the decoder");
            _context->pkt_timebase = stream->time_base;
            FfmpegException.Check(ffmpeg.avcodec_open2(_context, codec, null), "Opening the decoder");
        }

        public BgraConverter Converter { get; } = new();

        public delegate void PictureHandler(AVFrame* frame);

        /// <summary>Decodes a packet (null: the end) and hands over every picture that comes out.</summary>
        public void Send(AVPacket* packet, AVFrame* frame, PictureHandler onPicture)
        {
            var sent = ffmpeg.avcodec_send_packet(_context, packet);
            if (sent < 0 && sent != ffmpeg.AVERROR_EOF && packet is not null && (packet->flags & ffmpeg.AV_PKT_FLAG_CORRUPT) != 0)
            {
                return; // a damaged packet is skipped
            }

            while (ffmpeg.avcodec_receive_frame(_context, frame) == 0)
            {
                onPicture(frame);
                ffmpeg.av_frame_unref(frame);
            }
        }

        public void Dispose()
        {
            Converter.Dispose();
            var context = _context;
            _context = null;
            ffmpeg.avcodec_free_context(&context);
        }
    }
}
