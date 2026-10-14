using System.Runtime.InteropServices;
using CashGear.Capture.Core.Capture;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;

namespace CashGear.Capture.Engine.Capture;

/// <summary>What the OS can do for screen capture. Behind an interface so the backend policy is testable.</summary>
internal interface ICaptureCapabilities
{
    /// <summary>Windows.Graphics.Capture is available (Windows 10 1903+ with a usable GPU path).</summary>
    bool IsWgcSupported { get; }

    /// <summary>A WGC session can switch off the yellow capture border (<c>IsBorderRequired</c>, Windows 11+).</summary>
    bool CanDisableWgcBorder { get; }
}

internal sealed class WindowsCaptureCapabilities : ICaptureCapabilities
{
    private readonly Lazy<bool> _wgcSupported = new(QueryWgcSupported);
    private readonly Lazy<bool> _borderOptional = new(QueryBorderOptional);

    public bool IsWgcSupported => _wgcSupported.Value;

    public bool CanDisableWgcBorder => _borderOptional.Value;

    private static bool QueryWgcSupported()
    {
        try
        {
            return GraphicsCaptureSession.IsSupported();
        }
        catch (Exception ex) when (ex is COMException or TypeLoadException or PlatformNotSupportedException)
        {
            return false;
        }
    }

    private static bool QueryBorderOptional() =>
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 20348)
        && ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired");
}

/// <summary>
/// Turns the user's <see cref="CaptureBackend"/> preference into the backend to use.
/// <para>
/// <see cref="CaptureBackend.Auto"/> avoids the yellow WGC border for screen captures: monitors and regions
/// use WGC only where the border can be switched off (Windows 11), GDI otherwise. Window captures prefer WGC
/// because only WGC sees occluded windows.
/// </para>
/// </summary>
internal sealed class CaptureBackendSelector(ICaptureCapabilities capabilities)
{
    public CaptureBackend ForScreen(CaptureBackend preference) => preference switch
    {
        CaptureBackend.Gdi => CaptureBackend.Gdi,
        CaptureBackend.Wgc => capabilities.IsWgcSupported ? CaptureBackend.Wgc : CaptureBackend.Gdi,
        _ => capabilities.IsWgcSupported && capabilities.CanDisableWgcBorder ? CaptureBackend.Wgc : CaptureBackend.Gdi,
    };

    public CaptureBackend ForWindow(CaptureBackend preference) =>
        preference == CaptureBackend.Gdi || !capabilities.IsWgcSupported ? CaptureBackend.Gdi : CaptureBackend.Wgc;
}
