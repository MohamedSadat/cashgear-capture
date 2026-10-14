using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.Core.Tests.Geometry;

public sealed class DpiMathTests
{
    [Theory]
    [InlineData(96, 1.0)]
    [InlineData(120, 1.25)]
    [InlineData(144, 1.5)]
    [InlineData(168, 1.75)]
    [InlineData(192, 2.0)]
    public void FromDpi_gives_scale(int dpi, double expected)
    {
        var scale = DpiScale.FromDpi(dpi, dpi);

        scale.ScaleX.Should().Be(expected);
        scale.ScaleY.Should().Be(expected);
        scale.DpiX.Should().Be(dpi);
    }

    [Theory]
    [InlineData(96, 100, 100)]
    [InlineData(120, 100, 125)]
    [InlineData(144, 100, 150)]
    [InlineData(144, -100, -150)]
    [InlineData(192, 0.5, 1)]
    [InlineData(144, 1, 2)] // 1.5 rounds away from zero
    [InlineData(144, -1, -2)]
    public void Point_to_pixels_rounds_to_nearest(int dpi, double dip, int expected)
    {
        var p = DpiMath.ToPixels(dip, dip, DpiScale.FromDpi(dpi, dpi));

        p.Should().Be(new PixelPoint(expected, expected));
    }

    [Fact]
    public void Rect_to_pixels_covers_fractional_edges()
    {
        // 10 DIP at 125% = 12.5 px: floor the left/top, ceil the right/bottom.
        var r = DpiMath.ToPixels(10, 10, 10, 10, DpiScale.FromDpi(120, 120));

        r.Should().Be(PixelRect.FromLTRB(12, 12, 25, 25));
    }

    [Fact]
    public void Rect_to_pixels_ignores_floating_point_noise()
    {
        // 0.1 * 3 = 0.30000000000000004 — must not ceil 30.000000000000004 to 31.
        var r = DpiMath.ToPixels(0, 0, 0.1 * 3 * 100, 10, DpiScale.Identity);

        r.Should().Be(new PixelRect(0, 0, 30, 10));
    }

    [Fact]
    public void Rect_to_pixels_with_negative_origin()
    {
        var r = DpiMath.ToPixels(-853.3333333333334, 0, 853.3333333333334, 100, DpiScale.FromDpi(144, 144));

        r.Should().Be(new PixelRect(-1280, 0, 1280, 150));
    }

    [Fact]
    public void Pixels_to_dips_and_back_round_trips_at_150_percent()
    {
        var scale = DpiScale.FromDpi(144, 144);
        var original = new PixelRect(-1280, 58, 1280, 1024);

        var (x, y, w, h) = DpiMath.ToDips(original, scale);
        var back = DpiMath.ToPixels(x, y, w, h, scale);

        back.Should().Be(original);
    }

    [Fact]
    public void Point_to_dips()
    {
        var (x, y) = DpiMath.ToDips(new PixelPoint(150, -300), DpiScale.FromDpi(144, 144));

        x.Should().Be(100);
        y.Should().Be(-200);
    }
}
