using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Tests.Editor;
using CashGear.Capture.App.Tests.Platform;
using CashGear.Capture.App.Tools;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Tests.Capture;

namespace CashGear.Capture.App.Tests.Tools;

/// <summary>The screen tools for a screen reader, from the keyboard and under high contrast (no desktop needed).</summary>
public sealed class ScreenToolAccessibilityTests
{
    [Fact]
    public void Under_high_contrast_the_ruler_draws_with_the_system_colours()
    {
        try
        {
            HighContrast.Override = true;
            var palette = RulerPalette.Current;
            Colour(palette.Body).Should().Be(SystemColors.WindowColor);
            Colour(palette.Ink).Should().Be(SystemColors.WindowTextColor);
            Colour(palette.Marker).Should().Be(SystemColors.HighlightColor);
            Colour(palette.Readout).Should().Be(SystemColors.HighlightColor);
            Colour(palette.ReadoutText).Should().Be(SystemColors.HighlightTextColor);

            HighContrast.Override = false;
            Colour(RulerPalette.Current.Body).Should().Be(Color.FromRgb(0xFF, 0xF2, 0xB3), "it is read again each time, never kept");
        }
        finally
        {
            HighContrast.Override = null;
        }
    }

    [Fact]
    public Task The_ruler_is_a_named_window_whose_value_is_its_length() => StaDispatcher.RunAsync(() =>
    {
        var monitors = new FakeMonitorService(TestMonitors.Create("DISPLAY1", new PixelRect(0, 0, 1920, 1080), primary: true));
        var ruler = new RulerWindow(State(), monitors, new MutableSettings());
        try
        {
            var peer = UIElementAutomationPeer.CreatePeerForElement(ruler);
            peer.GetName().Should().Be("CashGear Capture - ruler");
            var value = (IValueProvider)peer.GetPattern(PatternInterface.Value);
            value.IsReadOnly.Should().BeTrue();
            value.Value.Should().Be("800 px long, across");

            ruler.SetLength(640);
            ruler.Turn();
            value.Value.Should().Be("640 px long, down");
        }
        finally
        {
            ruler.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task The_colour_picker_names_its_buttons_and_swatches_Enter_picks_and_Esc_closes() => StaDispatcher.RunAsync(async () =>
    {
        var picks = 0;
        var vm = new ColorPickerViewModel(new FakeOutputService(), new MutableSettings(), State(), new InlineUiDispatcher(), () =>
        {
            picks++;
            return Task.CompletedTask;
        });
        await vm.PickedAsync(0x12, 0x34, 0x56);
        await vm.PickedAsync(0xAB, 0xCD, 0xEF);
        var window = new ColorPickerWindow(vm, AppTheme.Light);
        var closed = false;
        window.Closed += (_, _) => closed = true;
        var content = TestLayout.Layout(window);

        var buttons = TestLayout.FindAll<Button>(content).ToList();
        var names = buttons.Select(b => UIElementAutomationPeer.CreatePeerForElement(b).GetName()).ToList();
        names.Should().Contain(["Copy HEX", "Copy RGB", "Copy HSL", "Copy C#"], "four Copy buttons, each named for what it copies");
        names.Should().ContainInOrder(["#ABCDEF", "#123456"], "the colours picked before are named by their value");

        var pick = buttons.Single(b => b.IsDefault);
        pick.Command.Execute(null);
        picks.Should().Be(1, "Enter presses Pick from the screen");

        TestKeys.Press(window, Key.Escape).Should().BeTrue();
        closed.Should().BeTrue("Esc closes it");
    });

    private static MemoryState State() => new();

    private static Color Colour(Brush brush) => ((SolidColorBrush)brush).Color;

    /// <summary>The tools' remembered state, in memory (the ruler writes it on every move).</summary>
    private sealed class MemoryState : IAppStateStore
    {
        public AppState Current { get; private set; } = new();

        public Task UpdateAsync(Func<AppState, AppState> update, CancellationToken ct = default)
        {
            Current = update(Current);
            return Task.CompletedTask;
        }
    }
}
