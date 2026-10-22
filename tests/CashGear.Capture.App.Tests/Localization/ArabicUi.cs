using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CashGear.Capture.App.Output;
using CashGear.Capture.Engine.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Engine.Capture.Gdi;
using CashGear.Capture.Engine.Display;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Localization;

/// <summary>
/// Checks a window shown in Arabic: laid out right to left, its text in Arabic, every control named for a screen
/// reader and reachable with Tab, its pixel surfaces left to right. Each window is also saved as a PNG in
/// <c>artifacts\arabic-ui</c>, to be looked at.
/// </summary>
internal static partial class ArabicUi
{
    /// <summary>
    /// Latin words that stay Latin in Arabic: product, format and technology names, units, key names
    /// (docs\localization.md).
    /// </summary>
    private static readonly HashSet<string> LatinWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "CashGear", "Capture", "Windows", "FFmpeg", "Whisper", "Sentry", "Velopack", "Narrator",
        "PNG", "JPEG", "JPG", "BMP", "GIF", "PDF", "MP4", "MP3", "M4A", "WebM", "WebP", "SRT", "WebVTT", "VTT", "JSON", "CSV",
        "H", "HEVC", "AV1", "VP9", "AAC", "Opus", "NVENC", "AMF", "QSV", "QuickSync", "Media", "Foundation", "Direct3D", "Vulkan", "CPU", "GPU",
        "FTP", "FTPS", "SFTP", "HTTP", "HTTPS", "URL", "DPI", "HDR", "SDR", "OCR", "UI", "RNNoise", "RGB", "HEX", "HSL", "HSV", "CSS", "C#", "WGC", "GDI",
        "Word", "PowerPoint", "Office", "Explorer", "Imgur",
        "px", "pt", "ms", "fps", "dB", "Hz", "kHz", "kbps", "Mbps", "KB", "MB", "GB", "x", "cm", "mm", "in",
        "Ctrl", "Alt", "Shift", "Win", "Tab", "Esc", "Enter", "Space", "PrintScreen", "Delete", "Del", "Home", "End",
        "PageUp", "PageDown", "Insert", "Backspace", "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
        "Up", "Down", "Left", "Right", "Ins",
    };

    /// <summary>Lays windows made on this thread out in Arabic (the app does this once, at start-up).</summary>
    public static IDisposable InArabic()
    {
        var before = (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("ar");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
        return new Restore(() => (CultureInfo.CurrentUICulture, CultureInfo.CurrentCulture) = before);
    }

    /// <summary>Lets the dispatcher lay out and render what is queued.</summary>
    public static void Settle()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    /// <summary>Waits <paramref name="time"/> while the dispatcher keeps running.</summary>
    public static async Task Pump(TimeSpan time)
    {
        await Task.Delay(time);
        Settle();
    }

    /// <summary>
    /// What is wrong with <paramref name="root"/> (a window, or a menu's content): text that isn't Arabic, controls
    /// without a name, islands that should be left to right but aren't. <paramref name="data"/> is text that comes
    /// from the user's data (file names, values) rather than from the app.
    /// </summary>
    public static List<string> Problems(FrameworkElement root, IEnumerable<FrameworkElement>? leftToRight = null, Func<string, bool>? data = null)
    {
        var problems = new List<string>();
        if (root.FlowDirection != FlowDirection.RightToLeft && root is Window or ContextMenu)
        {
            problems.Add($"{Describe(root)} is laid out {root.FlowDirection}");
        }

        foreach (var island in leftToRight ?? [])
        {
            if (island.FlowDirection != FlowDirection.LeftToRight)
            {
                problems.Add($"{Describe(island)} should stay left to right");
            }
        }

        foreach (var element in Visible(root))
        {
            if (element is TextBlock text && !IsInsideTextInput(text))
            {
                var shown = text.Text;
                if (!IsArabicOrAllowed(shown) && data?.Invoke(shown) != true)
                {
                    problems.Add($"English text \"{shown}\" in {Path(text)}");
                }
            }

            if (IsInteractive(element) && element.IsEnabled)
            {
                var peer = UIElementAutomationPeer.CreatePeerForElement(element);
                var name = peer?.GetName();
                if (string.IsNullOrWhiteSpace(name))
                {
                    problems.Add($"No screen-reader name: {Path(element)}");
                }
                else if (!IsArabicOrAllowed(name) && data?.Invoke(name) != true && element is not TextBoxBase and not PasswordBox)
                {
                    problems.Add($"English name \"{name}\" of {Path(element)}");
                }
            }

            if (element is ListBoxItem or TreeViewItem)
            {
                var name = UIElementAutomationPeer.CreatePeerForElement(element)?.GetName();
                if (string.IsNullOrWhiteSpace(name) || name.StartsWith("CashGear.", StringComparison.Ordinal) || name.StartsWith("System.", StringComparison.Ordinal))
                {
                    problems.Add($"List item without a name (\"{name}\"): {Path(element)}");
                }
            }

            var tip = element.ToolTip as string;
            if (tip is not null && !IsArabicOrAllowed(tip) && data?.Invoke(tip) != true)
            {
                problems.Add($"English tooltip \"{tip}\" of {Path(element)}");
            }
        }

        return problems;
    }

    /// <summary>
    /// Moves the keyboard focus with Tab from the first control to the last and back to the first; returns the
    /// interactive controls it never reached. The window must be active.
    /// </summary>
    public static List<string> Unreachable(Window window)
    {
        var reached = new HashSet<DependencyObject>();
        window.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        Settle();
        for (var i = 0; i < 1000; i++)
        {
            if (Keyboard.FocusedElement is not DependencyObject focused || !reached.Add(focused))
            {
                break;
            }

            (focused as UIElement)?.MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
            Settle();
        }

        return Visible(window)
            .Where(e => IsInteractive(e) && e.IsEnabled && e.Focusable && KeyboardNavigation.GetIsTabStop(e))
            .Where(e => !reached.Contains(e) && !reached.Any(r => r is DependencyObject d && IsInside(d, e)))
            .Select(Path)
            .ToList();
    }

    /// <summary>
    /// Shows <paramref name="window"/> (made in Arabic), saves it as a PNG and returns its problems: for every page
    /// <paramref name="pages"/> shows (Settings' tabs), what <see cref="Problems"/> finds; for a window with a title
    /// bar, the controls Tab never reaches; for a dialog, whether Esc closes it.
    /// </summary>
    public static async Task<List<string>> CheckAsync(
        Window window,
        string name,
        Func<IEnumerable<FrameworkElement>>? leftToRight = null,
        Func<string, bool>? data = null,
        bool escCloses = false,
        Func<IEnumerable<(string Page, Action Show)>>? pages = null)
    {
        window.Topmost = true;
        window.ShowInTaskbar = false;
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = 120;
        window.Top = 80;
        var closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        var problems = new List<string>();
        try
        {
            await Pump(TimeSpan.FromMilliseconds(700));
            foreach (var (page, show) in pages?.Invoke() ?? [(string.Empty, () => { })])
            {
                show();
                await Pump(TimeSpan.FromMilliseconds(400));
                var label = page.Length > 0 ? $"{name}-{page}" : name;
                problems.AddRange(Problems(window, leftToRight?.Invoke(), data).Select(p => page.Length > 0 ? $"[{page}] {p}" : p));
                await SaveAsync(window, label);
            }

            if (window.WindowStyle == WindowStyle.None)
            {
                return problems;
            }

            using var input = Overlays.Tests.TestInput.Exclusive();
            var bar = window.PointToScreen(new Point(window.ActualWidth / 2, -14));
            await Task.Run(() => Overlays.Tests.TestInput.Click((int)bar.X, (int)bar.Y));
            await Pump(TimeSpan.FromMilliseconds(400));
            if (!window.IsActive)
            {
                problems.Add("The window didn't come to the front for the keyboard checks");
                return problems;
            }

            if (pages is null)
            {
                problems.AddRange(Unreachable(window).Select(p => "Tab never reaches " + p));
            }

            if (escCloses)
            {
                await Task.Run(() => Overlays.Tests.TestInput.Keys([0x1B]));
                await Pump(TimeSpan.FromMilliseconds(400));
                if (!closed)
                {
                    problems.Add($"Esc doesn't close {name}");
                }

            }
        }
        finally
        {
            if (!closed)
            {
                window.Close();
            }
        }

        return problems;
    }

    /// <summary>
    /// Saves what <paramref name="window"/> shows on the screen (its pixels, through the app's GDI capture) as
    /// <c>artifactsrabic-ui\<paramref name="name"/>.png</c>.
    /// </summary>
    public static async Task<string> SaveAsync(Window window, string name)
    {
        var target = window.Content as FrameworkElement ?? window;
        var folder = System.IO.Path.Combine(RepositoryRoot(), "artifacts", "arabic-ui");
        Directory.CreateDirectory(folder);
        var file = System.IO.Path.Combine(folder, name + ".png");
        if (HidesFromCapture(window))
        {
            Write(file, Render(target));
            return file;
        }


        // In a right-to-left window x runs from the right, so the corners are taken both ways round.
        var a = target.PointToScreen(new Point(0, 0));
        var b = target.PointToScreen(new Point(target.ActualWidth, target.ActualHeight));
        var area = new PixelRect((int)Math.Min(a.X, b.X), (int)Math.Min(a.Y, b.Y), (int)Math.Abs(b.X - a.X), (int)Math.Abs(b.Y - a.Y));
        var monitor = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors().FirstOrDefault(m => m.Bounds.Contains(area))
            ?? throw new InvalidOperationException($"{name} isn't on one monitor ({area}).");
        using var image = await new GdiFrameGrabber(System.Buffers.ArrayPool<byte>.Shared, TimeProvider.System, NullLogger<GdiFrameGrabber>.Instance)
            .CaptureAsync(new FrameRequest(monitor, area, false, TimeSpan.FromSeconds(2)), CancellationToken.None);

        Write(file, image.ToBitmapSource());
        return file;
    }

    /// <summary>Screen tools and toolbars keep themselves out of captures (display affinity), so they are drawn instead.</summary>
    private static unsafe bool HidesFromCapture(Window window)
    {
        uint affinity = 0;
        var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        return handle != 0 && Interop.Native.User32.GetWindowDisplayAffinity(handle, &affinity) && affinity != 0;
    }

    /// <summary>
    /// <paramref name="target"/> drawn on the window's background. The window mirrors a right-to-left layout as it
    /// puts it on the screen; drawn on its own, the content comes out the other way round, so it is turned back.
    /// </summary>
    private static BitmapSource Render(FrameworkElement target)
    {
        var dpi = VisualTreeHelper.GetDpi(target);
        var bitmap = new RenderTargetBitmap(
            Math.Max(1, (int)Math.Ceiling(target.ActualWidth * dpi.DpiScaleX)),
            Math.Max(1, (int)Math.Ceiling(target.ActualHeight * dpi.DpiScaleY)),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
        {
            var brush = (Window.GetWindow(target)?.Background as SolidColorBrush) is { Color.A: > 0 } solid ? solid : SystemColors.WindowBrush;
            dc.DrawRectangle(brush, null, new Rect(0, 0, target.ActualWidth, target.ActualHeight));
        }

        bitmap.Render(background);
        bitmap.Render(target);
        return target.FlowDirection == FlowDirection.RightToLeft ? new TransformedBitmap(bitmap, new ScaleTransform(-1, 1)) : bitmap;
    }

    private static void Write(string file, BitmapSource picture)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var stream = File.Create(file);
        encoder.Save(stream);
    }

    /// <summary>Has Arabic letters, or no words that should have been translated.</summary>
    public static bool IsArabicOrAllowed(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Any(IsArabic))
        {
            return true;
        }

        // Colours (#1E90FF) and file or host names (keepass.exe, ftp.example.com) are data, the same in any language.
        var words = DataToken().Replace(text.Replace("\u200E", string.Empty, StringComparison.Ordinal), " ");

        return LatinWord().Matches(words).All(m => LatinWords.Contains(m.Value) || m.Value.Length == 1);
    }

    public static string Report(string window, List<string> problems) =>
        new StringBuilder().AppendLine($"{window}: {problems.Count} problem(s)").AppendJoin(Environment.NewLine, problems.Distinct()).ToString();

    private static bool IsArabic(char c) => c is >= '؀' and <= 'ۿ' or >= 'ݐ' and <= 'ݿ' or >= 'ﭐ' and <= '﷿' or >= 'ﹰ' and <= '﻿';

    private static bool IsInteractive(FrameworkElement element)
    {
        // Parts of a control's own template (a combo box's arrow, a scroll bar's buttons) belong to that control;
        // what a data template makes has a ContentPresenter as its templated parent. Tabs are named by their headers.
        return element is ButtonBase or TextBoxBase or PasswordBox or Selector or Slider
            && element is not TabControl
            && element.TemplatedParent is not Control;
    }

    private static bool IsInsideTextInput(DependencyObject element)
    {
        for (var parent = VisualTreeHelper.GetParent(element); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent is TextBoxBase or PasswordBox or ComboBox { IsEditable: true })
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsInside(DependencyObject child, DependencyObject ancestor)
    {
        for (var parent = VisualTreeHelper.GetParent(child); parent is not null; parent = VisualTreeHelper.GetParent(parent))
        {
            if (parent == ancestor)
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<FrameworkElement> Visible(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is UIElement { IsVisible: false })
            {
                continue;
            }

            if (child is FrameworkElement element)
            {
                yield return element;
            }

            foreach (var below in Visible(child))
            {
                yield return below;
            }
        }
    }

    private static string Describe(DependencyObject element) =>
        element is FrameworkElement { Name: { Length: > 0 } name } ? $"{element.GetType().Name} '{name}'" : element.GetType().Name;

    private static string Path(DependencyObject element)
    {
        var parts = new List<string>();
        for (var e = element; e is not null && parts.Count < 4; e = VisualTreeHelper.GetParent(e))
        {
            if (e == element || e is FrameworkElement { Name.Length: > 0 })
            {
                parts.Add(Describe(e));
            }
        }

        if (element is ButtonBase or HeaderedContentControl && element is ContentControl { Content: { } content } && content is not UIElement)
        {
            parts[0] += $" ({content})";
        }

        parts.Reverse();
        return string.Join(" > ", parts);
    }

    private static string RepositoryRoot()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
        {
            if (File.Exists(System.IO.Path.Combine(folder.FullName, "CashGear.Capture.sln")))
            {
                return folder.FullName;
            }
        }

        return System.IO.Path.GetTempPath();
    }

    [GeneratedRegex(@"[A-Za-z][A-Za-z#+]*")]
    private static partial Regex LatinWord();

    [GeneratedRegex(@"#[0-9A-Fa-f]{3,8}\b|[\w-]+(\.[\w-]+)+")]
    private static partial Regex DataToken();

    private sealed class Restore(Action restore) : IDisposable
    {
        public void Dispose() => restore();
    }
}
