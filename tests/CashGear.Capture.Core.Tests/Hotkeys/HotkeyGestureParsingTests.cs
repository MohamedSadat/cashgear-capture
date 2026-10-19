using CashGear.Capture.Core.Hotkeys;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Tests.Hotkeys;

public sealed class HotkeyGestureParsingTests
{
    [Theory]
    [InlineData("PrintScreen", HotkeyModifiers.None, 0x2C)]
    [InlineData("Alt+PrintScreen", HotkeyModifiers.Alt, 0x2C)]
    [InlineData("Ctrl+PrintScreen", HotkeyModifiers.Control, 0x2C)]
    [InlineData("Ctrl+Shift+PrintScreen", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x2C)]
    [InlineData("shift + ctrl + prtsc", HotkeyModifiers.Control | HotkeyModifiers.Shift, 0x2C)]
    [InlineData("Control+Alt+Shift+F24", HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x87)]
    [InlineData("Win+E", HotkeyModifiers.Win, 'E')]
    [InlineData("Windows+5", HotkeyModifiers.Win, '5')]
    [InlineData("Ctrl+NumPad7", HotkeyModifiers.Control, 0x67)]
    [InlineData("Alt+PgDn", HotkeyModifiers.Alt, 0x22)]
    [InlineData("F1", HotkeyModifiers.None, 0x70)]
    public void Parses(string text, HotkeyModifiers modifiers, uint key)
    {
        HotkeyGesture.TryParse(text, out var gesture).Should().BeTrue();

        gesture.Should().Be(new HotkeyGesture(modifiers, key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Ctrl")]
    [InlineData("Ctrl+")]
    [InlineData("Ctrl++A")]
    [InlineData("Ctrl+Ctrl+A")]
    [InlineData("Hyper+A")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+AB")]
    public void Rejects(string? text) => HotkeyGesture.TryParse(text, out _).Should().BeFalse();

    [Theory]
    [InlineData("prtsc", "PrintScreen")]
    [InlineData("shift+alt+ctrl+win+del", "Ctrl+Alt+Shift+Win+Delete")]
    [InlineData("ctrl+numpad0", "Ctrl+NumPad0")]
    [InlineData("alt+esc", "Alt+Escape")]
    public void Formats_canonically_and_round_trips(string text, string canonical)
    {
        var gesture = HotkeyGesture.Parse(text);

        gesture.ToString().Should().Be(canonical);
        HotkeyGesture.Parse(canonical).Should().Be(gesture);
    }

    [Fact]
    public void Parse_throws_on_invalid_text() =>
        FluentActions.Invoking(() => HotkeyGesture.Parse("Ctrl+")).Should().Throw<FormatException>();

    [Fact]
    public void Default_settings_are_the_spec_hotkeys_and_all_parse()
    {
        var hotkeys = new HotkeySettings();

        hotkeys.For(HotkeyAction.CaptureRegion).Should().Be("PrintScreen");
        hotkeys.For(HotkeyAction.CaptureActiveWindow).Should().Be("Alt+PrintScreen");
        hotkeys.For(HotkeyAction.CaptureFullScreen).Should().Be("Ctrl+PrintScreen");
        hotkeys.For(HotkeyAction.CaptureScrolling).Should().Be("Ctrl+Shift+PrintScreen");
        hotkeys.For(HotkeyAction.RepeatLastCapture).Should().Be("Ctrl+Alt+Shift+PrintScreen");

        // Every hotkey set by default parses; the newer modes start without one (set in Settings).
        var set = Enum.GetValues<HotkeyAction>().Where(a => hotkeys.For(a) is not null).ToList();
        set.Should().AllSatisfy(a => HotkeyGesture.TryParse(hotkeys.For(a), out _).Should().BeTrue());
        set.Should().HaveCount(8);
    }

    [Fact]
    public void Blank_hotkeys_normalize_to_disabled() =>
        new HotkeySettings(CaptureRegion: "  ", CaptureScrolling: " Ctrl+F9 ").Normalize()
            .Should().Be(new HotkeySettings(CaptureRegion: null, CaptureScrolling: "Ctrl+F9"));

    [Theory]
    [InlineData("PrintScreen", true)]
    [InlineData("F9", true)]
    [InlineData("F24", true)]
    [InlineData("Pause", true)]
    [InlineData("ScrollLock", true)]
    [InlineData("A", false)]
    [InlineData("Delete", false)]
    [InlineData("Space", false)]
    [InlineData("Ctrl+A", true)]
    [InlineData("Shift+Delete", true)]
    public void Single_typing_keys_are_not_safe_as_global_hotkeys(string text, bool safe) =>
        HotkeyGesture.Parse(text).IsSafeAsGlobalHotkey.Should().Be(safe);
}
