using System.ComponentModel;
using System.Windows.Media;
using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Licensing;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using EffectKind = CashGear.Capture.VideoEditor.Model.EffectKind;
using CashGear.Capture.VideoEditor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>
/// The selected item's settings for the properties panel, as the image editor's <see cref="EditParameter"/>s (so the
/// same templates show them). Changing one changes the item through <c>apply</c>: one undo step per setting,
/// dragging a slider merging into it. Each setting has a key that merges its steps (never translated) and the
/// step's name shown after Undo (in the UI language).
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

    /// <summary>
    /// A caption's buttons and its track's look: split it at the playhead, merge it with the next, and change the
    /// track's caption style ((track id, change, merge key, undo step's name)).
    /// </summary>
    public sealed record CaptionActions(Action<string> Split, Action<string> Merge, Action<string, Func<CaptionStyle, CaptionStyle>, string, string> ChangeStyle);

    private static readonly Lazy<IReadOnlyList<string>> FontFamilies =
        new(() => [.. System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).Order(StringComparer.CurrentCultureIgnoreCase)]);

    public static string ShapeName(AnnotationShape shape) => shape switch
    {
        AnnotationShape.Text => VideoEditorText.ShapeText,
        AnnotationShape.Arrow => VideoEditorText.ShapeArrow,
        AnnotationShape.Line => VideoEditorText.ShapeLine,
        AnnotationShape.Rectangle => VideoEditorText.ShapeRectangle,
        AnnotationShape.Ellipse => VideoEditorText.ShapeEllipse,
        AnnotationShape.Highlight => VideoEditorText.ShapeHighlight,
        AnnotationShape.Blur => VideoEditorText.ShapeBlurredArea,
        AnnotationShape.Pixelate => VideoEditorText.ShapePixelatedArea,
        _ => shape.ToString(),
    };

    public static string Title(VideoProject project, TimelineItem item)
    {
        ArgumentNullException.ThrowIfNull(project);
        return item switch
        {
            Clip clip when project.Find(clip.Id)?.Track is { } track => track.Kind switch
            {
                TrackKind.Webcam => VideoEditorText.WebcamClip,
                TrackKind.Audio => VideoEditorText.SoundClip,
                _ => VideoEditorText.VideoClip,
            },
            AnnotationItem annotation => ShapeName(annotation.Shape),
            EffectItem { Kind: EffectKind.Clicks } => VideoEditorText.ClickHighlights,
            EffectItem { Kind: EffectKind.Keystrokes } => VideoEditorText.Keystrokes,
            EffectItem => VideoEditorText.PointerEffects,
            ZoomItem => VideoEditorText.ZoomTitle,
            CaptionItem => VideoEditorText.CaptionTitle,
            LayoutItem => VideoEditorText.LayoutTitle,
            _ => VideoEditorText.ItemTitle,
        };
    }

    public static IReadOnlyList<EditParameter> For(VideoProject project, TimelineItem item, Func<VideoProject> current, Apply apply, Keyframes? keyframes = null, ClipActions? actions = null, CaptionActions? captions = null, IFeatureGate? features = null, Func<string?>? pickPicture = null, Action<MediaAsset, string, string>? addAsset = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(apply);
        var gate = features ?? FeatureGate.Open;
        if (item is LayoutItem layout)
        {
            return ForLayout(layout, current, apply);
        }

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
            return ForZoom(project, zoom, current, apply, gate);
        }

        if (item is CaptionItem caption)
        {
            return ForCaption(project, caption, current, apply, captions);
        }

        var list = new List<EditParameter>();
        if (item is not Clip clip || project.Find(clip.Id) is not { } found)
        {
            return list;
        }

        // Each change starts from the item as it is now (undo may have changed it since the panel was made).
        void Change<T>(T parameter, [Localizable(false)] string key, string name, Func<Clip, Clip> update)
            where T : EditParameter
        {
            parameter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is "Value" or "Selected" && current().Find(clip.Id)?.Item is Clip now)
                {
                    apply(update(now), name, $"property {clip.Id} {key}");
                }
            };
            list.Add(parameter);
        }

        double Fraction(NumberParameter p) => p.Value / 100;

        if (found.Track.HasSound)
        {
            var volume = new NumberParameter(VideoEditorText.Volume, 0, 200, clip.Volume * 100, 1, " %");
            Change(volume, "volume", VideoEditorText.UndoVolume, c => c with { Volume = Fraction(volume) });
        }

        if (actions is not null)
        {
            if (clip.IsFreeze)
            {
                var hold = new NumberParameter(VideoEditorText.HoldFrameFor, 0.1, 30, clip.FreezeLength, 0.1, VideoEditorText.SecondsUnit);
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
                var speed = new NumberParameter(VideoEditorText.Speed, Clip.SlowestSpeed, Clip.FastestSpeed, clip.Speed, 0.05, "×");
                var ramp = new SwitchParameter(VideoEditorText.SpeedRamp, clip.IsRamp);
                var end = new NumberParameter(VideoEditorText.SpeedAtEnd, Clip.SlowestSpeed, Clip.FastestSpeed, clip.EndSpeed ?? clip.Speed, 0.05, "×") { IsVisible = clip.IsRamp };
                void SetSpeed([Localizable(false)] string key) => actions.SetSpeed(clip.Id, Math.Round(speed.Value, 2), ramp.Value ? Math.Round(end.Value, 2) : null, $"property {clip.Id} {key}");
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
                list.Add(new ActionParameter(VideoEditorText.RemoveCrossfade, new CommunityToolkit.Mvvm.Input.RelayCommand(() => actions.RemoveCrossfade(clip.Id))));
            }
            else if (CashGear.Capture.VideoEditor.Editing.TransitionEdits.Following(found.Track, clip) is not null)
            {
                list.Add(new ActionParameter(VideoEditorText.AddCrossfade, new CommunityToolkit.Mvvm.Input.RelayCommand(() => actions.Crossfade(clip.Id))));
            }
        }

        var fadeIn = new NumberParameter(VideoEditorText.FadeIn, 0, Math.Min(10, clip.Duration), clip.FadeIn, 0.1, VideoEditorText.SecondsUnit);
        Change(fadeIn, "fade in", VideoEditorText.UndoFadeIn, c => c with { FadeIn = Math.Round(fadeIn.Value, 2) });
        var fadeOut = new NumberParameter(VideoEditorText.FadeOut, 0, Math.Min(10, clip.Duration), clip.FadeOut, 0.1, VideoEditorText.SecondsUnit);
        Change(fadeOut, "fade out", VideoEditorText.UndoFadeOut, c => c with { FadeOut = Math.Round(fadeOut.Value, 2) });

        if (found.Track.HasPictures)
        {
            var placement = clip.Placement ?? Placement.Fit;
            var size = new NumberParameter(VideoEditorText.Size, 5, 100, placement.Size * 100, 1, " %");
            Change(size, "size", VideoEditorText.UndoSize, c => c with { Placement = (c.Placement ?? Placement.Fit) with { Size = Fraction(size) } });
            var x = new NumberParameter(VideoEditorText.Across, 0, 100, placement.CenterX * 100, 1, " %");
            Change(x, "position", VideoEditorText.UndoPosition, c => c with { Placement = (c.Placement ?? Placement.Fit) with { CenterX = Fraction(x) } });
            var y = new NumberParameter(VideoEditorText.Down, 0, 100, placement.CenterY * 100, 1, " %");
            Change(y, "position", VideoEditorText.UndoPosition, c => c with { Placement = (c.Placement ?? Placement.Fit) with { CenterY = Fraction(y) } });
            var shape = new ChoiceParameter(VideoEditorText.Shape, [new(PlacementShape.Rectangle, VideoEditorText.Rectangle), new(PlacementShape.Rounded, VideoEditorText.RoundedRectangle), new(PlacementShape.Circle, VideoEditorText.Circle)], placement.Shape);
            Change(shape, "shape", VideoEditorText.UndoShape, c => c with { Placement = (c.Placement ?? Placement.Fit) with { Shape = shape.As<PlacementShape>() } });
            var border = new ColorParameter(VideoEditorText.Border, FrameCompositor.Color(placement.Border, SKColors.White));
            Change(border, "border", VideoEditorText.UndoBorder, c => c with { Placement = (c.Placement ?? Placement.Fit) with { Border = Hex(border.Value.Value) } });
            var width = new NumberParameter(VideoEditorText.BorderWidth, 0, 24, placement.BorderWidth, 1, " px");
            Change(width, "border width", VideoEditorText.UndoBorderWidth, c => c with { Placement = (c.Placement ?? Placement.Fit) with { BorderWidth = width.Value, Border = (c.Placement ?? Placement.Fit).Border ?? "#FFFFFF" } });
            var mirror = new SwitchParameter(VideoEditorText.Mirrored, placement.Mirror);
            Change(mirror, "mirror", VideoEditorText.UndoMirror, c => c with { Placement = (c.Placement ?? Placement.Fit) with { Mirror = mirror.Value } });
            var opacity = new NumberParameter(VideoEditorText.Opacity, 0, 100, placement.Opacity * 100, 1, " %");
            Change(opacity, "opacity", VideoEditorText.UndoOpacity, c => c with { Placement = (c.Placement ?? Placement.Fit) with { Opacity = Fraction(opacity) } });
            var corners = new NumberParameter(VideoEditorText.Corners, 0, 200, placement.CornerRadius, 1, " px");
            Change(corners, "corners", VideoEditorText.UndoCorners, c => c with { Placement = (c.Placement ?? Placement.Fit) with { CornerRadius = corners.Value } });
            var shadow = new NumberParameter(VideoEditorText.ShadowStrength, 0, 100, (placement.Shadow?.Opacity ?? 0) * 100, 1, " %");
            Change(shadow, "shadow", VideoEditorText.UndoShadow, c => c with { Placement = WithShadow(c.Placement ?? Placement.Fit, project.Canvas, Fraction(shadow)) });
            if (found.Track.Kind == TrackKind.Webcam && gate.IsEnabled(ProFeature.WebcamBackground))
            {
                AddBackdrop(clip, list, current, apply, pickPicture, addAsset);
            }
        }

        return list;
    }

    /// <summary>A placement with a shadow of this strength: a soft one in proportion to the canvas when it had none, none at 0.</summary>
    private static Placement WithShadow(Placement placement, ProjectCanvas canvas, double strength)
    {
        if (strength <= 0)
        {
            return placement with { Shadow = null };
        }

        var scale = canvas.Height / 1080.0;
        return placement with { Shadow = (placement.Shadow ?? new PlacementShadow(0, Math.Round(16 * scale, 2), Math.Round(24 * scale, 2))) with { Opacity = strength } };
    }

    /// <summary>A webcam clip's background: what is done with what is behind the person.</summary>
    private static void AddBackdrop(Clip clip, List<EditParameter> list, Func<VideoProject> current, Apply apply, Func<string?>? pickPicture, Action<MediaAsset, string, string>? addAsset)
    {
        var backdrop = clip.Background ?? new WebcamBackground();
        void Change<T>(T parameter, [Localizable(false)] string key, Func<WebcamBackground, WebcamBackground> update)
            where T : EditParameter
        {
            parameter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is "Value" or "Selected" && current().Find(clip.Id)?.Item is Clip now)
                {
                    apply(now with { Background = update(now.Background ?? new WebcamBackground()) }, VideoEditorText.UndoWebcamBackdrop, $"property {clip.Id} {key}");
                }
            };
            list.Add(parameter);
        }

        var effect = new ChoiceParameter(VideoEditorText.WebcamBackdrop, [new(BackgroundEffect.None, VideoEditorText.BackdropKeep), new(BackgroundEffect.Blur, VideoEditorText.BackdropBlur), new(BackgroundEffect.Replace, VideoEditorText.BackdropReplace), new(BackgroundEffect.Remove, VideoEditorText.BackdropRemove)], backdrop.Effect);
        var strength = new NumberParameter(VideoEditorText.Blur, 2, 60, backdrop.Strength, 1, " px") { IsVisible = backdrop.Effect == BackgroundEffect.Blur };
        var color = new ColorParameter(VideoEditorText.Colour, FrameCompositor.Color(backdrop.Color, SKColors.Black)) { IsVisible = backdrop.Effect == BackgroundEffect.Replace };
        var picture = new FileParameter(VideoEditorText.PictureLabel, backdrop.PictureAssetId is { } id ? current().Asset(id)?.Path : null, pickPicture ?? (() => null)) { IsVisible = backdrop.Effect == BackgroundEffect.Replace };
        effect.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Selected")
            {
                var chosen = effect.As<BackgroundEffect>();
                strength.IsVisible = chosen == BackgroundEffect.Blur;
                color.IsVisible = chosen == BackgroundEffect.Replace;
                picture.IsVisible = chosen == BackgroundEffect.Replace;
            }
        };
        Change(effect, "backdrop", b => b with { Effect = effect.As<BackgroundEffect>() });
        Change(strength, "backdrop blur", b => b with { Strength = strength.Value });
        Change(color, "backdrop colour", b => b with { Color = Hex(color.Value.Value), PictureAssetId = null });
        picture.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Path" && picture.Path is { } path && current() is var project)
            {
                // The picture joins the project's media and the clip uses it, as one undo step.
                var asset = project.Assets.FirstOrDefault(a => string.Equals(a.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) ?? Looks.PictureAsset(path);
                var mergeKey = $"property {clip.Id} picture {asset.Id}";
                addAsset?.Invoke(asset, VideoEditorText.UndoWebcamBackdrop, mergeKey);
                if (current().Find(clip.Id)?.Item is Clip latest)
                {
                    apply(latest with { Background = (latest.Background ?? new WebcamBackground()) with { PictureAssetId = asset.Id } }, VideoEditorText.UndoWebcamBackdrop, mergeKey);
                }
            }
        };
        list.Add(picture);
        list.Add(new NoteParameter(VideoEditorText.BackdropNote));
    }

    /// <summary>A layout's kind and how long the change into it takes.</summary>
    private static IReadOnlyList<EditParameter> ForLayout(LayoutItem layout, Func<VideoProject> current, Apply apply)
    {
        var list = new List<EditParameter>();
        void Change<T>(T parameter, [Localizable(false)] string key, string name, Func<LayoutItem, LayoutItem> update)
            where T : EditParameter
        {
            parameter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is "Value" or "Selected" && current().Find(layout.Id)?.Item is LayoutItem now)
                {
                    apply(update(now), name, $"property {layout.Id} {key}");
                }
            };
            list.Add(parameter);
        }

        var kind = new ChoiceParameter(VideoEditorText.LayoutTitle, [.. Enum.GetValues<LayoutKind>().Select(k => new Choice(k, CashGear.Capture.VideoEditor.Timeline.TimelineRenderer.LayoutLabel(k)))], layout.Kind);
        Change(kind, "layout", VideoEditorText.UndoLayout, l => l with { Kind = kind.As<LayoutKind>(), Auto = false });
        var time = new NumberParameter(VideoEditorText.LayoutChangeOver, 0, 3, layout.Transition, 0.1, VideoEditorText.SecondsUnit);
        Change(time, "layout time", VideoEditorText.UndoLayoutTime, l => l with { Transition = Math.Round(time.Value, 2), Auto = false });
        return list;
    }

    /// <summary>What the look panel asks of the window.</summary>
    /// <param name="Looks">The looks to choose from.</param>
    /// <param name="SavedNames">The looks saved by name.</param>
    /// <param name="ChangeInPlace">A change that keeps the panel: (undo step's name, merge key, change).</param>
    /// <param name="ChangeAndRemake">A change after which the panel is made again.</param>
    public sealed record LookActions(
        IReadOnlyList<StudioLook> Looks,
        IReadOnlyList<string> SavedNames,
        Action<string> ApplyLook,
        Func<Task> SaveLook,
        Func<string, Task> DeleteLook,
        System.Windows.Input.ICommand UseForRecordings,
        string NewRecordingsLook,
        Action<string, string, Func<VideoProject, VideoProject>> ChangeInPlace,
        Action<string, string, Func<VideoProject, VideoProject>> ChangeAndRemake,
        Func<string?> PickPicture,
        Func<string> ReframeNote);

    /// <summary>The project's look and canvas, for the panel when nothing is selected.</summary>
    public static IReadOnlyList<EditParameter> ForProject(VideoProject project, Func<VideoProject> current, LookActions actions, IFeatureGate? features = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(actions);
        var gate = features ?? FeatureGate.Open;
        var pro = gate.IsEnabled(ProFeature.StudioLook);
        var list = new List<EditParameter>();
        const string custom = "";
        var shown = Looks.Current(project, actions.Looks);
        var choices = new List<Choice>(actions.Looks.Select(l => new Choice(l.Id, l.Name)));
        if (shown is null)
        {
            choices.Add(new Choice(custom, VideoEditorText.LookCustom));
        }

        var look = new ChoiceParameter(VideoEditorText.LookLabel, choices, shown?.Id ?? custom);
        look.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Selected" && look.As<string>() is { Length: > 0 } id)
            {
                actions.ApplyLook(id);
            }
        };
        list.Add(look);
        if (pro)
        {
            var saved = new PresetsParameter(VideoEditorText.SavedLooks, actions.ApplyLook, actions.SaveLook, actions.DeleteLook);
            saved.Refresh(actions.SavedNames, shown?.Name);
            list.Add(saved);
        }

        list.Add(new ActionParameter(VideoEditorText.NewRecordingsLook(actions.NewRecordingsLook), new CommunityToolkit.Mvvm.Input.RelayCommand(() => actions.UseForRecordings.Execute(null))));

        // The background.
        void InPlace(string name, [Localizable(false)] string key, Func<VideoProject, VideoProject> change) => actions.ChangeInPlace(name, key, change);
        void Remake(string name, [Localizable(false)] string key, Func<VideoProject, VideoProject> change) => actions.ChangeAndRemake(name, key, change);
        var background = project.Canvas.Background;
        void Background([Localizable(false)] string key, Func<CanvasBackground, CanvasBackground> change) =>
            InPlace(VideoEditorText.UndoCanvasBackground, key, p => p with { Canvas = p.Canvas with { Background = change(p.Canvas.Background) } });
        void Watch(EditParameter parameter, Action changed)
        {
            parameter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is "Value" or "Selected")
                {
                    changed();
                }
            };
            list.Add(parameter);
        }

        var gradient = background.Kind is BackgroundKind.LinearGradient or BackgroundKind.RadialGradient;
        var kind = new ChoiceParameter(VideoEditorText.Background,
        [
            new(BackgroundKind.Color, VideoEditorText.BackgroundOneColour), new(BackgroundKind.LinearGradient, VideoEditorText.BackgroundAcross), new(BackgroundKind.RadialGradient, VideoEditorText.BackgroundFromMiddle),
            new(BackgroundKind.Picture, VideoEditorText.BackgroundPictureFile), new(BackgroundKind.BlurredScreen, VideoEditorText.BackgroundBlurredScreen),
        ], background.Kind);
        var first = new ColorParameter(VideoEditorText.Colour, FrameCompositor.Color(background.Color, SKColors.Black)) { IsVisible = background.Kind is BackgroundKind.Color || gradient };
        var second = new ColorParameter(VideoEditorText.SecondColour, FrameCompositor.Color(background.Color2, FrameCompositor.Color(background.Color, SKColors.Black))) { IsVisible = gradient };
        var angle = new NumberParameter(VideoEditorText.GradientDirection, 0, 360, background.Angle, 5, "°") { IsVisible = background.Kind == BackgroundKind.LinearGradient };
        var picture = new FileParameter(VideoEditorText.PictureLabel, background.PictureAssetId is { } id ? project.Asset(id)?.Path : null, actions.PickPicture) { IsVisible = background.Kind == BackgroundKind.Picture };
        var blur = new NumberParameter(VideoEditorText.Blur, 4, 200, background.Blur, 2, " px") { IsVisible = background.Kind == BackgroundKind.BlurredScreen };
        var parallax = new NumberParameter(VideoEditorText.Parallax, 0, 100, background.Parallax * 100, 5, " %") { IsVisible = background.Kind != BackgroundKind.Color };
        kind.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Selected")
            {
                Remake(VideoEditorText.UndoCanvasBackground, "background kind", p => p with { Canvas = p.Canvas with { Background = p.Canvas.Background with { Kind = kind.As<BackgroundKind>() } } });
            }
        };
        list.Add(kind);
        Watch(first, () => Background("background colour", b => b with { Color = Hex(first.Value.Value) }));
        Watch(second, () => Background("background second colour", b => b with { Color2 = Hex(second.Value.Value) }));
        Watch(angle, () => Background("background angle", b => b with { Angle = angle.Value }));
        picture.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Path" && picture.Path is { } path)
            {
                var asset = current().Assets.FirstOrDefault(a => string.Equals(a.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)) ?? Looks.PictureAsset(path);
                InPlace(VideoEditorText.UndoCanvasBackground, $"background picture {asset.Id}", p => p.WithAsset(asset) with { Canvas = p.Canvas with { Background = p.Canvas.Background with { PictureAssetId = asset.Id } } });
            }
        };
        list.Add(picture);
        Watch(blur, () => Background("background blur", b => b with { Blur = blur.Value }));
        Watch(parallax, () => Background("background parallax", b => b with { Parallax = parallax.Value / 100 }));

        // How the screen sits on it, for every screen clip.
        var placement = Looks.ScreenPlacement(project) ?? Placement.Fit;
        void Screen([Localizable(false)] string key, Func<Placement, Placement> change) =>
            InPlace(VideoEditorText.UndoScreenLook, key, p => Looks.WithScreenPlacement(p, change));
        var scale = project.Canvas.Height / 1080.0;
        var padding = new NumberParameter(VideoEditorText.Padding, 0, 30, (1 - placement.Size) / 2 * 100, 0.5, " %");
        Watch(padding, () => Screen("screen padding", s => s with { Size = Math.Round(1 - (2 * padding.Value / 100), 4) }));
        var corners = new NumberParameter(VideoEditorText.Corners, 0, 200, placement.CornerRadius, 1, " px");
        Watch(corners, () => Screen("screen corners", s => s with { CornerRadius = corners.Value }));
        var strength = new NumberParameter(VideoEditorText.ShadowStrength, 0, 100, (placement.Shadow?.Opacity ?? 0) * 100, 1, " %");
        Watch(strength, () => Screen("screen shadow", s => WithShadow(s, project.Canvas, strength.Value / 100)));
        var soft = new NumberParameter(VideoEditorText.ShadowSoftness, 1, 200, placement.Shadow?.Blur ?? 24 * scale, 1, " px");
        Watch(soft, () => Screen("screen shadow softness", s => s.Shadow is { } shadow ? s with { Shadow = shadow with { Blur = soft.Value } } : s));
        var drop = new NumberParameter(VideoEditorText.ShadowDrop, 0, 200, placement.Shadow?.OffsetY ?? 16 * scale, 1, " px");
        Watch(drop, () => Screen("screen shadow drop", s => s.Shadow is { } shadow ? s with { Shadow = shadow with { OffsetY = drop.Value } } : s));
        var shade = new ColorParameter(VideoEditorText.ShadowColour, FrameCompositor.Color(placement.Shadow?.Color, SKColors.Black));
        Watch(shade, () => Screen("screen shadow colour", s => s.Shadow is { } shadow ? s with { Shadow = shadow with { Color = Hex(shade.Value.Value) } } : s));
        var line = new NumberParameter(VideoEditorText.InnerLine, 0, 12, placement.InsetBorderWidth, 0.5, " px");
        Watch(line, () => Screen("screen line", s => s with { InsetBorderWidth = line.Value, InsetBorder = s.InsetBorder ?? "#30FFFFFF" }));
        var lineColor = new ColorParameter(VideoEditorText.InnerLineColour, FrameCompositor.Color(placement.InsetBorder, SKColors.White));
        Watch(lineColor, () => Screen("screen line colour", s => s with { InsetBorder = HexWithAlpha(lineColor.Value.Value, 0x60) }));
        if (pro)
        {
            var device = new ChoiceParameter(VideoEditorText.DeviceLabel,
            [
                new(DeviceFrame.None, VideoEditorText.DeviceNone), new(DeviceFrame.Browser, VideoEditorText.DeviceBrowser), new(DeviceFrame.Laptop, VideoEditorText.DeviceLaptop),
                new(DeviceFrame.Monitor, VideoEditorText.DeviceMonitor), new(DeviceFrame.Phone, VideoEditorText.DevicePhone),
            ], placement.Frame);
            device.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == "Selected")
                {
                    InPlace(VideoEditorText.UndoDevice, "screen device", p => Looks.WithScreenPlacement(p, s => s with { Frame = device.As<DeviceFrame>() }));
                }
            };
            list.Add(device);
        }

        // The canvas.
        var canvas = project.Canvas;
        var known = CanvasEdits.ShapeOf(canvas);
        var shapes = new List<Choice>
        {
            new(CanvasShape.Wide, VideoEditorText.ShapeWide), new(CanvasShape.Vertical, VideoEditorText.ShapeVertical), new(CanvasShape.Square, VideoEditorText.ShapeSquare), new(CanvasShape.Portrait, VideoEditorText.ShapePortrait),
        };
        if (known is null)
        {
            shapes.Add(new Choice(string.Empty, VideoEditorText.ShapeOther(canvas.Width, canvas.Height)));
        }

        var shape = new ChoiceParameter(VideoEditorText.CanvasShapeLabel, shapes, known is { } value ? value : string.Empty);
        shape.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Selected" && shape.Selected.Value is CanvasShape chosen)
            {
                Remake(VideoEditorText.UndoCanvasShape, "canvas shape", p =>
                {
                    var (width, height) = CanvasEdits.SizeOf(chosen, Math.Min(p.Canvas.Width, p.Canvas.Height));
                    var framing = chosen == CanvasShape.Wide ? ScreenFraming.Fit : pro ? ScreenFraming.FollowPointer : ScreenFraming.Fit;
                    return CanvasEdits.Change(p, width, height, framing);
                });
            }
        };
        list.Add(shape);
        var framings = new List<Choice> { new(ScreenFraming.Fit, VideoEditorText.FramingFit), new(ScreenFraming.Fill, VideoEditorText.FramingFill) };
        if (pro)
        {
            framings.Add(new Choice(ScreenFraming.FollowPointer, VideoEditorText.FramingFollow));
        }

        var framing = new ChoiceParameter(VideoEditorText.FramingLabel, framings, canvas.Framing);
        framing.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Selected")
            {
                Remake(VideoEditorText.UndoFraming, "canvas framing", p => CanvasEdits.Change(p, p.Canvas.Width, p.Canvas.Height, framing.As<ScreenFraming>()));
            }
        };
        list.Add(framing);
        if (canvas.Framing == ScreenFraming.FollowPointer)
        {
            list.Add(new NoteParameter(actions.ReframeNote()));
        }

        return list;
    }

    private static IReadOnlyList<EditParameter> ForAnnotation(AnnotationItem annotation, Func<VideoProject> current, Apply apply, Keyframes? keyframes)
    {
        var list = new List<EditParameter>();
        void Change<T>(T parameter, [Localizable(false)] string key, string name, Func<AnnotationItem, AnnotationItem> update)
            where T : EditParameter
        {
            parameter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is "Value" or "Selected" && current().Find(annotation.Id)?.Item is AnnotationItem now)
                {
                    apply(update(now), name, $"property {annotation.Id} {key}");
                }
            };
            list.Add(parameter);
        }

        var look = annotation.Look;
        var shape = annotation.Shape;
        var hidden = shape is AnnotationShape.Blur or AnnotationShape.Pixelate;
        if (shape == AnnotationShape.Text)
        {
            var text = new TextParameter(VideoEditorText.Text, annotation.Text, multiline: true);
            Change(text, "text", VideoEditorText.UndoEditText, a => a with { Text = text.Value });
            var font = new FontParameter(VideoEditorText.Font, FontFamilies.Value, look.FontFamily);
            Change(font, "font", VideoEditorText.UndoFont, a => a with { Look = a.Look with { FontFamily = font.Value } });
            var size = new NumberParameter(VideoEditorText.Size, 8, 200, look.FontSize, 1, " px");
            Change(size, "text size", VideoEditorText.UndoTextSize, a => a with { Look = a.Look with { FontSize = size.Value } });
            var bold = new SwitchParameter(VideoEditorText.Bold, look.Bold);
            Change(bold, "bold", VideoEditorText.UndoBold, a => a with { Look = a.Look with { Bold = bold.Value } });
        }

        if (!hidden)
        {
            var color = new ColorParameter(shape == AnnotationShape.Text ? VideoEditorText.TextColour : VideoEditorText.Colour, FrameCompositor.Color(look.Color, SKColors.Red));
            Change(color, "colour", VideoEditorText.UndoColour, a => a with { Look = a.Look with { Color = Hex(color.Value.Value) } });
        }

        if (shape is AnnotationShape.Text or AnnotationShape.Rectangle or AnnotationShape.Ellipse)
        {
            var fill = new ColorParameter(shape == AnnotationShape.Text ? VideoEditorText.Background : VideoEditorText.Fill, look.Fill is null ? SKColors.Transparent : FrameCompositor.Color(look.Fill, SKColors.Transparent), allowTransparent: true);
            Change(fill, "fill", VideoEditorText.UndoFill, a => a with { Look = a.Look with { Fill = fill.IsTransparent ? null : HexWithAlpha(fill.Value.Value, shape == AnnotationShape.Text ? (byte)0xB0 : (byte)0xFF) } });
        }

        if (shape is AnnotationShape.Arrow or AnnotationShape.Line or AnnotationShape.Rectangle or AnnotationShape.Ellipse)
        {
            var width = new NumberParameter(VideoEditorText.LineWidth, 1, 40, look.StrokeWidth, 1, " px");
            Change(width, "line width", VideoEditorText.UndoLineWidth, a => a with { Look = a.Look with { StrokeWidth = width.Value } });
        }

        if (hidden)
        {
            var strength = new NumberParameter(shape == AnnotationShape.Blur ? VideoEditorText.Blur : VideoEditorText.CellSize, 2, 60, look.Strength, 1, " px");
            Change(strength, "strength", VideoEditorText.UndoStrength, a => a with { Look = a.Look with { Strength = strength.Value } });
        }

        var opacity = new NumberParameter(VideoEditorText.Opacity, 5, 100, look.Opacity * 100, 1, " %");
        Change(opacity, "opacity", VideoEditorText.UndoOpacity, a => a with { Look = a.Look with { Opacity = opacity.Value / 100 } });
        var fadeIn = new NumberParameter(VideoEditorText.FadeIn, 0, Math.Min(5, annotation.Length), annotation.FadeIn, 0.1, VideoEditorText.SecondsUnit);
        Change(fadeIn, "fade in", VideoEditorText.UndoFadeIn, a => a with { FadeIn = Math.Round(fadeIn.Value, 2) });
        var fadeOut = new NumberParameter(VideoEditorText.FadeOut, 0, Math.Min(5, annotation.Length), annotation.FadeOut, 0.1, VideoEditorText.SecondsUnit);
        Change(fadeOut, "fade out", VideoEditorText.UndoFadeOut, a => a with { FadeOut = Math.Round(fadeOut.Value, 2) });
        var easing = new ChoiceParameter(VideoEditorText.Movement, [new(Easing.Smooth, VideoEditorText.Smooth), new(Easing.Linear, VideoEditorText.Even)], annotation.Easing);
        Change(easing, "movement", VideoEditorText.UndoMovement, a => a with { Easing = easing.As<Easing>() });
        if (keyframes is not null)
        {
            list.Add(new ActionParameter(VideoEditorText.AddKeyframe(annotation.Keyframes.Count), new CommunityToolkit.Mvvm.Input.RelayCommand(keyframes.Add)));
            list.Add(new ActionParameter(VideoEditorText.RemoveKeyframe, new CommunityToolkit.Mvvm.Input.RelayCommand(keyframes.Remove)));
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
        void Change<T>(T parameter, [Localizable(false)] string key, string name, Func<EffectItem, EffectItem> update)
            where T : EditParameter
        {
            parameter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is "Value" or "Selected" && current().Find(effect.Id)?.Item is EffectItem now)
                {
                    apply(update(now), name, $"property {effect.Id} {key}");
                }
            };
            list.Add(parameter);
        }

        var (hasMetadata, pointerInPictures) = MetadataOf(project);
        if (!hasMetadata)
        {
            list.Add(new NoteParameter(VideoEditorText.NoMetadataForEffects));
        }

        switch (effect.Kind)
        {
            case EffectKind.Clicks:
            {
                var clicks = effect.Clicks ?? new ClickOverlaySettings();
                var left = new ColorParameter(VideoEditorText.LeftButton, FrameCompositor.Color(clicks.LeftColor, SKColors.Yellow));
                Change(left, "left colour", VideoEditorText.UndoLeftColour, e => e with { Clicks = (e.Clicks ?? new ClickOverlaySettings()) with { LeftColor = Hex(left.Value.Value) } });
                var right = new ColorParameter(VideoEditorText.RightButton, FrameCompositor.Color(clicks.RightColor, SKColors.Red));
                Change(right, "right colour", VideoEditorText.UndoRightColour, e => e with { Clicks = (e.Clicks ?? new ClickOverlaySettings()) with { RightColor = Hex(right.Value.Value) } });
                var size = new NumberParameter(VideoEditorText.Size, 20, 200, clicks.Size, 1, " px");
                Change(size, "click size", VideoEditorText.UndoClickSize, e => e with { Clicks = (e.Clicks ?? new ClickOverlaySettings()) with { Size = (int)size.Value } });
                break;
            }

            case EffectKind.Keystrokes:
            {
                var keys = effect.Keystrokes ?? new KeystrokeOverlaySettings();
                var position = new ChoiceParameter(VideoEditorText.Position,
                [
                    new(OverlayAnchor.BottomCenter, VideoEditorText.BottomCentre), new(OverlayAnchor.BottomLeft, VideoEditorText.BottomLeft), new(OverlayAnchor.BottomRight, VideoEditorText.BottomRight),
                    new(OverlayAnchor.TopCenter, VideoEditorText.TopCentre), new(OverlayAnchor.TopLeft, VideoEditorText.TopLeft), new(OverlayAnchor.TopRight, VideoEditorText.TopRight),
                ], keys.Position);
                Change(position, "keys position", VideoEditorText.UndoKeysPosition, e => e with { Keystrokes = (e.Keystrokes ?? new KeystrokeOverlaySettings()) with { Position = position.As<OverlayAnchor>() } });
                var size = new NumberParameter(VideoEditorText.Size, 8, 72, keys.FontSize, 1, " pt");
                Change(size, "keys size", VideoEditorText.UndoKeysSize, e => e with { Keystrokes = (e.Keystrokes ?? new KeystrokeOverlaySettings()) with { FontSize = (int)size.Value } });
                var fade = new NumberParameter(VideoEditorText.ShownFor, 0.5, 10, keys.FadeSeconds, 0.1, VideoEditorText.SecondsUnit);
                Change(fade, "keys time", VideoEditorText.UndoKeysTime, e => e with { Keystrokes = (e.Keystrokes ?? new KeystrokeOverlaySettings()) with { FadeSeconds = fade.Value } });
                break;
            }

            default:
            {
                var pointer = effect.Pointer ?? new PointerOverlaySettings();
                var highlight = new ChoiceParameter(VideoEditorText.AroundPointer, [new(PointerEffect.None, VideoEditorText.PointerNothing), new(PointerEffect.Halo, VideoEditorText.PointerHalo), new(PointerEffect.Spotlight, VideoEditorText.PointerSpotlight)], pointer.Effect);
                Change(highlight, "pointer highlight", VideoEditorText.UndoPointerHighlight, e => e with { Pointer = (e.Pointer ?? new PointerOverlaySettings()) with { Effect = highlight.As<PointerEffect>() } });
                var color = new ColorParameter(VideoEditorText.HaloColour, FrameCompositor.Color(pointer.Color, SKColors.Yellow));
                Change(color, "halo colour", VideoEditorText.UndoHaloColour, e => e with { Pointer = (e.Pointer ?? new PointerOverlaySettings()) with { Color = Hex(color.Value.Value) } });
                var radius = new NumberParameter(VideoEditorText.Radius, 20, 300, pointer.Radius, 1, " px");
                Change(radius, "pointer radius", VideoEditorText.UndoPointerRadius, e => e with { Pointer = (e.Pointer ?? new PointerOverlaySettings()) with { Radius = (int)radius.Value } });
                var dim = new NumberParameter(VideoEditorText.DimTheRest, 10, 90, pointer.Dim, 1, " %");
                Change(dim, "dim", VideoEditorText.UndoDim, e => e with { Pointer = (e.Pointer ?? new PointerOverlaySettings()) with { Dim = (int)dim.Value } });
                if (pointerInPictures)
                {
                    list.Add(new NoteParameter(VideoEditorText.PointerInPictures));
                }
                else
                {
                    var scale = new NumberParameter(VideoEditorText.PointerSize, 50, 300, pointer.Scale * 100, 5, " %");
                    Change(scale, "pointer size", VideoEditorText.UndoPointerSize, e => e with { Pointer = (e.Pointer ?? new PointerOverlaySettings()) with { Scale = scale.Value / 100 } });
                    var smoothing = new NumberParameter(VideoEditorText.SmoothMovement, 0, 100, effect.Smoothing * 100, 5, " %");
                    Change(smoothing, "smoothing", VideoEditorText.UndoSmoothing, e => e with { Smoothing = smoothing.Value / 100 });
                    var sharp = new SwitchParameter(VideoEditorText.SharpPointer, effect.SharpPointer);
                    Change(sharp, "sharp pointer", VideoEditorText.UndoSharpPointer, e => e with { SharpPointer = sharp.Value });
                    var press = new SwitchParameter(VideoEditorText.PressAnimation, effect.PressAnimation);
                    Change(press, "press animation", VideoEditorText.UndoPressAnimation, e => e with { PressAnimation = press.Value });
                    var keep = new SwitchParameter(VideoEditorText.KeepPointerSize, effect.ConstantPointerSize);
                    Change(keep, "keep pointer size", VideoEditorText.UndoKeepPointerSize, e => e with { ConstantPointerSize = keep.Value });
                    var idle = new SwitchParameter(VideoEditorText.HideWhenStill, pointer.HideWhenIdle);
                    Change(idle, "hide when idle", VideoEditorText.UndoHideWhenIdle, e => e with { Pointer = (e.Pointer ?? new PointerOverlaySettings()) with { HideWhenIdle = idle.Value } });
                    var after = new NumberParameter(VideoEditorText.After, 0.5, 10, pointer.IdleSeconds, 0.5, VideoEditorText.SecondsUnit);
                    Change(after, "idle time", VideoEditorText.UndoIdleTime, e => e with { Pointer = (e.Pointer ?? new PointerOverlaySettings()) with { IdleSeconds = after.Value } });
                }

                break;
            }
        }

        return list;
    }

    /// <summary>A caption's text and buttons, and the look of every caption on its track.</summary>
    private static IReadOnlyList<EditParameter> ForCaption(VideoProject project, CaptionItem caption, Func<VideoProject> current, Apply apply, CaptionActions? actions)
    {
        var list = new List<EditParameter>();
        var text = new TextParameter(VideoEditorText.Text, caption.Text, multiline: true);
        text.PropertyChanged += (_, e) =>
        {
            // Words written by hand have no times of their own: the caption keeps its whole text from now on.
            if (e.PropertyName == "Value" && current().Find(caption.Id)?.Item is CaptionItem now && now.Text != text.Value)
            {
                apply(now with { Text = text.Value, Words = null }, VideoEditorText.UndoCaptionText, $"property {caption.Id} text");
            }
        };
        list.Add(text);
        if (actions is null || project.Find(caption.Id)?.Track is not { } track)
        {
            return list;
        }

        list.Add(new ActionParameter(VideoEditorText.SplitCaption, new CommunityToolkit.Mvvm.Input.RelayCommand(() => actions.Split(caption.Id))));
        list.Add(new ActionParameter(VideoEditorText.MergeCaption, new CommunityToolkit.Mvvm.Input.RelayCommand(() => actions.Merge(caption.Id))));
        list.Add(new NoteParameter(VideoEditorText.CaptionLookNote));

        void Style<T>(T parameter, [Localizable(false)] string key, string name, Func<CaptionStyle, CaptionStyle> change)
            where T : EditParameter
        {
            parameter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is "Value" or "Selected")
                {
                    actions.ChangeStyle(track.Id, change, key, name);
                }
            };
            list.Add(parameter);
        }

        var style = track.Captions ?? new CaptionStyle();
        var font = new FontParameter(VideoEditorText.Font, FontFamilies.Value, style.FontFamily);
        Style(font, "caption font", VideoEditorText.UndoCaptionFont, s => s with { FontFamily = font.Value });
        var size = new NumberParameter(VideoEditorText.Size, 12, 160, style.SizeFor(project.Canvas.Height), 1, " px");
        Style(size, "caption size", VideoEditorText.UndoCaptionSize, s => s with { FontSize = size.Value });
        var bold = new SwitchParameter(VideoEditorText.Bold, style.Bold);
        Style(bold, "caption bold", VideoEditorText.UndoCaptionBold, s => s with { Bold = bold.Value });
        var color = new ColorParameter(VideoEditorText.TextColour, FrameCompositor.Color(style.Color, SKColors.White));
        Style(color, "caption colour", VideoEditorText.UndoCaptionColour, s => s with { Color = Hex(color.Value.Value) });
        var box = new ColorParameter(VideoEditorText.Box, style.Box is null ? SKColors.Transparent : FrameCompositor.Color(style.Box, SKColors.Black), allowTransparent: true);
        Style(box, "caption box", VideoEditorText.UndoCaptionBox, s => s with { Box = box.IsTransparent ? null : HexWithAlpha(box.Value.Value, 0xB4) });
        var position = new ChoiceParameter(VideoEditorText.Position, [new(CaptionPosition.Bottom, VideoEditorText.AtTheBottom), new(CaptionPosition.Top, VideoEditorText.AtTheTop)], style.Position);
        Style(position, "caption position", VideoEditorText.UndoCaptionPosition, s => s with { Position = position.As<CaptionPosition>() });
        var width = new NumberParameter(VideoEditorText.LinesUpTo, 30, 100, style.MaxWidth * 100, 5, VideoEditorText.PercentOfWidthUnit);
        Style(width, "caption width", VideoEditorText.UndoCaptionWidth, s => s with { MaxWidth = width.Value / 100 });
        return list;
    }

    /// <summary>A zoom's settings; changing one makes it the user's own (Auto-Zoom no longer replaces it).</summary>
    private static IReadOnlyList<EditParameter> ForZoom(VideoProject project, ZoomItem zoom, Func<VideoProject> current, Apply apply, IFeatureGate gate)
    {
        var list = new List<EditParameter>();
        void Change<T>(T parameter, [Localizable(false)] string key, string name, Func<ZoomItem, ZoomItem> update)
            where T : EditParameter
        {
            parameter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is "Value" or "Selected" && current().Find(zoom.Id)?.Item is ZoomItem now)
                {
                    apply(update(now) with { Auto = false }, name, $"property {zoom.Id} {key}");
                }
            };
            list.Add(parameter);
        }

        var scale = new NumberParameter(VideoEditorText.Zoom, ZoomItem.SmallestScale * 100, ZoomItem.LargestScale * 100, zoom.ClampedScale * 100, 5, " %");
        Change(scale, "zoom", VideoEditorText.UndoZoom, z => z with { Scale = scale.Value / 100 });
        var follow = new SwitchParameter(VideoEditorText.FollowPointer, zoom.Focus == ZoomFocus.FollowPointer);
        var across = new NumberParameter(VideoEditorText.Across, 0, 100, zoom.CenterX * 100, 1, " %") { IsVisible = !follow.Value };
        var down = new NumberParameter(VideoEditorText.Down, 0, 100, zoom.CenterY * 100, 1, " %") { IsVisible = !follow.Value };
        follow.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Value")
            {
                across.IsVisible = !follow.Value;
                down.IsVisible = !follow.Value;
            }
        };
        Change(follow, "follow the pointer", VideoEditorText.UndoFollowPointer, z => z with { Focus = follow.Value ? ZoomFocus.FollowPointer : ZoomFocus.Fixed });
        if (!MetadataOf(project).HasMetadata)
        {
            list.Add(new NoteParameter(VideoEditorText.NoMetadataForZoom));
        }

        Change(across, "zoom across", VideoEditorText.UndoZoomAcross, z => z with { CenterX = across.Value / 100 });
        Change(down, "zoom down", VideoEditorText.UndoZoomDown, z => z with { CenterY = down.Value / 100 });
        var easeIn = new NumberParameter(VideoEditorText.ZoomInOver, 0, 3, zoom.EaseIn, 0.1, VideoEditorText.SecondsUnit);
        Change(easeIn, "zoom in time", VideoEditorText.UndoZoomInTime, z => z with { EaseIn = Math.Round(easeIn.Value, 2) });
        var easeOut = new NumberParameter(VideoEditorText.ZoomOutOver, 0, 3, zoom.EaseOut, 0.1, VideoEditorText.SecondsUnit);
        Change(easeOut, "zoom out time", VideoEditorText.UndoZoomOutTime, z => z with { EaseOut = Math.Round(easeOut.Value, 2) });
        var movement = new ChoiceParameter(VideoEditorText.ZoomMovement, [new(ZoomEasing.Smooth, VideoEditorText.Smooth), new(ZoomEasing.Spring, VideoEditorText.SpringEasing)], zoom.Easing);
        Change(movement, "zoom movement", VideoEditorText.UndoZoomMovement, z => z with { Easing = movement.As<ZoomEasing>() });
        if (gate.IsEnabled(ProFeature.StudioLook))
        {
            var blur = new NumberParameter(VideoEditorText.MotionBlur, 0, 100, zoom.MotionBlur * 100, 5, " %");
            Change(blur, "motion blur", VideoEditorText.UndoMotionBlur, z => z with { MotionBlur = blur.Value / 100 });
        }

        list.Add(new NoteParameter(VideoEditorText.ZoomNote));
        return list;
    }

    private static string Hex(SKColor color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    private static string HexWithAlpha(SKColor color, byte alpha) => $"#{alpha:X2}{color.Red:X2}{color.Green:X2}{color.Blue:X2}";
}
