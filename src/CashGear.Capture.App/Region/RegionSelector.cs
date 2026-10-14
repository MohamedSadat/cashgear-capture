using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CashGear.Capture.App.Output;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Selection;
using CashGear.Capture.Interop.Native;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CashGear.Capture.App.Region;

/// <summary>
/// Shows the region-selection overlay (one window over the whole virtual screen) on top of a frozen screenshot and reports what the user
/// picked. The result is cropped by the caller from the same frozen image, so it is pixel-exact by construction.
/// </summary>
public sealed partial class RegionSelector(ILogger<RegionSelector> logger)
{
    /// <summary>
    /// Opens the overlay. Must be called on the UI thread. Completes with null when the user cancels (Esc,
    /// right-click), when <paramref name="ct"/> fires, or when the display layout changes.
    /// </summary>
    /// <param name="frozen">The all-monitors capture to select from; must stay alive until the task completes.</param>
    public Task<RegionSelectionResult?> SelectAsync(
        CapturedImage frozen,
        IReadOnlyList<MonitorInfo> monitors,
        IReadOnlyList<WindowInfo> windows,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        var dispatcher = Dispatcher.CurrentDispatcher;
        var displays = monitors.DistinctBy(m => m.Bounds).ToList();
        var bounds = displays.Select(m => m.Bounds).ToList();
        var model = new RegionSelectionModel(bounds, windows, CursorPosition());
        var completion = new TaskCompletionSource<RegionSelectionResult?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var virtualBounds = model.VirtualBounds;
        var background = frozen.Metadata.SourceBounds == virtualBounds
            ? frozen.ToBitmapSource()
            : CropToBitmap(frozen, virtualBounds);
        var overlay = new RegionOverlayWindow(virtualBounds, displays, background, model, p => ColorAt(frozen, p), logger);

        CancellationTokenRegistration registration = default;
        EventHandler onDisplayChange = (_, _) => dispatcher.BeginInvoke(model.Cancel);

        void OnChanged(object? sender, EventArgs e)
        {
            if (!model.IsFinished)
            {
                return;
            }

            model.Changed -= OnChanged;
            SystemEvents.DisplaySettingsChanged -= onDisplayChange;
            registration.Dispose();
            overlay.Close();

            if (model.Result is { } result)
            {
                LogSelected(result.Bounds, result.Window?.Title);
            }
            else
            {
                LogCancelled();
            }

            completion.TrySetResult(model.Result);
        }

        model.Changed += OnChanged;
        SystemEvents.DisplaySettingsChanged += onDisplayChange; // monitor geometry no longer matches the frozen image
        registration = ct.Register(() => dispatcher.BeginInvoke(model.Cancel));

        overlay.Show();

        // Foreground + full-screen is what makes the shell drop every taskbar below the overlay. Works when this
        // process may take the foreground (tray click, hotkey, or a launch that called AllowSetForegroundWindow).
        var activated = overlay.Activate();
        Keyboard.Focus(overlay);
        LogOpened(virtualBounds, displays.Count, windows.Count, model.Cursor, activated);

        return completion.Task;
    }

    private static BitmapSource CropToBitmap(CapturedImage frozen, PixelRect bounds)
    {
        using var crop = frozen.Crop(VirtualScreen.ToLocal(bounds, frozen.Metadata.SourceBounds));
        return crop.ToBitmapSource();
    }

    private static unsafe PixelPoint CursorPosition()
    {
        POINT point;
        return User32.GetCursorPos(&point) ? new PixelPoint(point.X, point.Y) : default;
    }

    /// <summary>The frozen pixel at a virtual-screen point as 0xAARRGGBB, or null outside the image.</summary>
    private static uint? ColorAt(CapturedImage frozen, PixelPoint point)
    {
        var local = VirtualScreen.ToLocal(point, frozen.Metadata.SourceBounds);
        if (local.X < 0 || local.Y < 0 || local.X >= frozen.Width || local.Y >= frozen.Height)
        {
            return null;
        }

        var px = frozen.GetRow(local.Y).Slice(local.X * CapturedImage.BytesPerPixel, CapturedImage.BytesPerPixel);
        return ((uint)px[3] << 24) | ((uint)px[2] << 16) | ((uint)px[1] << 8) | px[0];
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Region overlay opened over {Bounds} ({Monitors} monitor(s), {Windows} pickable window(s), cursor {Cursor}, activated {Activated})")]
    private partial void LogOpened(PixelRect bounds, int monitors, int windows, PixelPoint cursor, bool activated);

    [LoggerMessage(Level = LogLevel.Information, Message = "Region selected {Bounds} (window: {Window})")]
    private partial void LogSelected(PixelRect bounds, string? window);

    [LoggerMessage(Level = LogLevel.Information, Message = "Region selection cancelled")]
    private partial void LogCancelled();
}
