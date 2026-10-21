using CashGear.Capture.Media;

namespace CashGear.Capture.VideoEditor.Playback;

/// <summary>
/// Gives the picture of one video stream shown at a source time, reading forward from where it is (cheap: the next
/// picture or a few) or seeking when the time is behind it or far ahead. What the export reads clips with, one
/// thread at a time; the preview's <see cref="FrameSource"/> wraps the same reading on a thread of its own.
/// </summary>
public sealed class FrameCursor : IDisposable
{
    /// <summary>Further ahead than this, seeking (to the keyframe before) beats decoding every picture in between.</summary>
    public const double SeekAhead = 1.5;

    private readonly VideoStreamReader _reader;
    private readonly FramePool _pool;
    private VideoFrame? _current;
    private VideoFrame? _next;
    private bool _ended;
    private double _seekedTo = double.NaN;

    /// <param name="width">The pictures' width as given (0: the video's own size).</param>
    public FrameCursor(string path, int stream, VideoDecoding decoding, int width = 0, int height = 0, FramePool? pool = null)
    {
        _reader = new VideoStreamReader(path, stream, decoding);
        _pool = pool ?? FramePool.Shared;
        (Width, Height) = width > 0 && height > 0 ? (width, height) : (_reader.Width, _reader.Height);
    }

    public int Width { get; }

    public int Height { get; }

    public double FrameRate => _reader.FrameRate;

    public bool IsHardware => _reader.IsHardware;

    /// <summary>Seeks taken (for tests: sequential reading shouldn't seek).</summary>
    public int Seeks { get; private set; }

    /// <summary>The picture shown at <paramref name="time"/>, owned by the cursor until its next call (AddRef to keep it); null for a stream with no pictures.</summary>
    public VideoFrame? At(double time)
    {
        const double eps = 1e-6;
        while (true)
        {
            if (_current is not null)
            {
                var covers = time >= _current.Time - eps || (!double.IsNaN(_seekedTo) && time >= _seekedTo - eps);
                if (covers && (_next is null ? _ended : time < _next.Time - eps))
                {
                    return _current;
                }

                var behind = time < _current.Time - eps && !covers;
                var farAhead = time > (_next?.Time ?? _current.Time) + SeekAhead;
                if (!behind && !farAhead)
                {
                    if (_next is null && !_ended)
                    {
                        _next = Read();
                        _ended = _next is null;
                        continue;
                    }

                    _current.Release();
                    _current = _next;
                    _next = Read();
                    _ended = _next is null;
                    _seekedTo = double.NaN;
                    if (_current is null)
                    {
                        return null;
                    }

                    continue;
                }
            }

            Seek(time);
            if (_current is null)
            {
                return null;
            }
        }
    }

    public void Dispose()
    {
        _current?.Release();
        _next?.Release();
        _current = null;
        _next = null;
        _reader.Dispose();
    }

    private void Seek(double time)
    {
        Seeks++;
        _current?.Release();
        _next?.Release();
        _reader.Seek(time);
        _seekedTo = time;
        _current = Read();
        _next = _current is null ? null : Read();
        _ended = _next is null;
    }

    private VideoFrame? Read()
    {
        var frame = _pool.Rent(Width, Height);
        var time = _reader.Read(frame.Pixels, Width, Height);
        if (time is null)
        {
            frame.Release();
            return null;
        }

        frame.Time = time.Value;
        return frame;
    }
}
