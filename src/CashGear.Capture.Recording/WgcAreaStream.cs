using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Engine.Capture.Wgc;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace CashGear.Capture.Recording;

/// <summary>
/// A running Windows.Graphics.Capture session on one monitor that keeps the latest picture of an area of it.
/// Each new frame (Windows sends one whenever the screen changes) is cropped on the GPU, read back through a
/// staging texture the size of the area, and stored; the recorder copies out the latest whenever it writes a
/// frame. Uses its own D3D11 device, so screenshots taken meanwhile don't wait for it.
/// </summary>
internal sealed partial class WgcAreaStream : IDisposable
{
    private const int PoolBuffers = 2;
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(5);

    private readonly object _gate = new();
    private readonly D3DDeviceManager _devices;
    private readonly PixelRect _crop;
    private readonly byte[] _latest;
    private readonly TaskCompletionSource _firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ILogger _logger;

    private D3DResources? _resources;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _staging;
    private SizeInt32 _poolSize;
    private long _sequence;
    private Exception? _failure;
    private bool _disposed;

    private WgcAreaStream(MonitorInfo monitor, PixelRect area, ILoggerFactory loggers)
    {
        _devices = new D3DDeviceManager(loggers.CreateLogger<D3DDeviceManager>());
        _logger = loggers.CreateLogger<WgcAreaStream>();
        _crop = new PixelRect(area.X - monitor.Bounds.X, area.Y - monitor.Bounds.Y, area.Width, area.Height);
        _latest = new byte[area.Width * area.Height * 4];
        Width = area.Width;
        Height = area.Height;
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>Starts capturing <paramref name="area"/> of <paramref name="monitor"/> and waits for the first frame.</summary>
    public static async Task<WgcAreaStream> StartAsync(
        WgcInterop interop, MonitorInfo monitor, PixelRect area, bool includeCursor, ILoggerFactory loggers, CancellationToken ct)
    {
        if (!monitor.Bounds.Contains(area))
        {
            throw new ArgumentException($"{area} is not inside monitor {monitor.ShortName} {monitor.Bounds}.", nameof(area));
        }

        // WinRT objects must not be created on the caller's (possibly STA UI) thread.
        var stream = await Task.Run(() =>
        {
            var created = new WgcAreaStream(monitor, area, loggers);
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

    /// <summary>Copies the latest picture into <paramref name="destination"/> (top-down BGRA); returns its sequence number.</summary>
    /// <exception cref="CaptureException">The capture failed (monitor resolution changed, device lost).</exception>
    public long CopyLatest(Span<byte> destination)
    {
        lock (_gate)
        {
            if (_failure is { } failure)
            {
                throw new CaptureException("Screen capture stopped: " + failure.Message, failure);
            }

            _latest.CopyTo(destination);
            return _sequence;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_pool is not null)
            {
                _pool.FrameArrived -= OnFrameArrived;
            }

            _session?.Dispose();
            _pool?.Dispose();
            _staging?.Dispose();
            _devices.Dispose();
        }
    }

    private void Start(WgcInterop interop, MonitorInfo monitor, bool includeCursor)
    {
        _resources = _devices.Acquire();
        var item = interop.CreateItemForMonitor(monitor.Handle);
        _poolSize = item.Size;
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            _resources.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, _poolSize);
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = includeCursor;
        _staging = _resources.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)Width,
            Height = (uint)Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
        });
        _pool.FrameArrived += OnFrameArrived;
        _session.StartCapture();
        LogStarted(monitor.ShortName, _crop, includeCursor);
    }

    /// <summary>Runs on a capture worker thread for every frame Windows delivers.</summary>
    private unsafe void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
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
                    sender.Recreate(_resources!.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, PoolBuffers, size);
                }

                if (_crop.Right > size.Width || _crop.Bottom > size.Height)
                {
                    throw new CaptureException($"The monitor is now {size.Width}x{size.Height}, smaller than the recorded area.");
                }

                using var texture = WgcInterop.GetTexture(frame.Surface);
                var context = _resources!.Context;
                context.CopySubresourceRegion(
                    _staging!, 0, 0, 0, 0, texture, 0, new Box(_crop.X, _crop.Y, 0, _crop.Right, _crop.Bottom, 1));
                var mapped = context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
                try
                {
                    var rowBytes = Width * 4;
                    var pitch = (int)mapped.RowPitch;
                    var source = new ReadOnlySpan<byte>((byte*)mapped.DataPointer, (pitch * (Height - 1)) + rowBytes);
                    Core.Imaging.PixelBuffer.CopyRows(source, pitch, _latest, rowBytes, rowBytes, Height);
                }
                finally
                {
                    context.Unmap(_staging!, 0);
                }

                _sequence++;
                _firstFrame.TrySetResult();
            }
            catch (Exception ex)
            {
                _failure = ex;
                _firstFrame.TrySetException(ex);
                LogFailed(ex);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording capture started on {Monitor}, area {Crop} (cursor {Cursor})")]
    private partial void LogStarted(string monitor, PixelRect crop, bool cursor);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording capture failed")]
    private partial void LogFailed(Exception ex);
}
