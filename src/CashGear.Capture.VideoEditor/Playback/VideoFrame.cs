using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Playback;

/// <summary>
/// A decoded picture: top-down premultiplied BGRA in native memory (opaque, so premultiplied is plain), with the
/// source time it is shown at. Reference-counted: whoever keeps it calls <see cref="AddRef"/> and later
/// <see cref="Release"/>; the memory goes back to <see cref="FramePool"/> when the last reference is released, so a
/// frame being drawn on another thread (or by Skia's GPU upload) is never freed under it.
/// </summary>
public sealed unsafe class VideoFrame
{
    private readonly FramePool _pool;
    private int _references = 1;

    internal VideoFrame(FramePool pool, nint memory, int width, int height)
    {
        _pool = pool;
        Memory = memory;
        Width = width;
        Height = height;
    }

    public int Width { get; }

    public int Height { get; }

    public int RowBytes => Width * 4;

    /// <summary>The source time it is shown from, in seconds.</summary>
    public double Time { get; internal set; }

    internal nint Memory { get; }

    public Span<byte> Pixels => new((void*)Memory, Width * Height * 4);

    public VideoFrame AddRef()
    {
        if (Interlocked.Increment(ref _references) <= 1)
        {
            throw new ObjectDisposedException(nameof(VideoFrame), "The frame was already released.");
        }

        return this;
    }

    public void Release()
    {
        var left = Interlocked.Decrement(ref _references);
        if (left == 0)
        {
            _pool.Return(this);
        }
        else if (left < 0)
        {
            throw new InvalidOperationException("A frame was released more often than it was referenced.");
        }
    }

    /// <summary>
    /// The pixels as a Skia image without copying them: it holds a reference to the frame until Skia lets go of the
    /// image (after the GPU has uploaded it), so the image can be disposed right after drawing.
    /// </summary>
    public SKImage ToImage()
    {
        AddRef();
        var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var pixmap = new SKPixmap(info, Memory, RowBytes);
        return SKImage.FromPixels(pixmap, (_, context) => ((VideoFrame)context!).Release(), this)
            ?? throw new InvalidOperationException("Skia couldn't wrap a frame.");
    }

    internal void Revive() => Volatile.Write(ref _references, 1);
}

/// <summary>
/// Native memory for frames, kept for reuse by size (a few of each), so playing and exporting don't allocate a
/// picture's worth of memory per frame. Thread-safe.
/// </summary>
public sealed class FramePool : IDisposable
{
    private const int KeptPerSize = 24;

    private readonly ConcurrentDictionary<(int Width, int Height), ConcurrentBag<VideoFrame>> _free = new();
    private bool _disposed;

    public static FramePool Shared { get; } = new();

    public VideoFrame Rent(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(height, 1);
        if (_free.TryGetValue((width, height), out var bag) && bag.TryTake(out var frame))
        {
            frame.Revive();
            return frame;
        }

        unsafe
        {
            var memory = (nint)NativeMemory.Alloc((nuint)width * (nuint)height * 4);
            return new VideoFrame(this, memory, width, height);
        }
    }

    internal unsafe void Return(VideoFrame frame)
    {
        var bag = _free.GetOrAdd((frame.Width, frame.Height), _ => []);
        if (_disposed || bag.Count >= KeptPerSize)
        {
            NativeMemory.Free((void*)frame.Memory);
            return;
        }

        bag.Add(frame);
    }

    public unsafe void Dispose()
    {
        _disposed = true;
        foreach (var bag in _free.Values)
        {
            while (bag.TryTake(out var frame))
            {
                NativeMemory.Free((void*)frame.Memory);
            }
        }
    }
}
