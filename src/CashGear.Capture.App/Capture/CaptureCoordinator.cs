using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Region;
using CashGear.Capture.App.Scrolling;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.History;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Ocr;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Selection;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Capture;

/// <summary>
/// Runs a capture end to end: hide our own windows, wait the capture delay, capture, then do the configured
/// after-capture actions (save under the file-name template, copy to the clipboard, open the editor) and notify.
/// Failures are reported to the user here rather than thrown.
/// </summary>
public sealed partial class CaptureCoordinator
{
    private readonly IScreenCaptureService _capture;
    private readonly IMonitorService _monitors;
    private readonly IWindowService _windows;
    private readonly IOutputService _output;
    private readonly ISettingsService _settings;
    private readonly AppPaths _paths;
    private readonly ITrayNotifier _notifier;
    private readonly IUiDispatcher _ui;
    private readonly IOwnWindows _ownWindows;
    private readonly RegionSelector _regionSelector;
    private readonly ICaptureCounter _counter;
    private readonly IEditorLauncher _editor;
    private readonly ScrollingCaptureService _scrolling;
    private readonly IScrollPlatform _scrollPlatform;
    private readonly IOcrService _ocr;
    private readonly ICaptureCountdown _countdown;
    private readonly ICaptureHistory _history;
    private readonly IShell _shell;
    private readonly TimeProvider _time;
    private readonly ILogger<CaptureCoordinator> _logger;
    private int _busy;

    public CaptureCoordinator(
        IScreenCaptureService capture,
        IMonitorService monitors,
        IWindowService windows,
        IOutputService output,
        ISettingsService settings,
        AppPaths paths,
        ITrayNotifier notifier,
        IUiDispatcher ui,
        IOwnWindows ownWindows,
        RegionSelector regionSelector,
        ICaptureCounter counter,
        IEditorLauncher editor,
        ScrollingCaptureService scrolling,
        IScrollPlatform scrollPlatform,
        IOcrService ocr,
        ICaptureCountdown countdown,
        ICaptureHistory history,
        IShell shell,
        TimeProvider time,
        ILogger<CaptureCoordinator> logger)
    {
        _capture = capture;
        _monitors = monitors;
        _windows = windows;
        _output = output;
        _settings = settings;
        _paths = paths;
        _notifier = notifier;
        _ui = ui;
        _ownWindows = ownWindows;
        _regionSelector = regionSelector;
        _counter = counter;
        _editor = editor;
        _scrolling = scrolling;
        _scrollPlatform = scrollPlatform;
        _ocr = ocr;
        _countdown = countdown;
        _history = history;
        _shell = shell;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Captures every monitor once and keeps, per <see cref="CaptureSettings.FullScreenOutput"/>, the stitched
    /// virtual-screen image and/or one image per monitor (cropped from the same capture, so all files show the
    /// same instant). The stitched image goes to the clipboard and the editor.
    /// </summary>
    public Task CaptureFullScreenAsync(CancellationToken ct = default) => RunAsync(async current =>
    {
        using var hidden = await PrepareAsync(current, hideOwnUi: true, ct).ConfigureAwait(false);

        var displays = _monitors.GetMonitors();
        using var all = await _capture.CaptureAllMonitorsAsync(ct).ConfigureAwait(false);
        LogCaptured(all.Width, all.Height, all.Metadata.Backend);

        var mode = current.Capture.FullScreenOutput;
        var files = new List<CapturedImage>();
        try
        {
            if (mode != FullScreenOutput.EachMonitor || displays.Count == 1)
            {
                files.Add(all);
            }

            if (current.AfterCapture.Save && mode != FullScreenOutput.AllMonitors && displays.Count > 1)
            {
                foreach (var monitor in displays)
                {
                    var crop = all.Crop(VirtualScreen.ToLocal(monitor.Bounds, all.Metadata.SourceBounds));
                    crop.Metadata = crop.Metadata with
                    {
                        Kind = CaptureKind.Monitor,
                        MonitorName = monitor.ShortName,
                        IsPrimaryMonitor = monitor.IsPrimary,
                        Dpi = monitor.Scale,
                        HasTransparency = false,
                    };
                    files.Add(crop);
                }
            }

            await DeliverAsync(all, files, current, hidden, ct).ConfigureAwait(false);
        }
        finally
        {
            foreach (var file in files.Where(f => !ReferenceEquals(f, all)))
            {
                file.Dispose();
            }
        }
    });

    /// <summary>
    /// Freezes the screen, lets the user pick a rectangle or window on the overlay, and crops the result from the
    /// frozen image — pixel-exact, with no second capture.
    /// </summary>
    public Task CaptureRegionAsync(CancellationToken ct = default) => CaptureSelectionAsync(CaptureMode.Region, ct);

    /// <summary>A frame of a set size (the last size used; Tab picks another on the overlay).</summary>
    public Task CaptureFixedRegionAsync(CancellationToken ct = default) => CaptureSelectionAsync(CaptureMode.FixedRegion, ct);

    /// <summary>An outline drawn by hand; transparent outside it.</summary>
    public Task CaptureFreehandAsync(CancellationToken ct = default) => CaptureSelectionAsync(CaptureMode.Freehand, ct);

    /// <summary>A polygon clicked corner by corner; transparent outside it.</summary>
    public Task CapturePolygonAsync(CancellationToken ct = default) => CaptureSelectionAsync(CaptureMode.Polygon, ct);

    /// <summary>A rectangle (or window) with rounded corners; transparent outside them.</summary>
    public Task CaptureRoundedRegionAsync(CancellationToken ct = default) => CaptureSelectionAsync(CaptureMode.RoundedRegion, ct);

    /// <summary>
    /// The captures chosen on the frozen screen: the overlay works in the mode's way, and the result is cropped from
    /// the frozen image (pixel-exact, no second capture) and made transparent outside its shape.
    /// </summary>
    private Task CaptureSelectionAsync(CaptureMode mode, CancellationToken ct) => RunAsync(async current =>
    {
        using var hidden = await PrepareAsync(current, hideOwnUi: true, ct).ConfigureAwait(false);

        // Snapshot the windows before the overlay exists, so it never offers itself as a target.
        var displays = _monitors.GetMonitors();
        var windows = _windows.GetVisibleWindows();
        using var frozen = await _capture.CaptureAllMonitorsAsync(ct).ConfigureAwait(false);

        var options = OptionsFor(mode, current.Capture);
        var selection = await _ui.InvokeAsync(() => _regionSelector.SelectAsync(frozen, displays, windows, options, ct)).ConfigureAwait(false);
        if (selection is null)
        {
            return; // cancelled by the user; nothing to report
        }

        await RememberChoicesAsync(mode, selection, current.Capture).ConfigureAwait(false);
        var kind = selection.Window is null ? CaptureKind.Region : CaptureKind.Window;
        using var image = Cut(frozen, selection.Bounds, selection.Shape, displays, kind, selection.Window?.Title);
        LogRegionCaptured(selection.Bounds, image.Metadata.Kind);

        await DeliverAsync(image, [image], current, hidden, ct).ConfigureAwait(false);
    });

    /// <summary>
    /// Crops <paramref name="bounds"/> from <paramref name="source"/> (a capture covering it, in virtual-screen
    /// pixels) and makes it transparent outside <paramref name="shape"/>. The metadata names the monitor it lies on.
    /// </summary>
    internal static CapturedImage Cut(
        CapturedImage source, PixelRect bounds, CaptureShape? shape, IReadOnlyList<MonitorInfo> displays, CaptureKind kind, string? title)
    {
        var image = source.Crop(VirtualScreen.ToLocal(bounds, source.Metadata.SourceBounds));
        var shaped = shape is not null && !shape.IsRectangle;
        if (shaped)
        {
            ShapeMask.Apply(image, shape!);
        }

        var onMonitor = displays.FirstOrDefault(m => m.Bounds.Contains(bounds));
        var covered = displays.Select(m => m.Bounds).Distinct().Sum(b => b.Intersect(bounds).Area);
        image.Metadata = image.Metadata with
        {
            Kind = kind,
            WindowTitle = title,
            MonitorName = onMonitor?.ShortName,
            IsPrimaryMonitor = onMonitor?.IsPrimary ?? false,
            Dpi = onMonitor?.Scale ?? DpiScale.Identity,
            HasTransparency = covered < bounds.Area || shaped,
            IsNonRectangular = shaped,
        };
        return image;
    }

    private static SelectionOptions OptionsFor(CaptureMode mode, CaptureSettings capture)
    {
        var (sizes, index) = FixedSizes.With(capture.FixedRegionSize);
        var selection = mode switch
        {
            CaptureMode.FixedRegion => SelectionMode.FixedSize,
            CaptureMode.Freehand => SelectionMode.Freehand,
            CaptureMode.Polygon => SelectionMode.Polygon,
            CaptureMode.RoundedRegion => SelectionMode.RoundedRectangle,
            _ => SelectionMode.RegionOrWindow,
        };
        return new SelectionOptions(selection) { FixedSizes = sizes, FixedSizeIndex = index, CornerRadius = capture.CornerRadius };
    }

    /// <summary>The fixed size or corner radius chosen on the overlay is where the next such capture starts.</summary>
    private Task RememberChoicesAsync(CaptureMode mode, RegionSelectionResult selection, CaptureSettings capture)
    {
        if (mode == CaptureMode.FixedRegion
            && (selection.Bounds.Width, selection.Bounds.Height) is var (width, height)
            && (width, height) != (capture.FixedRegionWidth, capture.FixedRegionHeight))
        {
            return _settings.UpdateAsync(s => s with { Capture = s.Capture with { FixedRegionWidth = width, FixedRegionHeight = height } });
        }

        var radius = selection.Shape?.Radius ?? 0;
        if (mode == CaptureMode.RoundedRegion && radius != capture.CornerRadius)
        {
            return _settings.UpdateAsync(s => s with { Capture = s.Capture with { CornerRadius = radius } });
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Lets the user pick a window on the overlay, then captures that window's own content (WGC sees it even
    /// where other windows cover it).
    /// </summary>
    public Task CaptureWindowAsync(CancellationToken ct = default) => RunAsync(async current =>
    {
        using var hidden = await PrepareAsync(current, hideOwnUi: true, ct).ConfigureAwait(false);

        var displays = _monitors.GetMonitors();
        var windows = _windows.GetVisibleWindows();
        RegionSelectionResult? selection;
        using (var frozen = await _capture.CaptureAllMonitorsAsync(ct).ConfigureAwait(false))
        {
            selection = await _ui.InvokeAsync(
                () => _regionSelector.SelectAsync(frozen, displays, windows, SelectionMode.WindowOnly, ct)).ConfigureAwait(false);
        }

        if (selection?.Window is not { } window)
        {
            return; // cancelled by the user
        }

        // Let the overlay leave the screen before a screen-based capture of a fully visible window.
        await Task.Delay(TimeSpan.FromMilliseconds(current.Capture.HideDelayMs), _time, ct).ConfigureAwait(false);
        await CaptureWindowCoreAsync(window.Handle, current, hidden, ct).ConfigureAwait(false);
    });

    /// <summary>
    /// Lets the user pick a window (the click also points at the part that scrolls), then scrolls it from where it is
    /// down to the bottom and stitches one tall image.
    /// </summary>
    public Task CaptureScrollingAsync(CancellationToken ct = default) => RunAsync(async current =>
    {
        using var hidden = await PrepareAsync(current, hideOwnUi: true, ct).ConfigureAwait(false);

        var displays = _monitors.GetMonitors();
        var windows = _windows.GetVisibleWindows();
        RegionSelectionResult? selection;
        using (var frozen = await _capture.CaptureAllMonitorsAsync(ct).ConfigureAwait(false))
        {
            selection = await _ui.InvokeAsync(
                () => _regionSelector.SelectAsync(frozen, displays, windows, SelectionMode.WindowOnly, ct)).ConfigureAwait(false);
        }

        if (selection?.Window is not { } window)
        {
            return; // cancelled by the user
        }

        // The overlay has just closed under the click, so the mouse is still on the part the user pointed at.
        var result = await _scrolling.CaptureAsync(window, _scrollPlatform.CursorPosition, ct).ConfigureAwait(false);
        if (result is null)
        {
            return; // Esc or Cancel
        }

        using var image = result.Image;
        if (Explain(result) is { } explanation)
        {
            var kind = result.Reason is ScrollStopReason.MaxHeight or ScrollStopReason.ReachedEnd ? NotificationKind.Info : NotificationKind.Warning;
            _notifier.Notify("Scrolling capture", explanation, kind);
        }

        await DeliverAsync(image, [image], current, hidden, ct).ConfigureAwait(false);
    });

    /// <summary>Why a scrolling capture ended, when it is worth telling (null when it simply reached the bottom).</summary>
    private static string? Explain(ScrollingResult result) => result.Reason switch
    {
        ScrollStopReason.ReachedEnd when result.Frames == 1 => "Nothing scrolled, so only the visible part was captured.",
        ScrollStopReason.MaxHeight => $"Stopped at the height limit ({result.Image.Height} px). It can be raised in Settings > Capture.",
        ScrollStopReason.LostTrack => $"Stopped early at {result.Image.Height} px: the next part did not line up with the last (the page may change as it scrolls).",
        ScrollStopReason.Resized => $"Stopped at {result.Image.Height} px: the window changed size.",
        _ => null,
    };

    /// <summary>
    /// Capture Text: the user selects an area (or clicks a window) on the frozen screen, the text in it is
    /// recognized (Windows OCR, in the configured language or automatically), and the text goes to the clipboard.
    /// Nothing is saved and the after-capture actions don't apply.
    /// </summary>
    public Task CaptureTextAsync(CancellationToken ct = default) => RunAsync(
        async current =>
        {
            using var hidden = await PrepareAsync(current, hideOwnUi: true, ct).ConfigureAwait(false);

            var displays = _monitors.GetMonitors();
            var windows = _windows.GetVisibleWindows();
            using var frozen = await _capture.CaptureAllMonitorsAsync(ct).ConfigureAwait(false);
            var selection = await _ui.InvokeAsync(() => _regionSelector.SelectAsync(frozen, displays, windows, ct: ct)).ConfigureAwait(false);
            if (selection is null)
            {
                return; // cancelled by the user
            }

            using var image = frozen.Crop(VirtualScreen.ToLocal(selection.Bounds, frozen.Metadata.SourceBounds));
            var result = await _ocr.RecognizeAsync(image, current.Ocr.Language, ct).ConfigureAwait(false);
            var language = string.Join(" + ", result.LanguageTag.Split('+').Select(
                tag => _ocr.AvailableLanguages.FirstOrDefault(l => l.Tag == tag)?.DisplayName ?? tag));
            if (result.IsEmpty)
            {
                _notifier.Notify("Capture Text", $"No text was found in the selected area ({language}).", NotificationKind.Warning);
                return;
            }

            await _output.CopyTextToClipboardAsync(result.Text, ct).ConfigureAwait(false);
            var lines = result.Text.Split("\r\n").Count(l => l.Length > 0);
            _notifier.Notify("Text copied", $"{OcrText.Preview(result.Text)}\n{lines} line{(lines == 1 ? string.Empty : "s")} · {language}");
        },
        producesImage: false);

    /// <summary>Captures the window that was in the foreground when the hotkey was pressed.</summary>
    public Task CaptureActiveWindowAsync(nint hwnd, CancellationToken ct = default) => RunAsync(async current =>
    {
        if (hwnd == 0 || _windows.GetWindow(hwnd) is not { } window || IsShell(window))
        {
            _notifier.Notify("Capture Active Window", "There is no active window to capture. Click a window first.");
            return;
        }

        // Our own UI is not involved (and the active window may even be ours), so nothing is hidden.
        using var hidden = await PrepareAsync(current, hideOwnUi: false, ct).ConfigureAwait(false);
        await CaptureWindowCoreAsync(window.Handle, current, hidden, ct).ConfigureAwait(false);
    });

    private async Task CaptureWindowCoreAsync(nint hwnd, AppSettings current, IDisposable hidden, CancellationToken ct)
    {
        var options = new CaptureWindowOptions
        {
            IncludeCursor = current.Capture.IncludeCursor,
            IncludeShadow = current.Capture.IncludeWindowShadow,
        };
        using var image = await _capture.CaptureWindowAsync(hwnd, options, ct).ConfigureAwait(false);
        await DeliverAsync(image, [image], current, hidden, ct).ConfigureAwait(false);
    }

    /// <summary>The desktop and taskbars are "active" when nothing else is; they are not windows to capture.</summary>
    private static bool IsShell(WindowInfo window) =>
        window.ClassName is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";

    /// <summary>True while a capture runs (from the countdown to the notification or editor).</summary>
    public bool IsBusy => Volatile.Read(ref _busy) != 0;

    /// <summary>
    /// Runs one capture at a time. Hotkeys, the tray and command-line switches can all start one; a request while
    /// another is counting down or selecting is ignored rather than stacking a second overlay.
    /// </summary>
    /// <param name="producesImage">False for Capture Text, which copies text and ignores the after-capture actions.</param>
    private async Task RunAsync(Func<AppSettings, Task> capture, bool producesImage = true)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            LogBusy();
            return;
        }

        try
        {
            await RunOneAsync(capture, producesImage).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    private async Task RunOneAsync(Func<AppSettings, Task> capture, bool producesImage)
    {
        var current = _settings.Current;
        if (producesImage && !current.AfterCapture.DoesAnything)
        {
            // The capture would just be thrown away.
            _notifier.Notify(
                "Nothing to do with the capture",
                "Turn on \"Open in the editor\", \"Save to file\" or \"Copy to clipboard\" in Settings › After capture.",
                NotificationKind.Warning);
            return;
        }

        try
        {
            await capture(current).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            LogCancelled();
        }
        catch (Exception ex)
        {
            LogFailed(ex);
            _notifier.Notify("Capture failed", ex.Message, NotificationKind.Error);
        }
    }

    /// <summary>
    /// Hides our windows, counts down the user's capture delay (on screen, with a Cancel button), then waits for the
    /// tray menu and hidden windows to fade out. Dispose the result to bring our windows back.
    /// </summary>
    private async Task<IDisposable> PrepareAsync(AppSettings current, bool hideOwnUi, CancellationToken ct)
    {
        var hidden = hideOwnUi ? await _ownWindows.HideAsync().ConfigureAwait(false) : NothingHidden.Instance;
        try
        {
            await _countdown.RunAsync(current.Capture.DelaySeconds, ct).ConfigureAwait(false);

            if (hideOwnUi)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(current.Capture.HideDelayMs), _time, ct).ConfigureAwait(false);
            }

            return hidden;
        }
        catch
        {
            hidden.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Saves <paramref name="files"/> (if saving is on) under the file-name template, copies <paramref name="primary"/>
    /// (if copying is on) and opens it in the editor (if that is on), else tells the user what happened (clicking
    /// the notification shows the file). <paramref name="primary"/> also goes into the history, alongside the rest.
    /// <paramref name="hidden"/> brings our windows back; that happens before the editor opens, so the new editor
    /// ends up in front of them.
    /// </summary>
    private async Task DeliverAsync(
        CapturedImage primary, IReadOnlyList<CapturedImage> files, AppSettings current, IDisposable hidden, CancellationToken ct)
    {
        var after = current.AfterCapture;
        var saved = new List<string>();
        if (after.Save)
        {
            var template = Template(current.Output, distinguishMonitors: files.Count > 1);
            var folder = _paths.ResolveCaptureDirectory(current.Output);
            foreach (var file in files)
            {
                var name = FileNameTemplate.Expand(template, Context(file), _counter.Next);
                var options = new SaveOptions(folder, name, FormatFor(file, current.Output), current.Output.JpegQuality);
                saved.Add(await _output.SaveAsync(file, options, ct).ConfigureAwait(false)
                    ?? throw new OperationCanceledException("Save was cancelled."));
            }
        }

        string? savedAs = null;
        for (var i = 0; i < saved.Count; i++)
        {
            savedAs = ReferenceEquals(files[i], primary) ? saved[i] : savedAs;
        }

        // Kept in the history while the rest happens; the image must stay alive until both are done.
        var pngCopy = FormatFor(primary, current.Output) == ImageFileFormat.Png ? savedAs : null;
        var history = _history.AddAsync(primary, savedAs, pngCopy, CancellationToken.None);
        try
        {
            if (after.CopyToClipboard)
            {
                await _output.CopyToClipboardAsync(primary, ct).ConfigureAwait(false);
            }

            if (after.OpenEditor)
            {
                // The editor shows the result, so no notification. If the image was saved, Ctrl+S in the editor updates that file.
                hidden.Dispose();
                await _editor.OpenAsync(primary, savedAs, ct).ConfigureAwait(false);
                return;
            }

            var shown = saved.FirstOrDefault();
            _notifier.Notify(
                saved.Count > 0 ? "Capture saved" : "Capture copied",
                Summary(saved, after.CopyToClipboard),
                onClick: shown is null ? null : () => _shell.ShowInFolder(shown));
        }
        finally
        {
            await history.ConfigureAwait(false);
        }
    }

    /// <summary>The configured format, except that a shaped capture is always PNG (the only format that keeps its transparency).</summary>
    private static ImageFileFormat FormatFor(CapturedImage image, OutputSettings output) =>
        image.Metadata.IsNonRectangular ? ImageFileFormat.Png : output.Format;

    /// <summary>The configured template, or the default if a hand edit broke it; several files from one capture get {monitor} so their names differ.</summary>
    private string Template(OutputSettings output, bool distinguishMonitors)
    {
        var template = output.FileNameTemplate;
        if (!FileNameTemplate.TryValidate(template, out var error))
        {
            LogBadTemplate(template, error);
            template = FileNameTemplate.Default;
        }

        return distinguishMonitors && !FileNameTemplate.Uses(template, "monitor") ? template + "_{monitor}" : template;
    }

    private static FileNameContext Context(CapturedImage image) => new(
        image.Metadata.CapturedAt,
        image.Metadata.Kind,
        image.Metadata.Kind == CaptureKind.AllMonitors ? null : image.Metadata.MonitorName,
        image.Metadata.WindowTitle);

    private static string Summary(IReadOnlyList<string> saved, bool copied)
    {
        var lines = new List<string>(3);
        if (saved.Count > 0)
        {
            lines.Add(saved.Count == 1
                ? Path.GetFileName(saved[0])
                : $"{Path.GetFileName(saved[0])} and {saved.Count - 1} more");
            lines.Add("in " + Path.GetDirectoryName(saved[0]) + " (click to show)");
        }

        if (copied)
        {
            lines.Add("Copied to the clipboard.");
        }

        return string.Join('\n', lines);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Captured full screen {Width}x{Height} via {Backend}")]
    private partial void LogCaptured(int width, int height, CaptureBackend backend);

    [LoggerMessage(Level = LogLevel.Information, Message = "Captured {Kind} {Bounds} from the frozen screen")]
    private partial void LogRegionCaptured(PixelRect bounds, CaptureKind kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "File-name template \"{Template}\" is invalid ({Error}); using the default")]
    private partial void LogBadTemplate(string template, string? error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Capture cancelled")]
    private partial void LogCancelled();

    [LoggerMessage(Level = LogLevel.Information, Message = "A capture is already in progress; the new request was ignored")]
    private partial void LogBusy();

    [LoggerMessage(Level = LogLevel.Error, Message = "Capture failed")]
    private partial void LogFailed(Exception ex);

    private sealed class NothingHidden : IDisposable
    {
        public static readonly NothingHidden Instance = new();

        public void Dispose()
        {
        }
    }
}
