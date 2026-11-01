using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using CashGear.Capture.Editor.Presets;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

public sealed class StudioLookTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cashgear-look-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static VideoProject Project(ProjectCanvas? canvas = null)
    {
        var project = TestProjects.Project(10);
        return canvas is null ? project : project with { Canvas = canvas };
    }

    private static VideoProject Placed(VideoProject project, Placement placement)
    {
        var track = project.Tracks.Single(t => t.Kind == TrackKind.Video);
        return project.WithTrack(track with { Items = [.. track.Clips.Select(c => c with { Placement = placement })] });
    }

    private static SKBitmap Render(VideoProject project, double time = 1, double scale = 1)
    {
        var width = (int)Math.Round(project.Canvas.Width * scale);
        var height = (int)Math.Round(project.Canvas.Height * scale);
        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Scale((float)scale);
        new FrameCompositor(new SolidFrames(_ => (1280, 720, 0xFFFF0000))).Render(canvas, project, time);
        return bitmap;
    }

    [Fact]
    public void A_project_of_1_1_1_is_brought_up_to_this_version()
    {
        const string old = """
            { "version": 1, // written by 1.1.1
              "canvas": { "width": 640, "height": 360, "fps": 30, "background": "#102030" }, "assets": [], "tracks": [], }
            """;
        var project = ProjectJson.Deserialize(Encoding.UTF8.GetBytes(old));
        project.Version.Should().Be(VideoProject.CurrentVersion);
        project.Canvas.Background.Should().Be(new CanvasBackground(BackgroundKind.Color, "#102030"));
        project.Canvas.Framing.Should().Be(ScreenFraming.Fit);

        var written = JsonNode.Parse(ProjectJson.Serialize(project))!;
        written["version"]!.GetValue<int>().Should().Be(2);
        written["canvas"]!["background"]!["color"]!.GetValue<string>().Should().Be("#102030");

        // A project with nothing to migrate (no background at all, as a hand-written one) reads too.
        ProjectJson.Deserialize("""{ "canvas": { "width": 2, "height": 2, "fps": 30 }, "assets": [], "tracks": [] }"""u8).Canvas.Background.Should().Be(CanvasBackground.Black);
    }

    [Fact]
    public void Everything_new_is_saved_and_read_back()
    {
        var asset = TestProjects.Recording(10, webcam: true);
        var project = ProjectFactory.AddRecording(VideoProject.Empty(new ProjectCanvas(
            1080, 1920, 30, new CanvasBackground(BackgroundKind.LinearGradient, "#112233", "#445566", 45, null, 12, 0.5), ScreenFraming.FollowPointer)), asset, 0);
        var screen = project.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.Single();
        project = project.WithItem(screen with { Placement = new Placement(0.5, 0.4, 0.8, CornerRadius: 22, Shadow: new PlacementShadow(1, 2, 3, "#010203", 0.4), InsetBorder: "#80FFFFFF", InsetBorderWidth: 2, Frame: DeviceFrame.Laptop) });
        var webcam = project.Tracks.Single(t => t.Kind == TrackKind.Webcam).Clips.Single();
        project = project.WithItem(webcam with { Background = new WebcamBackground(BackgroundEffect.Replace, 9, "#ABCDEF", "pic") });
        project = AutoZoom.AddZoom(project, new ZoomItem { Id = "z", Start = 1, Length = 2, Easing = ZoomEasing.Spring, MotionBlur = 0.75 });
        (project, var layout) = CanvasEdits.AddLayout(project, LayoutKind.CameraOnly, 2, 3);
        (project, var pointer) = AnnotationEdits.AddEffect(project, EffectKind.Pointer, 0, 4);
        project = project.WithItem((EffectItem)project.Find(pointer)!.Value.Item with { SharpPointer = true, PressAnimation = true, ConstantPointerSize = true });

        var back = ProjectJson.Deserialize(ProjectJson.Serialize(project));
        Encoding.UTF8.GetString(ProjectJson.Serialize(back)).Should().Be(Encoding.UTF8.GetString(ProjectJson.Serialize(project)));
        back.Canvas.Should().Be(project.Canvas);
        back.Find(layout)!.Value.Item.Should().BeOfType<LayoutItem>().Which.Kind.Should().Be(LayoutKind.CameraOnly);
        back.Find("z")!.Value.Item.Should().Be(project.Find("z")!.Value.Item);
        back.Find(pointer)!.Value.Item.Should().Be(project.Find(pointer)!.Value.Item);
        back.Find(webcam.Id)!.Value.Item.Should().BeOfType<Clip>().Which.Background.Should().Be(new WebcamBackground(BackgroundEffect.Replace, 9, "#ABCDEF", "pic"));
    }

    [Fact]
    public void A_gradient_runs_across_the_canvas_along_its_angle()
    {
        var across = Project(new ProjectCanvas(1280, 720, 30, new CanvasBackground(BackgroundKind.LinearGradient, "#000000", "#FFFFFF", 0)));
        using var hidden = Render(across.WithTrack(across.Tracks.Single(t => t.Kind == TrackKind.Video) with { Hidden = true }));
        hidden.GetPixel(0, 360).Red.Should().BeLessThan(4);
        hidden.GetPixel(640, 360).Red.Should().BeInRange(124, 131);
        hidden.GetPixel(1279, 360).Red.Should().BeGreaterThan(251);
        hidden.GetPixel(640, 0).Red.Should().BeInRange(124, 131, "an angle of 0 changes nothing going down");

        var down = across with { Canvas = across.Canvas with { Background = across.Canvas.Background with { Angle = 90 } } };
        using var vertical = Render(down.WithTrack(down.Tracks.Single(t => t.Kind == TrackKind.Video) with { Hidden = true }));
        vertical.GetPixel(640, 2).Red.Should().BeLessThan(4);
        vertical.GetPixel(640, 717).Red.Should().BeGreaterThan(251);

        var round = Project(new ProjectCanvas(1280, 720, 30, new CanvasBackground(BackgroundKind.RadialGradient, "#FFFFFF", "#000000")));
        using var radial = Render(round.WithTrack(round.Tracks.Single(t => t.Kind == TrackKind.Video) with { Hidden = true }));
        radial.GetPixel(640, 360).Red.Should().BeGreaterThan(251);
        radial.GetPixel(0, 0).Red.Should().BeLessThan(6);
    }

    [Fact]
    public void A_picture_covers_the_canvas_without_changing_its_shape()
    {
        var file = Path.Combine(_root, "back.png");
        using (var picture = new SKBitmap(100, 100))
        {
            // Left half green, right half blue; a square on a wide canvas loses its top and bottom, not its sides.
            for (var y = 0; y < 100; y++)
            {
                for (var x = 0; x < 100; x++)
                {
                    picture.SetPixel(x, y, x < 50 ? new SKColor(0, 255, 0) : new SKColor(0, 0, 255));
                }
            }

            using var data = picture.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(file, data.ToArray());
        }

        var asset = Looks.PictureAsset(file);
        var project = Placed(Project().WithAsset(asset), new Placement(Size: 0.5)) with { };
        project = project with { Canvas = project.Canvas with { Background = new CanvasBackground(BackgroundKind.Picture, PictureAssetId: asset.Id) } };
        using var bitmap = Render(project);
        bitmap.GetPixel(40, 360).Should().Be(new SKColor(0, 255, 0));
        bitmap.GetPixel(1240, 360).Should().Be(new SKColor(0, 0, 255));
        bitmap.GetPixel(640, 360).Should().Be(new SKColor(255, 0, 0), "the screen is on top of it");

        // Taking a track away keeps the picture among the project's media.
        TrackEdits.Remove(project, project.Tracks[0].Id).Asset(asset.Id).Should().NotBeNull();
    }

    [Fact]
    public void A_missing_picture_falls_back_to_the_colour()
    {
        var asset = Looks.PictureAsset(Path.Combine(_root, "gone.png"));
        var project = Placed(Project().WithAsset(asset), new Placement(Size: 0.5));
        project = project with { Canvas = project.Canvas with { Background = new CanvasBackground(BackgroundKind.Picture, "#00FF00", PictureAssetId: asset.Id) } };
        using var bitmap = Render(project);
        bitmap.GetPixel(40, 40).Should().Be(new SKColor(0, 255, 0));
    }

    [Fact]
    public void The_screen_itself_blurred_covers_the_canvas()
    {
        var project = Placed(Project(), new Placement(Size: 0.5));
        project = project with { Canvas = project.Canvas with { Background = new CanvasBackground(BackgroundKind.BlurredScreen, "#00FF00") } };
        using var bitmap = Render(project);
        var corner = bitmap.GetPixel(10, 10);
        corner.Red.Should().BeGreaterThan(240, "the red screen's own colour, blurred");
        corner.Green.Should().BeLessThan(10);
    }

    [Fact]
    public void Padding_leaves_the_background_and_a_corner_radius_rounds_the_picture()
    {
        var project = Project(new ProjectCanvas(1280, 720, 30, "#00FF00"));
        var padded = Placed(project, new Placement(Size: 0.8, CornerRadius: 40));
        using var bitmap = Render(padded);

        // 1024 × 576, centred: from (128, 72) to (1152, 648).
        bitmap.GetPixel(100, 360).Should().Be(new SKColor(0, 255, 0));
        bitmap.GetPixel(140, 360).Should().Be(new SKColor(255, 0, 0));
        bitmap.GetPixel(130, 74).Should().Be(new SKColor(0, 255, 0), "the corner itself is cut off");
        bitmap.GetPixel(128 + 40, 72 + 40).Should().Be(new SKColor(255, 0, 0));

        // Without a radius the corner is the picture's, and with one the pixels of the plain path don't change.
        using var square = Render(Placed(project, new Placement(Size: 0.8)));
        square.GetPixel(130, 74).Should().Be(new SKColor(255, 0, 0));
    }

    [Fact]
    public void A_shadow_falls_below_the_picture_and_fades_out()
    {
        var project = Project(new ProjectCanvas(1280, 720, 30, "#FFFFFF"));
        var shadowed = Placed(project, new Placement(Size: 0.6, CornerRadius: 20, Shadow: new PlacementShadow(0, 20, 16, "#000000", 0.5)));
        using var bitmap = Render(shadowed);

        // 768 × 432, centred: down to y = 576.
        var under = bitmap.GetPixel(640, 584).Red;
        var further = bitmap.GetPixel(640, 600).Red;
        var away = bitmap.GetPixel(640, 660).Red;
        var above = bitmap.GetPixel(640, 130).Red;
        under.Should().BeInRange(120, 175, "half strength just under the picture");
        further.Should().BeGreaterThan(under).And.BeLessThan(250);
        away.Should().BeGreaterThan(250, "it has faded out");
        above.Should().BeGreaterThan(under, "the offset pushes it down");
        bitmap.GetPixel(640, 360).Should().Be(new SKColor(255, 0, 0), "the picture isn't darkened");
    }

    [Fact]
    public void The_worked_out_shadow_matches_a_real_blur()
    {
        // The shader is an approximation of a Gaussian blur; Skia's own blur of the same shape is the measure.
        const int width = 400;
        const int height = 300;
        var box = SKRect.Create(100, 80, 200, 140);
        using var real = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(real))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true, MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 12) })
        {
            canvas.Clear(SKColors.White);
            canvas.DrawRoundRect(box, 18, 18, paint);
        }

        using var shaded = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(shaded))
        using (var shader = StudioShaders.Shadow(box, 18, 12, SKColors.Black))
        using (var paint = new SKPaint { Shader = shader })
        {
            canvas.Clear(SKColors.White);
            canvas.DrawRect(SKRect.Create(width, height), paint);
        }

        Mean(real, shaded).Should().BeLessThan(2.5);
    }

    [Fact]
    public void An_inset_border_stays_inside_the_picture()
    {
        var project = Project(new ProjectCanvas(1280, 720, 30, "#000000"));
        using var bitmap = Render(Placed(project, new Placement(Size: 0.5, InsetBorder: "#FFFFFFFF", InsetBorderWidth: 8)));

        // 640 × 360, centred: from x = 320; the line is the first 8 pixels inside it.
        bitmap.GetPixel(316, 360).Should().Be(new SKColor(0, 0, 0));
        bitmap.GetPixel(324, 360).Should().Be(new SKColor(255, 255, 255));
        bitmap.GetPixel(332, 360).Should().Be(new SKColor(255, 0, 0));
    }

    [Theory]
    [InlineData(DeviceFrame.Browser)]
    [InlineData(DeviceFrame.Laptop)]
    [InlineData(DeviceFrame.Monitor)]
    [InlineData(DeviceFrame.Phone)]
    public void A_device_is_drawn_round_the_picture(DeviceFrame frame)
    {
        var project = Project(new ProjectCanvas(1280, 720, 30, "#00FF00"));
        using var bitmap = Render(Placed(project, new Placement(Size: 0.6, Frame: frame)));
        var screen = SKRect.Create(256, 144, 768, 432);
        var (body, _) = DeviceFrames.Body(frame, screen);
        var bounds = DeviceFrames.Bounds(frame, screen);
        bounds.Contains(body).Should().BeTrue();
        bounds.Left.Should().BeGreaterThan(0);
        bounds.Bottom.Should().BeLessThan(720);
        bitmap.GetPixel(640, 360).Should().Be(new SKColor(255, 0, 0), "the picture is on top");
        bitmap.GetPixel((int)(screen.Left + 40), (int)screen.Top - 1).Should().NotBe(new SKColor(0, 255, 0), "the frame is round it");
        bitmap.GetPixel(4, 4).Should().Be(new SKColor(0, 255, 0), "away from it, the background");
    }

    [Fact]
    public void A_zoom_scales_the_screen_with_its_frame_and_shadow_and_the_background_stays()
    {
        var project = Placed(Project(new ProjectCanvas(1280, 720, 30, "#FFFFFF")), new Placement(Size: 0.8, CornerRadius: 30, Shadow: new PlacementShadow(0, 16, 12, "#000000", 0.5), Frame: DeviceFrame.Monitor));
        var zoomed = AutoZoom.AddZoom(project, new ZoomItem { Id = "z", Start = 0, Length = 10, Scale = 1.25, Focus = ZoomFocus.Fixed, EaseIn = 0, EaseOut = 0 });
        using var whole = Render(project);
        using var near = Render(zoomed);

        // At 1.25× the 80% picture fills the canvas exactly, so the frame has left it.
        whole.GetPixel(8, 8).Red.Should().Be(255);
        whole.GetPixel(8, 8).Blue.Should().Be(255, "the canvas's corner is background");
        whole.GetPixel(640, 40).Blue.Should().Be(255);
        near.GetPixel(640, 40).Should().Be(new SKColor(255, 0, 0), "the picture is larger");
        near.GetPixel(640, 360).Should().Be(new SKColor(255, 0, 0));

        // A smaller picture zoomed in as far: larger than it was, and the background still shows round it, unmoved.
        var small = Placed(project, new Placement(Size: 0.6, Shadow: new PlacementShadow(0, 16, 12, "#000000", 0.5), Frame: DeviceFrame.Monitor));
        using var smallWhole = Render(small);
        using var some = Render(AutoZoom.AddZoom(small, new ZoomItem { Id = "z", Start = 0, Length = 10, Scale = 1.25, Focus = ZoomFocus.Fixed, EaseIn = 0, EaseOut = 0 }));
        smallWhole.GetPixel(224, 360).Green.Should().BeGreaterThan(240, "the picture starts at x = 256, with only the end of its shadow here");
        some.GetPixel(224, 360).Should().Be(new SKColor(255, 0, 0), "it starts at x = 160 now");
        some.GetPixel(8, 8).Should().Be(new SKColor(255, 255, 255));
    }

    [Fact]
    public void Parallax_moves_a_background_a_little_with_the_zoom()
    {
        var gradient = new CanvasBackground(BackgroundKind.LinearGradient, "#000000", "#FFFFFF", 0, Parallax: 1);
        var project = AutoZoom.AddZoom(Placed(Project(new ProjectCanvas(1280, 720, 30, gradient)), new Placement(Size: 0.25)), new ZoomItem { Id = "z", Start = 0, Length = 10, Scale = 2, Focus = ZoomFocus.Fixed, EaseIn = 0, EaseOut = 0 });
        using var moving = Render(project);
        using var still = Render(project with { Canvas = project.Canvas with { Background = gradient with { Parallax = 0 } } });

        // At 2× the background grows by a tenth about the middle: x = 1200 shows what was at about 1149.
        moving.GetPixel(1200, 40).Red.Should().BeLessThan(still.GetPixel(1200, 40).Red);
        moving.GetPixel(640, 40).Red.Should().BeInRange((byte)(still.GetPixel(640, 40).Red - 2), (byte)(still.GetPixel(640, 40).Red + 2), "the middle doesn't move");
    }

    [Fact]
    public void Looks_fit_any_canvas_in_proportion_and_can_be_taken_off()
    {
        var soft = Looks.Find(Looks.SoftId)!;
        var hd = Looks.Apply(Project(new ProjectCanvas(1920, 1080, 30)), soft);
        var small = Looks.Apply(Project(new ProjectCanvas(960, 540, 30)), soft);
        var (big, little) = (Looks.ScreenPlacement(hd)!, Looks.ScreenPlacement(small)!);
        big.CornerRadius.Should().Be(soft.CornerRadius);
        little.CornerRadius.Should().Be(soft.CornerRadius / 2);
        little.Shadow!.Blur.Should().Be(soft.Shadow!.Blur / 2);
        big.Size.Should().Be(little.Size).And.BeApproximately(0.88, 1e-9);
        hd.Canvas.Background.Kind.Should().Be(BackgroundKind.LinearGradient);

        Looks.Current(hd, Looks.BuiltIn)!.Id.Should().Be(Looks.SoftId);
        var none = Looks.Apply(hd, Looks.Find(Looks.NoneId)!);
        Looks.ScreenPlacement(none).Should().Be(Placement.Fit);
        none.Canvas.Background.Should().Be(CanvasBackground.Black);
        Looks.Current(none, Looks.BuiltIn)!.Id.Should().Be(Looks.NoneId);

        // The webcam keeps its place.
        var withWebcam = TestProjects.Project(10, webcam: true);
        var webcam = withWebcam.Tracks.Single(t => t.Kind == TrackKind.Webcam).Clips.Single().Placement;
        Looks.Apply(withWebcam, soft).Tracks.Single(t => t.Kind == TrackKind.Webcam).Clips.Single().Placement.Should().Be(webcam);
    }

    [Fact]
    public void A_look_is_saved_by_name_in_the_presets_and_comes_back_the_same()
    {
        var project = Looks.Apply(Project(new ProjectCanvas(1920, 1080, 30)), Looks.Find(Looks.DarkId)!);
        var saved = Looks.Capture(project, "Mine");
        var library = PresetLibrary.FromJson(new PresetLibrary(Looks: [saved]).ToJson());
        library.SavedLooks.Should().ContainSingle().Which.Should().Be(saved);

        var again = Looks.Apply(Project(new ProjectCanvas(1920, 1080, 30)), Looks.Find("mine", library.SavedLooks)!);
        again.Canvas.Should().Be(project.Canvas);
        Looks.ScreenPlacement(again).Should().Be(Looks.ScreenPlacement(project));

        // Saved under the same name it replaces the last; a list without it merges with what is there.
        library.WithLook(saved with { Padding = 0.2 }).SavedLooks.Should().ContainSingle().Which.Padding.Should().Be(0.2);
        new PresetLibrary().MergedWith(library).SavedLooks.Should().HaveCount(1);
        library.WithoutLook("MINE").SavedLooks.Should().BeEmpty();
    }

    [Fact]
    public void A_preview_on_the_graphics_card_draws_what_the_export_draws()
    {
        using var gl = OffscreenGl.TryCreate();
        Assert.SkipWhen(gl is null, "No OpenGL here.");
        foreach (var (name, project, time) in GoldenProjects())
        {
            using var cpu = new SKBitmap(new SKImageInfo(project.Canvas.Width, project.Canvas.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(cpu))
            {
                new FrameCompositor(new PatternFrames()).Render(canvas, project, time);
            }

            using var surface = gl!.Surface(project.Canvas.Width, project.Canvas.Height);
            new FrameCompositor(new PatternFrames()).Render(surface.Canvas, project, time);
            using var gpu = gl.Read(surface, project.Canvas.Width, project.Canvas.Height);
            var mean = Mean(cpu, gpu);
            output.WriteLine($"{name}: {mean:0.000} levels on average");
            mean.Should().BeLessThan(1, "{0} must look the same in the preview and the export", name);
        }
    }

    /// <summary>Writes the looks as pictures to a folder, to look at them (<c>CASHGEAR_LOOKS_TO</c>).</summary>
    [Fact(Explicit = true)]
    public void Write_the_looks_as_pictures()
    {
        var folder = Environment.GetEnvironmentVariable("CASHGEAR_LOOKS_TO") ?? Path.Combine(Path.GetTempPath(), "cashgear-looks");
        Directory.CreateDirectory(folder);
        foreach (var (name, project, time) in GoldenProjects())
        {
            using var bitmap = new SKBitmap(new SKImageInfo(project.Canvas.Width, project.Canvas.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var canvas = new SKCanvas(bitmap))
            {
                new FrameCompositor(new PatternFrames()).Render(canvas, project, time);
            }

            using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(folder, name.Replace(' ', '-') + ".png"), data.ToArray());
        }

        output.WriteLine(folder);
    }

    /// <summary>The looks in turn, for comparing the two ways of drawing them.</summary>
    private static IEnumerable<(string Name, VideoProject Project, double Time)> GoldenProjects()
    {
        var plain = ProjectFactory.AddRecording(VideoProject.Empty(new ProjectCanvas(640, 360, 30)), new MediaAsset("rec", @"C:\nowhere\Rec.mp4", 10, [new AssetStream(0, StreamKind.Video, null, 0, 10, 640, 360, 30)]), 0);
        foreach (var look in Looks.BuiltIn)
        {
            yield return (look.Name, Looks.Apply(plain, look), 2);
        }

        var soft = Looks.Apply(plain, Looks.Find(Looks.SoftId)!);
        foreach (var frame in new[] { DeviceFrame.Browser, DeviceFrame.Laptop, DeviceFrame.Monitor, DeviceFrame.Phone })
        {
            yield return (frame.ToString(), Placed(soft, Looks.ScreenPlacement(soft)! with { Size = 0.7, Frame = frame }), 2);
        }

        yield return ("blurred screen", soft with { Canvas = soft.Canvas with { Background = new CanvasBackground(BackgroundKind.BlurredScreen) } }, 2);
        var zoomed = AutoZoom.AddZoom(soft, new ZoomItem { Id = "z", Start = 1, Length = 4, Scale = 2, Focus = ZoomFocus.Fixed, CenterX = 0.3, CenterY = 0.4, EaseIn = 1, EaseOut = 1 });
        yield return ("in a zoom", zoomed, 3);
        yield return ("zooming in", zoomed, 1.4);
    }

    /// <summary>The mean difference per channel between two pictures, in levels of 255.</summary>
    internal static double Mean(SKBitmap a, SKBitmap b)
    {
        var first = a.GetPixelSpan();
        var second = b.GetPixelSpan();
        long total = 0;
        var count = 0;
        for (var i = 0; i < first.Length; i++)
        {
            if (i % 4 != 3)
            {
                total += Math.Abs(first[i] - second[i]);
                count++;
            }
        }

        return (double)total / count;
    }
}
