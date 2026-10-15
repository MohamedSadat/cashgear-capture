using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Engine.Capture;
using CashGear.Capture.Engine.Capture.Gdi;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Engine.Diagnostics;
using CashGear.Capture.Engine.Display;
using Microsoft.Extensions.DependencyInjection;

namespace CashGear.Capture.Engine;

public static class EngineServiceCollectionExtensions
{
    public static IServiceCollection AddCaptureEngine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IMonitorService, Win32MonitorService>();
        services.AddSingleton<IWindowService, Win32WindowService>();

        services.AddSingleton<ICaptureCapabilities, WindowsCaptureCapabilities>();
        services.AddSingleton<CaptureBackendSelector>();
        services.AddSingleton<WgcInterop>();
        services.AddSingleton<D3DDeviceManager>();
        services.AddSingleton<IFrameGrabber, GdiFrameGrabber>();
        services.AddSingleton<WgcFrameGrabber>();
        services.AddSingleton<IFrameGrabber>(sp => sp.GetRequiredService<WgcFrameGrabber>());
        services.AddSingleton<IWindowGrabber>(sp => sp.GetRequiredService<WgcFrameGrabber>());
        services.AddSingleton<IScreenCaptureService, ScreenCaptureService>();

        services.AddSingleton<IStartupDiagnostics, StartupDiagnostics>();
        services.AddSingleton<Core.Ocr.IOcrService, Ocr.WindowsOcrService>();
        return services;
    }
}
