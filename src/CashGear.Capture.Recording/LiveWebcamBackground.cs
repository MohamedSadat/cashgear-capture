using System.Runtime.InteropServices;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Inference;

namespace CashGear.Capture.Recording;

/// <summary>
/// What is behind the person, done to each camera picture as the recording goes: the person found on the graphics card
/// (<see cref="IPersonSegmenter"/>), their edges kept from flickering (<see cref="MaskSmoother"/>), and what isn't them
/// blurred or made transparent. It works beside the camera's thread on the newest picture, so a slow one skips pictures
/// and never holds the camera up, and it watches its own time: once it can't keep up with the camera it stops, and the
/// webcam shows as it is (the editor can still do it, as the track is untouched).
/// </summary>
internal sealed class LiveWebcamBackground : IDisposable
{
    /// <summary>Pictures looked at before deciding it can't keep up.</summary>
    private const int Window = 45;

    /// <summary>How much of the time between pictures it may take on average.</summary>
    private const double Budget = 0.9;

    private const int BlurScale = 8;

    private readonly IPersonSegmenter _segmenter;
    private readonly WebcamLiveBackground _kind;
    private readonly double _pictureSeconds;
    private readonly Action _fellBehind;
    private readonly MaskSmoother _smoother;
    private readonly byte[] _mask;
    private readonly AutoResetEvent _arrived = new(false);
    private readonly Thread _thread;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private WebcamFrame? _next;
    private byte[] _small = [];
    private byte[] _blurred = [];
    private int[] _columnOf = [];
    private int[] _columnWeight = [];
    private volatile bool _stopping;
    private int _looked;
    private double _spent;

    /// <param name="deliver">Gets each worked picture, and the picture it came from.</param>
    /// <param name="fellBehind">It can't keep up, and has stopped.</param>
    public LiveWebcamBackground(IPersonSegmenter segmenter, WebcamLiveBackground kind, double cameraFps, Action<WebcamFrame, WebcamFrame> deliver, Action fellBehind, Func<int, int, WebcamFrame> rent, TimeProvider? time = null)
    {
        _segmenter = segmenter;
        _kind = kind;
        _pictureSeconds = 1 / Math.Max(1, cameraFps);
        _fellBehind = fellBehind;
        _mask = new byte[segmenter.MaskSize * segmenter.MaskSize];
        _smoother = new MaskSmoother(_mask.Length);
        _time = time ?? TimeProvider.System;
        _thread = new Thread(() => Work(deliver, rent)) { IsBackground = true, Name = ThreadName };
        _thread.Start();
    }

    [System.ComponentModel.Localizable(false)] // a thread's name, for debuggers and dumps
    private const string ThreadName = "Webcam background";

    /// <summary>It is working (it stops once it can't keep up).</summary>
    public bool IsWorking => !FellBehind && !_stopping;

    public bool FellBehind { get; private set; }

    /// <summary>The newest camera picture to work on, in place of one not started yet.</summary>
    public void Offer(WebcamFrame frame)
    {
        if (!IsWorking)
        {
            return;
        }

        frame.Hold();
        WebcamFrame? dropped;
        lock (_gate)
        {
            dropped = _next;
            _next = frame;
        }

        if (dropped is not null)
        {
            Returned?.Invoke(dropped);
        }

        _arrived.Set();
    }

    /// <summary>A picture it held and no longer needs.</summary>
    public Action<WebcamFrame>? Returned { get; set; }

    public void Dispose()
    {
        _stopping = true;
        _arrived.Set();
        _thread.Join(TimeSpan.FromSeconds(5));
        lock (_gate)
        {
            if (_next is { } left)
            {
                Returned?.Invoke(left);
                _next = null;
            }
        }

        _arrived.Dispose();
        _segmenter.Dispose();
    }

    private void Work(Action<WebcamFrame, WebcamFrame> deliver, Func<int, int, WebcamFrame> rent)
    {
        while (!_stopping)
        {
            _arrived.WaitOne(100);
            WebcamFrame? frame;
            lock (_gate)
            {
                frame = _next;
                _next = null;
            }

            if (frame is null)
            {
                continue;
            }

            try
            {
                var started = _time.GetTimestamp();
                var worked = rent(frame.Width, frame.Height);
                Do(frame, worked);
                deliver(frame, worked);
                Measure(_time.GetElapsedTime(started).TotalSeconds);
                if (FellBehind)
                {
                    _fellBehind();
                    return;
                }
            }
            catch (Exception e) when (e is InvalidOperationException or SEHException or OutOfMemoryException or Microsoft.ML.OnnxRuntime.OnnxRuntimeException)
            {
                FellBehind = true;
                _fellBehind();
                return;
            }
            finally
            {
                Returned?.Invoke(frame);
            }
        }
    }

    private void Measure(double seconds)
    {
        _spent += seconds;
        if (++_looked < Window)
        {
            return;
        }

        FellBehind = _spent / _looked > _pictureSeconds * Budget;
        _looked = 0;
        _spent = 0;
    }

    private void Do(WebcamFrame frame, WebcamFrame worked)
    {
        _segmenter.Segment(frame.Pixels, frame.Width, frame.Height, frame.Width * 4, _mask);
        _smoother.Apply(_mask);
        WebcamMatte.Apply(frame.Pixels, worked.Pixels, frame.Width, frame.Height, _mask, _segmenter.MaskSize, _kind, ref _small, ref _blurred, ref _columnOf, ref _columnWeight, BlurScale);
    }
}

/// <summary>A camera picture with what is behind the person blurred or made transparent, from a small square mask.</summary>
internal static class WebcamMatte
{
    /// <summary>
    /// Writes <paramref name="source"/> to <paramref name="target"/> (premultiplied): each pixel the picture as it is where
    /// the mask is sure of a person, and blurred, or nothing, where it is sure of none.
    /// </summary>
    public static void Apply(ReadOnlySpan<byte> source, Span<byte> target, int width, int height, ReadOnlySpan<byte> mask, int maskSize, WebcamLiveBackground kind, ref byte[] small, ref byte[] blurred, ref int[] columnOf, ref int[] columnWeight, int blurScale = 8)
    {
        var blur = kind == WebcamLiveBackground.Blur;
        var (smallWidth, smallHeight) = (Math.Max(1, width / blurScale), Math.Max(1, height / blurScale));
        if (blur)
        {
            Blur(source, width, height, smallWidth, smallHeight, ref small, ref blurred);
        }

        if (columnOf.Length != width)
        {
            columnOf = new int[width];
            columnWeight = new int[width];
            for (var x = 0; x < width; x++)
            {
                var at = (((x + 0.5) * maskSize / width) - 0.5) * 256;
                var clamped = Math.Clamp((int)at, 0, ((maskSize - 1) * 256) - 1);
                columnOf[x] = clamped >> 8;
                columnWeight[x] = clamped & 255;
            }
        }

        for (var y = 0; y < height; y++)
        {
            var rowAt = (((y + 0.5) * maskSize / height) - 0.5) * 256;
            var clampedRow = Math.Clamp((int)rowAt, 0, ((maskSize - 1) * 256) - 1);
            var (maskRow, rowWeight) = (clampedRow >> 8, clampedRow & 255);
            var smallY = y * smallHeight / height;
            for (var x = 0; x < width; x++)
            {
                var (column, weight) = (columnOf[x], columnWeight[x]);
                var top = (mask[(maskRow * maskSize) + column] * (256 - weight)) + (mask[(maskRow * maskSize) + column + 1] * weight);
                var bottom = (mask[((maskRow + 1) * maskSize) + column] * (256 - weight)) + (mask[((maskRow + 1) * maskSize) + column + 1] * weight);
                var person = ((top * (256 - rowWeight)) + (bottom * rowWeight)) >> 16; // 0 to 255
                var at = ((y * width) + x) * 4;
                if (blur)
                {
                    var behind = ((smallY * smallWidth) + (x * smallWidth / width)) * 4;
                    target[at] = (byte)(((source[at] * person) + (blurred[behind] * (255 - person))) / 255);
                    target[at + 1] = (byte)(((source[at + 1] * person) + (blurred[behind + 1] * (255 - person))) / 255);
                    target[at + 2] = (byte)(((source[at + 2] * person) + (blurred[behind + 2] * (255 - person))) / 255);
                    target[at + 3] = 255;
                }
                else
                {
                    target[at] = (byte)(source[at] * person / 255);
                    target[at + 1] = (byte)(source[at + 1] * person / 255);
                    target[at + 2] = (byte)(source[at + 2] * person / 255);
                    target[at + 3] = (byte)person;
                }
            }
        }
    }

    /// <summary>A small copy, each pixel the average of what it covers, then two passes of a 5-pixel box each way.</summary>
    private static void Blur(ReadOnlySpan<byte> source, int width, int height, int smallWidth, int smallHeight, ref byte[] small, ref byte[] blurred)
    {
        var bytes = smallWidth * smallHeight * 4;
        if (small.Length != bytes)
        {
            small = new byte[bytes];
            blurred = new byte[bytes];
        }

        for (var y = 0; y < smallHeight; y++)
        {
            var fromY = y * height / smallHeight;
            var toY = Math.Max(fromY + 1, (y + 1) * height / smallHeight);
            for (var x = 0; x < smallWidth; x++)
            {
                var fromX = x * width / smallWidth;
                var toX = Math.Max(fromX + 1, (x + 1) * width / smallWidth);
                int b = 0, g = 0, r = 0, count = 0;

                // Every other pixel is plenty for a blur.
                for (var sy = fromY; sy < toY; sy += 2)
                {
                    for (var sx = fromX; sx < toX; sx += 2)
                    {
                        var at = ((sy * width) + sx) * 4;
                        b += source[at];
                        g += source[at + 1];
                        r += source[at + 2];
                        count++;
                    }
                }

                var to = ((y * smallWidth) + x) * 4;
                small[to] = (byte)(b / count);
                small[to + 1] = (byte)(g / count);
                small[to + 2] = (byte)(r / count);
            }
        }

        for (var pass = 0; pass < 2; pass++)
        {
            Box(small, blurred, smallWidth, smallHeight, 4, smallWidth * 4, smallWidth, smallHeight);
            Box(blurred, small, smallWidth, smallHeight, smallWidth * 4, 4, smallHeight, smallWidth);
        }

        small.CopyTo(blurred, 0);
    }

    /// <summary>One direction of a 5-pixel box: along lines <paramref name="along"/> bytes apart per pixel, lines <paramref name="across"/> bytes apart.</summary>
    private static void Box(byte[] from, byte[] to, int width, int height, int along, int across, int alongCount, int acrossCount)
    {
        for (var line = 0; line < acrossCount; line++)
        {
            for (var i = 0; i < alongCount; i++)
            {
                int b = 0, g = 0, r = 0;
                for (var k = -2; k <= 2; k++)
                {
                    var at = (line * across) + (Math.Clamp(i + k, 0, alongCount - 1) * along);
                    b += from[at];
                    g += from[at + 1];
                    r += from[at + 2];
                }

                var target = (line * across) + (i * along);
                to[target] = (byte)(b / 5);
                to[target + 1] = (byte)(g / 5);
                to[target + 2] = (byte)(r / 5);
            }
        }
    }
}
