using CashGear.Capture.Editor.Presets;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Rendering;
using CashGear.Capture.VideoEditor.Resources;

namespace CashGear.Capture.VideoEditor.Editing;

/// <summary>
/// The looks: a few built in, others saved in <c>presets.json</c>. Applying one sets the canvas's background and how
/// every screen clip sits on it, in proportion to the canvas; the webcam and everything else keep their places.
/// </summary>
public static class Looks
{
    public const string NoneId = "none";
    public const string SoftId = "soft";
    public const string GradientId = "gradient";
    public const string DarkId = "dark";

    /// <summary>What a recording opens with until the user chooses another.</summary>
    public const string DefaultId = SoftId;

    /// <summary>The built-in looks, named in the UI's language.</summary>
    public static IReadOnlyList<StudioLook> BuiltIn =>
    [
        new(NoneId, VideoEditorStrings.LookNone, CanvasBackground.Black),
        new(SoftId, VideoEditorStrings.LookSoft, new CanvasBackground(BackgroundKind.LinearGradient, "#E9EDF5", "#C7D1E3"), 0.06, 20, new PlacementShadow(0, 18, 36, "#101828", 0.35)),
        new(GradientId, VideoEditorStrings.LookGradient, new CanvasBackground(BackgroundKind.LinearGradient, "#4F5BD5", "#C850C0"), 0.08, 26, new PlacementShadow(0, 26, 48, "#000000", 0.45)),
        new(DarkId, VideoEditorStrings.LookDark, new CanvasBackground(BackgroundKind.RadialGradient, "#2B2F39", "#111318"), 0.06, 18, new PlacementShadow(0, 14, 40, "#000000", 0.6), "#30FFFFFF", 1.5),
    ];

    /// <summary>The look with <paramref name="id"/> among the built-in and saved ones, or null.</summary>
    public static StudioLook? Find(string? id, IEnumerable<SavedLook>? saved = null)
    {
        if (id is null)
        {
            return null;
        }

        return BuiltIn.FirstOrDefault(l => l.Id == id)
            ?? (saved ?? []).Where(l => string.Equals(l.Name, id, StringComparison.OrdinalIgnoreCase)).Select(FromSaved).FirstOrDefault();
    }

    /// <summary>
    /// The project with <paramref name="look"/>: the background (a picture becomes one of its assets) and each screen
    /// clip's padding, corners, shadow, inset line and frame, in proportion to the canvas. Each picture keeps its place
    /// on the canvas, its webcam border, mirror and opacity.
    /// </summary>
    public static VideoProject Apply(VideoProject project, StudioLook look)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(look);
        var scale = project.Canvas.Height / 1080.0;
        var background = look.Background;
        var assets = project.Assets.Where(a => a.Id != project.Canvas.Background.PictureAssetId || InUse(project, a.Id)).ToList();
        if (background.Kind == BackgroundKind.Picture && look.PicturePath is { } path)
        {
            var picture = assets.FirstOrDefault(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)) ?? PictureAsset(path);
            if (!assets.Contains(picture))
            {
                assets.Add(picture);
            }

            background = background with { PictureAssetId = picture.Id };
        }

        var size = 1 - (2 * look.Padding);
        var shadow = look.Shadow is { } s ? s with { OffsetX = Pixels(s.OffsetX, scale), OffsetY = Pixels(s.OffsetY, scale), Blur = Pixels(s.Blur, scale) } : null;
        Clip Styled(Clip clip)
        {
            var placement = clip.Placement ?? Placement.Fit;
            return clip with
            {
                Placement = placement with
                {
                    Size = size,
                    CornerRadius = Pixels(look.CornerRadius, scale),
                    Shadow = shadow,
                    InsetBorder = look.InsetBorder,
                    InsetBorderWidth = Pixels(look.InsetBorderWidth, scale),
                    Frame = look.Frame,
                },
            };
        }

        return project with
        {
            Canvas = project.Canvas with { Background = background },
            Assets = assets,
            Tracks = [.. project.Tracks.Select(t => t.Kind == TrackKind.Video ? t with { Items = [.. t.Items.Select(i => i is Clip c ? Styled(c) : i)] } : t)],
        };
    }

    /// <summary>The look a project shows, in plain values, for saving it by name. Sizes back to a 1080-pixel-high canvas.</summary>
    public static SavedLook Capture(VideoProject project, string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        var scale = 1080.0 / project.Canvas.Height;
        var background = project.Canvas.Background;
        var placement = ScreenPlacement(project) ?? Placement.Fit;
        var shadow = placement.Shadow;
        return new SavedLook(
            name,
            background.Kind.ToString(),
            background.Color,
            background.Color2,
            background.Angle,
            background.PictureAssetId is { } id ? project.Asset(id)?.Path : null,
            background.Blur,
            background.Parallax,
            Math.Round((1 - placement.Size) / 2, 4),
            Pixels(placement.CornerRadius, scale),
            Pixels(shadow?.OffsetX ?? 0, scale),
            Pixels(shadow?.OffsetY ?? 0, scale),
            Pixels(shadow?.Blur ?? 0, scale),
            shadow?.Color ?? "#000000",
            shadow?.Opacity ?? 0,
            placement.InsetBorder,
            Pixels(placement.InsetBorderWidth, scale),
            placement.Frame.ToString());
    }

    public static StudioLook FromSaved(SavedLook saved)
    {
        ArgumentNullException.ThrowIfNull(saved);
        var kind = Enum.TryParse<BackgroundKind>(saved.Background, out var known) ? known : BackgroundKind.Color;
        var frame = Enum.TryParse<DeviceFrame>(saved.Frame, out var device) ? device : DeviceFrame.None;
        return new StudioLook(
            saved.Name,
            saved.Name,
            new CanvasBackground(kind, saved.Color, saved.Color2, saved.Angle, null, saved.Blur, saved.Parallax),
            saved.Padding,
            saved.CornerRadius,
            saved.ShadowOpacity > 0 ? new PlacementShadow(saved.ShadowOffsetX, saved.ShadowOffsetY, saved.ShadowBlur, saved.ShadowColor, saved.ShadowOpacity) : null,
            saved.InsetBorder,
            saved.InsetBorderWidth,
            frame,
            saved.Picture);
    }

    /// <summary>The look among <paramref name="looks"/> that <paramref name="project"/> shows, or null when it has been changed from any.</summary>
    public static StudioLook? Current(VideoProject project, IEnumerable<StudioLook> looks)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(looks);
        foreach (var look in looks)
        {
            var applied = Apply(project, look);
            if (applied.Canvas.Background.Kind == project.Canvas.Background.Kind
                && applied.Canvas.Background with { PictureAssetId = null } == project.Canvas.Background with { PictureAssetId = null }
                && Same(ScreenPlacement(applied), ScreenPlacement(project)))
            {
                return look;
            }
        }

        return null;
    }

    /// <summary>The first screen clip's placement, which the panel shows for them all.</summary>
    public static Placement? ScreenPlacement(VideoProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.Tracks.Where(t => t.Kind == TrackKind.Video).SelectMany(t => t.Clips).Select(c => c.Placement ?? Placement.Fit).FirstOrDefault();
    }

    /// <summary>The project with every screen clip's placement changed.</summary>
    public static VideoProject WithScreenPlacement(VideoProject project, Func<Placement, Placement> change)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(change);
        return project with
        {
            Tracks = [.. project.Tracks.Select(t => t.Kind == TrackKind.Video ? t with { Items = [.. t.Items.Select(i => i is Clip c ? c with { Placement = change(c.Placement ?? Placement.Fit) } : i)] } : t)],
        };
    }

    /// <summary>A picture file as an asset of a project (not a recording: it has no streams).</summary>
    public static MediaAsset PictureAsset(string path) => new(Ids.New(), Path.GetFullPath(path), 0, []);

    private static bool InUse(VideoProject project, string assetId) =>
        project.Tracks.SelectMany(t => t.Clips).Any(c => c.AssetId == assetId || c.Background?.PictureAssetId == assetId);

    private static bool Same(Placement? a, Placement? b) => a is null || b is null ? a == b : a == b;

    private static double Pixels(double value, double scale) => Math.Round(value * scale, 2);
}

/// <summary>A canvas's shape, for a kind of video.</summary>
public enum CanvasShape
{
    /// <summary>16:9: YouTube, players.</summary>
    Wide,

    /// <summary>9:16: Shorts, Reels, TikTok.</summary>
    Vertical,

    /// <summary>1:1.</summary>
    Square,

    /// <summary>4:5.</summary>
    Portrait,
}

/// <summary>Changing a project's canvas and keeping what is on it in place.</summary>
public static class CanvasEdits
{
    /// <summary>The webcam's layout that reframing puts over a canvas that isn't wide.</summary>
    private const LayoutKind Stacked = LayoutKind.SideBySide;

    /// <summary>A canvas of <paramref name="shape"/> for a project whose canvas's shorter side is <paramref name="shortSide"/>: the shorter side stays, both sides even.</summary>
    public static (int Width, int Height) SizeOf(CanvasShape shape, int shortSide)
    {
        var side = Math.Max(2, shortSide / 2 * 2);
        int Even(double value) => Math.Max(2, (int)Math.Round(value / 2) * 2);
        return shape switch
        {
            CanvasShape.Vertical => (side, Even(side * 16.0 / 9)),
            CanvasShape.Square => (side, side),
            CanvasShape.Portrait => (side, Even(side * 5.0 / 4)),
            _ => (Even(side * 16.0 / 9), side),
        };
    }

    /// <summary>The shape a canvas has, or null when it is none of them.</summary>
    public static CanvasShape? ShapeOf(ProjectCanvas canvas)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        foreach (var shape in Enum.GetValues<CanvasShape>())
        {
            var (width, height) = SizeOf(shape, Math.Min(canvas.Width, canvas.Height));
            if (Math.Abs(((double)canvas.Width / canvas.Height) - ((double)width / height)) < 0.02)
            {
                return shape;
            }
        }

        return null;
    }

    /// <summary>
    /// The project on a canvas of <paramref name="width"/> × <paramref name="height"/> with the screen framed by
    /// <paramref name="framing"/>. Things placed by parts of the canvas stay where they are; annotations, given in
    /// pixels, move with the canvas. A webcam over a screen that covers a canvas that isn't wide goes into a stacked
    /// layout, taken away again when the canvas goes back. The look's sizes are in proportion to the new canvas.
    /// </summary>
    public static VideoProject Change(VideoProject project, int width, int height, ScreenFraming framing)
    {
        ArgumentNullException.ThrowIfNull(project);
        var old = project.Canvas;
        var (scaleX, scaleY) = ((double)width / old.Width, (double)height / old.Height);
        var look = Math.Min(scaleX, scaleY);
        var tracks = project.Tracks.Select(track => track with
        {
            Items = [.. track.Items.Select(item => item switch
            {
                AnnotationItem annotation => annotation with
                {
                    Keyframes = [.. annotation.Keyframes.Select(k => k with { X = k.X * scaleX, Y = k.Y * scaleY, Width = k.Width * scaleX, Height = k.Height * scaleY })],
                    Look = annotation.Look with { FontSize = Math.Round(annotation.Look.FontSize * look, 1), StrokeWidth = Math.Round(annotation.Look.StrokeWidth * look, 2) },
                    CalloutX = annotation.Shape == AnnotationShape.Magnify ? annotation.CalloutX * scaleX : annotation.CalloutX,
                    CalloutY = annotation.Shape == AnnotationShape.Magnify ? annotation.CalloutY * scaleY : annotation.CalloutY,
                },
                Clip { Placement: { } placement } clip when height != old.Height => clip with { Placement = Scaled(placement, (double)height / old.Height) },
                _ => item,
            })],
        }).ToList();

        // The webcam stacked on a canvas that isn't wide; reframing's own layouts go when it is wide again.
        tracks = [.. tracks.Select(t => t.Kind == TrackKind.Layout ? t with { Items = [.. t.Items.Where(i => i is not LayoutItem { Auto: true })] } : t).Where(t => t.Kind != TrackKind.Layout || t.Items.Count > 0)];
        var moved = project with { Canvas = old with { Width = width, Height = height, Framing = framing }, Tracks = tracks };
        var webcam = tracks.Any(t => t.Kind == TrackKind.Webcam && !t.Hidden && t.Clips.Any());
        if (webcam && framing != ScreenFraming.Fit && width <= height * 1.1 && !tracks.Any(t => t.Kind == TrackKind.Layout))
        {
            var cover = new LayoutItem { Id = Ids.New(), Kind = Stacked, Length = Times.Round(Math.Max(project.Duration, TimelineEdits.ShortestItem)), Transition = 0, Auto = true };
            moved = moved with { Tracks = [.. moved.Tracks, new Track(Ids.New(), TrackKind.Layout, VideoEditorStrings.TrackLayout, [cover])] };
        }

        return moved;
    }

    private static Placement Scaled(Placement placement, double scale) => placement with
    {
        BorderWidth = Math.Round(placement.BorderWidth * scale, 2),
        CornerRadius = Math.Round(placement.CornerRadius * scale, 2),
        InsetBorderWidth = Math.Round(placement.InsetBorderWidth * scale, 2),
        Shadow = placement.Shadow is { } s ? s with { OffsetX = Math.Round(s.OffsetX * scale, 2), OffsetY = Math.Round(s.OffsetY * scale, 2), Blur = Math.Round(s.Blur * scale, 2) } : null,
    };

    /// <summary>A layout from <paramref name="at"/> for <paramref name="length"/> seconds on the first layout track with room for it (a new one on top if none has).</summary>
    public static (VideoProject Project, string Id) AddLayout(VideoProject project, LayoutKind kind, double at, double length)
    {
        ArgumentNullException.ThrowIfNull(project);
        var item = new LayoutItem { Id = Ids.New(), Kind = kind, Start = Times.Round(Math.Max(0, at)), Length = Times.Round(Math.Max(TimelineEdits.ShortestItem, length)) };
        var free = project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Layout && !t.Locked && !t.Items.Any(i => i.Overlaps(item.Start, item.End)));
        if (free is not null)
        {
            return (project.WithTrack(free.WithItems([.. free.Items, item])), item.Id);
        }

        var count = project.Tracks.Count(t => t.Kind == TrackKind.Layout);
        var track = new Track(Ids.New(), TrackKind.Layout, count == 0 ? VideoEditorStrings.TrackLayout : VideoEditorStrings.TrackLayoutNumbered(count + 1), [item]);
        return (project with { Tracks = [.. project.Tracks, track] }, item.Id);
    }
}
