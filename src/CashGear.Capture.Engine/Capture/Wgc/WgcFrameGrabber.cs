using System.Buffers;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Imaging;
using Microsoft.Extensions.Logging;
using Vortice.Direct3D11;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace CashGear.Capture.Engine.Capture.Wgc;

/// <summary>
/// Single-frame Windows.Graphics.Capture grab: start a session on the monitor, take the first frame, copy it
/// through a CPU-readable staging texture, tear everything down. Grabs are serialized (one shared device).
/// On Windows 10 the session shows a yellow border while it runs; Windows 11 lets us switch it off.
/// </summary>
internal sealed partial class WgcFrameGrabber(
    WgcInterop interop,
    D3DDeviceManager devices,
    ICaptureCapabilities capabilities,
    ArrayPool<byte> pool,
    TimeProvider time,
    ILogger<WgcFrameGrabber> logger) : IFrameGrabber, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Task<bool>? _borderlessAccess;

    public CaptureBackend Backend => CaptureBackend.Wgc;

    // Run entirely on the thread pool: WinRT objects must not be created on the caller's (possibly STA UI) thread.
    public Task<CapturedImage> CaptureAsync(FrameRequest request, CancellationToken ct) =>
        Task.Run(() => CaptureCoreAsync(request, ct), ct);

    public void Dispose() => _gate.Dispose();

    private async Task<CapturedImage> CaptureCoreAsync(FrameRequest request, CancellationToken ct)
    {
        if (request.Area.IsEmpty || !request.Monitor.Bounds.Contains(request.Area))
        {
            throw new ArgumentException($"Area {request.Area} is not inside monitor {request.Monitor.Bounds}.", nameof(request));
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var borderless = await TryGetBorderlessAccessAsync().ConfigureAwait(false);
            var resources = devices.Acquire();
            try
            {
                return await GrabAsync(request, resources, borderless, ct).ConfigureAwait(false);
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

    private async Task<CapturedImage> GrabAsync(FrameRequest request, D3DResources resources, bool borderless, CancellationToken ct)
    {
        var item = interop.CreateItemForMonitor(request.Monitor.Handle);
        using var framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            resources.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = framePool.CreateCaptureSession(item);
        session.IsCursorCaptureEnabled = request.IncludeCursor;
        if (borderless && OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348))
        {
            TryDisableBorder(session);
        }

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
            Direct3D11CaptureFrame captured;
            try
            {
                captured = await arrived.Task.WaitAsync(request.FrameTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                DisposeIfCompleted(arrived);
                throw new TimeoutException($"No WGC frame for {request.Monitor.ShortName} within {request.FrameTimeout.TotalMilliseconds:0} ms.");
            }
            catch
            {
                DisposeIfCompleted(arrived);
                throw;
            }

            using (captured)
            {
                return CopyFrame(captured, request, resources);
            }
        }
        finally
        {
            framePool.FrameArrived -= onFrame;
        }
    }

    private unsafe CapturedImage CopyFrame(Direct3D11CaptureFrame frame, FrameRequest request, D3DResources resources)
    {
        using var texture = WgcInterop.GetTexture(frame.Surface);
        var description = texture.Description;

        // The area relative to the monitor's top-left, which is the texture origin.
        var offsetX = request.Area.X - request.Monitor.Bounds.X;
        var offsetY = request.Area.Y - request.Monitor.Bounds.Y;
        var contentWidth = Math.Min(frame.ContentSize.Width, (int)description.Width);
        var contentHeight = Math.Min(frame.ContentSize.Height, (int)description.Height);
        if (offsetX + request.Area.Width > contentWidth || offsetY + request.Area.Height > contentHeight)
        {
            // Typically a resolution change between enumeration and capture.
            throw new CaptureException(
                $"WGC frame {contentWidth}x{contentHeight} does not cover {request.Area} on {request.Monitor.ShortName}.");
        }

        var stagingDescription = description with
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
        resources.Context.CopyResource(staging, texture);

        var image = CapturedImage.Allocate(request.Area.Width, request.Area.Height, request.ToMetadata(Backend, time), pool, clear: false);
        try
        {
            var mapped = resources.Context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                var rowPitch = (int)mapped.RowPitch;
                var rowBytes = request.Area.Width * CapturedImage.BytesPerPixel;
                var start = (byte*)mapped.DataPointer + (offsetY * rowPitch) + (offsetX * CapturedImage.BytesPerPixel);
                var source = new ReadOnlySpan<byte>(start, (rowPitch * (request.Area.Height - 1)) + rowBytes);
                PixelBuffer.CopyRows(source, rowPitch, image.Pixels, image.Stride, rowBytes, request.Area.Height);
            }
            finally
            {
                resources.Context.Unmap(staging, 0);
            }

            PixelBuffer.ForceOpaque(image.Pixels); // desktop frames are opaque; make it explicit like GDI
            return image;
        }
        catch
        {
            image.Dispose();
            throw;
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
}
