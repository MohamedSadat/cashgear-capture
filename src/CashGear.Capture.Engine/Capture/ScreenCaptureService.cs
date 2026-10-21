using System.Buffers;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Engine.Capture;

/// <summary>
/// Picks a backend per the settings, falls back from WGC to GDI on failure, and composes multi-monitor
/// captures. Every screen capture is made per monitor and blitted into a canvas in virtual-screen space; canvas
/// areas no monitor covers stay fully transparent. Window captures use the window's own content (WGC) or, when it
/// is fully visible and WGC would flash its border, the screen pixels under its frame.
/// </summary>
internal sealed partial class ScreenCaptureService : IScreenCaptureService
{
    private readonly IMonitorService _monitors;
    private readonly IWindowService _windows;
    private readonly ISettingsService _settings;
    private readonly CaptureBackendSelector _selector;
    private readonly IReadOnlyDictionary<CaptureBackend, IFrameGrabber> _grabbers;
    private readonly IWindowGrabber? _windowGrabber;
    private readonly ArrayPool<byte> _pool;
    private readonly TimeProvider _time;
    private readonly ILogger<ScreenCaptureService> _logger;

    public ScreenCaptureService(
        IMonitorService monitors,
        IWindowService windows,
        ISettingsService settings,
        CaptureBackendSelector selector,
        IEnumerable<IFrameGrabber> grabbers,
        IEnumerable<IWindowGrabber> windowGrabbers,
        ArrayPool<byte> pool,
        TimeProvider time,
        ILogger<ScreenCaptureService> logger)
    {
        _monitors = monitors;
        _windows = windows;
        _settings = settings;
        _selector = selector;
        _grabbers = grabbers.ToDictionary(g => g.Backend);
        _windowGrabber = windowGrabbers.FirstOrDefault();
        _pool = pool;
        _time = time;
        _logger = logger;

        if (!_grabbers.ContainsKey(CaptureBackend.Gdi))
        {
            throw new ArgumentException("The GDI grabber is required as the fallback backend.", nameof(grabbers));
        }
    }

    public Task<CapturedImage> CaptureMonitorAsync(MonitorInfo monitor, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return CaptureAreaAsync(monitor, monitor.Bounds, ScreenBackend(), _settings.Current.Capture.IncludeCursor, ct);
    }

    public Task<CapturedImage> CaptureAllMonitorsAsync(CancellationToken ct = default)
    {
        var monitors = _monitors.GetMonitors();
        if (monitors.Count == 0)
        {
            throw new CaptureException("No monitors found.");
        }

        var bounds = VirtualScreen.GetBounds(monitors.Select(m => m.Bounds));
        return ComposeAsync(bounds, CaptureKind.AllMonitors, monitors, ScreenBackend(), _settings.Current.Capture.IncludeCursor, ct);
    }

    public Task<CapturedImage> CaptureRegionAsync(PixelRect region, CancellationToken ct = default) =>
        CaptureRegionAsync(region, _settings.Current.Capture.IncludeCursor, ct);

    public Task<CapturedImage> CaptureRegionAsync(PixelRect region, bool includeCursor, CancellationToken ct = default)
    {
        if (region.IsEmpty)
        {
            throw new ArgumentException("Region is empty.", nameof(region));
        }

        return ComposeAsync(region, CaptureKind.Region, _monitors.GetMonitors(), ScreenBackend(), includeCursor, ct);
    }

    public async Task<CapturedImage> CaptureWindowAsync(nint hwnd, CaptureWindowOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var window = _windows.GetWindow(hwnd) ?? throw new CaptureException("That window no longer exists.");
        if (window.IsMinimized)
        {
            throw new CaptureException($"\"{Describe(window)}\" is minimized. Restore it, then capture again.");
        }

        var monitors = _monitors.GetMonitors();
        var onScreen = Covered(window.Bounds, monitors) == window.Bounds.Area;
        var unobstructed = onScreen && !_windows.IsOccluded(hwnd);
        var host = monitors
            .OrderByDescending(m => m.Bounds.Intersect(window.Bounds).Area)
            .FirstOrDefault(m => m.Bounds.IntersectsWith(window.Bounds));

        // A window on a monitor in HDR mode is read as float and tone-mapped: only WGC can.
        var hdr = HdrFor(host);
        var backend = hdr is not null && _settings.Current.Capture.Backend == CaptureBackend.Auto
            ? CaptureBackend.Wgc
            : _selector.ForWindow(_settings.Current.Capture.Backend, unobstructed);

        CapturedImage image;
        if (backend == CaptureBackend.Wgc && _windowGrabber is not null)
        {
            try
            {
                var timeout = TimeSpan.FromMilliseconds(_settings.Current.Capture.WgcFrameTimeoutMs);
                image = await _windowGrabber.CaptureWindowAsync(
                    new WindowRequest(window, options.IncludeCursor, timeout, host?.Scale ?? DpiScale.Identity, host?.ShortName) { Hdr = hdr }, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // The screen pixels are the best we can do; covered parts will show what covers them.
                LogWindowFallback(ex, Describe(window));
                image = await CaptureWindowFromScreenAsync(window, monitors, options.IncludeCursor, ct).ConfigureAwait(false);
            }
        }
        else
        {
            image = await CaptureWindowFromScreenAsync(window, monitors, options.IncludeCursor, ct).ConfigureAwait(false);
        }

        LogWindowCaptured(Describe(window), image.Width, image.Height, image.Metadata.Backend, unobstructed);
        if (!options.IncludeShadow)
        {
            return image;
        }

        using (image)
        {
            var scale = host?.Scale.ScaleX ?? 1;
            return DropShadow.Apply(
                image,
                (int)Math.Round(DropShadow.DefaultRadius * scale),
                (int)Math.Round(DropShadow.DefaultOffsetY * scale),
                DropShadow.DefaultOpacity,
                _pool);
        }
    }

    private async Task<CapturedImage> CaptureWindowFromScreenAsync(
        WindowInfo window, IReadOnlyList<MonitorInfo> monitors, bool includeCursor, CancellationToken ct)
    {
        var virtualBounds = VirtualScreen.GetBounds(monitors.Select(m => m.Bounds));
        var area = window.Bounds.Intersect(virtualBounds);
        if (area.IsEmpty)
        {
            throw new CaptureException($"\"{Describe(window)}\" is off screen.");
        }

        var image = await ComposeAsync(area, CaptureKind.Window, monitors, ScreenBackend(), includeCursor, ct).ConfigureAwait(false);
        image.Metadata = image.Metadata with { WindowTitle = window.Title };
        return image;
    }

    private static long Covered(PixelRect rect, IReadOnlyList<MonitorInfo> monitors) =>
        monitors.Select(m => m.Bounds).Distinct().Sum(b => b.Intersect(rect).Area);

    private static string Describe(WindowInfo window) => window.Title.Length > 0 ? window.Title : window.ClassName;

    private CaptureBackend ScreenBackend() => _selector.ForScreen(_settings.Current.Capture.Backend);

    /// <summary>How to read a monitor: as float, tone-mapped, when it is in HDR mode (unless Settings say not to); else null.</summary>
    private HdrInfo? HdrFor(MonitorInfo? monitor) =>
        monitor?.Hdr is { } hdr && _settings.Current.Capture.Hdr == HdrCapture.Auto ? hdr : null;

    private async Task<CapturedImage> ComposeAsync(
        PixelRect target,
        CaptureKind kind,
        IReadOnlyList<MonitorInfo> monitors,
        CaptureBackend backend,
        bool includeCursor,
        CancellationToken ct)
    {
        // Mirrored monitors share bounds; capture each distinct area once.
        var parts = monitors
            .Select(m => (Monitor: m, Area: m.Bounds.Intersect(target)))
            .Where(p => !p.Area.IsEmpty)
            .DistinctBy(p => p.Area)
            .ToList();
        if (parts.Count == 0)
        {
            throw new CaptureException($"{target} is not on any monitor.");
        }

        var metadata = new CaptureMetadata
        {
            Kind = kind,
            SourceBounds = target,
            CapturedAt = _time.GetLocalNow(),
            Backend = backend,
            Dpi = parts.Count == 1 ? parts[0].Monitor.Scale : DpiScale.Identity,
            MonitorName = parts.Count == 1 ? parts[0].Monitor.ShortName : null,
            IsPrimaryMonitor = parts.Count == 1 && parts[0].Monitor.IsPrimary,
        };

        var canvas = CapturedImage.Allocate(target.Width, target.Height, metadata, _pool, clear: true);
        try
        {
            var used = new HashSet<CaptureBackend>();
            long covered = 0;
            foreach (var (monitor, area) in parts)
            {
                using var part = await CaptureAreaAsync(monitor, area, backend, includeCursor, ct).ConfigureAwait(false);
                var local = VirtualScreen.ToLocal(area, target);
                PixelCompositor.Blit(part, canvas, local.X, local.Y);
                used.Add(part.Metadata.Backend);
                covered += area.Area;
            }

            canvas.Metadata = metadata with
            {
                // Any fallback means GDI produced at least part of the image.
                Backend = used.Count == 1 ? used.First() : CaptureBackend.Gdi,
                HasTransparency = covered < target.Area,
            };
            return canvas;
        }
        catch
        {
            canvas.Dispose();
            throw;
        }
    }

    private async Task<CapturedImage> CaptureAreaAsync(
        MonitorInfo monitor, PixelRect area, CaptureBackend backend, bool includeCursor, CancellationToken ct)
    {
        var options = _settings.Current.Capture;
        var hdr = HdrFor(monitor);
        var request = new FrameRequest(monitor, area, includeCursor, TimeSpan.FromMilliseconds(options.WgcFrameTimeoutMs)) { Hdr = hdr };

        // GDI's copy of a monitor in HDR mode looks washed out: such a monitor is read with WGC unless GDI was chosen.
        if (hdr is not null && options.Backend == CaptureBackend.Auto)
        {
            backend = CaptureBackend.Wgc;
        }

        if (backend == CaptureBackend.Wgc && _grabbers.TryGetValue(CaptureBackend.Wgc, out var wgc))
        {
            try
            {
                return await wgc.CaptureAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogFallback(ex, monitor.ShortName);
            }
        }

        return await _grabbers[CaptureBackend.Gdi].CaptureAsync(request, ct).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Window capture of \"{Window}\" via Windows.Graphics.Capture failed; using the screen pixels under it")]
    private partial void LogWindowFallback(Exception ex, string window);

    [LoggerMessage(Level = LogLevel.Information, Message = "Captured window \"{Window}\" {Width}x{Height} via {Backend} (unobstructed: {Unobstructed})")]
    private partial void LogWindowCaptured(string window, int width, int height, CaptureBackend backend, bool unobstructed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Windows.Graphics.Capture failed for {Monitor}; falling back to GDI")]
    private partial void LogFallback(Exception ex, string monitor);
}
