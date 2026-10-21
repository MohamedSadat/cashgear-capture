using System.Windows.Media;
using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.VideoEditor.Model;
using EffectKind = CashGear.Capture.VideoEditor.Model.EffectKind;
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
            EffectItem { Kind: EffectKind.Clicks } => "Click highlights",
            EffectItem { Kind: EffectKind.Keystrokes } => "Keystrokes",
            EffectItem => "Pointer effects",
            ZoomItem => "Zoom",
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

        if (item is EffectItem effect)
        {
            return ForEffect(project, effect, current, apply);
        }

        if (item is ZoomItem zoom)
        {
            return ForZoom(project, zoom, current, apply);
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

    /// <summary>
    /// Whether the recordings an effect is over have the pointer in their pictures (then it can't be resized,
    /// smoothed or hidden), and whether any has metadata at all.
    /// </summary>
    public static (bool HasMetadata, bool PointerInPictures) MetadataOf(VideoProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var any = false;
        var inPictures = false;
        foreach (var asset in project.Assets.Where(a => a.MetadataPath is not null))
        {
            try
            {
                var metadata = Core.Overlays.RecordingMetadata.Load(asset.MetadataPath!);
                any = true;
                inPictures |= metadata.CursorInVideo;
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                // not readable: no effects from it
            }
        }

        return (any, inPictures);
    }

    private static IReadOnlyList<EditParameter> ForEffect(VideoProject project, EffectItem effect, Func<VideoProject> current, Apply apply)
    {
        var list = new List<EditParameter>();
        void Change<T>(T parameter, string name, Func<EffectItem, EffectItem> update)
            where T : EditParameter
        {
            parameter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is "Value" or "Selected" && current().Find(effect.Id)?.Item is EffectItem now)
                {
                    apply(update(now), name, $"property {effect.Id} {name}");
                }
            };
            list.Add(parameter);
        }

        var (hasMetadata, pointerInPictures) = MetadataOf(project);
        if (!hasMetadata)
        {
            list.Add(new NoteParameter("No recording here has a metadata file, so there are no clicks, keys or pointer to show. Recordings get one when Settings › Overlays › Save a metadata file is on."));
        }

        switch (effect.Kind)
        {
            case EffectKind.Clicks:
            {
                var clicks = effect.Clicks ?? new ClickOverlaySettings();
                var left = new ColorParameter("Left button", FrameCompositor.Color(clicks.LeftColor, SKColors.Yellow));
                Change(left, "left colour", e => e with { Clicks = (e.Clicks ?? new ClickOverlaySettings()) with { LeftColor = Hex(left.Value.Value) } });
                var right = new ColorParameter("Right button", FrameCompositor.Color(clicks.RightColor, SKColors.Red));
                Change(right, "right colour", e => e with { Clicks = (e.Clicks ?? new ClickOverlaySettings()) with { RightColor = Hex(right.Value.Value) } });
                var size = new NumberParameter("Size", 20, 200, clicks.Size, 1, " px");
                Change(size, "click size", e => e with { Clicks = (e.Clicks ?? new ClickOverlaySettings()) with { Size = (int)size.Value } });
                break;
            }

            case EffectKind.Keystrokes:
            {
                var keys = effect.Keystrokes ?? new KeystrokeOverlaySettings();
                var position = new ChoiceParameter("Position",
                [
                    new(OverlayAnchor.BottomCenter, "Bottom centre"), new(OverlayAnchor.BottomLeft, "Bottom left"), new(OverlayAnchor.BottomRight, "Bottom right"),
                    new(OverlayAnchor.TopCenter, "Top centre"), new(OverlayAnchor.TopLeft, "Top left"), new(OverlayAnchor.TopRight, "Top right"),
                ], keys.Position);
                Change(position, "keys position", e => e with { Keystrokes = (e.Keystrokes ?? new KeystrokeOverlaySettings()) with { Position = position.As<OverlayAnchor>() } });
                var size = new NumberParameter("Size", 8, 72, keys.FontSize, 1, " pt");
                Change(size, "keys size", e => e with { Keystrokes = (e.Keystrokes ?? new KeystrokeOverlaySettings()) with { FontSize = (int)size.Value } });
                var fade = new NumberParameter("Shown for", 0.5, 10, keys.FadeSeconds, 0.1, " s");
                Change(fade, "keys time", e => e with { Keystrokes = (e.Keystrokes ?? new KeystrokeOverlaySettings()) with { FadeSeconds = fade.Value } });
                break;
            }

            default:
            {
                var pointer = effect.Pointer ?? new PointerOverlaySettings();
                var highlight = new ChoiceParameter("Around the pointer", [new(PointerEffect.None, "Nothing"), new(PointerEffect.Halo, "A halo"), new(PointerEffect.Spotlight, "A spotlight (the rest dimmed)")], pointer.Effect);
                Change(highlight, "pointer highlight", e => e with { Pointer = (e.Pointer ?? new PointerOverlaySettings()) with { Effect = highlight.As<PointerEffect>() } });
                var color = new ColorParameter("Halo colour", FrameCompositor.Color(pointer.Color, SKColors.Yellow));
                Change(color, "halo colour", e => e with { Pointer = (e.Pointer ?? new PointerOverlaySettings()) with { Color = Hex(color.Value.Value) } });
                var radius = new NumberParameter("Radius", 20, 300, pointer.Radius, 1, " px");
                Change(radius, "pointer radius", e => e with { Pointer = (e.Pointer ?? new PointerOverlaySettings()) with { Radius = (int)radius.Value } });
                var dim = new NumberParameter("Dim the rest", 10, 90, pointer.Dim, 1, " %");
                Change(dim, "dim", e => e with { Pointer = (e.Pointer ?? new PointerOverlaySettings()) with { Dim = (int)dim.Value } });
                if (pointerInPictures)
                {
                    list.Add(new NoteParameter("The pointer is in this recording's pictures, so it can't be made bigger, smoother or hidden here. Recordings made with Settings › Overlays › Keep the video clean leave it out for the editor to draw."));
                }
                else
                {
                    var scale = new NumberParameter("Pointer size", 50, 300, pointer.Scale * 100, 5, " %");
                    Change(scale, "pointer size", e => e with { Pointer = (e.Pointer ?? new PointerOverlaySettings()) with { Scale = scale.Value / 100 } });
                    var smoothing = new NumberParameter("Smooth its movement", 0, 100, effect.Smoothing * 100, 5, " %");
                    Change(smoothing, "smoothing", e => e with { Smoothing = smoothing.Value / 100 });
                    var idle = new SwitchParameter("Hide it when it stands still", pointer.HideWhenIdle);
                    Change(idle, "hide when idle", e => e with { Pointer = (e.Pointer ?? new PointerOverlaySettings()) with { HideWhenIdle = idle.Value } });
                    var after = new NumberParameter("After", 0.5, 10, pointer.IdleSeconds, 0.5, " s");
                    Change(after, "idle time", e => e with { Pointer = (e.Pointer ?? new PointerOverlaySettings()) with { IdleSeconds = after.Value } });
                }

                break;
            }
        }

        return list;
    }

    /// <summary>A zoom's settings; changing one makes it the user's own (Auto-Zoom no longer replaces it).</summary>
    private static IReadOnlyList<EditParameter> ForZoom(VideoProject project, ZoomItem zoom, Func<VideoProject> current, Apply apply)
    {
        var list = new List<EditParameter>();
        void Change<T>(T parameter, string name, Func<ZoomItem, ZoomItem> update)
            where T : EditParameter
        {
            parameter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is "Value" or "Selected" && current().Find(zoom.Id)?.Item is ZoomItem now)
                {
                    apply(update(now) with { Auto = false }, name, $"property {zoom.Id} {name}");
                }
            };
            list.Add(parameter);
        }

        var scale = new NumberParameter("Zoom", ZoomItem.SmallestScale * 100, ZoomItem.LargestScale * 100, zoom.ClampedScale * 100, 5, " %");
        Change(scale, "zoom", z => z with { Scale = scale.Value / 100 });
        var follow = new SwitchParameter("Follow the pointer", zoom.Focus == ZoomFocus.FollowPointer);
        var across = new NumberParameter("Across", 0, 100, zoom.CenterX * 100, 1, " %") { IsVisible = !follow.Value };
        var down = new NumberParameter("Down", 0, 100, zoom.CenterY * 100, 1, " %") { IsVisible = !follow.Value };
        follow.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Value")
            {
                across.IsVisible = !follow.Value;
                down.IsVisible = !follow.Value;
            }
        };
        Change(follow, "follow the pointer", z => z with { Focus = follow.Value ? ZoomFocus.FollowPointer : ZoomFocus.Fixed });
        if (!MetadataOf(project).HasMetadata)
        {
            list.Add(new NoteParameter("No recording here has a metadata file, so there is no pointer to follow: the zoom stays on its centre."));
        }

        Change(across, "zoom across", z => z with { CenterX = across.Value / 100 });
        Change(down, "zoom down", z => z with { CenterY = down.Value / 100 });
        var easeIn = new NumberParameter("Zoom in over", 0, 3, zoom.EaseIn, 0.1, " s");
        Change(easeIn, "zoom in time", z => z with { EaseIn = Math.Round(easeIn.Value, 2) });
        var easeOut = new NumberParameter("Zoom out over", 0, 3, zoom.EaseOut, 0.1, " s");
        Change(easeOut, "zoom out time", z => z with { EaseOut = Math.Round(easeOut.Value, 2) });
        list.Add(new NoteParameter("While it is selected, the preview shows the whole picture and the zoom's frame: drag the frame to place the zoom (it then stays there), or its corner to zoom in or out."));
        return list;
    }

    private static string Hex(SKColor color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    private static string HexWithAlpha(SKColor color, byte alpha) => $"#{alpha:X2}{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
}
