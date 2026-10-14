using Microsoft.Extensions.Logging;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Windows.Graphics.DirectX.Direct3D11;

namespace CashGear.Capture.Engine.Capture.Wgc;

/// <summary>A D3D11 device plus its WinRT wrapper, as WGC needs them.</summary>
internal sealed class D3DResources(ID3D11Device device, ID3D11DeviceContext context, IDirect3DDevice winRtDevice) : IDisposable
{
    public ID3D11Device Device { get; } = device;

    public ID3D11DeviceContext Context { get; } = context;

    public IDirect3DDevice WinRtDevice { get; } = winRtDevice;

    public void Dispose()
    {
        WinRtDevice.Dispose();
        Context.Dispose();
        Device.Dispose();
    }
}

/// <summary>
/// Lazily creates the shared D3D11 device (hardware, WARP fallback) and recreates it after device loss.
/// Not thread-safe: callers serialize access (see <see cref="WgcFrameGrabber"/>).
/// </summary>
internal sealed partial class D3DDeviceManager(ILogger<D3DDeviceManager> logger) : IDisposable
{
    private static readonly FeatureLevel[] FeatureLevels =
    [
        FeatureLevel.Level_11_1, FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0,
    ];

    private D3DResources? _resources;

    public D3DResources Acquire() => _resources ??= Create();

    /// <summary>Drops the device if it was removed/reset so the next <see cref="Acquire"/> builds a fresh one.</summary>
    public void InvalidateIfLost()
    {
        if (_resources is null)
        {
            return;
        }

        var reason = _resources.Device.DeviceRemovedReason;
        if (reason.Success)
        {
            return;
        }

        LogDeviceLost(reason.Code);
        _resources.Dispose();
        _resources = null;
    }

    public void Dispose()
    {
        _resources?.Dispose();
        _resources = null;
    }

    private D3DResources Create()
    {
        var (device, context, driver) = CreateDevice();
        try
        {
            using (var multithread = device.QueryInterfaceOrNull<ID3D11Multithread>())
            {
                // WGC's free-threaded frame pool touches the device from its own worker thread.
                multithread?.SetMultithreadProtected(true);
            }

            using var dxgi = device.QueryInterface<IDXGIDevice>();
            var winRt = WgcInterop.CreateWinRtDevice(dxgi);
            LogDeviceCreated(driver, device.FeatureLevel);
            return new D3DResources(device, context, winRt);
        }
        catch
        {
            context.Dispose();
            device.Dispose();
            throw;
        }
    }

    private (ID3D11Device Device, ID3D11DeviceContext Context, DriverType Driver) CreateDevice()
    {
        foreach (var driver in new[] { DriverType.Hardware, DriverType.Warp })
        {
            var result = D3D11.D3D11CreateDevice(
                null, driver, DeviceCreationFlags.BgraSupport, FeatureLevels, out ID3D11Device? device, out ID3D11DeviceContext? context);
            if (result.Success && device is not null && context is not null)
            {
                return (device, context, driver);
            }

            LogDeviceCreateFailed(driver, result.Code);
        }

        throw new InvalidOperationException("Could not create a Direct3D 11 device (hardware or WARP).");
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Created D3D11 {Driver} device, feature level {FeatureLevel}")]
    private partial void LogDeviceCreated(DriverType driver, FeatureLevel featureLevel);

    [LoggerMessage(Level = LogLevel.Warning, Message = "D3D11CreateDevice({Driver}) failed (HRESULT 0x{HResult:X8})")]
    private partial void LogDeviceCreateFailed(DriverType driver, int hResult);

    [LoggerMessage(Level = LogLevel.Warning, Message = "D3D11 device lost (reason 0x{Reason:X8}); it will be recreated")]
    private partial void LogDeviceLost(int reason);
}
