using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D11;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace CashGear.Capture.Engine.Capture.Wgc;

/// <summary>Opens live captures of monitors in HDR mode, for tools that show the screen as it changes (the magnifier).</summary>
public interface ILiveMonitorCapture
{
    /// <summary>A live, tone-mapped capture of <paramref name="monitor"/>, or null if Windows.Graphics.Capture can't start.</summary>
    ILiveMonitorStream? Open(MonitorInfo monitor, HdrInfo hdr);
}

/// <summary>A monitor captured live: the newest picture of any part of it, as 8-bit BGRA.</summary>
public interface ILiveMonitorStream : IDisposable
{
    MonitorInfo Monitor { get; }

    /// <summary>
    /// The newest picture's pixels of <paramref name="area"/> (virtual-screen pixels, inside the monitor), BGRA,
    /// rows of <c>Width * 4</c> bytes, opaque; valid until the next call. Empty before the first picture, after the
    /// monitor changed resolution, or if the area isn't on it.
    /// </summary>
    ReadOnlySpan<byte> Sample(PixelRect area);
}

internal sealed class LiveMonitorCapture(WgcInterop interop, ILoggerFactory loggers) : ILiveMonitorCapture
{
    public ILiveMonitorStream? Open(MonitorInfo monitor, HdrInfo hdr)
    {
        var stream = new LiveMonitorStream(monitor, hdr, loggers.CreateLogger<LiveMonitorStream>());
        try
        {
            stream.Start(interop);
            return stream;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or SharpGen.Runtime.SharpGenException)
        {
            stream.LogStartFailed(e);
            stream.Dispose();
            return null;
        }
    }
}

/// <summary>
/// A Windows.Graphics.Capture session on one monitor in HDR mode (16-bit float frames). Each frame Windows sends is
/// copied whole into one texture (a copy, so it never disturbs a draw in progress); <see cref="Sample"/> tone-maps
/// the part asked for (<see cref="ToneMapper"/>) and reads it back. On Windows 10 Windows draws its yellow border
/// around the monitor meanwhile. Sample on one thread only.
/// </summary>
internal sealed unsafe partial class LiveMonitorStream(MonitorInfo monitor, HdrInfo hdr, ILogger<LiveMonitorStream> logger) : ILiveMonitorStream
{
    private const int PoolBuffers = 2;

    private readonly D3DDeviceManager _devices = new(Microsoft.Extensions.Logging.Abstractions.NullLogger<D3DDeviceManager>.Instance);
    private readonly object _gate = new();
    private D3DResources? _resources;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private ToneMapper? _toneMapper;
    private ID3D11Texture2D? _latest;
    private ID3D11Texture2D? _lens;
    private ID3D11Texture2D? _staging;
    private SizeInt32 _size;
    private byte[] _pixels = [];
    private volatile bool _hasFrame;
    private volatile bool _resized;
    private bool _disposed;

    public MonitorInfo Monitor { get; } = monitor;

    public void Start(WgcInterop interop)
    {
        _resources = _devices.Acquire();
        var item = interop.CreateItemForMonitor(Monitor.Handle);
        _size = item.Size;
        _latest = _resources.Device.CreateTexture2D(new Texture2DDescription
        {
            Width = (uint)_size.Width,
            Height = (uint)_size.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = ToneMapper.SourceFormat,
            SampleDescription = new Vortice.DXGI.SampleDescription(1, 0),
            Usage = ResourceUsage.Default,
            BindFlags = BindFlags.ShaderResource,
        });
        _toneMapper = new ToneMapper(_resources.Device);
        _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_resources.WinRtDevice, DirectXPixelFormat.R16G16B16A16Float, PoolBuffers, _size);
        _session = _pool.CreateCaptureSession(item);
        _session.IsCursorCaptureEnabled = false;
        _pool.FrameArrived += OnFrameArrived;
        _session.StartCapture();
        LogStarted(Monitor.ShortName, hdr.SdrWhiteNits);
    }

    public ReadOnlySpan<byte> Sample(PixelRect area)
    {
        if (!_hasFrame || _resized || _resources is null || area.IsEmpty)
        {
            return [];
        }

        var local = new PixelRect(area.X - Monitor.Bounds.X, area.Y - Monitor.Bounds.Y, area.Width, area.Height);
        if (local.X < 0 || local.Y < 0 || local.Right > _size.Width || local.Bottom > _size.Height)
        {
            return [];
        }

        var context = _resources.Context;
        if (_lens is null || _lens.Description.Width != area.Width || _lens.Description.Height != area.Height)
        {
            if (_lens is not null)
            {
                _toneMapper!.Forget(_lens);
            }

            _lens?.Dispose();
            _staging?.Dispose();
            _lens = _resources.Device.CreateTexture2D(ToneMapper.TargetDescription(area.Width, area.Height));
            _staging = _resources.Device.CreateTexture2D(ToneMapper.TargetDescription(area.Width, area.Height) with
            {
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
            });
            _pixels = new byte[area.Width * area.Height * 4];
        }

        _toneMapper!.Draw(context, _latest!, _lens, local.X, local.Y, hdr.WhiteScale);
        context.CopyResource(_staging!, _lens);
        var mapped = context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowBytes = area.Width * 4;
            var pitch = (int)mapped.RowPitch;
            var source = new ReadOnlySpan<byte>((byte*)mapped.DataPointer, (pitch * (area.Height - 1)) + rowBytes);
            PixelBuffer.CopyRows(source, pitch, _pixels, rowBytes, rowBytes, area.Height);
        }
        finally
        {
            context.Unmap(_staging!, 0);
        }

        PixelBuffer.ForceOpaque(_pixels);
        return _pixels;
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
        }

        if (_pool is not null)
        {
            _pool.FrameArrived -= OnFrameArrived;
        }

        _session?.Dispose();
        _pool?.Dispose();
        _toneMapper?.Dispose();
        _lens?.Dispose();
        _staging?.Dispose();
        _latest?.Dispose();
        _devices.Dispose();
    }

    private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
    {
        lock (_gate)
        {
            if (_disposed)
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

                if (frame.ContentSize.Width != _size.Width || frame.ContentSize.Height != _size.Height)
                {
                    _resized = true; // the tool goes back to GDI; reopening at the new size is its business
                    return;
                }

                using var texture = WgcInterop.GetTexture(frame.Surface);
                _resources!.Context.CopySubresourceRegion(_latest!, 0, 0, 0, 0, texture, 0, new Vortice.Mathematics.Box(0, 0, 0, _size.Width, _size.Height, 1));
                _hasFrame = true;
            }
            catch (Exception e) when (e is System.Runtime.InteropServices.COMException or SharpGen.Runtime.SharpGenException or ObjectDisposedException)
            {
                _resized = true;
                LogFrameFailed(e);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Live HDR capture of {Monitor} started (SDR white {WhiteNits:0} nits)")]
    private partial void LogStarted(string monitor, double whiteNits);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The live HDR capture couldn't start; GDI is used instead")]
    internal partial void LogStartFailed(Exception e);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A live HDR frame couldn't be copied")]
    private partial void LogFrameFailed(Exception e);
}
