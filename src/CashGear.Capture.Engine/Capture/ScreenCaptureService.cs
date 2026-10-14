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
/// captures. Every capture is made per monitor and blitted into a canvas in virtual-screen space; canvas
/// areas no monitor covers stay fully transparent.
/// </summary>
internal sealed partial class ScreenCaptureService : IScreenCaptureService
{
    private readonly IMonitorService _monitors;
    private readonly ISettingsService _settings;
    private readonly CaptureBackendSelector _selector;
    private readonly IReadOnlyDictionary<CaptureBackend, IFrameGrabber> _grabbers;
    private readonly ArrayPool<byte> _pool;
    private readonly TimeProvider _time;
    private readonly ILogger<ScreenCaptureService> _logger;

    public ScreenCaptureService(
        IMonitorService monitors,
        ISettingsService settings,
        CaptureBackendSelector selector,
        IEnumerable<IFrameGrabber> grabbers,
        ArrayPool<byte> pool,
        TimeProvider time,
        ILogger<ScreenCaptureService> logger)
    {
        _monitors = monitors;
        _settings = settings;
        _selector = selector;
        _grabbers = grabbers.ToDictionary(g => g.Backend);
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
        return CaptureAreaAsync(monitor, monitor.Bounds, ScreenBackend(), ct);
    }

    public Task<CapturedImage> CaptureAllMonitorsAsync(CancellationToken ct = default)
    {
        var monitors = _monitors.GetMonitors();
        if (monitors.Count == 0)
        {
            throw new CaptureException("No monitors found.");
        }

        var bounds = VirtualScreen.GetBounds(monitors.Select(m => m.Bounds));
        return ComposeAsync(bounds, CaptureKind.AllMonitors, monitors, ct);
    }

    public Task<CapturedImage> CaptureRegionAsync(PixelRect region, CancellationToken ct = default)
    {
        if (region.IsEmpty)
        {
            throw new ArgumentException("Region is empty.", nameof(region));
        }

        return ComposeAsync(region, CaptureKind.Region, _monitors.GetMonitors(), ct);
    }

    public Task<CapturedImage> CaptureWindowAsync(nint hwnd, CaptureWindowOptions options, CancellationToken ct = default) =>
        throw new NotSupportedException("Window capture arrives in Phase 3.");

    private CaptureBackend ScreenBackend() => _selector.ForScreen(_settings.Current.Capture.Backend);

    private async Task<CapturedImage> ComposeAsync(
        PixelRect target, CaptureKind kind, IReadOnlyList<MonitorInfo> monitors, CancellationToken ct)
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

        var backend = ScreenBackend();
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
                using var part = await CaptureAreaAsync(monitor, area, backend, ct).ConfigureAwait(false);
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
        MonitorInfo monitor, PixelRect area, CaptureBackend backend, CancellationToken ct)
    {
        var options = _settings.Current.Capture;
        var request = new FrameRequest(monitor, area, options.IncludeCursor, TimeSpan.FromMilliseconds(options.WgcFrameTimeoutMs));

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Windows.Graphics.Capture failed for {Monitor}; falling back to GDI")]
    private partial void LogFallback(Exception ex, string monitor);
}
