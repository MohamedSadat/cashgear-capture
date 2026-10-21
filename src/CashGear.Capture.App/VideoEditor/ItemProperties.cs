using CashGear.Capture.App.Editor;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>
/// The selected item's settings for the properties panel, as the image editor's <see cref="EditParameter"/>s (so the
/// same templates show them). Changing one changes the item through <c>apply</c>: one undo step per setting,
/// dragging a slider merging into it.
/// </summary>
public static class ItemProperties
{
    /// <summary>Applies a changed item: (the item, the undo step's name, the merge key).</summary>
    public delegate void Apply(TimelineItem item, string name, string mergeKey);

    public static string Title(VideoProject project, TimelineItem item)
    {
        ArgumentNullException.ThrowIfNull(project);
        return item switch
        {
            Clip clip when project.Find(clip.Id)?.Track is { } track => track.Kind switch
            {
                TrackKind.Webcam => "Webcam clip",
                TrackKind.Audio => "Sound clip",
                _ => "Video clip",
            },
            _ => "Item",
        };
    }

    public static IReadOnlyList<EditParameter> For(VideoProject project, TimelineItem item, Func<VideoProject> current, Apply apply)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(apply);
        var list = new List<EditParameter>();
        if (item is not Clip clip || project.Find(clip.Id) is not { } found)
        {
            return list;
        }

        // Each change starts from the item as it is now (undo may have changed it since the panel was made).
        void Change<T>(T parameter, string name, Func<Clip, Clip> update)
            where T : EditParameter
        {
            parameter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is "Value" or "Selected" && current().Find(clip.Id)?.Item is Clip now)
                {
                    apply(update(now), name, $"property {clip.Id} {name}");
                }
            };
            list.Add(parameter);
        }

        double Fraction(NumberParameter p) => p.Value / 100;

        if (found.Track.HasSound)
        {
            var volume = new NumberParameter("Volume", 0, 200, clip.Volume * 100, 1, " %");
            Change(volume, "volume", c => c with { Volume = Fraction(volume) });
        }

        var fadeIn = new NumberParameter("Fade in", 0, Math.Min(10, clip.Duration), clip.FadeIn, 0.1, " s");
        Change(fadeIn, "fade in", c => c with { FadeIn = Math.Round(fadeIn.Value, 2) });
        var fadeOut = new NumberParameter("Fade out", 0, Math.Min(10, clip.Duration), clip.FadeOut, 0.1, " s");
        Change(fadeOut, "fade out", c => c with { FadeOut = Math.Round(fadeOut.Value, 2) });

        if (found.Track.HasPictures)
        {
            var placement = clip.Placement ?? Placement.Fit;
            var size = new NumberParameter("Size", 5, 100, placement.Size * 100, 1, " %");
            Change(size, "size", c => c with { Placement = (c.Placement ?? Placement.Fit) with { Size = Fraction(size) } });
            var x = new NumberParameter("Across", 0, 100, placement.CenterX * 100, 1, " %");
            Change(x, "position", c => c with { Placement = (c.Placement ?? Placement.Fit) with { CenterX = Fraction(x) } });
            var y = new NumberParameter("Down", 0, 100, placement.CenterY * 100, 1, " %");
            Change(y, "position", c => c with { Placement = (c.Placement ?? Placement.Fit) with { CenterY = Fraction(y) } });
            var shape = new ChoiceParameter("Shape", [new(PlacementShape.Rectangle, "Rectangle"), new(PlacementShape.Rounded, "Rounded rectangle"), new(PlacementShape.Circle, "Circle")], placement.Shape);
            Change(shape, "shape", c => c with { Placement = (c.Placement ?? Placement.Fit) with { Shape = shape.As<PlacementShape>() } });
            var border = new ColorParameter("Border", FrameCompositor.Color(placement.Border, SKColors.White));
            Change(border, "border", c => c with { Placement = (c.Placement ?? Placement.Fit) with { Border = Hex(border.Value.Value) } });
            var width = new NumberParameter("Border width", 0, 24, placement.BorderWidth, 1, " px");
            Change(width, "border width", c => c with { Placement = (c.Placement ?? Placement.Fit) with { BorderWidth = width.Value, Border = (c.Placement ?? Placement.Fit).Border ?? "#FFFFFF" } });
            var mirror = new SwitchParameter("Mirrored", placement.Mirror);
            Change(mirror, "mirror", c => c with { Placement = (c.Placement ?? Placement.Fit) with { Mirror = mirror.Value } });
            var opacity = new NumberParameter("Opacity", 0, 100, placement.Opacity * 100, 1, " %");
            Change(opacity, "opacity", c => c with { Placement = (c.Placement ?? Placement.Fit) with { Opacity = Fraction(opacity) } });
        }

        return list;
    }

    private static string Hex(SKColor color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
}
