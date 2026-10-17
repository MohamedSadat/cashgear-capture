using System.Threading.Channels;
using FFmpeg.AutoGen.Abstractions;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Media;

/// <summary>
/// A stream's codec settings, copied from its encoder on the encoder's own thread (after its first packet, when
/// every encoder has filled in its codec headers), so the writer thread never touches a live encoder.
/// </summary>
public sealed unsafe class StreamSetup : IDisposable
{
    private AVCodecParameters* _parameters;

    private StreamSetup(AVCodecContext* context, string? title, bool isDefault)
    {
        _parameters = ffmpeg.avcodec_parameters_alloc();
        FfmpegException.Check(ffmpeg.avcodec_parameters_from_context(_parameters, context), "Copying codec settings");
        TimeBase = context->time_base;
        Title = title;
        IsDefault = isDefault;
    }

    public string? Title { get; }

    public bool IsDefault { get; }

    internal AVRational TimeBase { get; }

    internal AVCodecParameters* Parameters => _parameters;

    internal bool HasHeaders => _parameters->extradata_size > 0;

    public static StreamSetup From(VideoEncoder encoder) => new(encoder.Context, null, true);

    public static StreamSetup From(AudioEncoder encoder, string title, bool isDefault) => new(encoder.Context, title, isDefault);

    public void Dispose()
    {
        var parameters = _parameters;
        _parameters = null;
        ffmpeg.avcodec_parameters_free(&parameters);
    }
}

/// <summary>A temporary file of a recording and the recording time it starts at.</summary>
public sealed record SegmentFile(string Path, double Start);

/// <summary>
/// Writes a recording's packets to its temporary files on one thread of its own: fragmented MP4, with a fragment
/// at least every second and every packet flushed to the file, so a crash loses about a second. Packets come from
/// any thread (<see cref="Write"/> never blocks). Each video encoder gets its own file (<see cref="BeginSegment"/>);
/// the sound tracks continue across files. Sound that arrives before the first video packet waits for it.
/// </summary>
/// <remarks>
/// Timestamps stay in recording time (they aren't rebased per file); the remuxer starts each finished part at 0.
/// If writing fails (a full disk, a removed drive) the writer stops, raises <see cref="Failed"/> once and drops
/// what still arrives; the files written so far stay recoverable.
/// </remarks>
public sealed unsafe partial class MediaWriter : IDisposable
{
    private const int QueueWarning = 600;

    private readonly Func<int, string> _segmentPath;
    private readonly IReadOnlyList<StreamSetup> _audio;
    private readonly ILogger _logger;
    private readonly Channel<object> _queue = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly List<SegmentFile> _segments = [];
    private readonly List<EncodedPacket> _waiting = [];
    private readonly Thread _thread;
    private AVFormatContext* _file;
    private int[] _streamOf = [];
    private int _queued;
    private int _warned;
    private Exception? _failure;

    /// <param name="segmentPath">The file for segment n (1, 2, …).</param>
    /// <param name="audio">The sound tracks (streams 1, 2, … in packets), owned by the writer from now on.</param>
    public MediaWriter(Func<int, string> segmentPath, IReadOnlyList<StreamSetup> audio, ILogger logger)
    {
        _segmentPath = segmentPath;
        _audio = audio;
        _logger = logger;
        _thread = new Thread(Run) { IsBackground = true, Name = "Recording file writer" };
        _thread.Start();
    }

    /// <summary>Raised once, on the writer thread, when writing fails.</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>The files written so far (complete after <see cref="Complete"/>).</summary>
    public IReadOnlyList<SegmentFile> Segments
    {
        get
        {
            lock (_segments)
            {
                return [.. _segments];
            }
        }
    }

    /// <summary>Packets waiting to be written (a growing number means the disk can't keep up).</summary>
    public int Queued => Volatile.Read(ref _queued);

    public Exception? Failure => Volatile.Read(ref _failure);

    /// <summary>Starts a new file for the video from now on; <paramref name="video"/> is owned by the writer.</summary>
    public void BeginSegment(StreamSetup video, double start) => Post(new Begin(video, start));

    /// <summary>Queues a packet; the writer owns (and disposes) it from now on.</summary>
    public void Write(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        Post(packet);
        if (Interlocked.Increment(ref _queued) > QueueWarning && Interlocked.Exchange(ref _warned, 1) == 0)
        {
            LogBacklog(QueueWarning);
        }
    }

    /// <summary>Writes everything queued, finishes the current file and stops the thread.</summary>
    public bool Complete(TimeSpan timeout)
    {
        _queue.Writer.TryComplete();
        return _thread.Join(timeout);
    }

    public void Dispose()
    {
        _queue.Writer.TryComplete();
        if (_thread.Join(TimeSpan.FromSeconds(30)))
        {
            foreach (var setup in _audio)
            {
                setup.Dispose();
            }
        }
    }

    private void Post(object item)
    {
        if (!_queue.Writer.TryWrite(item))
        {
            Discard(item);
        }
    }

    private void Run()
    {
        var reader = _queue.Reader;
        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var item))
            {
                if (item is EncodedPacket)
                {
                    Interlocked.Decrement(ref _queued);
                }

                if (Failure is not null)
                {
                    Discard(item);
                    continue;
                }

                try
                {
                    Handle(item);
                }
                catch (Exception e) when (e is FfmpegException or IOException or UnauthorizedAccessException)
                {
                    Fail(e);
                }
            }
        }

        try
        {
            if (Failure is null)
            {
                CloseFile();
            }
        }
        catch (FfmpegException e)
        {
            Fail(e);
        }
        finally
        {
            _waiting.ForEach(p => p.Dispose());
            _waiting.Clear();
            FreeFile();
        }
    }

    private void Handle(object item)
    {
        switch (item)
        {
            case Begin begin:
                using (begin.Video)
                {
                    CloseFile();
                    OpenFile(begin.Video, begin.Start);
                }

                foreach (var waiting in _waiting)
                {
                    using (waiting)
                    {
                        WritePacket(waiting);
                    }
                }

                _waiting.Clear();
                break;
            case EncodedPacket packet when _file is null:
                _waiting.Add(packet);
                break;
            case EncodedPacket packet:
                using (packet)
                {
                    WritePacket(packet);
                }

                break;
        }
    }

    private void OpenFile(StreamSetup video, double start)
    {
        var index = Segments.Count + 1;
        var path = _segmentPath(index);
        AVFormatContext* file = null;
        FfmpegException.Check(ffmpeg.avformat_alloc_output_context2(&file, null, "mp4", path), "Creating the recording file");
        _file = file;
        var streams = new List<StreamSetup> { video };
        streams.AddRange(_audio);
        _streamOf = new int[streams.Count];
        for (var i = 0; i < streams.Count; i++)
        {
            var setup = streams[i];
            var stream = ffmpeg.avformat_new_stream(file, null);
            FfmpegException.Check(ffmpeg.avcodec_parameters_copy(stream->codecpar, setup.Parameters), "Copying codec settings");
            stream->codecpar->codec_tag = 0;
            stream->time_base = setup.TimeBase;
            stream->disposition = setup.IsDefault ? ffmpeg.AV_DISPOSITION_DEFAULT : 0;
            if (setup.Title is { } title)
            {
                ffmpeg.av_dict_set(&stream->metadata, "title", title, 0);
                ffmpeg.av_dict_set(&stream->metadata, "handler_name", title, 0);
            }

            _streamOf[i] = stream->index;
        }

        FfmpegException.Check(ffmpeg.avio_open(&file->pb, path, ffmpeg.AVIO_FLAG_WRITE), $"Opening {path}");
        AVDictionary* options = null;
        ffmpeg.av_dict_set(&options, "movflags", "+frag_keyframe+empty_moov+delay_moov+default_base_moof", 0);
        ffmpeg.av_dict_set(&options, "frag_duration", "1000000", 0);
        ffmpeg.av_dict_set(&options, "flush_packets", "1", 0);
        ffmpeg.av_dict_set(&options, "max_interleave_delta", "1000000", 0);
        using var log = FfmpegLog.Capture();
        var result = ffmpeg.avformat_write_header(file, &options);
        ffmpeg.av_dict_free(&options);
        if (result < 0)
        {
            throw new FfmpegException("Starting the recording file", result, log.Summary());
        }

        lock (_segments)
        {
            _segments.Add(new SegmentFile(path, start));
        }

        LogSegment(index, path, start);
    }

    private void WritePacket(EncodedPacket packet)
    {
        var stream = _file->streams[_streamOf[packet.Stream]];
        var raw = packet.Packet;
        raw->stream_index = stream->index;
        ffmpeg.av_packet_rescale_ts(raw, packet.TimeBase, stream->time_base);
        FfmpegException.Check(ffmpeg.av_interleaved_write_frame(_file, raw), "Writing the recording");
    }

    private void CloseFile()
    {
        if (_file is null)
        {
            return;
        }

        FfmpegException.Check(ffmpeg.av_interleaved_write_frame(_file, null), "Writing the recording");
        FfmpegException.Check(ffmpeg.av_write_trailer(_file), "Finishing the recording file");
        FreeFile();
    }

    private void FreeFile()
    {
        if (_file is null)
        {
            return;
        }

        var file = _file;
        _file = null;
        ffmpeg.avio_closep(&file->pb);
        ffmpeg.avformat_free_context(file);
    }

    private void Fail(Exception e)
    {
        if (Interlocked.CompareExchange(ref _failure, e, null) is null)
        {
            LogFailed(e);
            _waiting.ForEach(p => p.Dispose());
            _waiting.Clear();
            Failed?.Invoke(this, e);
        }
    }

    private static void Discard(object item)
    {
        switch (item)
        {
            case EncodedPacket packet:
                packet.Dispose();
                break;
            case Begin begin:
                begin.Video.Dispose();
                break;
        }
    }

    private sealed record Begin(StreamSetup Video, double Start);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording segment {Index} at {Start:0.000} s: {Path}")]
    private partial void LogSegment(int index, string path, double start);

    [LoggerMessage(Level = LogLevel.Warning, Message = "More than {Count} packets wait to be written: the disk can't keep up")]
    private partial void LogBacklog(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Writing the recording failed; it stops here")]
    private partial void LogFailed(Exception e);
}
