using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Region;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Resources;
using CashGear.Capture.Core.Selection;

namespace CashGear.Capture.App.Tests.Region;

/// <summary>The selection overlay for a screen reader (a live text with its label and hint) and under high contrast.</summary>
public sealed class RegionOverlayAccessibilityTests
{
    private static readonly PixelRect Screen = new(0, 0, 1280, 720);
    private static readonly MonitorInfo Monitor = new(0, @"\\.\DISPLAY1", Screen, Screen, 96, 96, IsPrimary: true);

    [Fact]
    public Task The_overlay_is_a_live_text_with_the_size_label() => StaDispatcher.RunAsync(() =>
    {
        var model = new RegionSelectionModel([Screen], [], new PixelPoint(100, 100));
        var visual = Visual(model);
        var peer = UIElementAutomationPeer.CreatePeerForElement(visual);

        peer.GetAutomationControlType().Should().Be(AutomationControlType.Text);
        peer.GetLiveSetting().Should().Be(AutomationLiveSetting.Polite);
        peer.GetName().Should().Be("1280 × 720", "the monitor under the pointer is lit");
        visual.AnnounceIfChanged().Should().BeTrue("the first label is announced");
        visual.AnnounceIfChanged().Should().BeFalse("nothing changed since");

        model.PointerPressed(new PixelPoint(100, 100));
        model.PointerMoved(new PixelPoint(500, 400));
        model.PointerReleased(new PixelPoint(500, 400));
        var selected = model.Selection;
        selected.Width.Should().BeGreaterThan(300);
        peer.GetName().Should().Be($"{selected.Width} × {selected.Height}");
        visual.AnnounceIfChanged().Should().BeTrue("a new size is announced");

        model.KeyPressed(SelectionKey.Right, SelectionModifiers.Shift);
        peer.GetName().Should().Be($"{selected.Width + 1} × {selected.Height}", "Shift+arrow made it a pixel wider");
        return Task.CompletedTask;
    });

    [Fact]
    public Task The_hint_is_read_after_the_label() => StaDispatcher.RunAsync(() =>
    {
        var rounded = new RegionSelectionModel([Screen], [], new PixelPoint(100, 100), mode: SelectionMode.RoundedRectangle, cornerRadius: 12);
        var name = UIElementAutomationPeer.CreatePeerForElement(Visual(rounded)).GetName();
        name.Should().Be(CapturingText.RegionSpoken(rounded.View.Label!, CoreStrings.RoundedRegionHint));
        name.Should().StartWith("1280 × 720").And.EndWith(CoreStrings.RoundedRegionHint);

        var polygon = new ShapeSelectionModel([Screen], new PixelPoint(100, 100), SelectionMode.Polygon);
        UIElementAutomationPeer.CreatePeerForElement(Visual(polygon)).GetName().Should().Be(CoreStrings.PolygonHint, "nothing is lit before the first corner");

        var point = new PointSelectionModel([Screen], new PixelPoint(10, 20));
        UIElementAutomationPeer.CreatePeerForElement(Visual(point)).GetName()
            .Should().Be(CapturingText.RegionSpoken("10, 20   #112233", CoreStrings.ColourPickerHint), "picking a colour, the pixel's place and colour are read");
        return Task.CompletedTask;
    });

    [Fact]
    public void Under_high_contrast_the_overlay_draws_with_the_system_colours()
    {
        try
        {
            HighContrast.Override = true;
            var palette = RegionOverlayPalette.Current;
            Colour(palette.Panel).Should().Be(SystemColors.WindowColor);
            Colour(palette.Text).Should().Be(SystemColors.WindowTextColor);
            Colour(palette.Hint).Should().Be(SystemColors.WindowTextColor);
            Colour(palette.Accent).Should().Be(SystemColors.HighlightColor);
            Colour(palette.Guide).Should().Be(SystemColors.HighlightColor);
            Colour(palette.Ring).Should().Be(SystemColors.HighlightColor);
            Colour(palette.Dim).Should().Be(Color.FromArgb(0x80, 0, 0, 0), "the shade over the screen stays");

            HighContrast.Override = false;
            palette = RegionOverlayPalette.Current;
            Colour(palette.Accent).Should().Be(Color.FromRgb(0x1E, 0x90, 0xFF), "it is read again each time, never kept");
            Colour(palette.Panel).Should().Be(Color.FromArgb(0xE6, 0x1C, 0x1C, 0x1C));
        }
        finally
        {
            HighContrast.Override = null;
        }
    }

    private static RegionOverlayVisual Visual(ISelectionModel model) =>
        new(Screen, [Monitor], Background(), model, _ => 0xFF112233);

    private static BitmapSource Background()
    {
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static Color Colour(Brush brush) => ((SolidColorBrush)brush).Color;
}
