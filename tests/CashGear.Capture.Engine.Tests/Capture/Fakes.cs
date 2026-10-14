using System.Buffers;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Capture;

namespace CashGear.Capture.Engine.Tests.Capture;

internal static class TestMonitors
{
    public static MonitorInfo Create(string name, PixelRect bounds, bool primary = false, uint dpi = 96) =>
        new(0, $@"\\.\{name}", bounds, bounds, dpi, dpi, primary);

    /// <summary>The colour a <see cref="FakeGrabber"/> paints for a monitor: B = first digit of its name.</summary>
    public static byte TagOf(MonitorInfo monitor) => (byte)(monitor.ShortName[^1] - '0');
}

internal sealed class FakeMonitorService(params MonitorInfo[] monitors) : IMonitorService
{
    public IReadOnlyList<MonitorInfo> GetMonitors() => monitors;
}

internal sealed class FakeSettingsService(AppSettings settings) : ISettingsService
{
    public event EventHandler? Changed;

    public AppSettings Current { get; private set; } = settings;

    public static FakeSettingsService With(CaptureBackend backend) =>
        new(new AppSettings { Capture = new CaptureSettings(Backend: backend) });

    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken ct = default)
    {
        Current = update(Current);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }
}

internal sealed class FakeCapabilities(bool wgcSupported, bool borderOptional) : ICaptureCapabilities
{
    public static FakeCapabilities Windows10 => new(wgcSupported: true, borderOptional: false);

    public static FakeCapabilities Windows11 => new(wgcSupported: true, borderOptional: true);

    public static FakeCapabilities NoWgc => new(wgcSupported: false, borderOptional: false);

    public bool IsWgcSupported { get; } = wgcSupported;

    public bool CanDisableWgcBorder { get; } = borderOptional;
}

/// <summary>
/// Paints each request's area in BGRA (monitor tag, backend, 0, 255), or runs <see cref="Behavior"/> when set.
/// </summary>
internal sealed class FakeGrabber(CaptureBackend backend, ArrayPool<byte>? pool = null) : IFrameGrabber
{
    public CaptureBackend Backend { get; } = backend;

    public List<FrameRequest> Requests { get; } = [];

    /// <summary>Return null to fall through to the default painting.</summary>
    public Func<FrameRequest, CancellationToken, CapturedImage?>? Behavior { get; set; }

    public Task<CapturedImage> CaptureAsync(FrameRequest request, CancellationToken ct)
    {
        Requests.Add(request);
        if (Behavior?.Invoke(request, ct) is { } custom)
        {
            return Task.FromResult(custom);
        }

        var image = CapturedImage.Allocate(
            request.Area.Width, request.Area.Height, request.ToMetadata(Backend, TimeProvider.System), pool, clear: false);
        var pixels = image.Pixels;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = TestMonitors.TagOf(request.Monitor);
            pixels[i + 1] = (byte)Backend;
            pixels[i + 2] = 0;
            pixels[i + 3] = 255;
        }

        return Task.FromResult(image);
    }
}

/// <summary>Counts rentals and returns so tests can prove buffers are not leaked.</summary>
internal sealed class CountingPool : ArrayPool<byte>
{
    private int _rented;
    private int _returned;

    public int Outstanding => _rented - _returned;

    public override byte[] Rent(int minimumLength)
    {
        Interlocked.Increment(ref _rented);
        return new byte[minimumLength];
    }

    public override void Return(byte[] array, bool clearArray = false) => Interlocked.Increment(ref _returned);
}
