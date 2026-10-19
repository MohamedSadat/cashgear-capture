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
public sealed partial class RegionSelector(IObjectFinder objects, ILogger<RegionSelector> logger)
{
    private bool _isOpen; // UI thread only

    /// <summary>An overlay is showing (UI thread; for tests that drive it).</summary>
    internal bool IsOpen => _isOpen;

    /// <summary>
    /// Opens the overlay. Must be called on the UI thread. Completes with null when the user cancels (Esc,
    /// right-click), when <paramref name="ct"/> fires, or when the display layout changes — and straight away when an
    /// overlay is already open (e.g. a recording's area pick while a capture is selecting), so two never stack.
    /// </summary>
    /// <param name="frozen">The all-monitors capture to select from; must stay alive until the task completes.</param>
    public Task<RegionSelectionResult?> SelectAsync(
        CapturedImage frozen,
        IReadOnlyList<MonitorInfo> monitors,
        IReadOnlyList<WindowInfo> windows,
        SelectionMode mode = SelectionMode.RegionOrWindow,
        CancellationToken ct = default) =>
        SelectAsync(frozen, monitors, windows, new SelectionOptions(mode), ct);

    /// <summary>As above, with the options of the capture mode (fixed sizes, corner radius).</summary>
    public Task<RegionSelectionResult?> SelectAsync(
        CapturedImage frozen,
        IReadOnlyList<MonitorInfo> monitors,
        IReadOnlyList<WindowInfo> windows,
        SelectionOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(frozen);
        ArgumentNullException.ThrowIfNull(options);
        if (_isOpen)
        {
            LogAlreadyOpen();
            return Task.FromResult<RegionSelectionResult?>(null);
        }

        var dispatcher = Dispatcher.CurrentDispatcher;
        var displays = monitors.DistinctBy(m => m.Bounds).ToList();
        var bounds = displays.Select(m => m.Bounds).ToList();
        var model = SelectionModels.Create(options, bounds, windows, CursorPosition());
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
            _isOpen = false;

            if (model.Result is { } result)
            {
                LogSelected(options.Mode, result.Bounds, result.Window?.Title, result.Shape?.Kind);
            }
            else
            {
                LogCancelled();
            }

            completion.TrySetResult(model.Result);
        }

        model.Changed += OnChanged;
        if (model is ObjectSelectionModel objectModel)
        {
            var lookups = new ObjectLookups(objectModel, objects);
            objectModel.LookupNeeded += (_, lookup) => lookups.Request(lookup);
            if (objectModel.HoverWindow is { } first)
            {
                lookups.Request(new ObjectLookup(first, objectModel.Cursor));
            }
        }

        SystemEvents.DisplaySettingsChanged += onDisplayChange; // monitor geometry no longer matches the frozen image
        registration = ct.Register(() => dispatcher.BeginInvoke(model.Cancel));

        _isOpen = true;
        overlay.Show();

        // Foreground + full-screen is what makes the shell drop every taskbar below the overlay. Works when this
        // process may take the foreground (tray click, hotkey, or a launch that called AllowSetForegroundWindow).
        var activated = overlay.Activate();
        Keyboard.Focus(overlay);
        LogOpened(options.Mode, virtualBounds, displays.Count, windows.Count, model.Cursor, activated);

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

    /// <summary>
    /// Asks the object finder for what is under the cursor, one lookup at a time (on the UI thread): a request made
    /// while one runs replaces any other waiting, so a fast-moving cursor costs one lookup per answer, not per move.
    /// </summary>
    private sealed class ObjectLookups(ObjectSelectionModel model, IObjectFinder finder)
    {
        private bool _busy;
        private ObjectLookup? _next;

        public void Request(ObjectLookup lookup)
        {
            if (_busy)
            {
                _next = lookup;
                return;
            }

            _ = RunAsync(lookup);
        }

        private async Task RunAsync(ObjectLookup lookup)
        {
            _busy = true;
            try
            {
                var found = await finder.FindAtAsync(lookup.Window.Handle, lookup.Point);
                model.SetObjects(lookup.Window.Handle, lookup.Point, found);
            }
            catch (OperationCanceledException)
            {
                // dropped for a newer one
            }
            finally
            {
                _busy = false;
            }

            if (_next is { } next && !model.IsFinished)
            {
                _next = null;
                await RunAsync(next);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Selection overlay ({Mode}) opened over {Bounds} ({Monitors} monitor(s), {Windows} pickable window(s), cursor {Cursor}, activated {Activated})")]
    private partial void LogOpened(SelectionMode mode, PixelRect bounds, int monitors, int windows, PixelPoint cursor, bool activated);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Mode} selected {Bounds} (window: {Window}, shape: {Shape})")]
    private partial void LogSelected(SelectionMode mode, PixelRect bounds, string? window, ShapeKind? shape);

    [LoggerMessage(Level = LogLevel.Information, Message = "Region selection cancelled")]
    private partial void LogCancelled();

    [LoggerMessage(Level = LogLevel.Information, Message = "An overlay is already open; the new selection was not started")]
    private partial void LogAlreadyOpen();
}
