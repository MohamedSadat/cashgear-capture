using System.IO;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>Phase 22 M4: titles, callouts, templates, the brand kit and the library.</summary>
public sealed class TemplateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cashgear-template-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private static SKBitmap Draw(VideoProject project, double time, bool gpuless = true)
    {
        _ = gpuless;
        var bitmap = new SKBitmap(new SKImageInfo(project.Canvas.Width, project.Canvas.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        new FrameCompositor(new SolidFrames(_ => (1280, 720, 0xFF404040))).Render(canvas, project, time);
        return bitmap;
    }

    private static VideoProject Blank() => TestProjects.Project(10);

    private string LogoFile(string name = "logo.png")
    {
        using var surface = SKSurface.Create(new SKImageInfo(80, 40, SKColorType.Bgra8888, SKAlphaType.Premul));
        surface.Canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { Color = SKColors.Yellow };
        surface.Canvas.DrawRect(0, 0, 80, 40, paint);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public void Every_built_in_template_draws_comes_in_and_goes_out()
    {
        BuiltInTemplates.All.Should().HaveCountGreaterThanOrEqualTo(6);
        BuiltInTemplates.All.Select(t => t.Id).Should().OnlyHaveUniqueItems();
        BuiltInTemplates.All.Should().Contain(t => t.Kind == TemplateKind.LowerThird).And.Contain(t => t.Kind == TemplateKind.Title);
        using var plain = Draw(Blank(), 2);
        foreach (var template in BuiltInTemplates.All.Where(t => t.Elements.All(e => !e.IsLogo)))
        {
            var (project, id) = TemplateEdits.Insert(Blank(), template, 1);
            var item = (TemplateItem)project.Find(id)!.Value.Item;
            item.Duration.Should().Be(template.Length);
            using (var shown = Draw(project, 2.5))
            {
                PreviewCompare.MeanDifference(plain, shown).Should().BeGreaterThan(0.1, "{0} shows at its middle", template.Name);
            }

            using var before = Draw(project, 0.5);
            PreviewCompare.MeanDifference(plain, before).Should().Be(0, "{0} isn't there before it starts", template.Name);
            TemplateEdits.Bounds(item, project, 3).Width.Should().BeGreaterThan(10);
        }
    }

    [Fact]
    public void A_fields_text_shows_and_a_template_follows_the_canvas()
    {
        var template = BuiltInTemplates.All.Single(t => t.Id == "title-band");
        var (project, id) = TemplateEdits.Insert(Blank(), template, 0);
        var item = (TemplateItem)project.Find(id)!.Value.Item;
        var parts = TemplateEdits.Expand(item with { Values = new Dictionary<string, string> { ["title"] = "Hello" } }, project);
        parts.Single(p => p.Shape == AnnotationShape.Text && p.Text == "Hello").Look.FontSize.Should().BeApproximately(100 * (720 / 1080.0), 0.01, "sizes are in proportion to the canvas");

        // Across its place: the text's middle is the place's.
        var hello = parts.First(p => p.Text == "Hello");
        var bounds = FrameCompositor.Bounds(hello, 1);
        bounds.MidX.Should().BeApproximately(640, 2f);

        var vertical = project with { Canvas = project.Canvas with { Width = 720, Height = 1280 } };
        var band = TemplateEdits.Expand(item, vertical).First(p => p.Shape == AnnotationShape.Rectangle);
        band.Keyframes[0].Width.Should().Be(720);
    }

    [Fact]
    public void A_right_to_left_lower_third_goes_on_the_other_side()
    {
        var template = BuiltInTemplates.All.Single(t => t.Id == "third-bar");
        var (project, id) = TemplateEdits.Insert(Blank(), template, 0);
        var item = (TemplateItem)project.Find(id)!.Value.Item;
        var english = TemplateEdits.Expand(item, project).First(p => p.Shape == AnnotationShape.Rectangle);
        english.Keyframes[0].X.Should().BeLessThan(640);
        var arabic = TemplateEdits.Expand(item with { Values = new Dictionary<string, string> { ["name"] = "محمد" } }, project).First(p => p.Shape == AnnotationShape.Rectangle);
        (arabic.Keyframes[0].X + arabic.Keyframes[0].Width).Should().BeGreaterThan(640);
        (project.Canvas.Width - (arabic.Keyframes[0].X + arabic.Keyframes[0].Width)).Should().BeApproximately(english.Keyframes[0].X, 0.01);
    }

    [Fact]
    public void A_cut_through_a_template_keeps_each_part_where_it_was()
    {
        var (project, id) = TemplateEdits.Insert(Blank(), BuiltInTemplates.All.Single(t => t.Id == "title-centered"), 1);
        using var whole = Draw(project, 3.5);
        var split = TimelineEdits.Split(project, 2.5);
        var track = split.Find(id)!.Value.Track;
        track.Items.OfType<TemplateItem>().Should().HaveCount(2);
        using var second = Draw(split, 3.5);
        PreviewCompare.MeanDifference(whole, second).Should().Be(0);
    }

    [Fact]
    public void Changing_the_brands_colour_changes_every_template_that_uses_it()
    {
        var (first, _) = TemplateEdits.Insert(Blank(), BuiltInTemplates.All.Single(t => t.Id == "third-bar"), 0);
        var (project, _) = TemplateEdits.Insert(first, BuiltInTemplates.All.Single(t => t.Id == "title-band"), 0);
        project.Tracks.Count(t => t.Kind == TrackKind.Annotation).Should().Be(2, "they overlap, so each has a track");
        project = TemplateEdits.WithBrand(project, b => b with { Primary = "#E00000", Text = "#000000" });
        var (barX, barY) = (100, (int)(900 * (720 / 1080.0)));
        var (bandX, bandY) = (640, 400);
        barY.Should().BeGreaterThan(0);
        using (var red = Draw(project, 2.5))
        {
            red.GetPixel(bandX, 280).Should().Be(new SKColor(0xE0, 0, 0));
            red.GetPixel(barX + 300, 650).Should().Be(new SKColor(0xE0, 0, 0));
            bandY.Should().BeGreaterThan(0);
        }

        var green = TemplateEdits.WithBrand(project, b => b with { Primary = "#00C000" });
        using var changed = Draw(green, 2.5);
        changed.GetPixel(bandX, 280).Should().Be(new SKColor(0, 0xC0, 0));
        changed.GetPixel(barX + 300, 650).Should().Be(new SKColor(0, 0xC0, 0));
    }

    [Fact]
    public void A_template_exported_as_a_pack_and_imported_on_a_clean_profile_draws_the_same_frames()
    {
        var logo = LogoFile();
        var library = new TemplateLibrary(Path.Combine(_root, "first"));
        var assets = new Dictionary<string, string> { ["logo.png"] = logo };
        var picture = new TemplateElement(
            new AnnotationItem { Id = "pic", Shape = AnnotationShape.Picture, Length = 4, Keyframes = [new(0, 200, 200, 400, 200)] },
            AssetName: "logo.png");
        var words = new TemplateElement(
            new AnnotationItem { Id = "txt", Shape = AnnotationShape.Text, Length = 4, Text = "", Keyframes = [new(0, 300, 500, 0, 0)], Look = new AnnotationLook(FontSize: 72), TextIn = new TextAnimation(TextAnimationKind.Slide, 1) },
            Color: BrandColor.Accent,
            Font: BrandFont.Heading,
            Field: "line");
        var template = new TitleTemplate("mine", "Mine", TemplateKind.Saved, 4, [picture, words], [new("line", "Line", "مرحبا Team")]);
        library.Save(template, assets);
        library.SaveBrand(new LibraryBrand(new BrandKit { Accent = "#10C0F0" }));
        library.Templates().Single().Name.Should().Be("Mine");
        var pack = Path.Combine(_root, "team" + TemplateLibrary.PackExtension);
        library.ExportPack(pack, library.Templates(), library.Brand());

        var clean = new TemplateLibrary(Path.Combine(_root, "clean"));
        clean.Templates().Should().BeEmpty();
        var read = clean.ImportPack(pack);
        read.Templates.Should().ContainSingle();
        clean.Templates().Single().Should().NotBeNull();
        File.Exists(Path.Combine(clean.AssetsFolder, "logo.png")).Should().BeTrue();
        clean.Brand()!.Kit.Accent.Should().Be("#10C0F0");

        VideoProject Made(TemplateLibrary from)
        {
            var (project, _) = TemplateEdits.Insert(from.ApplyBrand(Blank(), from.Brand()!), from.Templates().Single(), 1, from.AssetsFolder);
            return project;
        }

        var (one, two) = (Made(library), Made(clean));
        foreach (var time in new[] { 1.2, 1.6, 2.5, 4.5 })
        {
            using var a = Draw(one, time);
            using var b = Draw(two, time);
            PreviewCompare.MeanDifference(a, b).Should().Be(0, "at {0} s", time);
        }

        using (var shown = Draw(two, 2.5))
        {
            // Its picture is there, from the pack's own file.
            shown.GetPixel((int)(400 * (1280 / 1920.0)), (int)(300 * (720 / 1080.0))).Should().Be(SKColors.Yellow);
        }

        // The project needs nothing else: its pictures are media of its own.
        two.Assets.Should().Contain(a => a.IsPicture && a.Name == "logo.png");
        ProjectJson.Deserialize(ProjectJson.Serialize(two)).Tracks.SelectMany(t => t.Items).OfType<TemplateItem>().Single().Template.Name.Should().Be("Mine");
    }

    [Fact]
    public void What_is_in_a_pack_cant_reach_outside_the_library()
    {
        var path = Path.Combine(_root, "evil.cgpack");
        using (var zip = System.IO.Compression.ZipFile.Open(path, System.IO.Compression.ZipArchiveMode.Create))
        {
            using (var entry = new StreamWriter(zip.CreateEntry("pack.json").Open()))
            {
                entry.Write("""{ "version": 1, "templates": [], "assets": ["..\\..\\evil.txt"] }""");
            }

            using var evil = new StreamWriter(zip.CreateEntry("assets/..\\..\\evil.txt").Open());
            evil.Write("x");
        }

        var library = new TemplateLibrary(Path.Combine(_root, "lib"));
        library.ImportPack(path);
        File.Exists(Path.Combine(_root, "evil.txt")).Should().BeFalse();

        File.WriteAllText(Path.Combine(_root, "no.cgpack"), "not a zip");
        var notZip = () => library.ImportPack(Path.Combine(_root, "no.cgpack"));
        notZip.Should().Throw<InvalidDataException>();
        var newer = Path.Combine(_root, "newer.cgpack");
        using (var zip = System.IO.Compression.ZipFile.Open(newer, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var entry = new StreamWriter(zip.CreateEntry("pack.json").Open());
            entry.Write("""{ "version": 99, "templates": [], "assets": [] }""");
        }

        var tooNew = () => library.ImportPack(newer);
        tooNew.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Annotations_can_be_saved_as_a_template_and_read_back()
    {
        var (project, _) = AnnotationEdits.Add(Blank(), AnnotationShape.Text, 2, 3);
        (project, _) = AnnotationEdits.Add(project, AnnotationShape.Rectangle, 3, 1);
        var items = project.Tracks.SelectMany(t => t.Items.OfType<AnnotationItem>()).OrderBy(a => a.Start).ToList();
        var template = TemplateEdits.FromItems(project, items, "Two");
        template.Length.Should().Be(3);
        template.Fields.Should().ContainSingle();
        template.Elements[1].Item.Start.Should().Be(1, "times from the first one's start");

        var (again, id) = TemplateEdits.Insert(Blank(), template, 2);
        using var drawn = Draw(again, 3.5);
        using var original = Draw(project, 3.5);
        PreviewCompare.MeanDifference(drawn, original).Should().BeLessThan(0.1, "it looks as the annotations did");
        var library = new TemplateLibrary(Path.Combine(_root, "saved"));
        library.Save(template);
        library.Templates().Single().Elements.Should().HaveCount(2);
        library.Delete(template.Id);
        library.Templates().Should().BeEmpty();
        again.Find(id).Should().NotBeNull();
    }

    public static TheoryData<AnnotationShape> Callouts => new() { AnnotationShape.SpeechBubble, AnnotationShape.StepNumber, AnnotationShape.Spotlight, AnnotationShape.Magnify };

    [Theory]
    [MemberData(nameof(Callouts))]
    public void Each_callout_draws_and_matches_between_preview_and_export(AnnotationShape shape)
    {
        using var turn = new CashGear.Capture.Tests.MachineTurn();
        var (project, id) = AnnotationEdits.Add(Blank(), shape, 0, 5);
        project.Find(id)!.Value.Item.Should().BeOfType<AnnotationItem>().Which.Shape.Should().Be(shape);
        using var plain = Draw(Blank(), 1);
        using var drawn = Draw(project, 1);
        PreviewCompare.MeanDifference(plain, drawn).Should().BeGreaterThan(0.1, "{0} shows", shape);
        FrameCompositor.Bounds((AnnotationItem)project.Find(id)!.Value.Item, 1).Width.Should().BeGreaterThan(0);

        using var gl = OffscreenGl.TryCreate();
        if (gl is null)
        {
            return;
        }

        using var gpu = SKSurface.Create(gl.Context, false, new SKImageInfo(project.Canvas.Width, project.Canvas.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        new FrameCompositor(new SolidFrames(_ => (1280, 720, 0xFF404040))).Render(gpu.Canvas, project, 1);
        gpu.Flush();
        using var read = new SKBitmap(new SKImageInfo(project.Canvas.Width, project.Canvas.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        gpu.ReadPixels(read.Info, read.GetPixels(), read.RowBytes, 0, 0);
        PreviewCompare.MeanDifference(drawn, read).Should().BeLessThan(1);
    }

    [Fact]
    public void A_stroke_is_drawn_through_its_points_and_steps_count_up()
    {
        var (project, id) = AnnotationEdits.AddFreehand(Blank(), [new SKPoint(100, 100), new SKPoint(200, 100), new SKPoint(300, 100), new SKPoint(300, 300)], 0);
        var stroke = (AnnotationItem)project.Find(id)!.Value.Item;
        stroke.Keyframes[0].Width.Should().Be(200);
        stroke.Points.Should().HaveCount(8);
        project = project.WithItem(stroke with { FadeIn = 0, FadeOut = 0, Look = stroke.Look with { Color = "#00FF00" } });
        using (var drawn = Draw(project, 1))
        {
            drawn.GetPixel(200, 100).Green.Should().BeGreaterThan(200, "the stroke goes through its points");
            drawn.GetPixel(200, 200).Green.Should().BeLessThan(0x80);
        }

        var (one, first) = AnnotationEdits.Add(Blank(), AnnotationShape.StepNumber, 0);
        var (two, second) = AnnotationEdits.Add(one, AnnotationShape.StepNumber, 0);
        ((AnnotationItem)two.Find(first)!.Value.Item).Number.Should().Be(1);
        ((AnnotationItem)two.Find(second)!.Value.Item).Number.Should().Be(2);
    }

    [Fact]
    public void A_magnifier_shows_what_is_under_it_larger()
    {
        var (project, solid) = MediaEdits.AddSolid(VideoProject.Empty(new ProjectCanvas(640, 360, 30)), "#2030C0", 0, 5);
        project.Find(solid).Should().NotBeNull();
        (project, var id) = AnnotationEdits.Add(project, AnnotationShape.Magnify, 0, 5);
        var item = (AnnotationItem)project.Find(id)!.Value.Item;
        item = item with { FadeIn = 0, FadeOut = 0 };
        project = project.WithItem(item);
        var place = item.At(1);
        var callout = (X: (int)(place.X + (place.Width / 2) + item.CalloutX), Y: (int)(place.Y + (place.Height / 2) + item.CalloutY));
        using var drawn = Draw(project, 1);
        drawn.GetPixel(callout.X, callout.Y).Should().Be(new SKColor(0x20, 0x30, 0xC0), "the lens shows the track below");
    }

    [Fact]
    public void Templates_callouts_and_the_brand_are_saved_and_read_back()
    {
        var (project, _) = TemplateEdits.Insert(Blank(), BuiltInTemplates.All[0], 0);
        (project, _) = AnnotationEdits.AddFreehand(project, [new SKPoint(1, 1), new SKPoint(9, 9)], 0);
        (project, _) = AnnotationEdits.Add(project, AnnotationShape.Magnify, 0);
        project = TemplateEdits.WithBrand(project, b => b with { Primary = "#123456", Look = "soft", LogoAssetId = "rec" });
        var read = ProjectJson.Deserialize(ProjectJson.Serialize(project));
        ProjectJson.Serialize(read).Should().Equal(ProjectJson.Serialize(project));
        read.Brand!.Primary.Should().Be("#123456");
        MediaEdits.IsUsed(project with { Tracks = [] }, "rec").Should().BeTrue("the brand's logo is the project's media");
    }

    [Fact]
    public void An_intro_pushes_what_is_there_along()
    {
        var project = TestProjects.Project(10);
        var intro = new MediaAsset("intro", @"C:\nowhere\I.mp4", 3, [new(0, StreamKind.Video, null, 0, 3, 1280, 720, 30), new(1, StreamKind.Audio, null, 0, 3)]);
        var (pushed, id) = MediaEdits.InsertPushing(project, intro, 0);
        pushed.Find(id!)!.Value.Track.Kind.Should().Be(TrackKind.Video);
        pushed.Duration.Should().Be(13);
        pushed.Tracks.Single(t => t.Kind == TrackKind.Video).Clips.Select(c => c.Start).Should().Equal(0.0, 3.0);
    }
}
