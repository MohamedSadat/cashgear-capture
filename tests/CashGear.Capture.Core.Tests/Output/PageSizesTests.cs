using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Tests.Output;

public sealed class PageSizesTests
{
    [Fact]
    public void Paper_is_turned_for_wide_pictures_when_automatic()
    {
        var wide = new PixelSize(300, 100);
        var tall = new PixelSize(100, 300);

        var (width, height) = PageSizes.Page(PaperSize.A4, PageOrientation.Auto, 10, wide, 1);
        var (tallWidth, _) = PageSizes.Page(PaperSize.A4, PageOrientation.Auto, 10, tall, 1);
        var (forced, _) = PageSizes.Page(PaperSize.A4, PageOrientation.Portrait, 10, wide, 1);

        width.Should().BeApproximately(297 * PrintLayout.UnitsPerMm, 1e-9);
        height.Should().BeApproximately(210 * PrintLayout.UnitsPerMm, 1e-9);
        tallWidth.Should().BeApproximately(210 * PrintLayout.UnitsPerMm, 1e-9);
        forced.Should().BeApproximately(210 * PrintLayout.UnitsPerMm, 1e-9);
    }

    [Fact]
    public void A_picture_sized_page_is_the_picture_at_its_scale_plus_the_margins()
    {
        var (width, height) = PageSizes.Page(PaperSize.Picture, PageOrientation.Portrait, 5, new PixelSize(300, 150), 1.5);

        width.Should().BeApproximately(200 + (10 * PrintLayout.UnitsPerMm), 1e-9);
        height.Should().BeApproximately(100 + (10 * PrintLayout.UnitsPerMm), 1e-9);
    }

    [Fact]
    public void Automatic_paper_is_a4_or_letter_and_every_size_is_known()
    {
        PageSizes.ForRegion().Should().BeOneOf(PaperSize.A4, PaperSize.Letter);
        PageSizes.Millimetres(PaperSize.Auto).Should().Be(PageSizes.Millimetres(PageSizes.ForRegion()));
        PageSizes.Millimetres(PaperSize.Letter).Should().Be((215.9, 279.4));
        PageSizes.Millimetres(PaperSize.A3).Should().Be((297, 420));
        var picture = () => PageSizes.Millimetres(PaperSize.Picture);
        picture.Should().Throw<ArgumentOutOfRangeException>();
    }
}
