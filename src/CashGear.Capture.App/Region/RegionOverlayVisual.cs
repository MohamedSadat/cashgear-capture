using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.Wpf;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Selection;
using CoreDpiScale = CashGear.Capture.Core.Geometry.DpiScale;

namespace CashGear.Capture.App.Region;

/// <summary>
/// Draws the selection UI over the frozen screenshot from the model's <see cref="SelectionView"/>: the dim mask,
/// the lit rectangle or shape and its outline, a path being drawn, crosshair guides, the size label (with a hint
/// line) and the magnifier loupe. Lives inside a physical canvas covering the virtual screen, so every coordinate
/// here is a device pixel relative to the virtual screen's top-left. Chrome (text, loupe) is sized with the DPI of
/// the monitor it appears on, so it looks the same on every monitor.
/// <para>A screen reader hears the size label and the hint: its peer is a live text, announced once they settle.</para>
/// </summary>
internal sealed class RegionOverlayVisual : FrameworkElement
{
    private const int LoupeCells = 17; // odd, so one cell is centred on the cursor pixel

    /// <summary>How long the label must stay the same before it is announced (a drag changes it every move).</summary>
    private static readonly TimeSpan AnnounceDelay = TimeSpan.FromMilliseconds(300);

    [Localizable(false)] // a font's name
    private static readonly Typeface LabelTypeface = new("Segoe UI");

    private readonly PixelRect _virtual;
    private readonly IReadOnlyList<MonitorInfo> _monitors;
    private readonly BitmapSource _background;
    private readonly ISelectionModel _model;
    private readonly Func<PixelPoint, uint?> _colorAt;
    private readonly DispatcherTimer _announce;
    private string? _announced;

    public RegionOverlayVisual(
        PixelRect virtualBounds,
        IReadOnlyList<MonitorInfo> monitors,
        BitmapSource background,
        ISelectionModel model,
        Func<PixelPoint, uint?> colorAt)
    {
        _virtual = virtualBounds;
        _monitors = monitors;
        _background = background;
        _model = model;
        _colorAt = colorAt;

        Width = virtualBounds.Width;
        Height = virtualBounds.Height;
        ClipToBounds = true;
        IsHitTestVisible = false; // the window handles all input
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor); // crisp loupe pixels
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased); // crisp 1-px rectangles
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);

        _announce = new DispatcherTimer(AnnounceDelay, DispatcherPriority.Background, (_, _) => AnnounceIfChanged(), Dispatcher);
        _announce.Stop();
        Loaded += (_, _) => _announce.Start(); // the hint, after the window's title
        Unloaded += (_, _) => _announce.Stop();
    }

    /// <summary>
    /// What a screen reader says about the selection: the size label and the hint under it; while picking a colour,
    /// the pointer's position and colour with the hint.
    /// </summary>
    internal string SpokenText
    {
        get
        {
            var view = _model.View;
            var label = !view.Dim ? (_model.IsFinished ? null : InfoText()) : view.Lit.IsEmpty ? null : view.Label;
            return (label, view.Hint) switch
            {
                ({ } l, { } h) => CapturingText.RegionSpoken(l, h),
                ({ } l, null) => l,
                (null, { } h) => h,
                _ => CapturingText.RegionWindowTitle,
            };
        }
    }

    /// <summary>The model changed: draws it again and, once it settles, tells a screen reader the new label.</summary>
    public void Refresh()
    {
        InvalidateVisual();
        _announce.Stop();
        if (!_model.IsFinished)
        {
            _announce.Start();
        }
    }

    /// <summary>The overlay closed: nothing more is announced (the frozen picture the label reads may be gone).</summary>
    public void StopAnnouncing() => _announce.Stop();

    /// <summary>Raises the live-region event when <see cref="SpokenText"/> differs from what was announced last; true if it did.</summary>
    internal bool AnnounceIfChanged()
    {
        _announce.Stop();
        if (_model.IsFinished)
        {
            return false;
        }

        var text = SpokenText;
        if (text == _announced)
        {
            return false;
        }

        _announced = text;
        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            UIElementAutomationPeer.CreatePeerForElement(this)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        }

        return true;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    protected override void OnRender(DrawingContext dc)
    {
        var palette = RegionOverlayPalette.Current;
        var view = _model.View;
        var all = new PixelRect(0, 0, _virtual.Width, _virtual.Height);
        var lit = view.Lit;
        var litLocal = lit.IsEmpty ? PixelRect.Empty : Local(lit);
        var cursor = Local(_model.Cursor);
        var cursorMonitor = MonitorAt(_model.Cursor);
        var shape = view.LitShape is { } s ? ShapeGeometry(s) : null;
        var litMonitor = lit.IsEmpty ? cursorMonitor : MonitorAt(lit.TopLeft) ?? cursorMonitor;
        var thickness = Math.Max(1, (int)Math.Round(ScaleOf(litMonitor).ScaleX));

        if (view.Dim)
        {
            DrawDim(dc, palette, all, litLocal.Intersect(all), shape);
        }

        if (shape is not null)
        {
            dc.DrawGeometry(null, Pen(palette.Accent, thickness), shape);
        }
        else if (!lit.IsEmpty)
        {
            DrawFrame(dc, palette.Accent, litLocal, thickness);
        }

        if (view.Path is { Count: > 0 } path && view.RubberBand)
        {
            DrawPolygonInProgress(dc, palette, path, cursor, view.CanClosePath, ScaleOf(cursorMonitor), thickness);
        }

        if (view.Tracking)
        {
            DrawGuides(dc, palette, cursor, all);
        }

        if (view.Label is not null && !lit.IsEmpty)
        {
            DrawLabel(dc, palette, litLocal, litMonitor, view.Label, view.Hint);
        }
        else if (view.Hint is not null && cursorMonitor is not null)
        {
            DrawLabel(dc, palette, PixelRect.Empty, cursorMonitor, null, view.Hint);
        }

        if (view.Tracking && cursorMonitor is not null)
        {
            DrawLoupe(dc, palette, cursor, cursorMonitor);
        }
    }

    /// <summary>Everything but the lit area is dimmed (even-odd, so the shape is a hole in the dim).</summary>
    private static void DrawDim(DrawingContext dc, RegionOverlayPalette palette, PixelRect all, PixelRect lit, Geometry? shape)
    {
        if (lit.IsEmpty && shape is null)
        {
            dc.DrawRectangle(palette.Dim, null, ToRect(all));
            return;
        }

        var mask = new GeometryGroup { FillRule = FillRule.EvenOdd };
        mask.Children.Add(new RectangleGeometry(ToRect(all)));
        mask.Children.Add(shape ?? new RectangleGeometry(ToRect(lit)));
        mask.Freeze();
        dc.DrawGeometry(palette.Dim, null, mask);
    }

    /// <summary>A frame just outside the rectangle, so none of the selected pixels are covered.</summary>
    private static void DrawFrame(DrawingContext dc, Brush brush, PixelRect r, int t)
    {
        dc.DrawRectangle(brush, null, new Rect(r.X - t, r.Y - t, r.Width + (2 * t), t));
        dc.DrawRectangle(brush, null, new Rect(r.X - t, r.Bottom, r.Width + (2 * t), t));
        dc.DrawRectangle(brush, null, new Rect(r.X - t, r.Y, t, r.Height));
        dc.DrawRectangle(brush, null, new Rect(r.Right, r.Y, t, r.Height));
    }

    /// <summary>The corners so far, the side to the cursor, and the first corner (larger when a click there closes the polygon).</summary>
    private void DrawPolygonInProgress(
        DrawingContext dc, RegionOverlayPalette palette, IReadOnlyList<PixelPoint> path, PixelPoint cursor, bool canClose, CoreDpiScale scale, int thickness)
    {
        var pen = Pen(palette.Accent, thickness);
        var last = Centre(Local(path[^1]));
        dc.DrawLine(pen, last, Centre(cursor));
        for (var i = 1; i < path.Count; i++)
        {
            dc.DrawLine(pen, Centre(Local(path[i - 1])), Centre(Local(path[i]))); // sides so far, before the shape is lit
        }

        var marker = DpiHelper.DipsToPhysical(3, scale);
        foreach (var point in path)
        {
            var c = Centre(Local(point));
            dc.DrawRectangle(palette.Accent, null, new Rect(c.X - marker, c.Y - marker, 2 * marker, 2 * marker));
        }

        if (canClose)
        {
            var first = Centre(Local(path[0]));
            var ring = DpiHelper.DipsToPhysical(7, scale);
            dc.DrawEllipse(null, Pen(palette.Ring, thickness), first, ring, ring);
        }
    }

    /// <summary>Full-width and full-height 1-px guides through the cursor pixel.</summary>
    private static void DrawGuides(DrawingContext dc, RegionOverlayPalette palette, PixelPoint cursor, PixelRect all)
    {
        dc.DrawRectangle(palette.Guide, null, new Rect(0, cursor.Y, all.Width, 1));
        dc.DrawRectangle(palette.Guide, null, new Rect(cursor.X, 0, 1, all.Height));
    }

    /// <summary>
    /// The label (and the hint under it) above the lit rectangle's top-left corner, or just inside it when there is
    /// no room; kept on one monitor. With nothing lit, in the monitor's top-left corner.
    /// </summary>
    private void DrawLabel(DrawingContext dc, RegionOverlayPalette palette, PixelRect litLocal, MonitorInfo? monitor, string? label, string? hint)
    {
        if (monitor is null)
        {
            return;
        }

        var scale = ScaleOf(monitor);
        var area = Local(monitor.Bounds);
        var main = label is null ? null : Text(label, 12, scale, palette.Text);
        var help = hint is null ? null : Text(hint, 11, scale, palette.Hint);
        var pad = DpiHelper.DipsToPhysical(4, scale);
        var gap = DpiHelper.DipsToPhysical(6, scale);
        var width = Math.Max(main?.Width ?? 0, help?.Width ?? 0) + (2 * pad);
        var height = (main?.Height ?? 0) + (help?.Height ?? 0) + (2 * pad);

        double x, y;
        if (litLocal.IsEmpty)
        {
            x = area.X + (2 * gap);
            y = area.Y + (2 * gap);
        }
        else
        {
            x = Math.Clamp((double)litLocal.X, area.X, Math.Max(area.X, area.Right - width));
            y = litLocal.Y - height - gap;
            if (y < area.Y)
            {
                y = Math.Max(litLocal.Y, area.Y) + gap;
            }

            y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - height));
        }

        dc.DrawRectangle(palette.Panel, null, new Rect(x, y, width, height));
        if (main is not null)
        {
            dc.DrawText(main, new Point(x + pad, y + pad));
        }

        if (help is not null)
        {
            dc.DrawText(help, new Point(x + pad, y + pad + (main?.Height ?? 0)));
        }
    }

    private void DrawLoupe(DrawingContext dc, RegionOverlayPalette palette, PixelPoint cursor, MonitorInfo monitor)
    {
        var scale = ScaleOf(monitor);
        var area = Local(monitor.Bounds);
        var cell = Math.Max(4, (int)Math.Round(7 * scale.ScaleX));
        var size = LoupeCells * cell;
        var info = Text(InfoText(), 11, scale, palette.Text);
        var pad = DpiHelper.DipsToPhysical(4, scale);
        var infoHeight = info.Height + (2 * pad);
        var gap = (int)Math.Round(DpiHelper.DipsToPhysical(20, scale));

        // Bottom-right of the cursor, flipped to stay on the cursor's monitor.
        double x = cursor.X + gap;
        if (x + size > area.Right)
        {
            x = cursor.X - gap - size;
        }

        double y = cursor.Y + gap;
        if (y + size + infoHeight > area.Bottom)
        {
            y = cursor.Y - gap - size - infoHeight;
        }

        x = Math.Max(area.X, x);
        y = Math.Max(area.Y, y);
        var loupe = new Rect(x, y, size, size);

        dc.DrawRectangle(Brushes.Black, null, loupe);
        dc.PushClip(new RectangleGeometry(loupe));
        var first = new PixelPoint(cursor.X - (LoupeCells / 2), cursor.Y - (LoupeCells / 2));
        dc.PushTransform(new MatrixTransform(cell, 0, 0, cell, x - (first.X * cell), y - (first.Y * cell)));
        dc.DrawImage(_background, new Rect(0, 0, _background.PixelWidth, _background.PixelHeight));
        dc.Pop();
        dc.Pop();

        // Centre-pixel marker and outer frame.
        DrawFrame(dc, palette.Accent, new PixelRect((int)x + ((LoupeCells / 2) * cell), (int)y + ((LoupeCells / 2) * cell), cell, cell), 1);
        DrawFrame(dc, palette.Accent, new PixelRect((int)x, (int)y, size, size), 1);

        var panel = new Rect(x - 1, y + size + 1, size + 2, infoHeight);
        dc.DrawRectangle(palette.Panel, null, panel);
        dc.DrawText(info, new Point(panel.X + pad, panel.Y + pad));

        if (_colorAt(_model.Cursor) is { } argb && (argb >> 24) != 0)
        {
            var swatch = info.Height;
            var brush = new SolidColorBrush(Color.FromRgb((byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
            brush.Freeze();
            dc.DrawRectangle(brush, null, new Rect(panel.Right - pad - swatch, panel.Y + pad, swatch, swatch));
        }
    }

    private string InfoText()
    {
        var c = _model.Cursor;
        var hex = _colorAt(c) is { } argb && (argb >> 24) != 0 ? $"#{argb & 0xFFFFFF:X6}" : "—";
        return $"{c.X}, {c.Y}   {hex}";
    }

    /// <summary>The shape's outline in canvas units (filled where its winding isn't zero).</summary>
    private StreamGeometry ShapeGeometry(CaptureShape shape)
    {
        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var context = geometry.Open())
        {
            foreach (var ring in shape.Rings())
            {
                context.BeginFigure(new Point(ring[0].X - _virtual.X, ring[0].Y - _virtual.Y), isFilled: true, isClosed: true);
                for (var i = 1; i < ring.Count; i++)
                {
                    context.LineTo(new Point(ring[i].X - _virtual.X, ring[i].Y - _virtual.Y), isStroked: true, isSmoothJoin: false);
                }
            }
        }

        geometry.Freeze();
        return geometry;
    }

    private static Point Centre(PixelPoint local) => new(local.X + 0.5, local.Y + 0.5);

    private static Pen Pen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    private MonitorInfo? MonitorAt(PixelPoint point) => _monitors.FirstOrDefault(m => m.Bounds.Contains(point));

    private CoreDpiScale ScaleOf(MonitorInfo? monitor) => monitor?.Scale ?? DpiHelper.GetScale(this);

    private PixelPoint Local(PixelPoint point) => VirtualScreen.ToLocal(point, _virtual);

    private PixelRect Local(PixelRect rect) => VirtualScreen.ToLocal(rect, _virtual);

    private FormattedText Text(string text, double dips, CoreDpiScale scale, Brush brush) =>
        new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            DpiHelper.DipsToPhysical(dips, scale),
            brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Rect ToRect(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>The overlay as a screen reader sees it: a live text that says the size label and the hint.</summary>
    private sealed class Peer(RegionOverlayVisual owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(RegionOverlayVisual);

        protected override string GetNameCore() => ((RegionOverlayVisual)Owner).SpokenText;

        protected override AutomationLiveSetting GetLiveSettingCore() => AutomationLiveSetting.Polite;

        protected override bool IsContentElementCore() => true;

        protected override bool IsControlElementCore() => true;
    }
}

/// <summary>
/// The selection overlay's colours, read each time it is drawn: the app's own, or the system's while high contrast
/// is on (panels Window, text WindowText, the frame, guides and corners Highlight). The shade over the screen stays.
/// </summary>
internal sealed record RegionOverlayPalette(Brush Dim, Brush Accent, Brush Guide, Brush Panel, Brush Hint, Brush Text, Brush Ring)
{
    private static readonly Brush NormalDim = Frozen(Color.FromArgb(0x80, 0x00, 0x00, 0x00));
    private static readonly Brush NormalAccent = Frozen(Color.FromRgb(0x1E, 0x90, 0xFF));
    private static readonly Brush NormalGuide = Frozen(Color.FromArgb(0xA0, 0x1E, 0x90, 0xFF));
    private static readonly Brush NormalPanel = Frozen(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1C));
    private static readonly Brush NormalHint = Frozen(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF));

    /// <summary>The colours for now (high contrast may have been turned on or off since the last time).</summary>
    public static RegionOverlayPalette Current => new(
        NormalDim,
        HighContrast.Pick(NormalAccent, () => HighContrast.Highlight),
        HighContrast.Pick(NormalGuide, () => HighContrast.Highlight),
        HighContrast.Pick(NormalPanel, () => HighContrast.Window),
        HighContrast.Pick(NormalHint, () => HighContrast.WindowText),
        HighContrast.Pick(Brushes.White, () => HighContrast.WindowText),
        HighContrast.Pick(Brushes.White, () => HighContrast.Highlight));

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
