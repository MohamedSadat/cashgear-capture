using CashGear.Capture.Editor.Interaction;
using SkiaSharp;

namespace CashGear.Capture.Editor.Tests.Interaction;

public sealed class ViewportTests
{
    private static Viewport Make(int imageWidth, int imageHeight, float viewWidth = 1000, float viewHeight = 600)
    {
        var viewport = new Viewport();
        viewport.SetImageSize(new SKSizeI(imageWidth, imageHeight));
        viewport.SetViewSize(new SKSize(viewWidth, viewHeight));
        return viewport;
    }

    [Fact]
    public void A_large_image_is_shrunk_to_fit_and_centred()
    {
        var viewport = Make(2000, 1000);

        viewport.Zoom.Should().BeApproximately((1000 - 48) / 2000f, 1e-4f);
        viewport.Offset.X.Should().Be(24);
        viewport.Offset.Y.Should().Be(MathF.Round((600 - (1000 * viewport.Zoom)) / 2));
    }

    [Fact]
    public void A_small_image_opens_at_100_percent_in_the_middle()
    {
        var viewport = Make(100, 50);

        viewport.Zoom.Should().Be(1);
        viewport.Offset.Should().Be(new SKPoint(450, 275));
    }

    [Fact]
    public void Image_and_view_coordinates_convert_both_ways()
    {
        var viewport = Make(2000, 1000);
        var p = new SKPoint(123.5f, 456.25f);

        var back = viewport.ToImage(viewport.ToView(p));

        back.X.Should().BeApproximately(p.X, 1e-3f);
        back.Y.Should().BeApproximately(p.Y, 1e-3f);
    }

    [Fact]
    public void Zooming_keeps_the_point_under_the_mouse_in_place()
    {
        var viewport = Make(2000, 1000);
        var mouse = new SKPoint(300, 200);
        var before = viewport.ToImage(mouse);

        viewport.ZoomTo(2, mouse);

        viewport.Zoom.Should().Be(2);
        var after = viewport.ToImage(mouse);
        after.X.Should().BeApproximately(before.X, 0.5f, "offsets are rounded to whole pixels");
        after.Y.Should().BeApproximately(before.Y, 0.5f);
        viewport.IsFitting.Should().BeFalse();
    }

    [Fact]
    public void Zoom_steps_go_through_familiar_values()
    {
        var viewport = Make(100, 50);
        var centre = new SKPoint(500, 300);

        viewport.ZoomIn(centre);
        viewport.Zoom.Should().Be(1.5f);
        viewport.ZoomIn(centre);
        viewport.Zoom.Should().Be(2);
        viewport.ZoomOut(centre);
        viewport.ZoomOut(centre);
        viewport.ZoomOut(centre);
        viewport.Zoom.Should().Be(0.75f);
    }

    [Fact]
    public void Panning_stops_at_the_image_edge_plus_a_margin()
    {
        var viewport = Make(2000, 1000);
        viewport.ActualSize();

        viewport.PanBy(100_000, 100_000);
        viewport.Offset.Should().Be(new SKPoint(Viewport.Margin, Viewport.Margin));

        viewport.PanBy(-100_000, -100_000);
        viewport.Offset.Should().Be(new SKPoint(1000 - 2000 - Viewport.Margin, 600 - 1000 - Viewport.Margin));
    }

    [Fact]
    public void Offsets_stay_on_whole_pixels()
    {
        var viewport = Make(333, 177, 901, 603);
        viewport.ZoomTo(1, new SKPoint(17.3f, 99.9f));

        viewport.Offset.X.Should().Be(MathF.Round(viewport.Offset.X));
        viewport.Offset.Y.Should().Be(MathF.Round(viewport.Offset.Y));
    }

    [Fact]
    public void While_fitting_a_resize_refits_and_after_zooming_it_keeps_the_zoom()
    {
        var viewport = Make(2000, 1000);

        viewport.SetViewSize(new SKSize(500, 600));
        viewport.Zoom.Should().BeApproximately((500 - 48) / 2000f, 1e-4f);

        viewport.ZoomTo(1, new SKPoint(250, 300));
        viewport.SetViewSize(new SKSize(800, 600));
        viewport.Zoom.Should().Be(1);
    }

    [Fact]
    public void A_new_image_size_is_fitted_while_fitting()
    {
        var viewport = Make(2000, 1000);

        viewport.SetImageSize(new SKSizeI(400, 300));

        viewport.Zoom.Should().Be(1);
        viewport.ImageBounds.Should().Be(SKRect.Create(300, 150, 400, 300));
    }
}
