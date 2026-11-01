using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Rendering;

/// <summary>Where a layout puts a picture at a moment, in canvas pixels.</summary>
/// <param name="Cover">The picture is cut to the area's shape rather than stretched into it.</param>
public readonly record struct PictureBox(SKRect Area, float Radius, float Opacity, bool Cover);

/// <summary>The layouts in force at a moment: <see cref="To"/> is <see cref="Amount"/> of the way in from <see cref="From"/>.</summary>
public readonly record struct LayoutMoment(LayoutKind From, LayoutKind To, double Amount);

/// <summary>
/// What the layout track says at each moment: a function of the project and the time alone, so the preview, seeks and
/// the export agree. A project with no layout track gets null everywhere, and each picture stays at its own placement.
/// </summary>
public static class LayoutEngine
{
    /// <summary>The space between the two pictures and round them, as a part of the canvas's shorter side.</summary>
    private const float Gutter = 0.025f;

    /// <summary>The corners of a picture in a side-by-side cell, as a part of the cell's shorter side.</summary>
    private const float CellCorner = 0.03f;

    /// <summary>The share of a wide canvas the screen gets, and of a tall one.</summary>
    private const float ScreenWide = 0.7f;

    private const float ScreenTall = 0.6f;

    /// <summary>The layout at <paramref name="time"/>, or null when each picture is at its own placement.</summary>
    public static LayoutMoment? At(VideoProject project, double time)
    {
        ArgumentNullException.ThrowIfNull(project);
        LayoutItem? current = null;
        LayoutItem? before = null;
        foreach (var track in project.Tracks)
        {
            if (track.Kind != TrackKind.Layout || track.Hidden)
            {
                continue;
            }

            foreach (var item in track.Items.OfType<LayoutItem>())
            {
                if (item.Covers(time))
                {
                    current = item;
                }
                else if (item.End <= time + Times.Epsilon && (before is null || item.End > before.End))
                {
                    before = item;
                }
            }
        }

        if (current is not null)
        {
            var from = before is not null && before.End >= current.Start - Times.Epsilon ? before.Kind : LayoutKind.CornerBubble;
            var amount = current.Transition <= 0 ? 1 : ZoomCamera.Ease((time - current.Start) / current.Transition);
            return from == current.Kind && current.Kind == LayoutKind.CornerBubble ? null : new LayoutMoment(from, current.Kind, amount);
        }

        if (before is not null && before.Kind != LayoutKind.CornerBubble && before.Transition > 0 && time < before.End + before.Transition)
        {
            return new LayoutMoment(before.Kind, LayoutKind.CornerBubble, ZoomCamera.Ease((time - before.End) / before.Transition));
        }

        return null;
    }

    /// <summary>The screen's part of the canvas at <paramref name="time"/> (the zooms' camera shows the pictures inside it), or null for all of it.</summary>
    public static SKRect? ScreenCell(VideoProject project, double time)
    {
        if (At(project, time) is not { } moment)
        {
            return null;
        }

        var whole = SKRect.Create(project.Canvas.Width, project.Canvas.Height);
        var cell = Lerp(Cell(moment.From, project.Canvas, whole), Cell(moment.To, project.Canvas, whole), (float)moment.Amount);
        return cell == whole ? null : cell;
    }

    /// <summary>Where the layout puts <paramref name="clip"/> on <paramref name="track"/> at <paramref name="time"/>, or null when it stays at its own placement.</summary>
    public static PictureBox? Box(VideoProject project, Track track, Clip clip, double time)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(clip);
        if (!track.HasPictures || At(project, time) is not { } moment)
        {
            return null;
        }

        var from = Target(project, track, clip, moment.From);
        var to = Target(project, track, clip, moment.To);
        var amount = (float)moment.Amount;
        return new PictureBox(
            Lerp(from.Area, to.Area, amount),
            from.Radius + ((to.Radius - from.Radius) * amount),
            from.Opacity + ((to.Opacity - from.Opacity) * amount),
            from.Cover || to.Cover);
    }

    /// <summary>The screen's and the webcam's cells side by side on a wide canvas, one above the other on any other.</summary>
    public static (SKRect Screen, SKRect Camera) Cells(ProjectCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        var gap = Math.Min(canvas.Width, canvas.Height) * Gutter;
        var (width, height) = ((float)canvas.Width, (float)canvas.Height);
        if (width > height * 1.1f)
        {
            var split = width * ScreenWide;
            return (new SKRect(gap, gap, split - (gap / 2), height - gap), new SKRect(split + (gap / 2), gap, width - gap, height - gap));
        }

        var line = height * ScreenTall;
        return (new SKRect(gap, gap, width - gap, line - (gap / 2)), new SKRect(gap, line + (gap / 2), width - gap, height - gap));
    }

    private static SKRect Cell(LayoutKind kind, ProjectCanvas canvas, SKRect whole) =>
        kind == LayoutKind.SideBySide ? Cells(canvas).Screen : whole;

    private static PictureBox Target(VideoProject project, Track track, Clip clip, LayoutKind kind)
    {
        var placement = clip.Placement ?? Placement.Fit;
        var own = PlacementEdits.Area(project, clip);
        var ownRadius = placement.Shape == PlacementShape.Circle
            ? Math.Min(own.Width, own.Height) / 2
            : FrameCompositor.Radius(placement, own);
        var whole = SKRect.Create(project.Canvas.Width, project.Canvas.Height);
        var (screen, camera) = Cells(project.Canvas);
        if (track.Kind == TrackKind.Webcam)
        {
            return kind switch
            {
                LayoutKind.SideBySide => new PictureBox(camera, Math.Min(camera.Width, camera.Height) * CellCorner, 1, true),
                LayoutKind.CameraOnly => new PictureBox(whole, 0, 1, true),
                LayoutKind.ScreenOnly => new PictureBox(own, ownRadius, 0, placement.Shape == PlacementShape.Circle),
                _ => new PictureBox(own, ownRadius, 1, placement.Shape == PlacementShape.Circle),
            };
        }

        return kind switch
        {
            LayoutKind.SideBySide => new PictureBox(Fit(own, screen), Math.Max(ownRadius, Math.Min(screen.Width, screen.Height) * CellCorner), 1, false),
            LayoutKind.CameraOnly => new PictureBox(own, ownRadius, 0, false),
            _ => new PictureBox(own, ownRadius, 1, false),
        };
    }

    /// <summary>A rectangle of <paramref name="shape"/>'s shape, as large as fits in the middle of <paramref name="cell"/>.</summary>
    private static SKRect Fit(SKRect shape, SKRect cell)
    {
        var scale = Math.Min(cell.Width / Math.Max(1, shape.Width), cell.Height / Math.Max(1, shape.Height));
        var (width, height) = (shape.Width * scale, shape.Height * scale);
        return SKRect.Create(cell.MidX - (width / 2), cell.MidY - (height / 2), width, height);
    }

    private static SKRect Lerp(SKRect from, SKRect to, float amount) => new(
        from.Left + ((to.Left - from.Left) * amount),
        from.Top + ((to.Top - from.Top) * amount),
        from.Right + ((to.Right - from.Right) * amount),
        from.Bottom + ((to.Bottom - from.Bottom) * amount));
}
