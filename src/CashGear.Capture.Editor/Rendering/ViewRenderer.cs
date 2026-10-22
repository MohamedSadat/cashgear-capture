using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Editor.Model;
using SkiaSharp;

namespace CashGear.Capture.Editor.Rendering;

/// <summary>
/// Paints the editor view: background, a checkerboard where the image is transparent, the document at the current
/// zoom, then (in screen space, so they keep their size at any zoom) the selection grips and the crop frame.
/// </summary>
public sealed class ViewRenderer : IDisposable
{
    private readonly EffectCache _effects = new();
    private SKShader? _checker;
    private float _checkerScale;

    public SKColor Background { get; set; } = new(0x2B, 0x2B, 0x2B);

    /// <summary>The inside of the grips (the host sets system colours under high contrast).</summary>
    public SKColor HandleFill { get; set; } = SKColors.White;

    /// <summary>The outline of the grips.</summary>
    public SKColor HandleOutline { get; set; } = EditorColors.Accent;

    /// <summary>What dims the part of the image a crop cuts away.</summary>
    public SKColor CropShade { get; set; } = new(0, 0, 0, 140);

    /// <summary>The background of the size shown on a crop or area frame.</summary>
    public SKColor LabelBackground { get; set; } = new(0, 0, 0, 180);

    /// <summary>The text of the size shown on a crop or area frame.</summary>
    public SKColor LabelText { get; set; } = SKColors.White;

    /// <summary>Physical pixels per device-independent pixel, so grips and lines look the same at any display scale.</summary>
    public float UiScale { get; set; } = 1;

    public float HandleSize => 8 * UiScale;

    /// <summary>Hit slack for grips and thin shapes, in image pixels at the viewport's zoom.</summary>
    public float Tolerance(Viewport viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        return ((HandleSize / 2) + (2 * UiScale)) / viewport.Zoom;
    }

    /// <param name="overlays">False to draw only what gets exported (tests compare this with the flattened image).</param>
    public void Render(SKCanvas canvas, EditorSession session, Viewport viewport, bool overlays = true)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(viewport);

        canvas.Clear(Background);
        var preview = session.Preview;
        var state = preview ?? session.Document.State;
        using (var checker = new SKPaint { Shader = Checker() })
        {
            canvas.DrawRect(viewport.ImageBounds, checker);
        }

        // Up close, show crisp pixels; zoomed out, average them so text stays legible.
        var sampling = viewport.Zoom >= 1
            ? new SKSamplingOptions(SKFilterMode.Nearest)
            : new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear);

        canvas.Save();
        var matrix = viewport.Matrix;
        canvas.Concat(in matrix);
        DocumentRenderer.Render(
            canvas, state, new RenderContext(state.Image, _effects, sampling), preview is null ? session.Draft : null, preview is null ? session.TextEdit?.Existing : null);
        canvas.Restore();
        _effects.Trim();

        // A preview shows only what the edit will make; grips and outlines would be in the wrong places.
        if (overlays && preview is null)
        {
            DrawDraftOutline(canvas, session, viewport);
            DrawSelection(canvas, session, viewport);
            DrawCrop(canvas, session, viewport);
            DrawArea(canvas, session, viewport);
        }
    }

    public void Dispose()
    {
        _effects.Dispose();
        _checker?.Dispose();
    }

    /// <summary>Blur and pixelate areas are invisible on plain backgrounds, so show where they are while they are drawn.</summary>
    private void DrawDraftOutline(SKCanvas canvas, EditorSession session, Viewport viewport)
    {
        if (session.Draft is EffectAnnotation effect)
        {
            DrawDashedRect(canvas, Geometry.MapRect(viewport.Matrix, effect.Rect));
        }
    }

    private void DrawSelection(SKCanvas canvas, EditorSession session, Viewport viewport)
    {
        if (session.Selected is not { } selected || session.TextEdit is not null)
        {
            return;
        }

        var handles = selected.GetHandles();
        if (selected is not TwoPointAnnotation)
        {
            var outline = selected switch
            {
                BoxAnnotation box => box.Rect,
                FreehandAnnotation pen => pen.PointBounds,
                _ => selected.Bounds,
            };
            DrawDashedRect(canvas, Geometry.MapRect(viewport.Matrix, outline));
        }

        DrawHandles(canvas, handles.Select(h => viewport.ToView(h.Position)));
    }

    private void DrawCrop(SKCanvas canvas, EditorSession session, Viewport viewport)
    {
        if (session.CropArea is not { } area)
        {
            return;
        }

        var image = viewport.ImageBounds;
        var crop = Geometry.MapRect(viewport.Matrix, new SKRect(area.Left, area.Top, area.Right, area.Bottom));

        // Dim what will be cut away.
        using (var shade = new SKPaint { Color = CropShade, Style = SKPaintStyle.Fill })
        {
            canvas.DrawRect(new SKRect(image.Left, image.Top, image.Right, crop.Top), shade);
            canvas.DrawRect(new SKRect(image.Left, crop.Bottom, image.Right, image.Bottom), shade);
            canvas.DrawRect(new SKRect(image.Left, crop.Top, crop.Left, crop.Bottom), shade);
            canvas.DrawRect(new SKRect(crop.Right, crop.Top, image.Right, crop.Bottom), shade);
        }

        DrawDashedRect(canvas, crop);
        DrawHandles(canvas, Geometry.BoxHandles(crop).Select(h => h.Position));
        DrawLabel(canvas, $"{area.Width} × {area.Height}", new SKPoint(crop.Left, crop.Top));
    }

    /// <summary>The Area tool's selection: an outline, grips and its size (nothing is dimmed: it isn't cut).</summary>
    private void DrawArea(SKCanvas canvas, EditorSession session, Viewport viewport)
    {
        if (session.SelectedArea is not { } area)
        {
            return;
        }

        var rect = Geometry.MapRect(viewport.Matrix, new SKRect(area.Left, area.Top, area.Right, area.Bottom));
        DrawDashedRect(canvas, rect);
        DrawHandles(canvas, Geometry.BoxHandles(rect).Select(h => h.Position));
        DrawLabel(canvas, $"{area.Width} × {area.Height}", new SKPoint(rect.Left, rect.Top));
    }

    /// <summary>White dashes over a dark line: visible on any background (high contrast too, so these colours stay).</summary>
    private void DrawDashedRect(SKCanvas canvas, SKRect rect)
    {
        using var under = new SKPaint { Color = new SKColor(0, 0, 0, 160), Style = SKPaintStyle.Stroke, StrokeWidth = UiScale };
        using var dash = SKPathEffect.CreateDash([4 * UiScale, 4 * UiScale], 0);
        using var over = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = UiScale, PathEffect = dash };
        var crisp = new SKRect(Snap(rect.Left), Snap(rect.Top), Snap(rect.Right), Snap(rect.Bottom));
        canvas.DrawRect(crisp, under);
        canvas.DrawRect(crisp, over);
    }

    private void DrawHandles(SKCanvas canvas, IEnumerable<SKPoint> positions)
    {
        using var fill = new SKPaint { Color = HandleFill, Style = SKPaintStyle.Fill };
        using var border = new SKPaint { Color = HandleOutline, Style = SKPaintStyle.Stroke, StrokeWidth = UiScale };
        var half = HandleSize / 2;
        foreach (var p in positions)
        {
            var box = new SKRect(MathF.Round(p.X - half) + 0.5f, MathF.Round(p.Y - half) + 0.5f, MathF.Round(p.X + half) - 0.5f, MathF.Round(p.Y + half) - 0.5f);
            canvas.DrawRect(box, fill);
            canvas.DrawRect(box, border);
        }
    }

    private void DrawLabel(SKCanvas canvas, string text, SKPoint corner)
    {
        using var typeface = SKTypeface.FromFamilyName(AnnotationStyle.DefaultFontFamily);
        using var font = new SKFont(typeface, 12 * UiScale);
        var width = font.MeasureText(text);
        var pad = 4 * UiScale;
        var box = SKRect.Create(corner.X + pad, corner.Y + pad, width + (2 * pad), font.Spacing + pad);
        using var back = new SKPaint { Color = LabelBackground, IsAntialias = true };
        using var fore = new SKPaint { Color = LabelText, IsAntialias = true };
        canvas.DrawRoundRect(box, 3 * UiScale, 3 * UiScale, back);
        canvas.DrawText(text, box.Left + pad, box.Top + (pad / 2) - font.Metrics.Ascent, SKTextAlign.Left, font, fore);
    }

    /// <summary>A grey checkerboard (8 px squares at 100% display scale) that stays put on screen.</summary>
    private SKShader Checker()
    {
        if (_checker is not null && _checkerScale == UiScale)
        {
            return _checker;
        }

        _checker?.Dispose();
        var cell = Math.Max(1, (int)MathF.Round(8 * UiScale));
        using var tile = new SKBitmap(new SKImageInfo(cell * 2, cell * 2, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var c = new SKCanvas(tile))
        {
            c.Clear(new SKColor(0xCC, 0xCC, 0xCC));
            using var dark = new SKPaint { Color = new SKColor(0x99, 0x99, 0x99) };
            c.DrawRect(0, 0, cell, cell, dark);
            c.DrawRect(cell, cell, cell, cell, dark);
        }

        _checker = SKShader.CreateBitmap(tile, SKShaderTileMode.Repeat, SKShaderTileMode.Repeat);
        _checkerScale = UiScale;
        return _checker;
    }

    /// <summary>Puts a 1-pixel line on a pixel centre so it is sharp.</summary>
    private static float Snap(float v) => MathF.Round(v) + 0.5f;
}
