using System.ComponentModel;
using System.Diagnostics;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;
using CashGear.Capture.Recording.Resources;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Recording;

/// <summary>
/// Another monitor during a recording, as a video track of its own: its pictures on the recording's clock, encoded at
/// the recording's frame rate on the "Recording screen" thread into a file in the recording's folder, saved as a track.
/// As with the webcam, its own <see cref="FrameGrid"/> goes by the time each picture was captured, so it stays with the
/// sound. A lost device holds the picture until it is back. A monitor unplugged, a new resolution or a failing encoder
/// ends this track only, with a warning: the recording goes on.
/// </summary>
internal sealed partial class ScreenFeed : IDisposable
{
    [Localizable(false)] // a thread's name, for debuggers and dumps
    private const string ThreadName = "Recording screen";

    private readonly IPictureStream _stream;
    private readonly RecordingClock _clock;
    private readonly FrameGrid _grid;
    private readonly Func<VideoFormat, VideoEncoder> _openEncoder;
    private readonly MediaWriter _writer;
    private readonly Action<string> _warn;
    private readonly ILogger _logger;
    private readonly object _gridGate = new();
    private readonly string _name;
    private Thread? _thread;
    private byte[] _picture = [];
    private long _written;
    private long _lostSince;
    private long _nextRecovery;
    private volatile bool _stopping;
    private volatile bool _closed;

    /// <param name="stream">The monitor's pictures, stamped through <see cref="Grid"/>.</param>
    /// <param name="grid">The track's frames (the same one the stream's slots come from).</param>
    /// <param name="name">The monitor, for messages.</param>
    /// <param name="path">The file it is written to.</param>
    /// <param name="openEncoder">Opens an encoder for the monitor's size (the first that works).</param>
    /// <param name="warn">Tells the user something went wrong while recording continues.</param>
    public ScreenFeed(IPictureStream stream, FrameGrid grid, RecordingClock clock, string name, string path, Func<VideoFormat, VideoEncoder> openEncoder, Action<string> warn, ILoggerFactory loggers)
    {
        _stream = stream;
        _grid = grid;
        _clock = clock;
        _name = name;
        _openEncoder = openEncoder;
        _warn = warn;
        _logger = loggers.CreateLogger<ScreenFeed>();
        _writer = new MediaWriter(_ => path, [], loggers.CreateLogger<MediaWriter>());
        _stream.Closed += OnClosed;
    }

    /// <summary>Pictures encoded into the track.</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>Frames skipped because encoding fell behind.</summary>
    public long Dropped
    {
        get
        {
            lock (_gridGate)
            {
                return _grid.Dropped;
            }
        }
    }

    /// <summary>Pictures captured.</summary>
    public long Delivered => _stream.Delivered;

    /// <summary>Starts encoding the track (once the recording clock runs).</summary>
    public void StartTrack()
    {
        _thread = new Thread(WriteTrack) { IsBackground = true, Name = ThreadName, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Watchdog: asks whether the device was lost when no picture came for a while.</summary>
    public void CheckDevice()
    {
        try
        {
            _stream.CheckDevice();
        }
        catch (Exception e) when (e is SharpGen.Runtime.SharpGenException or System.Runtime.InteropServices.COMException or ObjectDisposedException)
        {
            // Its thread finds out on its next read.
        }
    }

    /// <summary>The recording stops at <paramref name="elapsed"/> (at exactly that length, when <paramref name="exact"/>).</summary>
    public bool Finish(TimeSpan elapsed, bool exact, TimeSpan timeout)
    {
        lock (_gridGate)
        {
            if (exact)
            {
                _grid.FinishAtLength(elapsed);
            }
            else
            {
                _grid.Finish(elapsed);
            }
        }

        _stopping = true;
        var done = _thread?.Join(timeout) ?? true;
        _writer.Complete(timeout);
        return done;
    }

    public void Dispose()
    {
        _stopping = true;
        _stream.Closed -= OnClosed;
        _thread?.Join(TimeSpan.FromSeconds(5));
        _stream.Dispose();
        _writer.Dispose();
        LogFinished(_name, Written);
    }

    private void OnClosed(object? sender, EventArgs e) => _closed = true;

    private void WriteTrack()
    {
        var packets = new List<EncodedPacket>();
        VideoEncoder? encoder = null;
        FrameConverter? converter = null;
        Picture? current = null;
        var begun = false;
        var size = (_stream.Width, _stream.Height);
        var format = new VideoFormat(_stream.Width, _stream.Height, _grid.Fps);
        _picture = new byte[format.Width * format.Height * 4];
        try
        {
            encoder = _openEncoder(format);
            converter = new FrameConverter(format.Width, format.Height, encoder.PixelFormat);
            while (true)
            {
                GridStep step;
                lock (_gridGate)
                {
                    step = _grid.Step(_clock.Elapsed);
                    if (!step.Produce && (_grid.End is not null || _stopping))
                    {
                        break;
                    }
                }

                if (!step.Produce)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(Math.Clamp(step.Wait.TotalMilliseconds, 1, 20)));
                    continue;
                }

                if (_closed)
                {
                    _warn(RecordingStrings.ScreenTrackEnded(_name));
                    _closed = false;
                    break;
                }

                if (_stream.ResizePending)
                {
                    _stream.ApplyResize();
                    if ((_stream.Width, _stream.Height) != size)
                    {
                        // An encoder has one size, and a track has one encoder: the track ends here, the recording goes on.
                        _warn(RecordingStrings.ScreenTrackEnded(_name));
                        break;
                    }

                    if (current is not null)
                    {
                        _stream.Release(current);
                        current = null;
                    }
                }

                try
                {
                    if (_lostSince != 0)
                    {
                        Recover();
                    }

                    if (Take(step.Slot, ref current) && current is not null)
                    {
                        _stream.Read(current, _picture);
                    }
                }
                catch (DeviceLostException)
                {
                    current = null; // its texture went with the device
                    if (_lostSince == 0)
                    {
                        _lostSince = Stopwatch.GetTimestamp();
                        _nextRecovery = 0;
                    }
                }

                unsafe
                {
                    converter.Convert(_picture, format.Width * 4, encoder.NextFrame());
                }

                encoder.Encode(step.Slot, packets);
                begun = Post(packets, encoder, begun);
                Interlocked.Increment(ref _written);
                lock (_gridGate)
                {
                    _grid.Produced(repeated: false);
                }
            }

            encoder.Flush(packets);
            Post(packets, encoder, begun);
        }
        catch (Exception e) when (e is FfmpegException or InvalidOperationException or IOException or CaptureException)
        {
            LogFailed(e, _name);
            _warn(RecordingStrings.ScreenTrackStopped(_name, e.Message));
        }
        finally
        {
            if (current is not null)
            {
                _stream.Release(current);
            }

            packets.ForEach(p => p.Dispose());
            converter?.Dispose();
            encoder?.Dispose();
        }
    }

    private void Recover()
    {
        var now = Stopwatch.GetTimestamp();
        if (now < _nextRecovery)
        {
            return;
        }

        _nextRecovery = now + (Stopwatch.Frequency / 4);
        if (_stream.Recover())
        {
            _lostSince = 0;
        }
    }

    /// <summary>The pictures due by this frame, keeping the newest; true if it is a new one.</summary>
    private bool Take(long slot, ref Picture? current)
    {
        var changed = false;
        while (_stream.TryPeek(out var next) && (current is null || next.Slot <= slot))
        {
            _stream.TryTake(out _);
            if (current is not null)
            {
                _stream.Release(current);
            }

            current = next;
            changed = true;
        }

        return changed;
    }

    private bool Post(List<EncodedPacket> packets, VideoEncoder encoder, bool begun)
    {
        if (packets.Count == 0)
        {
            return begun;
        }

        if (!begun)
        {
            _writer.BeginSegment(StreamSetup.From(encoder), packets[0].Seconds);
        }

        foreach (var packet in packets)
        {
            _writer.Write(packet);
        }

        packets.Clear();
        return true;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen {Name}: {Frames} frames in its own track")]
    private partial void LogFinished(string name, long frames);

    [LoggerMessage(Level = LogLevel.Error, Message = "The track of screen {Name} failed; the recording goes on")]
    private partial void LogFailed(Exception e, string name);
}
