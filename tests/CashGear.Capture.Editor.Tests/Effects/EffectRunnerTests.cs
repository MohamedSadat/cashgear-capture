using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Effects;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Editor.Model;
using CashGear.Capture.Editor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.Editor.Tests.Effects;

public sealed class EffectRunnerTests
{
    private static readonly AnnotationStyle Style = TestImages.Red();

    /// <summary>One of each, with settings that change something visible.</summary>
    public static TheoryData<string> EffectNames => [.. All.Keys];

    private static readonly Dictionary<string, ImageEffect> All = new()
    {
        ["shadow"] = new ShadowEffect(Blur: 3, OffsetX: 2, OffsetY: 3, Opacity: 50),
        ["border"] = new BorderEffect(3, SKColors.Blue),
        ["torn"] = new TornEdgeEffect(Edges.Bottom | Edges.Right, Depth: 6, ToothWidth: 10, Seed: 5),
        ["fade"] = new FadeEdgeEffect(Edges.All, 10),
        ["rounded"] = new RoundedCornersEffect(9),
        ["perspective"] = new PerspectiveEffect(-20, PerspectiveAxis.Vertical),
        ["adjust"] = new AdjustEffect(20, 30),
        ["grayscale"] = new GrayscaleEffect(),
        ["sharpen"] = new SharpenEffect(80),
    };

    private static DocumentState State(SKImage image, params Annotation[] annotations) => new(image, [.. annotations]);

    [Fact]
    public void A_shadow_and_a_border_keep_annotations_as_objects_moved_with_the_image()
    {
        using var image = TestImages.Noise(60, 40);
        var rect = new RectangleAnnotation(new AnnotationId(1), Style, new SKRect(10, 10, 20, 20));
        var shadow = new ShadowEffect(Blur: 2, OffsetX: 0, OffsetY: 0);

        var result = EffectRunner.Apply(State(image, rect), [shadow, new BorderEffect(4)]);

        using var _ = result.Image;
        var shift = shadow.Margin + 4;
        result.Annotations.Should().ContainSingle().Which.Should().Be(rect with { Rect = new SKRect(10 + shift, 10 + shift, 20 + shift, 20 + shift) });
    }

    [Fact]
    public void Other_effects_merge_the_annotations_into_the_image_first()
    {
        using var image = TestImages.Solid(60, 40, SKColors.White);
        var rect = new RectangleAnnotation(new AnnotationId(1), Style with { Fill = SKColors.Red }, new SKRect(10, 10, 20, 20));

        var result = EffectRunner.Apply(State(image, rect), [new GrayscaleEffect()]);

        using var _ = result.Image;
        result.Annotations.Should().BeEmpty();
        TestImages.Pixel(result.Image, 15, 15).ShouldBeCloseTo(new SKColor(54, 54, 54)); // the red square was merged, then turned grey
    }

    [Fact]
    public void An_area_is_lifted_with_its_background_and_the_rest_stays_the_same()
    {
        using var image = TestImages.Noise(80, 60);
        var area = SKRectI.Create(20, 10, 40, 30);

        var result = EffectRunner.Apply(State(image), [new RoundedCornersEffect(10)], area, SKColors.Yellow);

        using var _ = result.Image;
        result.Image.Width.Should().Be(80);
        TestImages.Pixel(result.Image, 20, 10).Should().Be(SKColors.Yellow, "the cut corner shows the background");
        TestImages.Pixel(result.Image, 40, 25).Should().Be(TestImages.Pixel(image, 40, 25));
        TestImages.Pixel(result.Image, 5, 5).Should().Be(TestImages.Pixel(image, 5, 5));
        TestImages.Pixel(result.Image, 79, 59).Should().Be(TestImages.Pixel(image, 79, 59));
    }

    [Fact]
    public void A_border_on_an_area_frames_it_over_the_image_around()
    {
        using var image = TestImages.Solid(80, 60, SKColors.White);
        var area = SKRectI.Create(20, 10, 40, 30);

        var result = EffectRunner.Apply(State(image), [new BorderEffect(2, SKColors.Black)], area, SKColors.White);

        using var _ = result.Image;
        TestImages.Pixel(result.Image, 18, 20).Should().Be(SKColors.Black);
        TestImages.Pixel(result.Image, 17, 20).Should().Be(SKColors.White);
        TestImages.Pixel(result.Image, 61, 20).Should().Be(SKColors.Black);
        TestImages.Pixel(result.Image, 30, 20).Should().Be(SKColors.White);
    }

    [Fact]
    public void No_effects_change_nothing_and_the_input_is_never_modified()
    {
        using var image = TestImages.Noise(30, 20);
        var before = TestImages.Bytes(image);
        var state = State(image);

        EffectRunner.Apply(state, []).Should().BeSameAs(state);
        foreach (var effect in All.Values)
        {
            EffectRunner.Apply(state, [effect]).Image.Dispose();
        }

        TestImages.Bytes(image).Should().Equal(before);
    }

    [Fact]
    public void The_batch_entry_point_gives_the_same_pixels_as_the_document_one()
    {
        using var image = TestImages.Noise(50, 30);
        ImageEffect[] stack = [All["torn"], All["shadow"], All["border"]];

        using var alone = EffectRunner.Apply(image, stack);
        using var viaDocument = EffectRunner.Apply(State(image), stack).Image;

        TestImages.Bytes(alone).Should().Equal(TestImages.Bytes(viaDocument));
        using var copy = EffectRunner.Apply(image, []);
        copy.Should().NotBeSameAs(image);
        TestImages.Bytes(copy).Should().Equal(TestImages.Bytes(image));
    }

    /// <summary>The acceptance criterion: what the canvas shows while an effect is previewed is exactly what gets saved.</summary>
    [Theory]
    [MemberData(nameof(EffectNames))]
    public void The_preview_on_screen_is_exactly_the_committed_and_exported_image(string name)
    {
        using var doc = new EditorDocument(TestImages.Noise(90, 70));
        doc.Execute(new AddAnnotationCommand(new ArrowAnnotation(doc.NewId(), Style, new SKPoint(5, 5), new SKPoint(50, 40))));
        doc.Execute(new AddAnnotationCommand(new TextAnnotation(doc.NewId(), new AnnotationStyle { Color = SKColors.Black, FontSize = 12 }, new SKPoint(40, 50), "Hi")));
        var session = new EditorSession(doc);

        var preview = EffectRunner.Apply(doc.State, [All[name]]);
        session.ShowPreview(preview);
        using var shown = RenderAt100(session, preview.Image.Width, preview.Image.Height);

        session.CommitPreview(All[name].Name).Should().BeTrue();
        doc.Image.Should().BeSameAs(preview.Image, "the committed image is the previewed one, not a recomputation");
        using var exported = DocumentRenderer.Flatten(doc.State);
        using var expected = OverChecker(exported);
        TestImages.Bytes(shown).Should().Equal(TestImages.Bytes(expected));

        doc.History.UndoName.Should().Be(All[name].Name);
        session.Undo();
        doc.Width.Should().Be(90);
        doc.Annotations.Should().HaveCount(2, "undo gives the annotations back as objects");
    }

    [Fact]
    public void Cancelling_a_preview_frees_it_and_leaves_the_document_alone()
    {
        using var doc = new EditorDocument(TestImages.Noise(40, 30));
        var session = new EditorSession(doc);
        var before = doc.Image;
        var preview = EffectRunner.Apply(doc.State, [new GrayscaleEffect()]);
        var replaced = EffectRunner.Apply(doc.State, [new SharpenEffect()]);

        session.ShowPreview(preview);
        session.ShowPreview(replaced);
        preview.Image.Handle.Should().Be(IntPtr.Zero, "a newer preview frees the older one");
        session.CancelPreview();

        replaced.Image.Handle.Should().Be(IntPtr.Zero);
        session.Preview.Should().BeNull();
        doc.Image.Should().BeSameAs(before);
        doc.History.CanUndo.Should().BeFalse();
        session.CommitPreview("nothing").Should().BeFalse();
    }

    [Fact]
    public void Effects_on_the_selected_area_keep_the_area_for_the_next_one()
    {
        using var doc = new EditorDocument(TestImages.Noise(100, 80));
        var session = new EditorSession(doc) { Tool = EditorTool.Area };
        session.PointerDown(new SKPoint(10, 10), PointerModifiers.None, 1, 4);
        session.PointerMove(new SKPoint(60, 50), PointerModifiers.None);
        session.PointerUp(new SKPoint(60, 50), PointerModifiers.None);
        session.SelectedArea.Should().Be(SKRectI.Create(10, 10, 50, 40));
        var outside = TestImages.Pixel(doc.Image, 80, 70);

        session.ApplyEffects([new GrayscaleEffect()], toArea: true, SKColors.White);

        session.SelectedArea.Should().Be(SKRectI.Create(10, 10, 50, 40));
        var pixel = TestImages.Pixel(doc.Image, 30, 30);
        (pixel.Red == pixel.Green && pixel.Green == pixel.Blue).Should().BeTrue();
        TestImages.Pixel(doc.Image, 80, 70).Should().Be(outside);

        session.ApplyEffects([new ShadowEffect()], toArea: false, SKColors.White);
        session.SelectedArea.Should().BeNull("the whole image changed size");
        session.Tool = EditorTool.Select;
    }

    [Fact]
    public void Leaving_the_area_tool_or_pressing_escape_drops_the_area()
    {
        using var doc = new EditorDocument(TestImages.Noise(100, 80));
        var session = new EditorSession(doc) { Tool = EditorTool.Area };
        session.PointerDown(new SKPoint(10, 10), PointerModifiers.None, 1, 4);
        session.PointerUp(new SKPoint(40, 40), PointerModifiers.None);
        session.SelectedArea.Should().NotBeNull();

        session.Cancel().Should().BeTrue();
        session.SelectedArea.Should().BeNull();

        session.PointerDown(new SKPoint(10, 10), PointerModifiers.None, 1, 4);
        session.PointerUp(new SKPoint(40, 40), PointerModifiers.None);
        session.Tool = EditorTool.Arrow;
        session.SelectedArea.Should().BeNull();
        session.CropArea.Should().BeNull();
    }

    /// <summary>The exported image as the canvas shows it: over the checkerboard where it is transparent.</summary>
    private static SKImage OverChecker(SKImage exported)
    {
        using var empty = new EditorDocument(SkiaImaging.Draw(exported.Width, exported.Height, _ => { }));
        using var checker = RenderAt100(new EditorSession(empty), exported.Width, exported.Height);
        return SkiaImaging.Draw(exported.Width, exported.Height, c =>
        {
            c.DrawImage(checker, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
            c.DrawImage(exported, 0, 0, new SKSamplingOptions(SKFilterMode.Nearest), null);
        });
    }

    private static SKImage RenderAt100(EditorSession session, int width, int height)
    {
        var viewport = new Viewport();
        viewport.SetImageSize(new SKSizeI(width, height));
        viewport.SetViewSize(new SKSize(width + 101, height + 63));
        viewport.Zoom.Should().Be(1);
        using var renderer = new ViewRenderer();
        using var screen = SkiaImaging.Draw(width + 101, height + 63, c => renderer.Render(c, session, viewport, overlays: true));
        var x0 = (int)viewport.Offset.X;
        var y0 = (int)viewport.Offset.Y;
        return SkiaImaging.Draw(width, height, c => c.DrawImage(screen, -x0, -y0, new SKSamplingOptions(SKFilterMode.Nearest), null));
    }
}
