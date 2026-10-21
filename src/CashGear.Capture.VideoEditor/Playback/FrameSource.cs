using CashGear.Capture.Media;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.VideoEditor.Playback;

/// <summary>
/// The preview's pictures of one video stream, decoded on a thread of its own: <see cref="TryGet"/> never waits (the
/// picture if it is ready, else null and the thread fetches it), and while playing the thread stays a few pictures
/// ahead of the playhead. Seeks go to the keyframe before and decode forward, so scrubbing shows exact frames.
/// </summary>
public sealed partial class FrameSource : IDisposable
{
    /// <summary>Pictures kept decoded ahead of the playhead while playing.</summary>
    private const int Ahead = 8;

    /// <summary>Further ahead than this, the thread seeks rather than decoding everything in between.</summary>
    private const double SeekAhead = 1.0;

    private readonly object _gate = new(); // Monitor.Wait and PulseAll need a plain object
    private readonly List<VideoFrame> _frames = [];
    private readonly Thread _thread;
    private readonly ILogger _logger;
    private readonly string _path;
    private readonly int _stream;
    private readonly VideoDecoding _decoding;
    private readonly FramePool _pool;
    private double? _wanted;
    private bool _playing;
    private bool _ended;
    private double _runStart = double.NaN;
    private bool _disposed;
    private Exception? _failure;

    public FrameSource(string path, int stream, VideoDecoding decoding, int width, int height, ILogger logger, FramePool? pool = null)
    {
        _path = path;
        _stream = stream;
        _decoding = decoding;
        Width = width;
        Height = height;
        _logger = logger;
        _pool = pool ?? FramePool.Shared;
        _thread = new Thread(Run) { IsBackground = true, Name = $"Video editor decoder ({Path.GetFileName(path)} #{stream})", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Raised on the decoder thread when a picture someone waits for is ready.</summary>
    public event EventHandler? FrameReady;

    public int Width { get; }

    public int Height { get; }

    /// <summary>Why the stream can't be shown (it couldn't be opened or decoded), or null.</summary>
    public Exception? Failure => Volatile.Read(ref _failure);

    public bool IsHardware { get; private set; }

    /// <summary>
    /// The picture shown at <paramref name="time"/> (AddRef'd: release it), or null if it isn't decoded yet; the
    /// thread is asked for it either way.
    /// </summary>
    public VideoFrame? TryGet(double time)
    {
        lock (_gate)
        {
            if (_wanted != time)
            {
                _wanted = time;
                Monitor.PulseAll(_gate);
            }

            return Lookup(time)?.AddRef();
        }
    }

    /// <summary>Like <see cref="TryGet"/>, but waits up to <paramref name="timeout"/> for it.</summary>
    public VideoFrame? Get(double time, TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        lock (_gate)
        {
            _wanted = time;
            Monitor.PulseAll(_gate);
            while (true)
            {
                if (Lookup(time) is { } frame)
                {
                    return frame.AddRef();
                }

                var left = until - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || _failure is not null || _disposed)
                {
                    return null;
                }

                Monitor.Wait(_gate, left);
            }
        }
    }

    /// <summary>Playing: the thread decodes ahead of the times asked for.</summary>
    public void SetPlaying(bool playing)
    {
        lock (_gate)
        {
            _playing = playing;
            Monitor.PulseAll(_gate);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            Monitor.PulseAll(_gate);
        }

        _thread.Join(TimeSpan.FromSeconds(5));
        lock (_gate)
        {
            _frames.ForEach(f => f.Release());
            _frames.Clear();
        }
    }

    /// <summary>The picture for <paramref name="time"/> among those decoded, if it can be told yet.</summary>
    private VideoFrame? Lookup(double time)
    {
        const double eps = 1e-6;
        if (_frames.Count == 0)
        {
            return null;
        }

        var index = _frames.FindLastIndex(f => f.Time <= time + eps);
        if (index < 0)
        {
            // Before the first picture decoded: it is the answer if the run was sought to this time or earlier.
            return !double.IsNaN(_runStart) && time >= _runStart - eps ? _frames[0] : null;
        }

        return index < _frames.Count - 1 || _ended ? _frames[index] : null;
    }

    private enum Work
    {
        None,
        Seek,
        Read,
    }

    private void Run()
    {
        VideoStreamReader? reader = null;
        try
        {
            reader = new VideoStreamReader(_path, _stream, _decoding);
            while (true)
            {
                Work work;
                double target;
                lock (_gate)
                {
                    while (true)
                    {
                        if (_disposed)
                        {
                            return;
                        }

                        (work, target) = Decide();
                        if (work != Work.None)
                        {
                            break;
                        }

                        Monitor.Wait(_gate);
                    }

                    if (work == Work.Seek)
                    {
                        _frames.ForEach(f => f.Release());
                        _frames.Clear();
                        _runStart = target;
                        _ended = false;
                    }
                }

                if (work == Work.Seek)
                {
                    reader.Seek(target);
                }

                var frame = _pool.Rent(Width, Height);
                var time = reader.Read(frame.Pixels, Width, Height);
                IsHardware = reader.IsHardware;
                lock (_gate)
                {
                    if (time is null)
                    {
                        frame.Release();
                        _ended = true;
                    }
                    else
                    {
                        frame.Time = time.Value;
                        _frames.Add(frame);
                        Trim();
                    }

                    Monitor.PulseAll(_gate);
                }

                FrameReady?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception e) when (e is FfmpegException or IOException or OutOfMemoryException)
        {
            LogFailed(e, _path, _stream);
            lock (_gate)
            {
                _failure = e;
                Monitor.PulseAll(_gate);
            }

            FrameReady?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            reader?.Dispose();
        }
    }

    /// <summary>What the thread should do next (under the lock).</summary>
    private (Work Work, double Target) Decide()
    {
        if (_wanted is not { } wanted)
        {
            return (Work.None, 0);
        }

        const double eps = 1e-6;
        var first = _frames.Count > 0 ? Math.Min(_frames[0].Time, double.IsNaN(_runStart) ? _frames[0].Time : _runStart) : double.NaN;
        var last = _frames.Count > 0 ? _frames[^1].Time : double.NaN;
        if (_frames.Count == 0 && double.IsNaN(_runStart))
        {
            return (Work.Seek, wanted);
        }

        if (_frames.Count > 0 && (wanted < first - eps || wanted > last + SeekAhead))
        {
            return (Work.Seek, wanted);
        }

        if (_ended)
        {
            return (Work.None, 0);
        }

        if (_frames.Count == 0 || Lookup(wanted) is null)
        {
            return (Work.Read, 0);
        }

        var ahead = _frames.Count(f => f.Time > wanted + eps);
        return _playing && ahead < Ahead ? (Work.Read, 0) : (Work.None, 0);
    }

    /// <summary>Drops pictures well behind what is wanted (one before it stays, for a step back).</summary>
    private void Trim()
    {
        if (_wanted is not { } wanted)
        {
            return;
        }

        var shown = _frames.FindLastIndex(f => f.Time <= wanted + 1e-6);
        var drop = Math.Max(0, shown - 1);
        if (drop > 0)
        {
            for (var i = 0; i < drop; i++)
            {
                _frames[i].Release();
            }

            _frames.RemoveRange(0, drop);
            _runStart = double.NaN;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Video editor: can't decode {Path} stream {Stream}")]
    private partial void LogFailed(Exception e, string path, int stream);
}
