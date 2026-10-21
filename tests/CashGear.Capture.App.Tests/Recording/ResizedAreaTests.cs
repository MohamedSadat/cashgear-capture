using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Recording;

namespace CashGear.Capture.App.Tests.Recording;

public sealed class ResizedAreaTests
{
    [Fact]
    public void A_whole_monitor_recording_follows_the_monitor_to_its_new_size()
    {
        ResizedArea.Crop(new PixelRect(0, 0, 1920, 1080), 1280, 1024, wholeMonitor: true).Should().Be(new PixelRect(0, 0, 1280, 1024));
        ResizedArea.Crop(new PixelRect(0, 0, 1280, 1024), 2560, 1440, wholeMonitor: true).Should().Be(new PixelRect(0, 0, 2560, 1440));
    }

    [Fact]
    public void An_area_keeps_its_place_and_is_cut_to_what_is_left_with_an_even_size()
    {
        var area = new PixelRect(100, 50, 800, 600);

        ResizedArea.Crop(area, 2560, 1440, wholeMonitor: false).Should().Be(area, "it still fits");
        ResizedArea.Crop(area, 801, 601, wholeMonitor: false).Should().Be(new PixelRect(100, 50, 700, 550));
        ResizedArea.Crop(area, 619, 400, wholeMonitor: false).Should().Be(new PixelRect(100, 50, 518, 350), "odd sides are made even");
    }

    [Fact]
    public void An_area_left_off_the_monitor_can_not_go_on()
    {
        ResizedArea.Crop(new PixelRect(1500, 900, 300, 200), 1280, 1024, wholeMonitor: false).Should().BeNull();
        ResizedArea.Crop(new PixelRect(1270, 0, 300, 200), 1280, 1024, wholeMonitor: false).Should().BeNull("10 px is too small");
    }
}
