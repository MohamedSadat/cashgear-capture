using System.Collections.Concurrent;
using System.Diagnostics;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Overlays.Rendering;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace CashGear.Capture.Recording;

/// <summary>
/// A picture of the recorded area on the GPU, when Windows captured it (a <see cref="Stopwatch"/> timestamp), and the
/// video frame it belongs to (-1 when the stream wasn't told how to work that out).
/// </summary>
internal sealed record Picture(ID3D11Texture2D Texture, long Timestamp, long Slot);

/// <summary>
/// A running Windows.Graphics.Capture session on one monitor, delivering pictures of an area of it. Each frame
/// Windows sends (whenever the screen changes) is cropped on the GPU into a texture of the area's size and queued
/// with its capture time (<c>SystemRelativeTime</c>, the same QPC clock the sound is stamped with) and the video
/// frame it belongs to. Windows sends a picture whenever anything on the monitor changes, often faster than the
/// recording's frame rate, so the queue keeps one picture per frame: a newer picture for the same frame replaces
/// the older one. When the recorder falls far behind, the oldest are dropped. Only the pictures the recorder
/// actually encodes are read back to the CPU (<see cref="Read"/>). Uses its own D3D11 device, so screenshots
/// taken meanwhile don't wait for it. A monitor in HDR mode is captured as 16-bit float and each picture read
/// is tone-mapped to SDR first (<see cref="ToneMapper"/>).
/// </summary>
internal sealed partial class WgcAreaStream : IDisposable
{
    private const int PoolBuffers = 2;
    private const int QueueLength = 8;
    private const int MaxTextures = QueueLength + 4;
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();

    /// <summary>Serializes reading back and drawing overlays (the capture thread's copies rely on the device's multithread protection).</summary>
    private readonly object _contextGate = new();
    private readonly D3DDeviceManager _devices;
    private readonly PixelRect _crop;
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Picture> _pictures = [];
    private readonly Func<long, long>? _slotOf;
    private readonly ConcurrentBag<ID3D11Texture2D> _free = [];
    private readonly List<ID3D11Texture2D> _textures = [];
    private readonly ILogger _logger;
    private readonly HdrInfo? _hdr;
    private readonly DirectXPixelFormat _poolFormat;

    private D3DResources? _resources;
    private ToneMapper? _toneMapper;
    private ID3D11Texture2D? _toned;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _staging;
    private SizeInt32 _poolSize;
    private long _delivered;
    private long _skipped;
    private Exception? _failure;
    private bool _disposed;

    private WgcAreaStream(MonitorInfo monitor, PixelRect area, Func<long, long>? slotOf, HdrInfo? hdr, ILoggerFactory loggers)
    {
        _slotOf = slotOf;
        _hdr = hdr;
        _poolFormat = hdr is null ? DirectXPixelFormat.B8G8R8A8UIntNormalized : DirectXPixelFormat.R16G16B16A16Float;
        _devices = new D3DDeviceManager(loggers.CreateLogger<D3DDeviceManager>());
        _logger = loggers.CreateLogger<WgcAreaStream>();
        _crop = new PixelRect(area.X - monitor.Bounds.X, area.Y - monitor.Bounds.Y, area.Width, area.Height);
        Width = area.Width;
        Height = area.Height;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Pictures Windows delivered so far.</summary>
    public long Delivered => Interlocked.Read(ref _delivered);

    /// <summary>Pictures replaced by a newer one for the same frame, or dropped because the recorder was far behind.</summary>
    public long Skipped => Interlocked.Read(ref _skipped);

    /// <summary>The stream's own D3D11 device (overlays are drawn with it).</summary>
    public ID3D11Device Device => _resources!.Device;

    /// <summary>Starts capturing <paramref name="area"/> of <paramref name="monitor"/> and waits for the first picture.</summary>
    /// <param name="slotOf">The video frame a picture captured at a timestamp belongs to; pictures for the same frame replace each other.</param>
    /// <param name="hdr">Capture as float and tone-map with this white level (a monitor in HDR mode); null captures 8-bit.</param>
    public static async Task<WgcAreaStream> StartAsync(
        WgcInterop interop, MonitorInfo monitor, PixelRect area, bool includeCursor, ILoggerFactory loggers, CancellationToken ct,
        Func<long, long>? slotOf = null, HdrInfo? hdr = null)
    {
        if (!monitor.Bounds.Contains(area))
        {
            throw new ArgumentException($"{area} is not inside monitor {monitor.ShortName} {monitor.Bounds}.", nameof(area));
        }

        // WinRT objects must not be created on the caller's (possibly STA UI) thread.
        var stream = await Task.Run(() =>
        {
            var created = new WgcAreaStream(monitor, area, slotOf, hdr, loggers);
            try
            {
                created.Start(interop, monitor, includeCursor);
                return created;
            }
            catch
            {
                created.Dispose();
                throw;
            }
        }, ct).ConfigureAwait(false);

        try
        {
            await stream._firstFrame.Task.WaitAsync(FirstFrameTimeout, ct).ConfigureAwait(false);
            return stream;
        }
        catch (TimeoutException)
        {
            stream.Dispose();
            throw new CaptureException($"Windows sent no picture of {monitor.ShortName} within {FirstFrameTimeout.TotalSeconds:0} s.");
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>The oldest queued picture, without taking it.</summary>
    /// <exception cref="CaptureException">The capture failed (monitor resolution changed, device lost).</exception>
    public bool TryPeek(out Picture picture)
    {
        ThrowIfFailed();
        lock (_pictures)
        {
            picture = _pictures.Count > 0 ? _pictures[0] : null!;
            return picture is not null;
        }
    }

    public bool TryTake(out Picture picture)
    {
        lock (_pictures)
        {
            picture = _pictures.Count > 0 ? _pictures[0] : null!;
            if (picture is not null)
            {
                _pictures.RemoveAt(0);
            }

            return picture is not null;
        }
    }

    /// <summary>Copies a picture into <paramref name="destination"/> as top-down BGRA rows of <c>Width * 4</c> bytes.</summary>
    public void Read(Picture picture, Span<byte> destination)
    {
        lock (_contextGate)
        {
            ThrowIfFailed();
            ReadBack(ToSdr(picture.Texture), destination);
        }
    }

    /// <summary>
    /// Copies a picture with <paramref name="scene"/> drawn over it (by <paramref name="compositor"/>, made on
    /// <see cref="Device"/>) into <paramref name="destination"/>, like <see cref="Read"/>.
    /// </summary>
    public void Compose(Picture picture, OverlayCompositor compositor, OverlayScene scene, WebcamPicture? webcam, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        lock (_contextGate)
        {
            ThrowIfFailed();
            compositor.Compose(_resources!.Context, ToSdr(picture.Texture), scene, webcam);
            ReadBack(compositor.Target, destination);
        }
    }

    /// <summary>A picture as 8-bit BGRA: itself, or tone-mapped into <see cref="_toned"/> when it is float (under the context lock).</summary>
    private ID3D11Texture2D ToSdr(ID3D11Texture2D picture)
    {
        if (_toneMapper is null)
        {
            return picture;
        }

        _toneMapper.Draw(_resources!.Context, picture, _toned!, 0, 0, _hdr!.WhiteScale);
        return _toned!;
    }

    /// <summary>Reads an 8-bit texture of the area's size back as top-down BGRA (under the context lock).</summary>
    private unsafe void ReadBack(ID3D11Texture2D texture, Span<byte> destination)
    {
        var context = _resources!.Context;
        context.CopyResource(_staging!, texture);
        var mapped = context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowBytes = Width * 4;
            var pitch = (int)mapped.RowPitch;
            var source = new ReadOnlySpan<byte>((byte*)mapped.DataPointer, (pitch * (Height - 1)) + rowBytes);
            Core.Imaging.PixelBuffer.CopyRows(source, pitch, destination, rowBytes, rowBytes, Height);
        }
        finally
        {
            context.Unmap(_staging!, 0);
        }
    }

    /// <summary>Gives a picture's texture back for the next frame Windows sends.</summary>
    public void Release(Picture picture) => _free.Add(picture.Texture);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true; // a frame that arrives from now on is ignored, without touching anything below
        }

        // Not under the lock: releasing the capture waits for a frame callback that is running, and that callback
        // may be waiting for the lock (it froze Stop, about once in 20 recordings).
        if (_pool is not null)
        {
            _pool.FrameArrived -= OnFrameArrived;
        }

        _session?.Dispose();
        _pool?.Dispose();
        lock (_pictures)
        {
            _pictures.Clear();
        }

        lock (_textures)
        {
            _textures.ForEach(t => t.Dispose());
            _textures.Clear();
        }

        _staging?.Dispose();
        _toned?.Dispose();
        _toneMapper?.Dispose();
        _devices.Dispose();
    }

    private void ThrowIfFailed()
    {
        if (Volatile.Read(ref _failure) is { } failure)
        {
            throw new CaptureException("Screen capture stopped: " + failure.Message, failure);
        }
    }

    private void Start(WgcInterop interop, MonitorInfo monitor, bool includeCursor)
    {
        _resources = _devices.Acquire();
        var item = interop.CreateItemForMonitor(monitor.Handle);
        _poolSize = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_resources.WinRtDevice, _poolFormat, PoolBuffers, _poolSize);
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = includeCursor;
        _staging = _resources.Device.CreateTexture2D(Description(ResourceUsage.Staging, CpuAccessFlags.Read) with { Format = Format.B8G8R8A8_UNorm });
        if (_hdr is not null)
        {
            _toneMapper = new ToneMapper(_resources.Device);
            _toned = _resources.Device.CreateTexture2D(ToneMapper.TargetDescription(Width, Height));
        }
        _pool.FrameArrived += OnFrameArrived;
        _session.StartCapture();
        LogStarted(monitor.ShortName, _crop, includeCursor, _hdr is null ? "off" : $"tone-mapped, SDR white {_hdr.SdrWhiteNits:0} nits");
    }

    private Texture2DDescription Description(ResourceUsage usage, CpuAccessFlags cpu) => new()
    {
        Width = (uint)Width,
        Height = (uint)Height,
        MipLevels = 1,
        ArraySize = 1,
        Format = _hdr is null ? Format.B8G8R8A8_UNorm : ToneMapper.SourceFormat,
        SampleDescription = new SampleDescription(1, 0),
        Usage = usage,
        BindFlags = _hdr is null || usage == ResourceUsage.Staging ? BindFlags.None : BindFlags.ShaderResource,
        CPUAccessFlags = cpu,
    };

    /// <summary>A free texture for the next picture, a new one while fewer than <see cref="MaxTextures"/> exist, or null.</summary>
    private ID3D11Texture2D? RentTexture()
    {
        if (_free.TryTake(out var texture))
        {
            return texture;
        }

        lock (_textures)
        {
            if (_textures.Count >= MaxTextures)
            {
                return null;
            }

            texture = _resources!.Device.CreateTexture2D(Description(ResourceUsage.Default, CpuAccessFlags.None));
            _textures.Add(texture);
            return texture;
        }
    }

    /// <summary>Runs on a capture worker thread for every frame Windows delivers.</summary>
    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gate)
        {
            if (_disposed || _failure is not null)
            {
                return;
            }

            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame is null)
                {
                    return;
                }

                var size = frame.ContentSize;
                if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
                {
                    // The monitor changed resolution; later frames come at the new size.
                    _poolSize = size;
                    sender.Recreate(_resources!.WinRtDevice, _poolFormat, PoolBuffers, size);
                }

                if (_crop.Right > size.Width || _crop.Bottom > size.Height)
                {
                    throw new CaptureException($"The monitor is now {size.Width}x{size.Height}, smaller than the recorded area.");
                }

                var timestamp = QpcTime.FromHundredNanoseconds(frame.SystemRelativeTime.Ticks, Stopwatch.Frequency);
                if (RentTexture() is not { } target)
                {
                    Interlocked.Increment(ref _skipped); // every texture is queued or in use: the recorder is far behind
                    return;
                }

                using (var texture = WgcInterop.GetTexture(frame.Surface))
                {
                    // Not under the context lock: Windows may hold the device's own lock while it calls here, and the
                    // video thread holds the context lock while it waits for the device (reading back, drawing).
                    // The device is multithread-protected, so this copy is serialized with theirs anyway.
                    _resources!.Context.CopySubresourceRegion(
                        target, 0, 0, 0, 0, texture, 0, new Box(_crop.X, _crop.Y, 0, _crop.Right, _crop.Bottom, 1));
                }

                Queue(new Picture(target, timestamp, _slotOf?.Invoke(timestamp) ?? -1));
                Interlocked.Increment(ref _delivered);
                _firstFrame.TrySetResult();
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _failure, ex);
                _firstFrame.TrySetException(ex);
                LogFailed(ex);
            }
        }
    }

    /// <summary>Adds a picture: it replaces a queued one for the same frame, and the oldest goes when the queue is full.</summary>
    private void Queue(Picture picture)
    {
        Picture? dropped = null;
        lock (_pictures)
        {
            if (_pictures.Count > 0 && picture.Slot >= 0 && _pictures[^1].Slot == picture.Slot)
            {
                dropped = _pictures[^1];
                _pictures[^1] = picture;
            }
            else
            {
                if (_pictures.Count == QueueLength)
                {
                    dropped = _pictures[0];
                    _pictures.RemoveAt(0);
                }

                _pictures.Add(picture);
            }
        }

        if (dropped is not null)
        {
            Release(dropped);
            Interlocked.Increment(ref _skipped);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording capture started on {Monitor}, area {Crop} (cursor {Cursor}, HDR {Hdr})")]
    private partial void LogStarted(string monitor, PixelRect crop, bool cursor, string hdr);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording capture failed")]
    private partial void LogFailed(Exception ex);
}
