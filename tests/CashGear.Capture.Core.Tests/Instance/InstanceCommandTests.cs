using CashGear.Capture.Core.Hotkeys;
using CashGear.Capture.Core.Instance;

namespace CashGear.Capture.Core.Tests.Instance;

public sealed class InstanceCommandTests
{
    [Theory]
    [InlineData(new string[0], InstanceCommand.Activate)]
    [InlineData(new[] { "--exit" }, InstanceCommand.Exit)]
    [InlineData(new[] { "/EXIT" }, InstanceCommand.Exit)]
    [InlineData(new[] { "--capture-fullscreen" }, InstanceCommand.CaptureFullScreen)]
    [InlineData(new[] { "--unknown", "-capture-fullscreen" }, InstanceCommand.CaptureFullScreen)]
    [InlineData(new[] { "--exit", "--capture-fullscreen" }, InstanceCommand.Exit)]
    [InlineData(new[] { "--activate" }, InstanceCommand.Activate)]
    [InlineData(new[] { "--capture-region" }, InstanceCommand.CaptureRegion)]
    [InlineData(new[] { "--capture-window" }, InstanceCommand.CaptureWindow)]
    [InlineData(new[] { "--capture-scrolling" }, InstanceCommand.CaptureScrolling)]
    [InlineData(new[] { "--record" }, InstanceCommand.Record)]
    [InlineData(new[] { "--capture-text" }, InstanceCommand.CaptureText)]
    [InlineData(new[] { "--capture-fixed" }, InstanceCommand.CaptureFixedRegion)]
    [InlineData(new[] { "--capture-freehand" }, InstanceCommand.CaptureFreehand)]
    [InlineData(new[] { "--capture-polygon" }, InstanceCommand.CapturePolygon)]
    [InlineData(new[] { "--capture-rounded" }, InstanceCommand.CaptureRoundedRegion)]
    [InlineData(new[] { "--repeat-last" }, InstanceCommand.RepeatLastCapture)]
    public void FromArgs(string[] args, InstanceCommand expected) =>
        InstanceCommandParser.FromArgs(args).Should().Be(expected);

    [Fact]
    public void Wire_format_round_trips_for_every_command()
    {
        foreach (var command in Enum.GetValues<InstanceCommand>())
        {
            InstanceCommandParser.TryParseWire(InstanceCommandParser.ToWire(command) + "\r\n", out var parsed).Should().BeTrue();
            parsed.Should().Be(command);
        }

        InstanceCommandParser.Switches.Should().OnlyHaveUniqueItems().And.HaveCount(Enum.GetValues<InstanceCommand>().Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("format c:")]
    public void Unknown_wire_text_is_rejected(string? text) =>
        InstanceCommandParser.TryParseWire(text, out _).Should().BeFalse();
}

public sealed class HotkeyGestureTests
{
    [Fact]
    public void Modifier_values_match_win32_MOD_flags()
    {
        ((int)HotkeyModifiers.Alt).Should().Be(0x1);
        ((int)HotkeyModifiers.Control).Should().Be(0x2);
        ((int)HotkeyModifiers.Shift).Should().Be(0x4);
        ((int)HotkeyModifiers.Win).Should().Be(0x8);
    }

    [Fact]
    public void Display_text()
    {
        new HotkeyGesture(HotkeyModifiers.Control | HotkeyModifiers.Shift, HotkeyGesture.VkSnapshot)
            .ToString().Should().Be("Ctrl+Shift+PrintScreen");
        new HotkeyGesture(HotkeyModifiers.None, HotkeyGesture.VkSnapshot).ToString().Should().Be("PrintScreen");
    }
}
