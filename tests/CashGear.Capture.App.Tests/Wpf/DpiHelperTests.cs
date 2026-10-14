using System.Windows;
using System.Windows.Media;
using CashGear.Capture.App.Wpf;
using CashGear.Capture.Core.Geometry;

namespace CashGear.Capture.App.Tests.Wpf;

public sealed class DpiHelperTests
{
    [Theory]
    [InlineData(1279.9999999, 0.0000001, 1280, 0)]
    [InlineData(-1280.0000001, -199.9999999, -1280, -200)]
    [InlineData(0.4999, 1.5, 0, 2)]
    [InlineData(-0.5, -1.5, -1, -2)]
    public void ToPixel_rounds_device_pixel_noise_to_the_nearest_pixel(double x, double y, int px, int py) =>
        DpiHelper.ToPixel(new Point(x, y)).Should().Be(new PixelPoint(px, py));

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Physical_transform_undoes_the_dpi_scale(double scale)
    {
        var transform = DpiHelper.PhysicalPixelTransform(new Core.Geometry.DpiScale(scale, scale));

        // physical units -> DIPs -> device pixels must be the identity.
        var dip = transform.Transform(new Point(1281, 717));
        new Point(dip.X * scale, dip.Y * scale).Should().BeEquivalentTo(new Point(1281, 717), o => o.Using<double>(c => c.Subject.Should().BeApproximately(c.Expectation, 1e-9)).WhenTypeIs<double>());
        transform.IsFrozen.Should().BeTrue();
    }

    [Fact]
    public void Dips_to_physical_scales_chrome_sizes() =>
        DpiHelper.DipsToPhysical(12, new Core.Geometry.DpiScale(1.5, 1.5)).Should().Be(18);
}
