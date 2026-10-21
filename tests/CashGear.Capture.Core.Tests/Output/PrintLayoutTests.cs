using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Tests.Output;

public sealed class PrintLayoutTests
{
    // A4 portrait in device-independent units (1/96 inch).
    private const double A4Width = 210 * PrintLayout.UnitsPerMm;
    private const double A4Height = 297 * PrintLayout.UnitsPerMm;

    [Theory]
    [InlineData(PageOrientation.Auto, 1920, 1080, true)]
    [InlineData(PageOrientation.Auto, 600, 800, false)]
    [InlineData(PageOrientation.Auto, 500, 500, false)]
    [InlineData(PageOrientation.Portrait, 1920, 1080, false)]
    [InlineData(PageOrientation.Landscape, 600, 800, true)]
    public void Orientation_follows_the_setting_or_the_capture(PageOrientation orientation, int width, int height, bool landscape) =>
        PrintLayout.IsLandscape(orientation, new PixelSize(width, height)).Should().Be(landscape);

    [Fact]
    public void Fit_is_as_large_as_fits_inside_the_margins_and_centred()
    {
        var placement = PrintLayout.Place(A4Width, A4Height, 10, new PixelSize(1000, 500), 1, PrintFit.FitToPage);

        var margin = 10 * PrintLayout.UnitsPerMm;
        placement.Area.Should().Be(new PageRect(margin, margin, A4Width - (2 * margin), A4Height - (2 * margin)));
        placement.Picture.Width.Should().BeApproximately(placement.Area.Width, 1e-9, "a wide picture fills the width");
        placement.Picture.Height.Should().BeApproximately(placement.Area.Width / 2, 1e-9, "and keeps its proportions");
        (placement.Picture.Y + (placement.Picture.Height / 2)).Should().BeApproximately(A4Height / 2, 1e-9);
        placement.Clips.Should().BeFalse();
    }

    [Fact]
    public void Fit_also_enlarges_a_small_capture()
    {
        var placement = PrintLayout.Place(A4Width, A4Height, 0, new PixelSize(100, 100), 1, PrintFit.FitToPage);

        placement.Picture.Width.Should().BeApproximately(A4Width, 1e-9);
        placement.Picture.Height.Should().BeApproximately(A4Width, 1e-9);
    }

    [Fact]
    public void Fill_covers_the_area_and_is_cut_off_evenly()
    {
        var placement = PrintLayout.Place(A4Width, A4Height, 10, new PixelSize(1000, 500), 1, PrintFit.FillPage);

        placement.Picture.Height.Should().BeApproximately(placement.Area.Height, 1e-9);
        placement.Picture.Width.Should().BeApproximately(placement.Area.Height * 2, 1e-9);
        (placement.Area.X - placement.Picture.X).Should().BeApproximately(placement.Picture.Right - placement.Area.Right, 1e-9);
        placement.Clips.Should().BeTrue();
    }

    [Theory]
    [InlineData(1.0, 480, 240)]
    [InlineData(1.5, 320, 160)]
    [InlineData(2.0, 240, 120)]
    public void Actual_size_is_as_big_as_on_the_screen(double scale, double width, double height)
    {
        var placement = PrintLayout.Place(A4Width, A4Height, 10, new PixelSize(480, 240), scale, PrintFit.ActualSize);

        placement.Picture.Width.Should().BeApproximately(width, 1e-9);
        placement.Picture.Height.Should().BeApproximately(height, 1e-9);
        (placement.Picture.X + (width / 2)).Should().BeApproximately(A4Width / 2, 1e-9, "centred when it fits");
        placement.Clips.Should().BeFalse();
    }

    [Fact]
    public void Actual_size_larger_than_the_page_starts_at_the_top_left_margin()
    {
        var placement = PrintLayout.Place(A4Width, A4Height, 10, new PixelSize(3000, 4000), 1, PrintFit.ActualSize);

        placement.Picture.X.Should().BeApproximately(placement.Area.X, 1e-9);
        placement.Picture.Y.Should().BeApproximately(placement.Area.Y, 1e-9);
        placement.Clips.Should().BeTrue();
    }

    [Fact]
    public void Margins_never_leave_less_than_a_third_of_the_page()
    {
        var placement = PrintLayout.Place(300, 300, 1000, new PixelSize(10, 10), 1, PrintFit.FitToPage);

        placement.Area.Should().Be(new PageRect(100, 100, 100, 100));
    }

    [Fact]
    public void A_bad_scale_counts_as_100_percent() =>
        PrintLayout.Place(A4Width, A4Height, 0, new PixelSize(96, 96), double.NaN, PrintFit.ActualSize).Picture.Width.Should().Be(96);
}
