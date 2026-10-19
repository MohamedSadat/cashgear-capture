using CashGear.Capture.Core.Imaging;

namespace CashGear.Capture.Core.Tests.Imaging;

public sealed class ColorFormatsTests
{
    [Theory]
    [InlineData(ColorFormat.Hex, "#1E90FF")]
    [InlineData(ColorFormat.Rgb, "rgb(30, 144, 255)")]
    [InlineData(ColorFormat.Hsl, "hsl(210, 100%, 56%)")]
    [InlineData(ColorFormat.CSharp, "Color.FromArgb(255, 30, 144, 255)")]
    public void Dodger_blue_in_every_format(ColorFormat format, string expected) =>
        ColorFormats.Format(format, 30, 144, 255).Should().Be(expected);

    [Theory]
    [InlineData(255, 0, 0, 0, 100, 50)]
    [InlineData(0, 255, 0, 120, 100, 50)]
    [InlineData(0, 0, 255, 240, 100, 50)]
    [InlineData(255, 255, 255, 0, 0, 100)]
    [InlineData(0, 0, 0, 0, 0, 0)]
    [InlineData(128, 128, 128, 0, 0, 50)]
    [InlineData(255, 0, 128, 330, 100, 50)]
    [InlineData(51, 102, 153, 210, 50, 40)]
    public void Hsl_matches_the_css_definition(byte r, byte g, byte b, int h, int s, int l) =>
        ColorFormats.ToHsl(r, g, b).Should().Be((h, s, l));

    [Fact]
    public void Hex_reads_back_with_or_without_the_hash()
    {
        ColorFormats.TryParseHex("#1E90FF", out var color).Should().BeTrue();
        color.Should().Be(((byte)30, (byte)144, (byte)255));
        ColorFormats.TryParseHex(" 00ff00 ", out color).Should().BeTrue();
        color.Should().Be(((byte)0, (byte)255, (byte)0));
        ColorFormats.TryParseHex("#12345", out _).Should().BeFalse();
        ColorFormats.TryParseHex("#GGGGGG", out _).Should().BeFalse();
        ColorFormats.TryParseHex(null, out _).Should().BeFalse();
    }
}
