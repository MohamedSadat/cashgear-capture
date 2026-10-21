using System.Collections.Concurrent;
using System.Diagnostics;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Interop.Com;
using CashGear.Capture.Overlays.Rendering;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace CashGear.Capture.Recording;

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
/// <para>
/// Faults: a lost graphics device (a driver reset, a GPU switch) makes reads throw <see cref="DeviceLostException"/>
/// until <see cref="Recover"/> builds everything again; a resolution change that changes the area waits for
/// <see cref="ApplyResize"/>; a monitor that goes away raises <see cref="Closed"/>.
/// </para>
/// </summary>
internal sealed partial class WgcAreaStream : IPictureStream
{
    private const int PoolBuffers = 2;
    private const int QueueLength = 8;
    private const int MaxTextures = QueueLength + 4;
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How long without a picture before the watchdog asks the device whether it was removed.</summary>
    private static readonly TimeSpan StallCheck = TimeSpan.FromSeconds(1);

    /// <summary>Held by the frame callback; never held while calling the device (Windows may hold the device's lock while it calls us).</summary>
    private readonly object _gate = new();

    /// <summary>Serializes reading back and drawing overlays (the capture thread's copies rely on the device's multithread protection).</summary>
    private readonly object _contextGate = new();
    private readonly WgcInterop _interop;
    private readonly bool _includeCursor;
    private readonly bool _wholeMonitor;
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<Picture> _pictures = [];
    private readonly Func<long, long>? _slotOf;
    private readonly ConcurrentQueue<Picture> _free = new();
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly HdrInfo? _hdr;
    private readonly DirectXPixelFormat _poolFormat;

    private List<Picture> _textures = [];
    private MonitorInfo _monitor;
    private D3DDeviceManager _devices;
    private PixelRect _crop;
    private PixelRect? _pendingCrop;
    private D3DResources? _resources;
    private ToneMapper? _toneMapper;
    private ID3D11Texture2D? _toned;
    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private nint _poolAbi;
    private bool _abiChecked;
    private bool _abiWorks;
    private long _callbackAllocated;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _staging;
    private SizeInt32 _poolSize;
    private SizeInt32 _monitorSize;
    private long _delivered;
    private long _skipped;
    private long _lastPictureAt = Stopwatch.GetTimestamp();
    private Exception? _failure;
    private volatile bool _lost;
    private volatile bool _simulateLoss;
    private int _generation;
    private bool _paused;
    private bool _disposed;

    private WgcAreaStream(WgcInterop interop, MonitorInfo monitor, PixelRect area, bool includeCursor, Func<long, long>? slotOf, HdrInfo? hdr, ILoggerFactory loggers)
    {
        _interop = interop;
        _monitor = monitor;
        _includeCursor = includeCursor;
        _wholeMonitor = area == monitor.Bounds;
        _slotOf = slotOf;
        _hdr = hdr;
        _poolFormat = hdr is null ? DirectXPixelFormat.B8G8R8A8UIntNormalized : DirectXPixelFormat.R16G16B16A16Float;
        _loggers = loggers;
        _devices = new D3DDeviceManager(loggers.CreateLogger<D3DDeviceManager>());
        _logger = loggers.CreateLogger<WgcAreaStream>();
        _crop = new PixelRect(area.X - monitor.Bounds.X, area.Y - monitor.Bounds.Y, area.Width, area.Height);
        Width = area.Width;
        Height = area.Height;
    }

    public event EventHandler? Closed;

    public int Width { get; private set; }

    public int Height { get; private set; }

    public PixelRect Area => new(_monitor.Bounds.X + _crop.X, _monitor.Bounds.Y + _crop.Y, _crop.Width, _crop.Height);

    /// <summary>Pictures Windows delivered so far.</summary>
    public long Delivered => Interlocked.Read(ref _delivered);

    /// <summary>Pictures replaced by a newer one for the same frame, or dropped because the recorder was far behind.</summary>
    public long Skipped => Interlocked.Read(ref _skipped);

    /// <summary>The stream's own D3D11 device (overlays are drawn with it).</summary>
    public ID3D11Device Device => _resources!.Device;

    public int Generation => Volatile.Read(ref _generation);

    /// <summary>Bytes the frame callbacks allocated in all (tests: none per picture).</summary>
    internal long CallbackAllocatedBytes => Interlocked.Read(ref _callbackAllocated);

    /// <summary>Frames are read through the WinRT ABI directly (checked against the projection on the first frame).</summary>
    internal bool UsesAbi => _abiWorks;

    public bool ResizePending
    {
        get
        {
            lock (_gate)
            {
                return _pendingCrop is not null;
            }
        }
    }

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
            var created = new WgcAreaStream(interop, monitor, area, includeCursor, slotOf, hdr, loggers);
            try
            {
                created.Start();
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
    /// <exception cref="CaptureException">The capture failed (the area isn't on the monitor any more).</exception>
    /// <exception cref="DeviceLostException">The device was lost: <see cref="Recover"/>.</exception>
    public bool TryPeek(out Picture picture)
    {
        ThrowIfFailed();
        if (_lost)
        {
            throw new DeviceLostException(null);
        }

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
            ThrowIfUnreadable(picture);
            try
            {
                SimulatedLoss();
                ReadBack(ToSdr(picture.Texture), destination);
            }
            catch (Exception e) when (e is not DeviceLostException && DeviceLostException.IsLost(e))
            {
                throw Lost(e);
            }
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
            ThrowIfUnreadable(picture);
            try
            {
                SimulatedLoss();
                compositor.Compose(_resources!.Context, ToSdr(picture.Texture), scene, webcam);
                ReadBack(compositor.Target, destination);
            }
            catch (Exception e) when (e is not DeviceLostException && DeviceLostException.IsLost(e))
            {
                throw Lost(e);
            }
        }
    }

    /// <summary>Gives a picture's texture back for the next frame Windows sends (one from before a rebuild or resize is forgotten).</summary>
    public void Release(Picture picture)
    {
        if (picture.Generation == Generation)
        {
            _free.Enqueue(picture);
        }
    }

    /// <summary>Tests: the next read fails as if the graphics device had been removed, and the device is dropped.</summary>
    internal void SimulateDeviceLoss() => _simulateLoss = true;

    public void CheckDevice()
    {
        if (_lost || Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastPictureAt)) < StallCheck)
        {
            return;
        }

        lock (_contextGate)
        {
            if (_resources is { } resources && resources.Device.DeviceRemovedReason.Failure)
            {
                Lost(new SharpGen.Runtime.SharpGenException(resources.Device.DeviceRemovedReason));
            }
        }
    }

    public bool Recover()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return false;
            }

            _paused = true; // frames arriving from now on are ignored; none is being handled now
        }

        StopCapture();
        lock (_contextGate)
        {
            DropPictures();
            DisposeViews();
            _devices.Dispose();
            _devices = new D3DDeviceManager(_loggers.CreateLogger<D3DDeviceManager>());
            _monitor = FindMonitor(_monitor) ?? _monitor;
            try
            {
                Start();
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or SharpGen.Runtime.SharpGenException or InvalidOperationException or ArgumentException)
            {
                // The graphics card isn't back yet (a reset takes a few seconds): the caller tries again.
                StopCapture();
                DisposeViews();
                LogRecoverFailed(e);
                return false;
            }

            Interlocked.Increment(ref _generation);
            _lost = false;
        }

        lock (_gate)
        {
            _paused = false;
        }

        LogRecovered(_monitor.ShortName, Generation);
        return true;
    }

    public void ApplyResize()
    {
        PixelRect crop;
        lock (_gate)
        {
            if (_pendingCrop is not { } pending)
            {
                return;
            }

            crop = pending;
            _pendingCrop = null;
            _paused = true;
        }

        // The textures, the staging texture and the tone mapper's target are of the old size.
        lock (_contextGate)
        {
            DropPictures();
            _staging?.Dispose();
            if (_toned is not null)
            {
                _toneMapper?.Forget(_toned);
                _toned.Dispose();
            }

            _toned = null;
            _monitor = FindMonitor(_monitor) ?? _monitor;
            _crop = crop;
            Width = crop.Width;
            Height = crop.Height;
            CreateTargets();
            Interlocked.Increment(ref _generation);
        }

        lock (_gate)
        {
            _paused = false;
        }

        LogResized(_monitor.ShortName, _monitorSize.Width, _monitorSize.Height, _crop);
    }

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
        StopCapture();
        lock (_contextGate)
        {
            DropPictures();
            DisposeViews();
            _devices.Dispose();
        }
    }

    private void ThrowIfFailed()
    {
        if (Volatile.Read(ref _failure) is { } failure)
        {
            throw failure as CaptureException ?? new CaptureException("Screen capture stopped: " + failure.Message, failure);
        }
    }

    private void ThrowIfUnreadable(Picture picture)
    {
        ThrowIfFailed();
        if (_lost)
        {
            throw new DeviceLostException(null);
        }

        if (picture.Generation != Generation)
        {
            throw new InvalidOperationException("A picture from before the stream was rebuilt can't be read.");
        }
    }

    private void SimulatedLoss()
    {
        if (_simulateLoss)
        {
            _simulateLoss = false;
            _resources?.Dispose(); // as gone as a removed device's
            _resources = null;
            throw new SharpGen.Runtime.SharpGenException(new SharpGen.Runtime.Result(unchecked((int)0x887A0005)));
        }
    }

    private DeviceLostException Lost(Exception e)
    {
        if (!_lost)
        {
            _lost = true;
            LogDeviceLost(e);
        }

        return e as DeviceLostException ?? new DeviceLostException(e);
    }

    /// <summary>The monitor again (by its device name): its handle and bounds change with the display layout.</summary>
    private static MonitorInfo? FindMonitor(MonitorInfo monitor) =>
        new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors()
            .FirstOrDefault(m => string.Equals(m.DeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase));

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

    /// <summary>Makes the device, the capture item, the frame pool and the session, and starts capturing.</summary>
    private void Start()
    {
        _resources = _devices.Acquire();
        _item = _interop.CreateItemForMonitor(_monitor.Handle);
        _item.Closed += OnItemClosed;
        _poolSize = _item.Size;
        _monitorSize = _item.Size;
        if (ResizedArea.Crop(_crop, _monitorSize.Width, _monitorSize.Height, _wholeMonitor) is { } crop && crop != _crop)
        {
            _pendingCrop = crop; // the monitor changed while the device was being made again
        }

        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_resources.WinRtDevice, _poolFormat, PoolBuffers, _poolSize);
        _poolAbi = ((WinRT.IWinRTObject)_pool).NativeObject.ThisPtr;
        _session = _pool.CreateCaptureSession(_item);
        _session.IsCursorCaptureEnabled = _includeCursor;
        CreateTargets();
        _pool.FrameArrived += OnFrameArrived;
        _session.StartCapture();
        LogStarted(_monitor.ShortName, _crop, _includeCursor, _hdr is null ? "off" : $"tone-mapped, SDR white {_hdr.SdrWhiteNits:0} nits");
    }

    /// <summary>The staging texture (and, for HDR, the tone mapper and its target) at the area's size.</summary>
    private void CreateTargets()
    {
        _staging = _resources!.Device.CreateTexture2D(Description(ResourceUsage.Staging, CpuAccessFlags.Read) with { Format = Format.B8G8R8A8_UNorm });
        if (_hdr is not null)
        {
            _toneMapper ??= new ToneMapper(_resources.Device);
            _toned = _resources.Device.CreateTexture2D(ToneMapper.TargetDescription(Width, Height));
        }
    }

    /// <summary>Ends the capture session (not under a lock; see <see cref="Dispose"/>).</summary>
    private void StopCapture()
    {
        // The capture's WinRT objects were made on the thread pool (MTA); an STA thread (a window's, a test's) is
        // refused when it closes them ("marshalled for a different thread"), so it closes them from the pool.
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            Task.Run(StopCapture).GetAwaiter().GetResult();
            return;
        }

        if (_pool is not null)
        {
            _pool.FrameArrived -= OnFrameArrived;
        }

        if (_item is not null)
        {
            _item.Closed -= OnItemClosed;
        }

        _session?.Dispose();
        _pool?.Dispose();
        _session = null;
        _pool = null;
        _poolAbi = 0;
        _item = null;
    }

    /// <summary>Drops the queued pictures and releases every picture texture (under the context lock).</summary>
    private void DropPictures()
    {
        lock (_pictures)
        {
            _pictures.Clear();
        }

        List<Picture> textures;
        lock (_gate)
        {
            textures = _textures;
            _textures = [];
        }

        ReleaseTextures(textures);
    }

    private void ReleaseTextures(List<Picture> pictures)
    {
        foreach (var picture in pictures)
        {
            _toneMapper?.Forget(picture.Texture);
            picture.Texture.Dispose();
        }

        while (_free.TryDequeue(out _))
        {
        }
    }

    private void DisposeViews()
    {
        _staging?.Dispose();
        _staging = null;
        _toned?.Dispose();
        _toned = null;
        _toneMapper?.Dispose();
        _toneMapper = null;
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

    /// <summary>A free picture (with its texture) for the next frame, a new one while fewer than <see cref="MaxTextures"/> exist, or null (under <see cref="_gate"/>).</summary>
    private Picture? RentPicture()
    {
        if (_free.TryDequeue(out var picture))
        {
            return picture;
        }

        if (_textures.Count >= MaxTextures)
        {
            return null;
        }

        picture = new Picture(_resources!.Device.CreateTexture2D(Description(ResourceUsage.Default, CpuAccessFlags.None)));
        _textures.Add(picture);
        return picture;
    }

    private void OnItemClosed(GraphicsCaptureItem sender, object args)
    {
        LogClosed(_monitor.ShortName);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Runs on a capture worker thread for every frame Windows delivers. After the first frame (read through the
    /// projection and checked against the ABI) frames are read through the WinRT ABI, which allocates nothing.
    /// </summary>
    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        var before = GC.GetAllocatedBytesForCurrentThread();
        lock (_gate)
        {
            if (_disposed || _paused || _lost || _failure is not null)
            {
                return;
            }

            try
            {
                if (_abiWorks)
                {
                    OnFrame(sender);
                }
                else
                {
                    OnFrameProjected(sender);
                }
            }
            catch (Exception ex) when (DeviceLostException.IsLost(ex))
            {
                Lost(ex); // the recorder rebuilds the stream; until then frames are ignored
            }
            catch (Exception ex)
            {
                Volatile.Write(ref _failure, ex);
                _firstFrame.TrySetException(ex);
                LogFailed(ex);
            }
        }

        Interlocked.Add(ref _callbackAllocated, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>A frame through the ABI: no wrapper objects.</summary>
    private unsafe void OnFrame(Direct3D11CaptureFramePool sender)
    {
        var frame = CaptureFrameAbi.TryGetNextFrame(_poolAbi);
        if (frame == 0)
        {
            return;
        }

        try
        {
            var (width, height) = CaptureFrameAbi.ContentSize(frame);
            var ticks = CaptureFrameAbi.SystemRelativeTime(frame);
            if (!Sized(sender, new SizeInt32 { Width = width, Height = height }))
            {
                return;
            }

            if (RentPicture() is not { } target)
            {
                Interlocked.Increment(ref _skipped); // every texture is queued or in use: the recorder is far behind
                return;
            }

            var texture = CaptureFrameAbi.Texture(frame, InteropGuids.ID3D11Texture2D);
            try
            {
                CaptureFrameAbi.CopySubresourceRegion(_resources!.Context.NativePointer, target.Texture.NativePointer, texture, _crop.X, _crop.Y, _crop.Right, _crop.Bottom);
            }
            finally
            {
                CaptureFrameAbi.Release(texture);
            }

            Deliver(target, QpcTime.FromHundredNanoseconds(ticks, Stopwatch.Frequency));
        }
        finally
        {
            CaptureFrameAbi.CloseAndRelease(frame);
        }
    }

    /// <summary>A frame through the projection (the first, and every one if the ABI check failed).</summary>
    private void OnFrameProjected(Direct3D11CaptureFramePool sender)
    {
        using var frame = sender.TryGetNextFrame();
        if (frame is null)
        {
            return;
        }

        if (!_abiChecked)
        {
            // The ABI must read this very frame as the projection does before the recorder relies on it.
            _abiChecked = true;
            var native = ((WinRT.IWinRTObject)frame).NativeObject.ThisPtr;
            var size = CaptureFrameAbi.ContentSize(native);
            _abiWorks = _poolAbi != 0
                && size == (frame.ContentSize.Width, frame.ContentSize.Height)
                && CaptureFrameAbi.SystemRelativeTime(native) == frame.SystemRelativeTime.Ticks;
            LogAbi(_abiWorks);
        }

        if (!Sized(sender, frame.ContentSize))
        {
            return;
        }

        var timestamp = QpcTime.FromHundredNanoseconds(frame.SystemRelativeTime.Ticks, Stopwatch.Frequency);
        if (RentPicture() is not { } target)
        {
            Interlocked.Increment(ref _skipped);
            return;
        }

        using (var texture = WgcInterop.GetTexture(frame.Surface))
        {
            // Not under the context lock: Windows may hold the device's own lock while it calls here, and the
            // video thread holds the context lock while it waits for the device (reading back, drawing).
            // The device is multithread-protected, so this copy is serialized with theirs anyway.
            _resources!.Context.CopySubresourceRegion(
                target.Texture, 0, 0, 0, 0, texture, 0, new Box(_crop.X, _crop.Y, 0, _crop.Right, _crop.Bottom, 1));
        }

        Deliver(target, timestamp);
    }

    private void Deliver(Picture target, long timestamp)
    {
        Queue(target.Set(timestamp, _slotOf?.Invoke(timestamp) ?? -1, Generation));
        Interlocked.Increment(ref _delivered);
        Interlocked.Exchange(ref _lastPictureAt, Stopwatch.GetTimestamp());
        _firstFrame.TrySetResult();
    }

    /// <summary>Follows the monitor's size; false while the recorder has yet to move to a new area size (the frame is skipped).</summary>
    private bool Sized(Direct3D11CaptureFramePool sender, SizeInt32 size)
    {
        if (size.Width != _poolSize.Width || size.Height != _poolSize.Height)
        {
            // The monitor changed resolution; later frames come at the new size.
            _poolSize = size;
            sender.Recreate(_resources!.WinRtDevice, _poolFormat, PoolBuffers, size);
        }

        if (size.Width != _monitorSize.Width || size.Height != _monitorSize.Height)
        {
            _monitorSize = size;
            var next = ResizedArea.Crop(_crop, size.Width, size.Height, _wholeMonitor)
                ?? throw new CaptureException($"{_monitor.ShortName} is now {size.Width}x{size.Height}, and the recorded area isn't on it any more, so the recording stopped.");
            _pendingCrop = next == _crop ? null : next;
            if (_pendingCrop is not null)
            {
                LogResizing(_monitor.ShortName, size.Width, size.Height, next);
            }
        }

        return _pendingCrop is null; // else the recorder moves to the new size first
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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording capture: the graphics device was lost")]
    private partial void LogDeviceLost(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording capture rebuilt on {Monitor} after the device was lost (generation {Generation})")]
    private partial void LogRecovered(string monitor, int generation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording capture: rebuilding after the device was lost failed (will try again)")]
    private partial void LogRecoverFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording capture: {Monitor} is now {Width}x{Height}; the area becomes {Crop}")]
    private partial void LogResizing(string monitor, int width, int height, PixelRect crop);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording capture moved to {Monitor}'s new size {Width}x{Height}, area {Crop}")]
    private partial void LogResized(string monitor, int width, int height, PixelRect crop);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording capture: {Monitor} went away")]
    private partial void LogClosed(string monitor);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording capture reads frames through the WinRT ABI: {Works}")]
    private partial void LogAbi(bool works);
}
