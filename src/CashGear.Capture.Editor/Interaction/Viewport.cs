using SkiaSharp;

namespace CashGear.Capture.Editor.Interaction;

/// <summary>
/// Zoom and pan: maps image pixels to the view's physical pixels (view = image × <see cref="Zoom"/> + <see cref="Offset"/>).
/// At 100% one image pixel is one screen pixel. The offset is kept whole, so at 100% the screen shows the exact
/// pixels that get exported. An image smaller than the view is centred; a larger one can't be dragged away.
/// </summary>
public sealed class Viewport
{
    public const float MinZoom = 0.05f;
    public const float MaxZoom = 32f;

    /// <summary>Free space kept around the image so edge grips stay reachable.</summary>
    public const float Margin = 24;

    private static readonly float[] Steps =
        [0.05f, 0.1f, 0.125f, 0.167f, 0.25f, 0.333f, 0.5f, 0.667f, 0.75f, 1, 1.5f, 2, 3, 4, 6, 8, 12, 16, 24, 32];

    public float Zoom { get; private set; } = 1;

    public SKPoint Offset { get; private set; }

    public SKSize ViewSize { get; private set; }

    public SKSizeI ImageSize { get; private set; }

    /// <summary>True while the zoom follows the view size (after <see cref="Fit"/>, until the user zooms).</summary>
    public bool IsFitting { get; private set; } = true;

    public SKMatrix Matrix => SKMatrix.CreateScaleTranslation(Zoom, Zoom, Offset.X, Offset.Y);

    /// <summary>Where the image is in the view.</summary>
    public SKRect ImageBounds => SKRect.Create(Offset.X, Offset.Y, ImageSize.Width * Zoom, ImageSize.Height * Zoom);

    public event EventHandler? Changed;

    public SKPoint ToImage(SKPoint view) => new((view.X - Offset.X) / Zoom, (view.Y - Offset.Y) / Zoom);

    public SKPoint ToView(SKPoint image) => new((image.X * Zoom) + Offset.X, (image.Y * Zoom) + Offset.Y);

    /// <summary>The view was resized: keep the same image point in the centre (or keep fitting).</summary>
    public void SetViewSize(SKSize size)
    {
        if (size == ViewSize)
        {
            return;
        }

        var centre = ViewSize.Width > 0 ? ToImage(new SKPoint(ViewSize.Width / 2, ViewSize.Height / 2)) : (SKPoint?)null;
        ViewSize = size;
        if (IsFitting || centre is null)
        {
            ApplyFit();
        }
        else
        {
            CentreOn(centre.Value);
        }
    }

    /// <summary>The image changed size (crop, rotate, resize): fit the new image if fitting, otherwise keep the zoom.</summary>
    public void SetImageSize(SKSizeI size)
    {
        if (size == ImageSize)
        {
            return;
        }

        ImageSize = size;
        if (IsFitting)
        {
            ApplyFit();
        }
        else
        {
            CentreOn(new SKPoint(size.Width / 2f, size.Height / 2f));
        }
    }

    /// <summary>Shows the whole image, shrinking it if needed (never enlarging past 100%), and keeps doing so on resize.</summary>
    public void Fit()
    {
        IsFitting = true;
        ApplyFit();
    }

    /// <summary>100%, keeping the centre of the view in place.</summary>
    public void ActualSize() => ZoomTo(1, new SKPoint(ViewSize.Width / 2, ViewSize.Height / 2));

    public void ZoomIn(SKPoint anchor) => ZoomTo(Steps.FirstOrDefault(s => s > Zoom * 1.001f, MaxZoom), anchor);

    public void ZoomOut(SKPoint anchor) => ZoomTo(Steps.LastOrDefault(s => s < Zoom * 0.999f, MinZoom), anchor);

    /// <summary>Sets the zoom so the image point under <paramref name="anchor"/> (view pixels) stays under it.</summary>
    public void ZoomTo(float zoom, SKPoint anchor)
    {
        var fixedPoint = ToImage(anchor);
        IsFitting = false;
        Zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        Offset = new SKPoint(anchor.X - (fixedPoint.X * Zoom), anchor.Y - (fixedPoint.Y * Zoom));
        ClampAndNotify();
    }

    public void PanBy(float dx, float dy)
    {
        Offset = new SKPoint(Offset.X + dx, Offset.Y + dy);
        ClampAndNotify();
    }

    private void CentreOn(SKPoint image)
    {
        Offset = new SKPoint((ViewSize.Width / 2) - (image.X * Zoom), (ViewSize.Height / 2) - (image.Y * Zoom));
        ClampAndNotify();
    }

    private void ApplyFit()
    {
        if (ImageSize.Width > 0 && ImageSize.Height > 0 && ViewSize.Width > 0 && ViewSize.Height > 0)
        {
            var fit = MathF.Min(
                (ViewSize.Width - (2 * Margin)) / ImageSize.Width,
                (ViewSize.Height - (2 * Margin)) / ImageSize.Height);
            Zoom = Math.Clamp(MathF.Min(1, fit), MinZoom, MaxZoom);
        }

        ClampAndNotify();
    }

    private void ClampAndNotify()
    {
        Offset = new SKPoint(ClampAxis(Offset.X, ViewSize.Width, ImageSize.Width * Zoom), ClampAxis(Offset.Y, ViewSize.Height, ImageSize.Height * Zoom));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static float ClampAxis(float offset, float view, float content)
    {
        var value = content + (2 * Margin) <= view
            ? (view - content) / 2
            : Math.Clamp(offset, view - content - Margin, Margin);
        return MathF.Round(value);
    }
}
