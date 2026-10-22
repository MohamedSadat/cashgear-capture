using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Wpf;

namespace CashGear.Capture.App.Tests.Wpf;

/// <summary>The sound level meter for a screen reader and under high contrast.</summary>
public sealed class LevelMeterTests
{
    [Fact]
    public Task A_screen_reader_reads_a_named_read_only_progress_bar_with_the_level_in_dB() => StaDispatcher.RunAsync(() =>
    {
        var meter = new LevelMeter { Peak = 0.8 };
        AutomationProperties.SetName(meter, "Microphone level");

        var peer = UIElementAutomationPeer.CreatePeerForElement(meter);

        peer.Should().BeOfType<LevelMeterAutomationPeer>();
        peer.GetName().Should().Be("Microphone level", "the owner names it");
        peer.GetAutomationControlType().Should().Be(AutomationControlType.ProgressBar);
        peer.GetClassName().Should().Be(nameof(LevelMeter));
        var range = peer.GetPattern(PatternInterface.RangeValue).Should().BeAssignableTo<IRangeValueProvider>().Subject;
        (range.Minimum, range.Maximum, range.IsReadOnly).Should().Be((-60.0, 0.0, true));
        range.Value.Should().BeApproximately(-12, 0.05, "0.8 of the way up a 60 dB meter");
        meter.Peak = 0;
        range.Value.Should().Be(-60, "silence is the floor");
        meter.Peak = 1;
        range.Value.Should().Be(0);
        FluentActions.Invoking(() => range.SetValue(-6)).Should().Throw<InvalidOperationException>();
        return Task.CompletedTask;
    });

    [Fact]
    public Task Under_high_contrast_it_draws_with_the_system_colours_read_when_it_draws() => StaDispatcher.RunAsync(() =>
    {
        try
        {
            HighContrast.Override = true;
            var contrast = LevelMeter.Palette.Current();
            ColorOf(contrast.Track).Should().Be(SystemColors.GrayTextColor);
            new[] { contrast.Low, contrast.Middle, contrast.High }.Select(ColorOf).Should().AllBeEquivalentTo(SystemColors.HighlightColor);
            new[] { contrast.Hold, contrast.HoldHigh, contrast.ClipOn }.Select(ColorOf).Should().AllBeEquivalentTo(SystemColors.WindowTextColor);
            ColorOf(contrast.ClipOff).Should().Be(SystemColors.GrayTextColor);

            // The same meter has its own colours again when high contrast is turned off (ThemeService then refreshes
            // the chrome brushes, which the meter listens to): nothing was kept.
            var meter = new LevelMeter { Peak = 0.5, Rms = 0.5, Width = 100, Height = 10 };
            PixelAt(meter, 10, 5).Should().Be(SystemColors.HighlightColor);
            HighContrast.Override = false;
            ChromeBrushes.Current.Refresh();
            ColorOf(LevelMeter.Palette.Current().Low).Should().Be(Color.FromRgb(0x2E, 0xB8, 0x5C));
            PixelAt(meter, 10, 5).Should().Be(Color.FromRgb(0x2E, 0xB8, 0x5C));
        }
        finally
        {
            HighContrast.Override = null;
            ChromeBrushes.Current.Refresh();
        }

        return Task.CompletedTask;
    });

    private static Color ColorOf(Brush brush) => ((SolidColorBrush)brush).Color;

    private static Color PixelAt(FrameworkElement element, int x, int y)
    {
        element.Measure(new Size(element.Width, element.Height));
        element.Arrange(new Rect(0, 0, element.Width, element.Height));
        var bitmap = new RenderTargetBitmap((int)element.Width, (int)element.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }
}
