using System.Buffers;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Interop.Native;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D11;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace CashGear.Capture.Engine.Capture.Wgc;

/// <summary>
/// Single-frame Windows.Graphics.Capture grab of a monitor or a window: start a session, take the first frame,
/// copy the wanted part through a CPU-readable staging texture, tear everything down. Grabs are serialized (one
/// shared device). On Windows 10 the session shows a yellow border while it runs; Windows 11 lets us switch it off.
/// Window grabs see the window's own content even where other windows cover it.
/// </summary>
internal sealed partial class WgcFrameGrabber(
    WgcInterop interop,
    D3DDeviceManager devices,
    ICaptureCapabilities capabilities,
    ArrayPool<byte> pool,
    TimeProvider time,
    ILogger<WgcFrameGrabber> logger) : IFrameGrabber, IWindowGrabber, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task<bool>? _borderlessAccess;

    public CaptureBackend Backend => CaptureBackend.Wgc;

    // Run entirely on the thread pool: WinRT objects must not be created on the caller's (possibly STA UI) thread.
    public Task<CapturedImage> CaptureAsync(FrameRequest request, CancellationToken ct) =>
        Task.Run(() => CaptureMonitorAreaAsync(request, ct), ct);

    public Task<CapturedImage> CaptureWindowAsync(WindowRequest request, CancellationToken ct) =>
        Task.Run(() => CaptureWindowCoreAsync(request, ct), ct);

    public void Dispose() => _gate.Dispose();

    private Task<CapturedImage> CaptureMonitorAreaAsync(FrameRequest request, CancellationToken ct)
    {
        if (request.Area.IsEmpty || !request.Monitor.Bounds.Contains(request.Area))
        {
            throw new ArgumentException($"Area {request.Area} is not inside monitor {request.Monitor.Bounds}.", nameof(request));
        }

        return GrabAsync(
            () => interop.CreateItemForMonitor(request.Monitor.Handle),
            request.Monitor.ShortName,
            request.IncludeCursor,
            request.FrameTimeout,
            content =>
            {
                // The texture origin is the monitor's top-left.
                var crop = VirtualScreen.ToLocal(request.Area, request.Monitor.Bounds);
                if (!new PixelRect(0, 0, content.Width, content.Height).Contains(crop))
                {
                    // Typically a resolution change between enumeration and capture.
                    throw new CaptureException($"WGC frame {content.Width}x{content.Height} does not cover {request.Area} on {request.Monitor.ShortName}.");
                }

                return (crop, request.ToMetadata(Backend, time));
            },
            keepAlpha: false,
            request.Hdr,
            ct);
    }

    private Task<CapturedImage> CaptureWindowCoreAsync(WindowRequest request, CancellationToken ct)
    {
        var window = request.Window;
        return GrabAsync(
            () => interop.CreateItemForWindow(window.Handle),
            $"window \"{window.Title}\"",
            request.IncludeCursor,
            request.FrameTimeout,
            content =>
            {
                var crop = WindowCrop(window.Handle, content);
                var metadata = new CaptureMetadata
                {
                    Kind = CaptureKind.Window,
                    SourceBounds = new PixelRect(window.Bounds.X, window.Bounds.Y, crop.Width, crop.Height),
                    Dpi = request.Dpi,
                    CapturedAt = time.GetLocalNow(),
                    Backend = Backend,
                    MonitorName = request.MonitorName,
                    WindowTitle = window.Title,
                };
                return (crop, metadata);
            },
            keepAlpha: true, // Windows 11 rounded corners come through transparent
            request.Hdr,
            ct);
    }

    /// <summary>
    /// The part of a window frame to keep: the visible frame (DWM extended frame bounds), without the invisible
    /// resize borders, whichever of the two rectangles the capture item happens to report.
    /// </summary>
    private unsafe PixelRect WindowCrop(nint hwnd, PixelSize content)
    {
        var all = new PixelRect(0, 0, content.Width, content.Height);
        RECT windowRect;
        RECT frameRect;
        if (!User32.GetWindowRect(hwnd, &windowRect)
            || Dwmapi.DwmGetWindowAttribute(hwnd, Dwmapi.DwmwaExtendedFrameBounds, &frameRect, (uint)sizeof(RECT)) < 0)
        {
            return all;
        }

        var outer = windowRect.ToPixelRect();
        var frame = frameRect.ToPixelRect();
        if (content.Width == outer.Width && content.Height == outer.Height && outer.Contains(frame))
        {
            return VirtualScreen.ToLocal(frame, outer);
        }

        if (content.Width != frame.Width || content.Height != frame.Height)
        {
            LogUnexpectedWindowSize(content.Width, content.Height, outer, frame);
        }

        return all;
    }

    private async Task<CapturedImage> GrabAsync(
        Func<GraphicsCaptureItem> createItem,
        string target,
        bool includeCursor,
        TimeSpan frameTimeout,
        Func<PixelSize, (PixelRect Crop, CaptureMetadata Metadata)> describe,
        bool keepAlpha,
        HdrInfo? hdr,
        CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var borderless = await TryGetBorderlessAccessAsync().ConfigureAwait(false);
            var resources = devices.Acquire();
            try
            {
                var item = createItem();
                if (item.Size.Width <= 0 || item.Size.Height <= 0)
                {
                    // Windows 10 gives a window off every monitor no picture (0x0).
                    throw new CaptureException($"Windows.Graphics.Capture has no picture of the {target}.");
                }

                using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                    resources.WinRtDevice, hdr is null ? DirectXPixelFormat.B8G8R8A8UIntNormalized : DirectXPixelFormat.R16G16B16A16Float, 1, item.Size);
                using var session = framePool.CreateCaptureSession(item);
                session.IsCursorCaptureEnabled = includeCursor;
                if (borderless && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
                {
                    TryDisableBorder(session);
                }

                using var frame = await WaitForFrameAsync(framePool, session, target, frameTimeout, ct).ConfigureAwait(false);
                return CopyFrame(frame, describe, keepAlpha, hdr, resources);
            }
            catch
            {
                devices.InvalidateIfLost();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<Direct3D11CaptureFrame> WaitForFrameAsync(
        Direct3D11CaptureFramePool framePool, GraphicsCaptureSession session, string target, TimeSpan timeout, CancellationToken ct)
    {
        var arrived = new TaskCompletionSource<Direct3D11CaptureFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        TypedEventHandler<Direct3D11CaptureFramePool, object> onFrame = (sender, _) =>
        {
            var frame = sender.TryGetNextFrame();
            if (frame is not null && !arrived.TrySetResult(frame))
            {
                frame.Dispose(); // a late or extra frame nobody will read
            }
        };

        framePool.FrameArrived += onFrame;
        try
        {
            session.StartCapture();
            try
            {
                return await arrived.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                DisposeIfCompleted(arrived);
                throw new TimeoutException($"No WGC frame for {target} within {timeout.TotalMilliseconds:0} ms.");
            }
            catch
            {
                DisposeIfCompleted(arrived);
                throw;
            }
        }
        finally
        {
            framePool.FrameArrived -= onFrame;
        }
    }

    private unsafe CapturedImage CopyFrame(
        Direct3D11CaptureFrame frame,
        Func<PixelSize, (PixelRect Crop, CaptureMetadata Metadata)> describe,
        bool keepAlpha,
        HdrInfo? hdr,
        D3DResources resources)
    {
        using var texture = WgcInterop.GetTexture(frame.Surface);
        var description = texture.Description;
        var content = new PixelSize(
            Math.Min(frame.ContentSize.Width, (int)description.Width),
            Math.Min(frame.ContentSize.Height, (int)description.Height));
        var (crop, metadata) = describe(content);
        if (crop.IsEmpty || !new PixelRect(0, 0, content.Width, content.Height).Contains(crop))
        {
            throw new CaptureException($"Nothing to copy: {crop} is not inside the {content.Width}x{content.Height} frame.");
        }

        // An HDR frame is tone-mapped into an 8-bit texture of the crop's size first; an SDR one is copied whole.
        using var toned = hdr is null ? null : ToneMap(texture, crop, hdr, resources);
        var copied = toned ?? texture;
        var stagingDescription = copied.Description with
        {
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
            MipLevels = 1,
            ArraySize = 1,
            SampleDescription = new(1, 0),
        };
        using var staging = resources.Device.CreateTexture2D(stagingDescription);
        resources.Context.CopyResource(staging, copied);
        var origin = toned is null ? crop : crop with { X = 0, Y = 0 };

        var image = CapturedImage.Allocate(crop.Width, crop.Height, metadata, pool, clear: false);
        try
        {
            var mapped = resources.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var rowPitch = (int)mapped.RowPitch;
                var rowBytes = crop.Width * CapturedImage.BytesPerPixel;
                var start = (byte*)mapped.DataPointer + (origin.Y * rowPitch) + (origin.X * CapturedImage.BytesPerPixel);
                var source = new ReadOnlySpan<byte>(start, (rowPitch * (crop.Height - 1)) + rowBytes);
                PixelBuffer.CopyRows(source, rowPitch, image.Pixels, image.Stride, rowBytes, crop.Height);
            }
            finally
            {
                resources.Context.Unmap(staging, 0);
            }

            NormalizeAlpha(image, keepAlpha);
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
        }
    }

    /// <summary>The crop of a float (scRGB) frame, tone-mapped to 8-bit sRGB on the GPU.</summary>
    private static ID3D11Texture2D ToneMap(ID3D11Texture2D frame, PixelRect crop, HdrInfo hdr, D3DResources resources)
    {
        var target = resources.Device.CreateTexture2D(ToneMapper.TargetDescription(crop.Width, crop.Height));
        try
        {
            using var mapper = new ToneMapper(resources.Device);
            mapper.Draw(resources.Context, frame, target, crop.X, crop.Y, hdr.WhiteScale);
            return target;
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Desktop frames are opaque. Window frames carry real alpha only at shaped edges (Windows 11 corners); if
    /// most of a window comes back transparent, its alpha is meaningless (legacy redirection surfaces) and is
    /// forced opaque too.
    /// </summary>
    private static void NormalizeAlpha(CapturedImage image, bool keepAlpha)
    {
        var pixels = image.Pixels;
        if (!keepAlpha)
        {
            PixelBuffer.ForceOpaque(pixels);
            return;
        }

        long transparent = 0, partial = 0;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] == 0)
            {
                transparent++;
            }
            else if (pixels[i] != 255)
            {
                partial++;
            }
        }

        var total = (long)image.Width * image.Height;
        if (transparent * 2 > total)
        {
            PixelBuffer.ForceOpaque(pixels);
        }
        else if (transparent + partial > 0)
        {
            image.Metadata = image.Metadata with { HasTransparency = true };
        }
    }

    private Task<bool> TryGetBorderlessAccessAsync()
    {
        if (!capabilities.CanDisableWgcBorder || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            return Task.FromResult(false);
        }

        return _borderlessAccess ??= RequestBorderlessAccessAsync();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.20348.0")]
    private async Task<bool> RequestBorderlessAccessAsync()
    {
        try
        {
            var status = await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
            LogBorderlessAccess(status.ToString());
            return status == Windows.Security.Authorization.AppCapabilityAccess.AppCapabilityAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            LogBorderlessAccessFailed(ex);
            return false;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.20348.0")]
    private void TryDisableBorder(GraphicsCaptureSession session)
    {
        try
        {
            session.IsBorderRequired = false;
        }
        catch (Exception ex)
        {
            LogBorderlessAccessFailed(ex);
        }
    }

    private static void DisposeIfCompleted(TaskCompletionSource<Direct3D11CaptureFrame> arrived)
    {
        // Close the race where the frame lands just as we give up: whoever loses must dispose it.
        if (!arrived.TrySetCanceled() && arrived.Task.IsCompletedSuccessfully)
        {
            arrived.Task.Result.Dispose();
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Borderless capture access: {Status}")]
    private partial void LogBorderlessAccess(string status);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not disable the WGC capture border")]
    private partial void LogBorderlessAccessFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Debug, Message = "WGC window frame is {Width}x{Height}, matching neither the window rect {Outer} nor the visible frame {Frame}; keeping it whole")]
    private partial void LogUnexpectedWindowSize(int width, int height, PixelRect outer, PixelRect frame);
}

/// <summary>A width and height in pixels.</summary>
internal readonly record struct PixelSize(int Width, int Height);
