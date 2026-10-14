using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CashGear.Capture.App.Wpf;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Selection;
using CoreDpiScale = CashGear.Capture.Core.Geometry.DpiScale;

namespace CashGear.Capture.App.Region;

/// <summary>
/// Draws the selection UI over the frozen screenshot: the dim mask, the lit rectangle and its frame, crosshair
/// guides, the size label and the magnifier loupe. Lives inside a physical canvas covering the virtual screen,
/// so every coordinate here is a device pixel relative to the virtual screen's top-left. Chrome (text, loupe)
/// is sized with the DPI of the monitor it appears on, so it looks the same on every monitor.
/// </summary>
internal sealed class RegionOverlayVisual : FrameworkElement
{
    private const int LoupeCells = 17; // odd, so one cell is centred on the cursor pixel

    private static readonly Brush DimBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x80, 0x00, 0x00, 0x00)));
    private static readonly Brush AccentBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x1E, 0x90, 0xFF)));
    private static readonly Brush GuideBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xA0, 0x1E, 0x90, 0xFF)));
    private static readonly Brush PanelBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1C)));
    private static readonly Brush TextBrush = Brushes.White;
    private static readonly Typeface LabelTypeface = new("Segoe UI");

    private readonly PixelRect _virtual;
    private readonly IReadOnlyList<MonitorInfo> _monitors;
    private readonly BitmapSource _background;
    private readonly RegionSelectionModel _model;
    private readonly Func<PixelPoint, uint?> _colorAt;

    public RegionOverlayVisual(
        PixelRect virtualBounds,
        IReadOnlyList<MonitorInfo> monitors,
        BitmapSource background,
        RegionSelectionModel model,
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
    }

    protected override void OnRender(DrawingContext dc)
    {
        var all = new PixelRect(0, 0, _virtual.Width, _virtual.Height);
        var lit = _model.Highlight;
        var litLocal = lit.IsEmpty ? PixelRect.Empty : Local(lit);
        var cursor = Local(_model.Cursor);
        var tracking = _model.Phase is SelectionPhase.Hovering or SelectionPhase.Dragging;
        var cursorMonitor = MonitorAt(_model.Cursor);

        DrawDim(dc, all, litLocal.Intersect(all));

        if (!lit.IsEmpty)
        {
            DrawFrame(dc, litLocal, Math.Max(1, (int)Math.Round(ScaleOf(MonitorAt(lit.TopLeft) ?? cursorMonitor).ScaleX)));
        }

        if (tracking)
        {
            DrawGuides(dc, cursor, all);
        }

        if (!lit.IsEmpty)
        {
            DrawSizeLabel(dc, lit, litLocal, MonitorAt(lit.TopLeft) ?? cursorMonitor);
        }

        if (tracking && cursorMonitor is not null)
        {
            DrawLoupe(dc, cursor, cursorMonitor);
        }
    }

    private static void DrawDim(DrawingContext dc, PixelRect all, PixelRect lit)
    {
        if (lit.IsEmpty)
        {
            dc.DrawRectangle(DimBrush, null, ToRect(all));
            return;
        }

        var mask = new GeometryGroup { FillRule = FillRule.EvenOdd };
        mask.Children.Add(new RectangleGeometry(ToRect(all)));
        mask.Children.Add(new RectangleGeometry(ToRect(lit)));
        mask.Freeze();
        dc.DrawGeometry(DimBrush, null, mask);
    }

    /// <summary>A frame just outside the rectangle, so none of the selected pixels are covered.</summary>
    private static void DrawFrame(DrawingContext dc, PixelRect r, int t)
    {
        dc.DrawRectangle(AccentBrush, null, new Rect(r.X - t, r.Y - t, r.Width + (2 * t), t));
        dc.DrawRectangle(AccentBrush, null, new Rect(r.X - t, r.Bottom, r.Width + (2 * t), t));
        dc.DrawRectangle(AccentBrush, null, new Rect(r.X - t, r.Y, t, r.Height));
        dc.DrawRectangle(AccentBrush, null, new Rect(r.Right, r.Y, t, r.Height));
    }

    /// <summary>Full-width and full-height 1-px guides through the cursor pixel.</summary>
    private static void DrawGuides(DrawingContext dc, PixelPoint cursor, PixelRect all)
    {
        dc.DrawRectangle(GuideBrush, null, new Rect(0, cursor.Y, all.Width, 1));
        dc.DrawRectangle(GuideBrush, null, new Rect(cursor.X, 0, 1, all.Height));
    }

    /// <summary>Above the lit rectangle's top-left corner, or just inside it when there is no room; kept on one monitor.</summary>
    private void DrawSizeLabel(DrawingContext dc, PixelRect lit, PixelRect litLocal, MonitorInfo? monitor)
    {
        if (monitor is null)
        {
            return;
        }

        var scale = ScaleOf(monitor);
        var area = Local(monitor.Bounds);
        var title = _model.Phase == SelectionPhase.Hovering && _model.HoverWindow is { Title.Length: > 0 } window
            ? "   " + Truncate(window.Title, 60)
            : string.Empty;
        var text = Text($"{lit.Width} × {lit.Height}{title}", 12, scale);

        var pad = DpiHelper.DipsToPhysical(4, scale);
        var gap = DpiHelper.DipsToPhysical(6, scale);
        var width = text.Width + (2 * pad);
        var height = text.Height + (2 * pad);

        var x = Math.Clamp((double)litLocal.X, area.X, Math.Max(area.X, area.Right - width));
        var y = litLocal.Y - height - gap;
        if (y < area.Y)
        {
            y = Math.Max(litLocal.Y, area.Y) + gap;
        }

        y = Math.Clamp(y, area.Y, Math.Max(area.Y, area.Bottom - height));
        dc.DrawRectangle(PanelBrush, null, new Rect(x, y, width, height));
        dc.DrawText(text, new Point(x + pad, y + pad));
    }

    private void DrawLoupe(DrawingContext dc, PixelPoint cursor, MonitorInfo monitor)
    {
        var scale = ScaleOf(monitor);
        var area = Local(monitor.Bounds);
        var cell = Math.Max(4, (int)Math.Round(7 * scale.ScaleX));
        var size = LoupeCells * cell;
        var info = Text(InfoText(), 11, scale);
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
        DrawFrame(dc, new PixelRect((int)x + ((LoupeCells / 2) * cell), (int)y + ((LoupeCells / 2) * cell), cell, cell), 1);
        DrawFrame(dc, new PixelRect((int)x, (int)y, size, size), 1);

        var panel = new Rect(x - 1, y + size + 1, size + 2, infoHeight);
        dc.DrawRectangle(PanelBrush, null, panel);
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

    private MonitorInfo? MonitorAt(PixelPoint point) => _monitors.FirstOrDefault(m => m.Bounds.Contains(point));

    private CoreDpiScale ScaleOf(MonitorInfo? monitor) => monitor?.Scale ?? DpiHelper.GetScale(this);

    private PixelPoint Local(PixelPoint point) => VirtualScreen.ToLocal(point, _virtual);

    private PixelRect Local(PixelRect rect) => VirtualScreen.ToLocal(rect, _virtual);

    private FormattedText Text(string text, double dips, CoreDpiScale scale) =>
        new(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            LabelTypeface,
            DpiHelper.DipsToPhysical(dips, scale),
            TextBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static string Truncate(string text, int max) => text.Length <= max ? text : string.Concat(text.AsSpan(0, max - 1), "…");

    private static Rect ToRect(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
