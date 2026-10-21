using FFmpeg.AutoGen.Abstractions;

namespace CashGear.Capture.Media;

/// <summary>The kinds of file an export writes.</summary>
public enum ExportContainer
{
    /// <summary>MP4 with its index at the front (<c>+faststart</c>), so it plays while downloading.</summary>
    Mp4,

    WebM,

    /// <summary>Sound only, AAC in an MPEG-4 file.</summary>
    M4a,

    /// <summary>Sound only, MP3.</summary>
    Mp3,

    /// <summary>An animated GIF (no sound), written by <see cref="GifWriter"/> rather than this writer.</summary>
    Gif,
}

/// <summary>
/// Writes an export's encoded streams to one file, on the thread that encodes (an export encodes and writes on one
/// thread). Each encoder is added before the first packet; packets are given in any order and interleaved by FFmpeg.
/// The file is started once every video encoder has made its first packet (some encoders only fill in their codec
/// headers then); packets that come before wait.
/// </summary>
public sealed unsafe class ExportWriter : IDisposable
{
    private readonly string _path;
    private readonly ExportContainer _container;
    private readonly Dictionary<int, Entry> _entries = [];
    private readonly List<EncodedPacket> _waiting = [];
    private AVFormatContext* _output;
    private bool _started;

    public ExportWriter(string path, ExportContainer container)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        _path = path;
        _container = container;
    }

    /// <summary>The end of the last packet written, in seconds.</summary>
    public double End { get; private set; }

    /// <param name="title">The stream's name (a second video, such as a webcam's).</param>
    public void Add(VideoEncoder encoder, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ThrowIfStarted();
        _entries.Add(encoder.Stream, new Entry(encoder, null, title));
    }

    public void Add(AudioEncoder encoder, string? title = null)
    {
        ArgumentNullException.ThrowIfNull(encoder);
        ThrowIfStarted();
        _entries.Add(encoder.Stream, new Entry(null, encoder, title));
    }

    /// <summary>Writes (or keeps until the file starts) one packet; the writer owns it from now on.</summary>
    public void Write(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (!_entries.TryGetValue(packet.Stream, out var entry))
        {
            packet.Dispose();
            throw new ArgumentException($"No encoder was added for stream {packet.Stream}.", nameof(packet));
        }

        if (!_started)
        {
            entry.HasPacket = true;
            _waiting.Add(packet);
            if (_entries.Values.All(e => e.Video is null || e.HasPacket))
            {
                Start();
            }

            return;
        }

        using (packet)
        {
            WritePacket(packet);
        }
    }

    /// <summary>Writes what still waits and the file's end; returns the file's size in bytes.</summary>
    public long Finish()
    {
        if (!_started)
        {
            Start();
        }

        FfmpegException.Check(ffmpeg.av_interleaved_write_frame(_output, null), "Writing the export");
        FfmpegException.Check(ffmpeg.av_write_trailer(_output), "Finishing the export");
        ffmpeg.avio_closep(&_output->pb);
        return new FileInfo(_path).Length;
    }

    public void Dispose()
    {
        _waiting.ForEach(p => p.Dispose());
        _waiting.Clear();
        if (_output is not null)
        {
            ffmpeg.avio_closep(&_output->pb);
            ffmpeg.avformat_free_context(_output);
            _output = null;
        }
    }

    private void ThrowIfStarted()
    {
        if (_started)
        {
            throw new InvalidOperationException("Encoders must be added before the first packet.");
        }
    }

    private void Start()
    {
        _started = true;
        if (_container == ExportContainer.Gif)
        {
            throw new InvalidOperationException("GIFs are written by GifWriter.");
        }

        var format = _container switch
        {
            ExportContainer.WebM => "webm",
            ExportContainer.M4a => "ipod",
            ExportContainer.Mp3 => "mp3",
            _ => "mp4",
        };
        AVFormatContext* output = null;
        FfmpegException.Check(ffmpeg.avformat_alloc_output_context2(&output, null, format, _path), "Creating the export");
        _output = output;
        foreach (var (_, entry) in _entries.OrderBy(e => e.Key))
        {
            using var setup = entry.Video is { } video ? StreamSetup.From(video) : StreamSetup.From(entry.Audio!, entry.Title ?? "", false);
            var stream = ffmpeg.avformat_new_stream(output, null);
            FfmpegException.Check(ffmpeg.avcodec_parameters_copy(stream->codecpar, setup.Parameters), "Copying codec settings");
            stream->codecpar->codec_tag = 0;
            if (_container == ExportContainer.Mp4 && stream->codecpar->codec_id == AVCodecID.AV_CODEC_ID_HEVC)
            {
                stream->codecpar->codec_tag = 'h' | ('v' << 8) | ('c' << 16) | ('1' << 24); // hvc1: what Apple's players want
            }

            stream->time_base = setup.TimeBase;

            // The first video and the first sound are what players show and play.
            var first = entry.Video is not null ? !_entries.Values.Any(e => e.Video is not null && e.Placed) : !_entries.Values.Any(e => e.Audio is not null && e.Placed);
            stream->disposition = first ? ffmpeg.AV_DISPOSITION_DEFAULT : 0;
            entry.Placed = true;
            if (!string.IsNullOrEmpty(entry.Title))
            {
                ffmpeg.av_dict_set(&stream->metadata, "title", entry.Title, 0);
                ffmpeg.av_dict_set(&stream->metadata, "handler_name", entry.Title, 0);
            }

            entry.Output = stream->index;
            entry.TimeBase = stream->time_base;
        }

        FfmpegException.Check(ffmpeg.avio_open(&output->pb, _path, ffmpeg.AVIO_FLAG_WRITE), $"Creating {_path}");
        AVDictionary* options = null;
        if (_container is ExportContainer.Mp4 or ExportContainer.M4a)
        {
            ffmpeg.av_dict_set(&options, "movflags", "+faststart", 0);
        }

        using var log = FfmpegLog.Capture();
        var header = ffmpeg.avformat_write_header(output, &options);
        ffmpeg.av_dict_free(&options);
        if (header < 0)
        {
            throw new FfmpegException("Starting the export", header, log.Summary());
        }

        foreach (var entry in _entries.Values)
        {
            entry.TimeBase = output->streams[entry.Output]->time_base; // the muxer may have chosen its own
        }

        foreach (var packet in _waiting)
        {
            using (packet)
            {
                WritePacket(packet);
            }
        }

        _waiting.Clear();
    }

    private void WritePacket(EncodedPacket packet)
    {
        var entry = _entries[packet.Stream];
        var raw = packet.Packet;
        if (entry.Video is not null && raw->duration <= 0)
        {
            raw->duration = 1; // one frame: without it the last picture would fall outside an MP4's edit list
        }

        raw->stream_index = entry.Output;
        ffmpeg.av_packet_rescale_ts(raw, packet.TimeBase, entry.TimeBase);
        if (raw->pts != ffmpeg.AV_NOPTS_VALUE)
        {
            End = Math.Max(End, (raw->pts + raw->duration) * ffmpeg.av_q2d(entry.TimeBase));
        }

        FfmpegException.Check(ffmpeg.av_interleaved_write_frame(_output, raw), "Writing the export");
    }

    private sealed class Entry(VideoEncoder? video, AudioEncoder? audio, string? title)
    {
        public VideoEncoder? Video { get; } = video;

        public AudioEncoder? Audio { get; } = audio;

        public string? Title { get; } = title;

        public bool HasPacket { get; set; }

        public int Output { get; set; }

        public bool Placed { get; set; }

        public AVRational TimeBase { get; set; }
    }
}
