using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CashGear.Capture.App.Platform;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Platform;

public sealed class HighContrastTests
{
    // HighContrast.Override flows with each test's own async context, so these tests don't affect others running
    // beside them.
    [Fact]
    public void Chrome_brushes_become_system_colours_under_high_contrast()
    {
        HighContrast.Override = false;
        Colour(ChromeBrushes.Current.Error).Should().Be(Color.FromRgb(0xD1, 0x34, 0x38));
        Colour(ChromeBrushes.Current.Record).Should().Be(Color.FromRgb(0xE8, 0x11, 0x23));

        HighContrast.Override = true;
        Colour(ChromeBrushes.Current.Error).Should().Be(SystemColors.WindowTextColor);
        Colour(ChromeBrushes.Current.Separator).Should().Be(SystemColors.WindowTextColor);
        Colour(ChromeBrushes.Current.Record).Should().Be(SystemColors.HighlightColor);
        Colour(ChromeBrushes.Current.Idle).Should().Be(SystemColors.GrayTextColor);
        Colour(ChromeBrushes.Current.Preview).Should().Be(SystemColors.WindowColor);
        HighContrast.Pick(SkiaSharp.SKColors.Red, () => SystemColors.WindowColor)
            .Should().Be(HighContrast.ToSkia(SystemColors.WindowColor));
    }

    [Fact]
    public async Task A_chrome_brush_in_xaml_follows_a_switch()
    {
        await StaDispatcher.RunAsync(() =>
        {
            HighContrast.Override = false;
            var text = new TextBlock();
            text.SetValue(TextBlock.ForegroundProperty, new ChromeExtension("Error").ProvideValue(new Services(text, TextBlock.ForegroundProperty)));
            Colour(text.Foreground).Should().Be(Color.FromRgb(0xD1, 0x34, 0x38));

            HighContrast.Override = true;
            ChromeBrushes.Current.Refresh();
            text.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
            Colour(text.Foreground).Should().Be(SystemColors.WindowTextColor);
            return Task.CompletedTask;
        });
    }

    [Fact]
    public void Turning_high_contrast_on_rethemes_and_refreshes_the_chrome()
    {
        HighContrast.Override = false;
        using var themes = new ThemeService(new MutableSettings(), new InlineUiDispatcher(), NullLogger<ThemeService>.Instance, () => false);
        var changes = 0;
        var refreshed = 0;
        themes.ThemeChanged += (_, _) => changes++;
        PropertyChangedEventHandler onRefresh = (_, _) => refreshed++;
        ChromeBrushes.Current.PropertyChanged += onRefresh;
        try
        {
            themes.Refresh();
            changes.Should().Be(0);

            HighContrast.Override = true;
            themes.Refresh();
            changes.Should().Be(1);
            refreshed.Should().BeGreaterThanOrEqualTo(1);
        }
        finally
        {
            ChromeBrushes.Current.PropertyChanged -= onRefresh;
        }
    }

    private static Color Colour(Brush brush) => ((SolidColorBrush)brush).Color;

    /// <summary>What a markup extension needs to know about where it is used.</summary>
    private sealed class Services(DependencyObject target, DependencyProperty property) : IServiceProvider, System.Windows.Markup.IProvideValueTarget
    {
        public object TargetObject => target;

        public object TargetProperty => property;

        public object? GetService(Type serviceType) => serviceType == typeof(System.Windows.Markup.IProvideValueTarget) ? this : null;
    }
}
