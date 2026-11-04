using System.IO;
using CashGear.Capture.Editor.Model;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using CashGear.Capture.VideoEditor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>Phase 22 M3: transitions, fades with curves, clip effects, text animations.</summary>
public sealed class ClipEffectTests(ITestOutputHelper output)
{
    /// <summary>A 60 fps project: two clips of different colours meeting at 5 s, a third alone after a gap.</summary>
    private static (VideoProject Project, string First, string Second, string Third) Clips()
    {
        var project = VideoProject.Empty(new ProjectCanvas(640, 360, 60));
        var (a, first) = MediaEdits.AddSolid(project, "#D02020", 0, 5);
        var (b, second) = MediaEdits.AddSolid(a, "#2040D0", 5, 5, a.Tracks[0].Id);
        var (c, third) = MediaEdits.AddSolid(b, "#20A040", 12, 4, a.Tracks[0].Id);
        return (c, first, second, third);
    }

    private static SKBitmap Draw(VideoProject project, double time)
    {
        var bitmap = new SKBitmap(new SKImageInfo(project.Canvas.Width, project.Canvas.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        new FrameCompositor(new SolidFrames(_ => (640, 360, 0xFF808080))).Render(canvas, project, time);
        return bitmap;
    }

    public static TheoryData<TransitionKind, TransitionDirection> Transitions => new()
    {
        { TransitionKind.Dissolve, TransitionDirection.Left },
        { TransitionKind.DipToColor, TransitionDirection.Left },
        { TransitionKind.Wipe, TransitionDirection.Left },
        { TransitionKind.Wipe, TransitionDirection.Right },
        { TransitionKind.Wipe, TransitionDirection.Up },
        { TransitionKind.Wipe, TransitionDirection.Down },
        { TransitionKind.Slide, TransitionDirection.Left },
        { TransitionKind.Push, TransitionDirection.Up },
        { TransitionKind.Zoom, TransitionDirection.Left },
        { TransitionKind.Blur, TransitionDirection.Left },
        { TransitionKind.Circle, TransitionDirection.Left },
    };

    [Theory]
    [MemberData(nameof(Transitions))]
    public void Each_transition_matches_between_preview_and_export_at_both_edges_at_60_fps(TransitionKind kind, TransitionDirection direction)
    {
        using var turn = new CashGear.Capture.Tests.MachineTurn();
        using var gl = OffscreenGl.TryCreate();
        if (gl is null)
        {
            Assert.Skip("No OpenGL here");
        }

        var (project, first, second, third) = Clips();
        var transition = new ClipTransition(kind, 1, MotionEasing.EaseInOut, direction, "#FFFFFF");
        project = TransitionEdits.SetTransition(project, first, ItemEdge.Start, transition);
        project = TransitionEdits.SetTransition(project, second, ItemEdge.Start, transition);
        project = TransitionEdits.SetTransition(project, third, ItemEdge.End, transition);
        var clips = project.Tracks[0].Clips.ToList();
        clips[1].Start.Should().Be(4, "the clips come to overlap by the transition's length");
        var frames = new SolidFrames(_ => (640, 360, 0xFF808080));
        var memory = new FrameCompositor(frames);
        var graphics = new FrameCompositor(frames);
        var (width, height) = (project.Canvas.Width, project.Canvas.Height);
        var worst = 0.0;

        // Over the way in from nothing, the cut, and the way out to nothing, on frames at 60 fps.
        double[] moments = [0.1, 0.5, 0.9, 4.1, 4.3, 4.5, 4.7, 4.9, clips[2].End - 0.9, clips[2].End - 0.5, clips[2].End - 0.1];
        foreach (var moment in moments.Select(m => Math.Round(m * 60) / 60))
        {
            using var inMemory = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(inMemory))
            {
                memory.Render(canvas, project, moment);
            }

            using var gpu = SKSurface.Create(gl!.Context, false, new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            graphics.Render(gpu.Canvas, project, moment);
            gpu.Flush();
            using var read = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            gpu.ReadPixels(read.Info, read.GetPixels(), read.RowBytes, 0, 0);
            worst = Math.Max(worst, PreviewCompare.MeanDifference(inMemory, read));
        }

        output.WriteLine($"{kind} {direction}: {worst:0.000} levels at worst");
        worst.Should().BeLessThan(1, "{0} {1}", kind, direction);
    }

    [Fact]
    public void A_dissolve_ends_on_the_new_picture_and_a_wipe_shows_each_side_in_its_place()
    {
        var (project, first, second, _) = Clips();
        project = TransitionEdits.SetTransition(project, second, ItemEdge.Start, new ClipTransition(TransitionKind.Wipe, 1, MotionEasing.Linear, TransitionDirection.Right));
        first.Should().NotBeNull();
        using var half = Draw(project, 4.5);

        // Going right: the new picture on the left half, the old on the right.
        half.GetPixel(100, 180).Blue.Should().BeGreaterThan(190);
        half.GetPixel(540, 180).Red.Should().BeGreaterThan(190);
        using var after = Draw(project, 5.01);
        after.GetPixel(540, 180).Blue.Should().BeGreaterThan(190);
    }

    [Fact]
    public void A_way_in_and_out_at_a_free_edge_comes_from_and_goes_to_nothing()
    {
        var (project, _, _, third) = Clips();
        project = TransitionEdits.SetTransition(project, third, ItemEdge.Start, new ClipTransition(TransitionKind.Dissolve, 1, MotionEasing.Linear));
        project = TransitionEdits.SetTransition(project, third, ItemEdge.End, new ClipTransition(TransitionKind.Dissolve, 1, MotionEasing.Linear));
        var clip = (Clip)project.Find(third)!.Value.Item;
        clip.Start.Should().Be(12, "nothing moves for a free edge");
        using var coming = Draw(project, 12.5);
        ((int)coming.GetPixel(320, 180).Green).Should().BeInRange(70, 90, "half of it over black");
        using var going = Draw(project, 15.5);
        ((int)going.GetPixel(320, 180).Green).Should().BeInRange(70, 90);
    }

    [Fact]
    public void A_transition_can_be_taken_away_with_its_overlap()
    {
        var (project, first, second, third) = Clips();
        var with = TransitionEdits.SetTransition(project, first, ItemEdge.End, new ClipTransition(TransitionKind.Zoom, 1));
        ((Clip)with.Find(second)!.Value.Item).TransitionIn!.Kind.Should().Be(TransitionKind.Zoom, "a cut's way is on the clip coming in");
        with.Find(second)!.Value.Item.Start.Should().Be(4);
        with.Find(third)!.Value.Item.Start.Should().Be(11);

        var longer = TransitionEdits.SetTransition(with, second, ItemEdge.Start, new ClipTransition(TransitionKind.Zoom, 2));
        longer.Find(second)!.Value.Item.Start.Should().Be(3, "the overlap follows its length");

        var without = TransitionEdits.SetTransition(longer, second, ItemEdge.Start, null);
        without.Find(second)!.Value.Item.Start.Should().Be(5);
        without.Find(third)!.Value.Item.Start.Should().Be(12);
        ((Clip)without.Find(second)!.Value.Item).TransitionIn.Should().BeNull();
    }

    [Fact]
    public void A_transition_keeps_a_cuts_join_on_the_sound_together_with_the_pictures()
    {
        var words = new List<SpokenWord>();
        for (var i = 0; i < 20; i++)
        {
            words.Add(new SpokenWord(i + 0.2, i + 0.8, "word"));
        }

        var project = TestProjects.Project(20);
        project = TranscriptEdits.WithTranscript(project, "rec", new MediaTranscript(1, "en", words));
        var cut = TranscriptEdits.Cut(project, [new CutRange(5, 6)]);
        var screen = cut.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.ToList();
        var after = screen[1];
        var joined = cut.Tracks.First(t => t.HasSound).Clips.ToList();
        joined.Should().HaveCount(2);
        (joined[0].End - joined[1].Start).Should().BeApproximately(TranscriptEdits.JoinLength, 1e-6);

        // The picture's transition moves the sound after the cut back with it.
        var moved = TransitionEdits.SetTransition(cut, after.Id, ItemEdge.Start, new ClipTransition(TransitionKind.Dissolve, 1));
        var picture = (Clip)moved.Find(after.Id)!.Value.Item;
        var sound = moved.Tracks.First(t => t.HasSound).Clips.ToList();
        sound[1].TimelineAt(after.SourceIn).Should().BeApproximately(picture.Start, 1e-5, "picture and sound stay together");

        // And a speed change after the cut does too (a bug since Phase 21: the sound's join kept it behind).
        var faster = TransitionEdits.SetSpeed(cut, screen[0].Id, 2);
        var fasterSound = faster.Tracks.First(t => t.HasSound).Clips.ToList();
        var fasterPicture = (Clip)faster.Find(after.Id)!.Value.Item;
        fasterSound[1].TimelineAt(after.SourceIn).Should().BeApproximately(fasterPicture.Start, 1e-5);
    }

    [Theory]
    [InlineData(MotionEasing.Linear, 0.25)]
    [InlineData(MotionEasing.EaseIn, 0.125)]
    [InlineData(MotionEasing.EaseOut, 0.875)]
    public void Fades_have_curves_in_the_picture_and_the_sound_alike(MotionEasing curve, double expected)
    {
        var clip = new Clip { Id = "c", AssetId = "a", SourceOut = 4, FadeIn = 2, FadeInCurve = curve, FadeOut = 2, FadeOutCurve = curve };
        var at = curve == MotionEasing.Linear ? 0.5 : 1.0;
        FrameCompositor.Fade(clip, at).Should().BeApproximately(expected, 1e-9);
        TimelineMixer.Envelope(clip, at).Should().BeApproximately(expected, 1e-9);
        FrameCompositor.Fade(clip, 4 - at).Should().BeApproximately(expected, 1e-9);

        // An overlap's crossfade stays a straight line.
        TimelineMixer.Envelope(clip with { FadeIn = 0 }, 1, overlapIn: 2).Should().BeApproximately(0.5, 1e-9);
    }

    [Fact]
    public void Colour_effects_change_the_picture_and_stay_off_when_at_zero()
    {
        var (project, id, _, _) = Clips();
        project = project.WithItem(((Clip)project.Find(id)!.Value.Item) with { Solid = "#808080" });
        Clip Clip(VideoProject p) => (Clip)p.Find(id)!.Value.Item;
        SKColor At(ClipEffects effects)
        {
            using var drawn = Draw(project.WithItem(Clip(project) with { Effects = effects }), 1);
            return drawn.GetPixel(320, 180);
        }

        var plain = At(new ClipEffects());
        plain.Should().Be(new SKColor(0x80, 0x80, 0x80));
        At(new ClipEffects(Brightness: 0.5)).Red.Should().BeGreaterThan(plain.Red);
        At(new ClipEffects(Brightness: -0.5)).Red.Should().BeLessThan(plain.Red);

        var warm = At(new ClipEffects(Temperature: 1));
        warm.Red.Should().BeGreaterThan(warm.Blue);
        var cool = At(new ClipEffects(Temperature: -1));
        cool.Blue.Should().BeGreaterThan(cool.Red);

        project = project.WithItem(Clip(project) with { Solid = "#C04020" });
        var grey = At(new ClipEffects(Saturation: -1));
        Math.Abs(grey.Red - grey.Blue).Should().BeLessThan(3);
        var sharper = At(new ClipEffects(Contrast: 1));
        sharper.Red.Should().BeGreaterThan(0xC0);
        sharper.Blue.Should().BeLessThan(0x20);
    }

    [Fact]
    public void A_cube_file_changes_the_colours_and_is_kept_with_the_media()
    {
        var folder = Directory.CreateTempSubdirectory("cashgear-lut-").FullName;
        try
        {
            // A table that swaps red and blue, at half and whole strength.
            var lines = new List<string> { "TITLE \"swap\"", "LUT_3D_SIZE 2", "# red and blue swapped" };
            for (var b = 0; b < 2; b++)
            {
                for (var g = 0; g < 2; g++)
                {
                    for (var r = 0; r < 2; r++)
                    {
                        lines.Add($"{b} {g} {r}");
                    }
                }
            }

            var path = Path.Combine(folder, "swap.cube");
            File.WriteAllLines(path, lines);
            var (project, id, _, _) = Clips();
            var asset = Looks.PictureAsset(path);
            project = project.WithAsset(asset);
            var clip = (Clip)project.Find(id)!.Value.Item with { Solid = "#FF0000" };
            using (var whole = Draw(project.WithItem(clip with { Effects = new ClipEffects(LutAssetId: asset.Id) }), 1))
            {
                whole.GetPixel(320, 180).Blue.Should().BeGreaterThan(250);
                whole.GetPixel(320, 180).Red.Should().BeLessThan(5);
            }

            using (var half = Draw(project.WithItem(clip with { Effects = new ClipEffects(LutAssetId: asset.Id, LutStrength: 0.5) }), 1))
            {
                ((int)half.GetPixel(320, 180).Red).Should().BeInRange(120, 135);
            }

            MediaEdits.IsUsed(project.WithItem(clip with { Effects = new ClipEffects(LutAssetId: asset.Id) }), asset.Id).Should().BeTrue();
            var bad = () => CubeLut.Parse(["LUT_3D_SIZE 2", "1 2"], "bad.cube");
            bad.Should().Throw<InvalidDataException>();
            using var curves = CubeLut.Parse(["LUT_1D_SIZE 2", "0 0 0", "1 1 1"], "curves.cube");
            curves.Cells.Should().Be(33);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>A subject on a green screen: a soft-edged disc, as a camera's edge blends the two.</summary>
    private sealed class GreenScreen : IFrameProvider
    {
        public VideoFrame? Frame(Clip clip, MediaAsset asset, double sourceTime)
        {
            const int size = 256;
            var frame = FramePool.Shared.Rent(size, size);
            var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(frame.Pixels);
            var random = new Random(7);
            for (var y = 0; y < size; y++)
            {
                for (var x = 0; x < size; x++)
                {
                    var distance = Math.Sqrt(((x - 128) * (x - 128)) + ((y - 128) * (y - 128)));
                    var subject = Math.Clamp(80 - distance, 0, 1) * 0.5 + Math.Clamp((80 - distance) / 3, 0, 1) * 0.5;

                    // A green screen isn't even: a little noise, and a darker corner. The subject is a skin tone.
                    var shade = 0.92 + (0.06 * random.NextDouble()) - (0.12 * (x + y) / 512.0);
                    var (backR, backG, backB) = (0.08 * shade, 0.82 * shade, 0.12 * shade);
                    var (r, g, b) = (0.78, 0.55, 0.45);
                    var red = (backR * (1 - subject)) + (r * subject);
                    var green = (backG * (1 - subject)) + (g * subject);
                    var blue = (backB * (1 - subject)) + (b * subject);
                    pixels[(y * size) + x] = 0xFF000000 | ((uint)(red * 255) << 16) | ((uint)(green * 255) << 8) | (uint)(blue * 255);
                }
            }

            frame.Time = sourceTime;
            return frame;
        }
    }

    [Fact]
    public void A_chroma_key_removes_the_green_without_a_green_fringe()
    {
        var asset = new MediaAsset("screen", @"C:\nowhere\g.mp4", 2, [new AssetStream(0, StreamKind.Video, null, 0, 2, 256, 256, 30)]);
        var clip = new Clip { Id = "c", AssetId = "screen", SourceOut = 2, Effects = new ClipEffects(Key: new ChromaKey()) };
        var project = new VideoProject(VideoProject.CurrentVersion, new ProjectCanvas(256, 256, 30, new CanvasBackground(BackgroundKind.Color, "#FF00FF")), [asset], [new Track("t", TrackKind.Media, "m", [clip])]);

        using var bitmap = new SKBitmap(new SKImageInfo(256, 256, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            new FrameCompositor(new GreenScreen()).Render(canvas, project, 1);
        }

        // Over magenta: outside the subject nothing of the screen stays, and inside the subject stays whole.
        var left = 0;
        for (var y = 0; y < 256; y += 3)
        {
            for (var x = 0; x < 256; x += 3)
            {
                var distance = Math.Sqrt(((x - 128) * (x - 128)) + ((y - 128) * (y - 128)));
                var pixel = bitmap.GetPixel(x, y);
                if (distance > 86 && pixel != new SKColor(0xFF, 0x00, 0xFF))
                {
                    left++;
                }
            }
        }

        left.Should().Be(0, "the green screen goes altogether");
        var middle = bitmap.GetPixel(128, 128);
        ((int)middle.Red).Should().BeInRange(190, 205);
        ((int)middle.Green).Should().BeInRange(125, 145, "the subject keeps its colours");

        // The fringe: how much greener than red and blue any pixel near the subject's edge is. Set level: 8 of 255.
        var worst = 0;
        for (var y = 0; y < 256; y++)
        {
            for (var x = 0; x < 256; x++)
            {
                var distance = Math.Sqrt(((x - 128) * (x - 128)) + ((y - 128) * (y - 128)));
                if (distance is > 70 and < 90)
                {
                    var pixel = bitmap.GetPixel(x, y);
                    worst = Math.Max(worst, pixel.Green - Math.Max(pixel.Red, pixel.Blue));
                }
            }
        }

        output.WriteLine($"The fringe's greenest pixel: {worst} levels above red and blue");
        worst.Should().BeLessThanOrEqualTo(8);
    }

    [Fact]
    public void A_see_through_picture_has_a_shadow_of_its_own_shape()
    {
        var folder = Directory.CreateTempSubdirectory("cashgear-shape-").FullName;
        try
        {
            using (var surface = SKSurface.Create(new SKImageInfo(100, 100, SKColorType.Bgra8888, SKAlphaType.Premul)))
            {
                surface.Canvas.Clear(SKColors.Transparent);
                using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
                surface.Canvas.DrawCircle(50, 50, 30, paint);
                using var image = surface.Snapshot();
                using var data = image.Encode(SKEncodedImageFormat.Png, 100);
                File.WriteAllBytes(Path.Combine(folder, "dot.png"), data.ToArray());
            }

            var (project, id) = MediaEdits.Add(VideoProject.Empty(new ProjectCanvas(200, 200, 30, new CanvasBackground(BackgroundKind.Color, "#FFFFFF"))), ProjectFactory.ReadMedia(Path.Combine(folder, "dot.png")), 0);
            var clip = (Clip)project.Find(id!)!.Value.Item;
            project = project.WithItem(clip with { Placement = new Placement(Size: 0.5, Shadow: new PlacementShadow(10, 0, 3, "#000000", 1)) });
            using var drawn = Draw(project, 1);

            // Its corner, in the picture's box but not under its shape: no shadow; just right of the dot: shadow.
            drawn.GetPixel(148, 52).Should().Be(SKColors.White);
            drawn.GetPixel(135, 100).Red.Should().BeLessThan(100);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Text_comes_in_goes_on_and_goes_out()
    {
        var item = new AnnotationItem
        {
            Id = "t",
            Length = 4,
            Text = "Hi",
            TextIn = new TextAnimation(TextAnimationKind.Fade, 1, MotionEasing.Linear),
            TextDuring = new TextAnimation(TextAnimationKind.Pop, 1),
            TextOut = new TextAnimation(TextAnimationKind.Slide, 1, MotionEasing.Linear),
        };
        TextAnimator.At(item, 0.5).Opacity.Should().BeApproximately(0.5, 1e-9);
        TextAnimator.At(item, 3.5).Opacity.Should().BeApproximately(0.5, 1e-9);
        TextAnimator.At(item, 3.5).Lift.Should().BeGreaterThan(0.2);
        TextAnimator.At(item, 1.25).Scale.Should().BeGreaterThan(1.01);
        TextAnimator.At(item, 1.75).Scale.Should().BeLessThan(0.99);
        TextAnimator.At(item, 3.99).Opacity.Should().BeLessThan(0.02);

        var plain = item with { TextIn = null, TextDuring = null, TextOut = null };
        TextAnimator.At(plain, 1).Should().Be(TextMoment.Still);

        // A split keeps what is at its own ends.
        var parts = ((AnnotationItem)item.Slice(0, 2), (AnnotationItem)item.Slice(2, 4));
        parts.Item1.TextIn.Should().NotBeNull();
        parts.Item1.TextOut.Should().BeNull();
        parts.Item2.TextIn.Should().BeNull();
        parts.Item2.TextOut.Should().NotBeNull();
    }

    /// <summary>The Arabic sentence the goldens show: every letter joins its neighbours, and it has a mark and a lam-alef.</summary>
    private const string Arabic = "مرحبًا بالعالم في السلام";

    private static VideoProject Typewriter(string text)
    {
        var project = VideoProject.Empty(new ProjectCanvas(640, 120, 30));
        var item = new AnnotationItem
        {
            Id = "w",
            Shape = AnnotationShape.Text,
            Length = 4,
            Text = text,
            Look = new AnnotationLook(Color: "#FFFFFF", FontSize: 48),
            Keyframes = [new(0, 20, 20, 0, 0)],
            TextIn = new TextAnimation(TextAnimationKind.Typewriter, 2, MotionEasing.Linear),
        };
        return project with { Tracks = [new Track("t", TrackKind.Annotation, "a", [item])] };
    }

    [Fact]
    public void The_typewriter_reveals_whole_grapheme_clusters()
    {
        TextAnnotation.GraphemeCount("e\u0301a").Should().Be(2, "a letter and its mark are one");
        TextAnimator.Graphemes("abcd", 0.5).Should().Be(2);
        var item = (AnnotationItem)Typewriter("abcd").Tracks[0].Items[0];
        var annotation = (TextAnnotation)FrameCompositor.AnnotationFor(item, 1)!;
        annotation.Revealed.Should().Be(2);
        ((TextAnnotation)FrameCompositor.AnnotationFor(item, 3)!).Revealed.Should().BeNull("all of it, drawn as ever");
    }

    [Fact]
    public void Arabic_typewriter_text_shows_joined_letters_at_every_step()
    {
        // At every step the pixels are those of the whole sentence, cut off: never a letter in another form. A letter drawn
        // alone (not joined) would put ink where the whole sentence has none.
        var project = Typewriter(Arabic);
        var total = TextAnnotation.GraphemeCount(Arabic);
        total.Should().BeLessThan(Arabic.Length, "the mark goes with its letter");
        using var whole = Draw(project, 3);
        for (var step = 1; step < total; step++)
        {
            using var drawn = Draw(project, 2.0 * step / total + 1e-6);
            var inked = 0;
            for (var y = 0; y < drawn.Height; y++)
            {
                for (var x = 0; x < drawn.Width; x++)
                {
                    var (mine, full) = (drawn.GetPixel(x, y).Red, whole.GetPixel(x, y).Red);
                    if (mine > 0)
                    {
                        inked++;
                        (mine - full).Should().BeLessThanOrEqualTo(16, "step {0} at ({1}, {2}) is the whole sentence's own letter", step, x, y);
                    }
                }
            }

            inked.Should().BeGreaterThan(0, "step {0} shows something", step);
        }
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(1.5)]
    public void Arabic_typewriter_golden_frames(double time)
    {
        var name = FormattableString.Invariant($"typewriter-{time:0.0}.png");
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "phase-22", name);
        using var drawn = Draw(Typewriter(Arabic), time);
        if (Environment.GetEnvironmentVariable("CASHGEAR_WRITE_GOLDENS") == "1")
        {
            var source = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Fixtures", "phase-22"));
            Directory.CreateDirectory(source);
            using var encoded = SKImage.FromBitmap(drawn).Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(source, name), encoded.ToArray());
            return;
        }

        using var golden = SKBitmap.Decode(path).Copy(SKColorType.Bgra8888);
        PreviewCompare.MeanDifference(golden, drawn).Should().Be(0, "{0} is as it was checked in", name);
    }

    [Fact]
    public void Everything_new_in_a_clip_and_an_annotation_is_saved_and_read_back()
    {
        var (project, id, second, _) = Clips();
        project = project.WithItem((Clip)project.Find(id)!.Value.Item with
        {
            FadeInCurve = MotionEasing.EaseIn,
            FadeOutCurve = MotionEasing.EaseOut,
            TransitionOut = new ClipTransition(TransitionKind.Circle, 2, MotionEasing.Spring),
            Effects = new ClipEffects(0.1, 0.2, 0.3, 0.4, "lut", 0.5, new ChromaKey("#0000FF", 0.2, 0.3, 0.4)),
        });
        project = TransitionEdits.SetTransition(project, second, ItemEdge.Start, new ClipTransition(TransitionKind.Push, 1, Direction: TransitionDirection.Down));
        (project, var text) = AnnotationEdits.Add(project, AnnotationShape.Text, 0, 4);
        project = project.WithItem((AnnotationItem)project.Find(text)!.Value.Item with { TextIn = new TextAnimation(TextAnimationKind.Pop), TextDuring = new TextAnimation(TextAnimationKind.Fade, 2), TextOut = new TextAnimation(TextAnimationKind.Typewriter), FadeInCurve = MotionEasing.EaseInOut });
        var read = ProjectJson.Deserialize(ProjectJson.Serialize(project));
        ProjectJson.Serialize(read).Should().Equal(ProjectJson.Serialize(project));
        ((Clip)read.Find(id)!.Value.Item).Effects!.Key!.Color.Should().Be("#0000FF");
        ((AnnotationItem)read.Find(text)!.Value.Item).TextOut!.Kind.Should().Be(TextAnimationKind.Typewriter);
    }
}
