using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Interop.Com;
using CashGear.Capture.Interop.Native;
using CashGear.Capture.Overlays.Rendering;
using CashGear.Capture.Recording.Resources;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace CashGear.Capture.Recording;

/// <summary>What a <see cref="WgcFittedStream"/> captures, and where on its canvas that goes.</summary>
/// <param name="Name">For messages and the log.</param>
/// <param name="OpenItem">Makes the capture item (again, after a lost device: a monitor's handle may have changed).</param>
/// <param name="Place">Its place on the canvas: what it sends is fitted into this, its shape kept.</param>
/// <param name="Hdr">Capture as float and tone-map with this white level (a monitor in HDR mode); null captures 8-bit.</param>
/// <param name="Window">The window, when it is one (no pictures are taken of it while it is minimized).</param>
internal sealed record FittedSource(string Name, Func<WgcInterop, GraphicsCaptureItem> OpenItem, PixelRect Place, HdrInfo? Hdr = null, nint Window = 0);

/// <summary>
/// Windows.Graphics.Capture items, each drawn into its place on one canvas of a fixed size, on one D3D11 device: a
/// window (its pictures fitted into the recording's size, whatever size it is now, so moving, covering or resizing it
/// never starts a new part) or several monitors in their layout.
/// <para>
/// The frame callback only copies what Windows sent into a texture of its source and queues that (a single call, safe
/// beside the video thread's drawing, as in <see cref="WgcAreaStream"/>). The video thread draws each picture onto the
/// canvas as it takes it (<see cref="TryTake"/>), in the order they came, so at any frame the canvas shows the newest
/// picture of every source up to that frame's time. A source's textures are as large as its largest picture so far.
/// When a picture doesn't fit, that frame is skipped and the video thread makes larger ones (<see cref="ApplyResize"/>).
/// </para>
/// </summary>
internal sealed partial class WgcFittedStream : IPictureStream
{
    private const int PoolBuffers = 2;
    private const int QueuePerSource = 8;
    private const int MaxTexturesPerSource = QueuePerSource + 4;
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StallCheck = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly object _contextGate = new();
    private readonly WgcInterop _interop;
    private readonly bool _includeCursor;
    private readonly Func<long, long>? _slotOf;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly List<Picture> _pictures = [];
    private readonly Source[] _sources;
    private D3DDeviceManager _devices;
    private D3DResources? _resources;
    private PictureFitter? _fitter;
    private ID3D11Texture2D? _canvas;
    private ID3D11Texture2D? _staging;
    private long _delivered;
    private long _skipped;
    private long _lastPictureAt = Stopwatch.GetTimestamp();
    private Exception? _failure;
    private volatile bool _lost;
    private volatile bool _simulateLoss;
    private bool _paused;
    private bool _disposed;
    private int _generation;

    private WgcFittedStream(WgcInterop interop, IReadOnlyList<FittedSource> sources, int width, int height, PixelRect area, bool includeCursor, Func<long, long>? slotOf, ILoggerFactory loggers)
    {
        _interop = interop;
        _includeCursor = includeCursor;
        _slotOf = slotOf;
        _loggers = loggers;
        _logger = loggers.CreateLogger<WgcFittedStream>();
        _devices = new D3DDeviceManager(loggers.CreateLogger<D3DDeviceManager>());
        _sources = [.. sources.Select((s, i) => new Source(this, s, i))];
        Width = width;
        Height = height;
        Area = area;
    }

    public event EventHandler? Closed;

    public int Width { get; }

    public int Height { get; }

    public PixelRect Area { get; }

    public long Delivered => Interlocked.Read(ref _delivered);

    public long Skipped => Interlocked.Read(ref _skipped);

    public ID3D11Device Device => _resources!.Device;

    public int Generation => Volatile.Read(ref _generation);

    public bool ResizePending
    {
        get
        {
            lock (_gate)
            {
                return _sources.Any(s => s.Larger is not null);
            }
        }
    }

    /// <summary>Starts capturing and waits for the first picture of every source.</summary>
    public static async Task<WgcFittedStream> StartAsync(
        WgcInterop interop, IReadOnlyList<FittedSource> sources, int width, int height, PixelRect area, bool includeCursor, ILoggerFactory loggers, CancellationToken ct, Func<long, long>? slotOf = null)
    {
        // WinRT objects must not be created on the caller's (possibly STA UI) thread.
        var stream = await Task.Run(() =>
        {
            var created = new WgcFittedStream(interop, sources, width, height, area, includeCursor, slotOf, loggers);
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

        foreach (var source in stream._sources)
        {
            try
            {
                await source.First.Task.WaitAsync(FirstFrameTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                stream.Dispose();
                throw new CaptureException(RecordingStrings.NoPictureFromWindows(source.Spec.Name, FirstFrameTimeout.TotalSeconds));
            }
            catch
            {
                stream.Dispose();
                throw;
            }
        }

        return stream;
    }

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

    /// <summary>The oldest queued picture, drawn onto the canvas now.</summary>
    public bool TryTake(out Picture picture)
    {
        lock (_pictures)
        {
            picture = _pictures.Count > 0 ? _pictures[0] : null!;
            if (picture is null)
            {
                return false;
            }

            _pictures.RemoveAt(0);
        }

        lock (_contextGate)
        {
            try
            {
                SimulatedLoss();
                Draw(picture);
            }
            catch (Exception e) when (e is not DeviceLostException && DeviceLostException.IsLost(e))
            {
                throw Lost(e);
            }
        }

        return true;
    }

    /// <summary>Copies the canvas, which holds every picture taken so far, into <paramref name="destination"/>.</summary>
    public void Read(Picture picture, Span<byte> destination)
    {
        lock (_contextGate)
        {
            ThrowIfUnreadable();
            try
            {
                ReadBack(_canvas!, destination);
            }
            catch (Exception e) when (e is not DeviceLostException && DeviceLostException.IsLost(e))
            {
                throw Lost(e);
            }
        }
    }

    public void Compose(Picture picture, OverlayCompositor compositor, OverlayScene scene, WebcamPicture? webcam, Span<byte> destination)
    {
        ArgumentNullException.ThrowIfNull(compositor);
        lock (_contextGate)
        {
            ThrowIfUnreadable();
            try
            {
                compositor.Compose(_resources!.Context, _canvas!, scene, webcam);
                ReadBack(compositor.Target, destination);
            }
            catch (Exception e) when (e is not DeviceLostException && DeviceLostException.IsLost(e))
            {
                throw Lost(e);
            }
        }
    }

    public void Release(Picture picture)
    {
        if (picture.Generation == Generation)
        {
            _sources[picture.Source].Free.Enqueue(picture);
        }
    }

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

            _paused = true;
        }

        StopCapture();
        lock (_contextGate)
        {
            DropPictures();
            DisposeTargets();
            _devices.Dispose();
            _devices = new D3DDeviceManager(_loggers.CreateLogger<D3DDeviceManager>());
            try
            {
                Start();
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or SharpGen.Runtime.SharpGenException or InvalidOperationException or ArgumentException)
            {
                StopCapture();
                DisposeTargets();
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

        LogRecovered(Generation);
        return true;
    }

    /// <summary>A source sent a picture too large for its textures: larger ones. The canvas stays as it is, so nothing else changes.</summary>
    public void ApplyResize()
    {
        lock (_gate)
        {
            if (!_sources.Any(s => s.Larger is not null))
            {
                return;
            }

            _paused = true;
        }

        lock (_contextGate)
        {
            DropPictures();
            lock (_gate)
            {
                foreach (var source in _sources)
                {
                    if (source.Larger is { } larger)
                    {
                        source.Capacity = larger;
                        source.Larger = null;
                    }
                }
            }

            Interlocked.Increment(ref _generation);
        }

        lock (_gate)
        {
            _paused = false;
        }
    }

    /// <summary>A window moves, so where a place on the screen is on the picture does too: the first source's window, as it is now.</summary>
    public unsafe bool TryMovingMap(out PointMap map)
    {
        map = default;
        var source = _sources[0];
        if (source.Spec.Window == 0)
        {
            return false;
        }

        Vortice.RawRect bounds = default;
        if (Dwmapi.DwmGetWindowAttribute(source.Spec.Window, Dwmapi.DwmwaExtendedFrameBounds, &bounds, (uint)sizeof(Vortice.RawRect)) != 0)
        {
            return false;
        }

        var window = new PixelRect(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top);
        var content = Volatile.Read(ref source.LastContent);
        var (width, height) = content == 0 ? (window.Width, window.Height) : ((int)(content >> 32), (int)content);
        map = LetterboxFit.Map(window, LetterboxFit.Fit(width, height, new PixelRect(0, 0, Width, Height)));
        return true;
    }

    /// <summary>Tests: the next picture taken fails as if the graphics device had been removed.</summary>
    internal void SimulateDeviceLoss() => _simulateLoss = true;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        StopCapture();
        lock (_contextGate)
        {
            DropPictures();
            DisposeTargets();
            _devices.Dispose();
        }
    }

    private void ThrowIfFailed()
    {
        if (Volatile.Read(ref _failure) is { } failure)
        {
            throw failure as CaptureException ?? new CaptureException(RecordingStrings.CaptureStopped(failure.Message), failure);
        }
    }

    private void ThrowIfUnreadable()
    {
        ThrowIfFailed();
        if (_lost)
        {
            throw new DeviceLostException(null);
        }
    }

    private void SimulatedLoss()
    {
        if (_simulateLoss)
        {
            _simulateLoss = false;
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

    /// <summary>A picture onto its place on the canvas (under the context lock). One that fills its place exactly is copied, every pixel as it came.</summary>
    private void Draw(Picture picture)
    {
        var context = _resources!.Context;
        var source = _sources[picture.Source];
        var place = source.Spec.Place;
        var fit = LetterboxFit.Fit(picture.ContentWidth, picture.ContentHeight, place);
        if (fit != source.DrawnAt)
        {
            if (fit != place)
            {
                _fitter!.Clear(context, _canvas!, place); // the bars
            }

            source.DrawnAt = fit;
        }

        if (source.Spec.Hdr is null && fit.Width == picture.ContentWidth && fit.Height == picture.ContentHeight)
        {
            context.CopySubresourceRegion(_canvas!, 0, (uint)fit.X, (uint)fit.Y, 0, picture.Texture, 0, new Box(0, 0, 0, picture.ContentWidth, picture.ContentHeight, 1));
        }
        else
        {
            _fitter!.Draw(context, picture.Texture, picture.ContentWidth, picture.ContentHeight, _canvas!, fit, source.Spec.Hdr?.WhiteScale ?? 0);
        }
    }

    private unsafe void ReadBack(ID3D11Texture2D texture, Span<byte> destination)
    {
        var context = _resources!.Context;
        context.CopyResource(_staging!, texture);
        var mapped = context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowBytes = Width * 4;
            var pitch = (int)mapped.RowPitch;
            var bytes = new ReadOnlySpan<byte>((byte*)mapped.DataPointer, (pitch * (Height - 1)) + rowBytes);
            Core.Imaging.PixelBuffer.CopyRows(bytes, pitch, destination, rowBytes, rowBytes, Height);
        }
        finally
        {
            context.Unmap(_staging!, 0);
        }
    }

    private void Start()
    {
        _resources = _devices.Acquire();
        _fitter = new PictureFitter(_resources.Device);
        _canvas = _resources.Device.CreateTexture2D(ToneMapper.TargetDescription(Width, Height));
        _staging = _resources.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        foreach (var source in _sources)
        {
            source.Start(_interop, _resources, _includeCursor);
        }

        LogStarted(string.Join(", ", _sources.Select(s => s.Spec.Name)), Width, Height, _includeCursor);
    }

    private void StopCapture()
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            Task.Run(StopCapture).GetAwaiter().GetResult();
            return;
        }

        foreach (var source in _sources)
        {
            source.Stop();
        }
    }

    private void DropPictures()
    {
        lock (_pictures)
        {
            _pictures.Clear();
        }

        foreach (var source in _sources)
        {
            List<Picture> textures;
            lock (_gate)
            {
                textures = source.Textures;
                source.Textures = [];
            }

            foreach (var picture in textures)
            {
                _fitter?.Forget(picture.Texture);
                picture.Texture.Dispose();
            }

            while (source.Free.TryDequeue(out _))
            {
            }
        }
    }

    private void DisposeTargets()
    {
        _staging?.Dispose();
        _staging = null;
        if (_canvas is not null)
        {
            _fitter?.Forget(_canvas);
            _canvas.Dispose();
            _canvas = null;
        }

        _fitter?.Dispose();
        _fitter = null;
        foreach (var source in _sources)
        {
            source.DrawnAt = default;
        }
    }

    private void Deliver(Source source, Picture target, int width, int height, long timestamp)
    {
        target.SetContent(width, height, source.Index);
        Queue(target.Set(timestamp, _slotOf?.Invoke(timestamp) ?? -1, Generation));
        Interlocked.Increment(ref _delivered);
        Interlocked.Exchange(ref _lastPictureAt, Stopwatch.GetTimestamp());
        Volatile.Write(ref source.LastContent, ((long)width << 32) | (uint)height);
        source.First.TrySetResult();
    }

    /// <summary>Adds a picture: it replaces its source's queued one for the same frame, and the oldest goes when the queue is full.</summary>
    private void Queue(Picture picture)
    {
        Picture? dropped = null;
        lock (_pictures)
        {
            var replaced = false;
            if (picture.Slot >= 0)
            {
                for (var i = _pictures.Count - 1; i >= 0; i--)
                {
                    if (_pictures[i].Source == picture.Source)
                    {
                        if (_pictures[i].Slot == picture.Slot)
                        {
                            dropped = _pictures[i];
                            _pictures[i] = picture;
                            replaced = true;
                        }

                        break;
                    }
                }
            }

            if (!replaced)
            {
                if (_pictures.Count == QueuePerSource * _sources.Length)
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording capture started on {Sources}, {Width}x{Height} (cursor {Cursor})")]
    private partial void LogStarted(string sources, int width, int height, bool cursor);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording capture failed")]
    private partial void LogFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording capture: the graphics device was lost")]
    private partial void LogDeviceLost(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording capture rebuilt after the device was lost (generation {Generation})")]
    private partial void LogRecovered(int generation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording capture: rebuilding after the device was lost failed (will try again)")]
    private partial void LogRecoverFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording capture: {Source} went away")]
    private partial void LogClosed([Localizable(false)] string source);

    /// <summary>One capture item: its session, its pool and its textures.</summary>
    private sealed class Source(WgcFittedStream owner, FittedSource spec, int index)
    {
        private GraphicsCaptureItem? _item;
        private Direct3D11CaptureFramePool? _pool;
        private GraphicsCaptureSession? _session;
        private nint _poolAbi;
        private bool _abiChecked;
        private bool _abiWorks;
        private SizeInt32 _poolSize;
        private DirectXPixelFormat _format;
        private D3DResources? _resources;

        public FittedSource Spec { get; } = spec;

        public int Index { get; } = index;

        public TaskCompletionSource First { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<Picture> Free { get; } = new();

        public List<Picture> Textures { get; set; } = [];

        /// <summary>How large its textures are.</summary>
        public (int Width, int Height) Capacity { get; set; }

        /// <summary>How large they have to become before the next picture fits.</summary>
        public (int Width, int Height)? Larger { get; set; }

        /// <summary>Where its picture was last drawn, to know when the bars change.</summary>
        public PixelRect DrawnAt { get; set; }

        /// <summary>The last picture's size, width in the high half (read from the video thread).</summary>
        public long LastContent;

        public void Start(WgcInterop interop, D3DResources resources, bool includeCursor)
        {
            _resources = resources;
            _format = Spec.Hdr is null ? DirectXPixelFormat.B8G8R8A8UIntNormalized : DirectXPixelFormat.R16G16B16A16Float;
            _item = Spec.OpenItem(interop);
            _item.Closed += OnItemClosed;
            _poolSize = _item.Size;
            Capacity = (Math.Max(Capacity.Width, _poolSize.Width), Math.Max(Capacity.Height, _poolSize.Height));
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(resources.WinRtDevice, _format, PoolBuffers, _poolSize);
            _poolAbi = ((WinRT.IWinRTObject)_pool).NativeObject.ThisPtr;
            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = includeCursor;
            _pool.FrameArrived += OnFrameArrived;
            _session.StartCapture();
        }

        public void Stop()
        {
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

        private void OnItemClosed(GraphicsCaptureItem sender, object args)
        {
            owner.LogClosed(Spec.Name);
            owner.Closed?.Invoke(owner, EventArgs.Empty);
        }

        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            lock (owner._gate)
            {
                if (owner._disposed || owner._paused || owner._lost || owner._failure is not null)
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
                    owner.Lost(ex);
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref owner._failure, ex);
                    First.TrySetException(ex);
                    owner.LogFailed(ex);
                }
            }
        }

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
                if (Fits(width, height) && Rent(width, height) is { } target)
                {
                    var texture = CaptureFrameAbi.Texture(frame, InteropGuids.ID3D11Texture2D);
                    try
                    {
                        CaptureFrameAbi.CopySubresourceRegion(_resources!.Context.NativePointer, target.Texture.NativePointer, texture, 0, 0, width, height);
                    }
                    finally
                    {
                        CaptureFrameAbi.Release(texture);
                    }

                    owner.Deliver(this, target, width, height, QpcTime.FromHundredNanoseconds(ticks, Stopwatch.Frequency));
                }

                FollowSize(sender, width, height);
            }
            finally
            {
                CaptureFrameAbi.CloseAndRelease(frame);
            }
        }

        private void OnFrameProjected(Direct3D11CaptureFramePool sender)
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null)
            {
                return;
            }

            var (width, height) = (frame.ContentSize.Width, frame.ContentSize.Height);
            if (!_abiChecked)
            {
                _abiChecked = true;
                var native = ((WinRT.IWinRTObject)frame).NativeObject.ThisPtr;
                _abiWorks = _poolAbi != 0
                    && CaptureFrameAbi.ContentSize(native) == (width, height)
                    && CaptureFrameAbi.SystemRelativeTime(native) == frame.SystemRelativeTime.Ticks;
            }

            if (Fits(width, height) && Rent(width, height) is { } target)
            {
                using (var texture = WgcInterop.GetTexture(frame.Surface))
                {
                    _resources!.Context.CopySubresourceRegion(target.Texture, 0, 0, 0, 0, texture, 0, new Box(0, 0, 0, width, height, 1));
                }

                owner.Deliver(this, target, width, height, QpcTime.FromHundredNanoseconds(frame.SystemRelativeTime.Ticks, Stopwatch.Frequency));
            }

            FollowSize(sender, width, height);
        }

        /// <summary>
        /// Whether this picture can be used: not when there is nothing to show (a minimized window sends odd sizes), or
        /// when it is larger than the pool it came from or the textures it has to go in, which the video thread then
        /// makes larger.
        /// </summary>
        private bool Fits(int width, int height)
        {
            if (width < 2 || height < 2 || (Spec.Window != 0 && User32.IsIconic(Spec.Window)))
            {
                return false;
            }

            if (width > Capacity.Width || height > Capacity.Height)
            {
                // A little more than it needs: a window being dragged larger would ask again for every picture.
                Larger = (Math.Max(Capacity.Width, width + (width / 4)), Math.Max(Capacity.Height, height + (height / 4)));
                return false;
            }

            return width <= _poolSize.Width && height <= _poolSize.Height && Larger is null;
        }

        /// <summary>The pool is made again at the picture's size, once that picture has been copied.</summary>
        private void FollowSize(Direct3D11CaptureFramePool sender, int width, int height)
        {
            if (width >= 2 && height >= 2 && (width != _poolSize.Width || height != _poolSize.Height))
            {
                _poolSize = new SizeInt32 { Width = width, Height = height };
                sender.Recreate(_resources!.WinRtDevice, _format, PoolBuffers, _poolSize);
            }
        }

        /// <summary>A free picture for the next frame, a new one while there are few, or null (under the gate).</summary>
        private Picture? Rent(int width, int height)
        {
            if (Free.TryDequeue(out var picture))
            {
                return picture;
            }

            if (Textures.Count >= MaxTexturesPerSource)
            {
                Interlocked.Increment(ref owner._skipped);
                return null;
            }

            picture = new Picture(_resources!.Device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)Math.Max(width, Capacity.Width),
                Height = (uint)Math.Max(height, Capacity.Height),
                MipLevels = 1,
                ArraySize = 1,
                Format = Spec.Hdr is null ? Format.B8G8R8A8_UNorm : ToneMapper.SourceFormat,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Default,
                BindFlags = BindFlags.ShaderResource,
            }));
            Textures.Add(picture);
            return picture;
        }
    }
}
