using System.Windows.Media;
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

    /// <summary>An annotation's keyframe buttons: add one at the playhead, remove the one there.</summary>
    public sealed record Keyframes(Action Add, Action Remove);

    /// <summary>
    /// Changes that move what follows the clip (speed, a freeze frame's length, crossfades): (clip id, speed, end
    /// speed or null, merge key), (clip id, length, merge key), (clip id) and (clip id).
    /// </summary>
    public sealed record ClipActions(Action<string, double, double?, string> SetSpeed, Action<string, double, string> SetFreeze, Action<string> Crossfade, Action<string> RemoveCrossfade);

    private static readonly Lazy<IReadOnlyList<string>> FontFamilies =
        new(() => [.. System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).Order(StringComparer.CurrentCultureIgnoreCase)]);

    public static string ShapeName(AnnotationShape shape) => shape switch
    {
        AnnotationShape.Blur => "Blurred area",
        AnnotationShape.Pixelate => "Pixelated area",
        _ => shape.ToString(),
    };

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
            AnnotationItem annotation => ShapeName(annotation.Shape),
            _ => "Item",
        };
    }

    public static IReadOnlyList<EditParameter> For(VideoProject project, TimelineItem item, Func<VideoProject> current, Apply apply, Keyframes? keyframes = null, ClipActions? actions = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(apply);
        if (item is AnnotationItem annotation)
        {
            return ForAnnotation(annotation, current, apply, keyframes);
        }

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

        if (actions is not null)
        {
            if (clip.IsFreeze)
            {
                var hold = new NumberParameter("Hold the frame for", 0.1, 30, clip.FreezeLength, 0.1, " s");
                hold.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == "Value")
                    {
                        actions.SetFreeze(clip.Id, Math.Round(hold.Value, 2), $"property {clip.Id} freeze");
                    }
                };
                list.Add(hold);
            }
            else
            {
                var speed = new NumberParameter("Speed", Clip.SlowestSpeed, Clip.FastestSpeed, clip.Speed, 0.05, "×");
                var ramp = new SwitchParameter("Change the speed along the clip", clip.IsRamp);
                var end = new NumberParameter("Speed at the end", Clip.SlowestSpeed, Clip.FastestSpeed, clip.EndSpeed ?? clip.Speed, 0.05, "×") { IsVisible = clip.IsRamp };
                void SetSpeed(string key) => actions.SetSpeed(clip.Id, Math.Round(speed.Value, 2), ramp.Value ? Math.Round(end.Value, 2) : null, $"property {clip.Id} {key}");
                speed.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == "Value")
                    {
                        SetSpeed("speed");
                    }
                };
                ramp.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == "Value")
                    {
                        end.IsVisible = ramp.Value;
                        SetSpeed("ramp");
                    }
                };
                end.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == "Value")
                    {
                        SetSpeed("end speed");
                    }
                };
                list.Add(speed);
                list.Add(ramp);
                list.Add(end);
            }

            if (CashGear.Capture.VideoEditor.Editing.TransitionEdits.CrossfadeLength(project, clip.Id) > 0)
            {
                list.Add(new ActionParameter("Remove the crossfade into the next clip", new CommunityToolkit.Mvvm.Input.RelayCommand(() => actions.RemoveCrossfade(clip.Id))));
            }
            else if (CashGear.Capture.VideoEditor.Editing.TransitionEdits.Following(found.Track, clip) is not null)
            {
                list.Add(new ActionParameter("Crossfade into the next clip (1 s)", new CommunityToolkit.Mvvm.Input.RelayCommand(() => actions.Crossfade(clip.Id))));
            }
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

    private static IReadOnlyList<EditParameter> ForAnnotation(AnnotationItem annotation, Func<VideoProject> current, Apply apply, Keyframes? keyframes)
    {
        var list = new List<EditParameter>();
        void Change<T>(T parameter, string name, Func<AnnotationItem, AnnotationItem> update)
            where T : EditParameter
        {
            parameter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is "Value" or "Selected" && current().Find(annotation.Id)?.Item is AnnotationItem now)
                {
                    apply(update(now), name, $"property {annotation.Id} {name}");
                }
            };
            list.Add(parameter);
        }

        var look = annotation.Look;
        var shape = annotation.Shape;
        var hidden = shape is AnnotationShape.Blur or AnnotationShape.Pixelate;
        if (shape == AnnotationShape.Text)
        {
            var text = new TextParameter("Text", annotation.Text, multiline: true);
            Change(text, "text", a => a with { Text = text.Value });
            var font = new FontParameter("Font", FontFamilies.Value, look.FontFamily);
            Change(font, "font", a => a with { Look = a.Look with { FontFamily = font.Value } });
            var size = new NumberParameter("Size", 8, 200, look.FontSize, 1, " px");
            Change(size, "text size", a => a with { Look = a.Look with { FontSize = size.Value } });
            var bold = new SwitchParameter("Bold", look.Bold);
            Change(bold, "bold", a => a with { Look = a.Look with { Bold = bold.Value } });
        }

        if (!hidden)
        {
            var color = new ColorParameter(shape == AnnotationShape.Text ? "Text colour" : "Colour", FrameCompositor.Color(look.Color, SKColors.Red));
            Change(color, "colour", a => a with { Look = a.Look with { Color = Hex(color.Value.Value) } });
        }

        if (shape is AnnotationShape.Text or AnnotationShape.Rectangle or AnnotationShape.Ellipse)
        {
            var fill = new ColorParameter(shape == AnnotationShape.Text ? "Background" : "Fill", look.Fill is null ? SKColors.Transparent : FrameCompositor.Color(look.Fill, SKColors.Transparent), allowTransparent: true);
            Change(fill, "fill", a => a with { Look = a.Look with { Fill = fill.IsTransparent ? null : HexWithAlpha(fill.Value.Value, shape == AnnotationShape.Text ? (byte)0xB0 : (byte)0xFF) } });
        }

        if (shape is AnnotationShape.Arrow or AnnotationShape.Line or AnnotationShape.Rectangle or AnnotationShape.Ellipse)
        {
            var width = new NumberParameter("Line width", 1, 40, look.StrokeWidth, 1, " px");
            Change(width, "line width", a => a with { Look = a.Look with { StrokeWidth = width.Value } });
        }

        if (hidden)
        {
            var strength = new NumberParameter(shape == AnnotationShape.Blur ? "Blur" : "Cell size", 2, 60, look.Strength, 1, " px");
            Change(strength, "strength", a => a with { Look = a.Look with { Strength = strength.Value } });
        }

        var opacity = new NumberParameter("Opacity", 5, 100, look.Opacity * 100, 1, " %");
        Change(opacity, "opacity", a => a with { Look = a.Look with { Opacity = opacity.Value / 100 } });
        var fadeIn = new NumberParameter("Fade in", 0, Math.Min(5, annotation.Length), annotation.FadeIn, 0.1, " s");
        Change(fadeIn, "fade in", a => a with { FadeIn = Math.Round(fadeIn.Value, 2) });
        var fadeOut = new NumberParameter("Fade out", 0, Math.Min(5, annotation.Length), annotation.FadeOut, 0.1, " s");
        Change(fadeOut, "fade out", a => a with { FadeOut = Math.Round(fadeOut.Value, 2) });
        var easing = new ChoiceParameter("Movement between keyframes", [new(Easing.Smooth, "Smooth"), new(Easing.Linear, "Even")], annotation.Easing);
        Change(easing, "movement", a => a with { Easing = easing.As<Easing>() });
        if (keyframes is not null)
        {
            list.Add(new ActionParameter($"Add a keyframe at the playhead ({annotation.Keyframes.Count} now)", new CommunityToolkit.Mvvm.Input.RelayCommand(keyframes.Add)));
            list.Add(new ActionParameter("Remove the keyframe at the playhead", new CommunityToolkit.Mvvm.Input.RelayCommand(keyframes.Remove)));
        }

        return list;
    }

    private static string Hex(SKColor color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    private static string HexWithAlpha(SKColor color, byte alpha) => $"#{alpha:X2}{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
}
