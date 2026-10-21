using System.Collections.Concurrent;
using System.Diagnostics;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;
using CashGear.Capture.Overlays.Rendering;
using CashGear.Capture.Overlays.Webcam;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Recording;

/// <summary>A webcam picture kept for drawing and encoding: top-down BGRA rows of <c>Width * 4</c> bytes. Counted, so a reader can hold it.</summary>
internal sealed class WebcamFrame(int width, int height)
{
    private int _references;

    public int Width { get; } = width;

    public int Height { get; } = height;

    public byte[] Pixels { get; } = new byte[width * height * 4];

    public long Version { get; set; }

    public WebcamPicture Picture => new(Width, Height, Pixels, Version);

    public void Hold() => Interlocked.Increment(ref _references);

    /// <summary>Lets go of it; true when nobody holds it any more.</summary>
    public bool Release() => Interlocked.Decrement(ref _references) == 0;
}

/// <summary>
/// The webcam during a recording. Each picture is copied (into a reused buffer) and placed on the recording clock by
/// the time the camera captured it, in a <see cref="TimedQueue{T}"/> of the latest few. The video thread draws the
/// picture shown at each frame's time (<see cref="Hold"/>); the "Recording webcam" thread encodes the webcam's own
/// track at the recording's frame rate, frame n showing the picture shown at its time (like the screen's
/// <see cref="FrameGrid"/>), into <c>webcam.mp4</c> in the recording's folder, saved as a second video track. As
/// both go by the camera's own timestamps, the webcam can't drift from the sound however long the recording. If its
/// encoder fails, the track ends there and the overlay goes on.
/// </summary>
internal sealed partial class WebcamFeed : IDisposable
{
    public const string FileName = "webcam.mp4";
    public const string TrackTitle = "Webcam";
    private const int Kept = 6;
    private static readonly TimeSpan FirstPictureTimeout = TimeSpan.FromSeconds(5);

    private readonly IWebcamSource _source;
    private readonly RecordingClock _clock;
    private readonly FrameGrid _grid;
    private readonly TimedQueue<WebcamFrame> _pictures = new(Kept);
    private readonly ConcurrentBag<WebcamFrame> _free = [];
    private readonly TaskCompletionSource _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<VideoFormat, VideoEncoder> _openEncoder;
    private readonly MediaWriter _writer;
    private readonly Action<string> _warn;
    private readonly ILogger _logger;
    private readonly object _gridGate = new();
    private Thread? _thread;
    private long _version;
    private long _written;
    private volatile bool _stopping;

    /// <param name="openEncoder">Opens an encoder for the webcam's size (the first that works).</param>
    /// <param name="warn">Tells the user something went wrong while recording continues.</param>
    public WebcamFeed(IWebcamSource source, RecordingClock clock, int fps, string folder, Func<VideoFormat, VideoEncoder> openEncoder, Action<string> warn, ILoggerFactory loggers)
    {
        _source = source;
        _clock = clock;
        _grid = new FrameGrid(fps);
        _openEncoder = openEncoder;
        _warn = warn;
        _logger = loggers.CreateLogger<WebcamFeed>();
        _writer = new MediaWriter(_ => Path.Combine(folder, FileName), [], loggers.CreateLogger<MediaWriter>());
        (Width, Height) = EncoderPresets.EvenSize(source.Width, source.Height);
    }

    public string Name => _source.Name;

    /// <summary>The track's size (the camera's, made even).</summary>
    public int Width { get; }

    public int Height { get; }

    /// <summary>Pictures encoded into the webcam's track.</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>Starts the camera and waits for its first picture.</summary>
    /// <exception cref="TimeoutException">No picture came.</exception>
    public async Task StartAsync(CancellationToken ct)
    {
        _source.Start(OnPicture);
        await _first.Task.WaitAsync(FirstPictureTimeout, ct).ConfigureAwait(false);
    }

    /// <summary>Starts encoding the webcam's track (once the recording clock runs).</summary>
    public void StartTrack()
    {
        _thread = new Thread(WriteTrack) { IsBackground = true, Name = "Recording webcam", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>The picture shown at recording time <paramref name="time"/>, held: give it back with <see cref="Release"/>.</summary>
    public WebcamFrame? Hold(TimeSpan time) => _pictures.At(time, f => f.Hold())?.Item;

    public void Release(WebcamFrame frame)
    {
        if (frame.Release())
        {
            _free.Add(frame);
        }
    }

    /// <summary>The recording stops at <paramref name="elapsed"/>: the track gets its frames up to there, then ends.</summary>
    public bool Finish(TimeSpan elapsed, TimeSpan timeout)
    {
        lock (_gridGate)
        {
            _grid.Finish(elapsed);
        }

        _stopping = true;
        var done = _thread?.Join(timeout) ?? true;
        _writer.Complete(timeout);
        return done;
    }

    public void Dispose()
    {
        _stopping = true;
        _source.Dispose();
        _thread?.Join(TimeSpan.FromSeconds(5));
        _writer.Dispose();
        LogFinished(_source.Name, Written);
    }

    /// <summary>The camera's thread: the picture is copied and placed by its capture time.</summary>
    private void OnPicture(ReadOnlySpan<byte> bgra, int width, int height, int stride, long timestamp)
    {
        if (!_free.TryTake(out var frame) || frame.Width != width || frame.Height != height)
        {
            frame = new WebcamFrame(width, height);
        }

        var row = width * 4;
        for (var y = 0; y < height && ((y * stride) + row) <= bgra.Length; y++)
        {
            bgra.Slice(y * stride, row).CopyTo(frame.Pixels.AsSpan(y * row, row));
        }

        frame.Version = Interlocked.Increment(ref _version);
        frame.Hold(); // the queue's
        if (_pictures.Add(_clock.MapInstant(timestamp), frame) is { } dropped)
        {
            Release(dropped);
        }

        _first.TrySetResult();
    }

    /// <summary>Encodes the webcam's own track: frame n shows the picture shown at n / fps.</summary>
    private unsafe void WriteTrack()
    {
        var packets = new List<EncodedPacket>();
        VideoEncoder? encoder = null;
        FrameConverter? converter = null;
        var begun = false;
        try
        {
            encoder = _openEncoder(new VideoFormat(Width, Height, _grid.Fps));
            converter = new FrameConverter(Width, Height, encoder.PixelFormat);
            while (true)
            {
                GridStep step;
                lock (_gridGate)
                {
                    step = _grid.Step(_clock.Elapsed);
                    if (!step.Produce && (_grid.End is not null || _stopping))
                    {
                        break; // the end of the recording (or it was thrown away)
                    }
                }

                if (!step.Produce)
                {
                    Thread.Sleep(TimeSpan.FromMilliseconds(Math.Clamp(step.Wait.TotalMilliseconds, 1, 20)));
                    continue;
                }

                var frame = Hold(_grid.TimeOf(step.Slot));
                if (frame is not null)
                {
                    try
                    {
                        converter.Convert(frame.Pixels, frame.Width * 4, encoder.NextFrame());
                    }
                    finally
                    {
                        Release(frame);
                    }

                    encoder.Encode(step.Slot, packets);
                    begun = Post(packets, encoder, begun);
                    Interlocked.Increment(ref _written);
                }

                lock (_gridGate)
                {
                    _grid.Produced(repeated: false);
                }
            }

            encoder.Flush(packets);
            Post(packets, encoder, begun);
        }
        catch (Exception e) when (e is FfmpegException or InvalidOperationException or IOException)
        {
            LogFailed(e);
            _warn($"The webcam's own track stopped ({e.Message}); the webcam is still drawn over the recording.");
        }
        finally
        {
            packets.ForEach(p => p.Dispose());
            converter?.Dispose();
            encoder?.Dispose();
        }
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Webcam {Name}: {Frames} frames in its own track")]
    private partial void LogFinished(string name, long frames);

    [LoggerMessage(Level = LogLevel.Error, Message = "The webcam's track failed; the overlay goes on")]
    private partial void LogFailed(Exception e);
}
