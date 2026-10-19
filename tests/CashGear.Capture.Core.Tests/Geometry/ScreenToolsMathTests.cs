using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Tests.Geometry;

/// <summary>The ruler's marks, the magnifier's lens, and what the tools remember.</summary>
public sealed class ScreenToolsMathTests
{
    [Fact]
    public void Pixels_are_numbered_every_50_with_a_mark_every_5()
    {
        var ticks = RulerScale.Ticks(200, RulerUnit.Pixels, dpi: 96);

        ticks.Where(t => t.Level == 0).Select(t => t.Label).Should().Equal("0", "50", "100", "150", "200");
        ticks.Where(t => t.Level == 1).Select(t => t.Position).Should().Equal(25, 75, 125, 175);
        ticks.Should().HaveCount(41);
        ticks.Select(t => t.Position).Should().BeInAscendingOrder();
    }

    [Fact]
    public void Inches_follow_the_dpi_with_eighths()
    {
        var at96 = RulerScale.Ticks(300, RulerUnit.Inches, dpi: 96);
        at96.Where(t => t.Level == 0).Select(t => t.Position).Should().Equal(0, 96, 192, 288);
        at96.Where(t => t.Level == 0).Select(t => t.Label).Should().Equal("0", "1", "2", "3");
        at96[1].Position.Should().Be(12, "an eighth of an inch at 96 dpi");

        var at144 = RulerScale.Ticks(300, RulerUnit.Inches, dpi: 144);
        at144.Where(t => t.Level == 0).Select(t => t.Position).Should().Equal([0, 144, 288], "the same inch is 1.5 times the pixels at 150%");
    }

    [Fact]
    public void Centimetres_have_millimetres_when_they_are_far_enough_apart()
    {
        var at96 = RulerScale.Ticks(200, RulerUnit.Centimetres, dpi: 96);
        at96.Where(t => t.Level == 0).Select(t => t.Label).Should().Equal("0", "1", "2", "3", "4", "5");
        at96.Where(t => t.Level == 0).Select(t => Math.Round(t.Position, 2)).Should().Equal(0, 37.8, 75.59, 113.39, 151.18, 188.98);
        at96[1].Position.Should().BeApproximately(3.78, 0.01, "a millimetre");

        foreach (var unit in Enum.GetValues<RulerUnit>())
        {
            foreach (var dpi in new[] { 30.0, 60, 96, 120, 144, 192, 300 })
            {
                var ticks = RulerScale.Ticks(1000, unit, dpi);
                ticks.Zip(ticks.Skip(1), (a, b) => b.Position - a.Position).Should().OnlyContain(gap => gap >= RulerScale.MinTickGap, $"{unit} at {dpi} dpi");
                var labels = ticks.Where(t => t.Label is not null).ToList();
                labels.Zip(labels.Skip(1), (a, b) => b.Position - a.Position).Should().OnlyContain(gap => gap >= RulerScale.MinLabelGap, $"{unit} at {dpi} dpi");
            }
        }
    }

    [Theory]
    [InlineData(123.4, RulerUnit.Pixels, 96, "123 px")]
    [InlineData(144, RulerUnit.Inches, 96, "1.50 in")]
    [InlineData(144, RulerUnit.Inches, 144, "1.00 in")]
    [InlineData(96, RulerUnit.Centimetres, 96, "2.54 cm")]
    public void Lengths_read_in_each_unit(double pixels, RulerUnit unit, double dpi, string expected) =>
        RulerScale.Format(pixels, unit, dpi).Should().Be(expected);

    [Fact]
    public void The_lens_shows_an_odd_square_of_pixels_centred_on_the_cursor()
    {
        Magnifier.Source(new PixelPoint(100, 50), lensPixels: 240, zoom: 4).Should().Be(new PixelRect(70, 20, 61, 61));
        Magnifier.Source(new PixelPoint(-5, -5), lensPixels: 240, zoom: 8).Should().Be(new PixelRect(-20, -20, 31, 31));
        Magnifier.Source(new PixelPoint(0, 0), lensPixels: 240, zoom: 99).Width.Should().Be(31, "the zoom is at most 8");
        Magnifier.ClampZoom(1).Should().Be(2);
    }

    [Fact]
    public void The_lens_is_centred_on_the_cursor_and_kept_on_its_monitor()
    {
        var monitor = new PixelRect(-1280, -200, 1280, 1024);
        Magnifier.Lens(new PixelPoint(-640, 300), 240, monitor).Should().Be(new PixelRect(-760, 180, 240, 240));
        Magnifier.Lens(new PixelPoint(-1275, -195), 240, monitor).Should().Be(new PixelRect(-1280, -200, 240, 240));
    }

    [Fact]
    public void The_colour_history_keeps_sixteen_newest_first_once_each()
    {
        var state = new AppState();
        for (var i = 0; i < 20; i++)
        {
            state = state.WithColor($"#0000{i:X2}");
        }

        state = state.WithColor("#000005");
        state.Colors.Should().HaveCount(AppState.MaxColors);
        state.Colors[0].Should().Be("#000005");
        state.Colors[1].Should().Be("#000013");
        state.Colors.Should().OnlyHaveUniqueItems();
    }
}
