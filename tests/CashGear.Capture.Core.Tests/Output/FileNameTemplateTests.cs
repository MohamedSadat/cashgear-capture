using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Output;

namespace CashGear.Capture.Core.Tests.Output;

public sealed class FileNameTemplateTests
{
    private static readonly DateTimeOffset Time = new(2026, 3, 7, 9, 5, 4, TimeSpan.FromHours(3));

    [Fact]
    public void Default_template_for_each_kind()
    {
        Expand(FileNameTemplate.Default, CaptureKind.AllMonitors).Should().Be("Capture_2026-03-07_09-05-04_fullscreen");
        Expand(FileNameTemplate.Default, CaptureKind.Monitor, "DISPLAY1").Should().Be("Capture_2026-03-07_09-05-04_monitor");
        Expand(FileNameTemplate.Default, CaptureKind.Region).Should().Be("Capture_2026-03-07_09-05-04_region");
        Expand(FileNameTemplate.Default, CaptureKind.Window, window: "Notepad").Should().Be("Capture_2026-03-07_09-05-04_window");
    }

    [Fact]
    public void Every_date_and_time_token_is_zero_padded_and_case_sensitive() =>
        Expand("{yyyy}{MM}{dd}-{HH}{mm}{ss}", CaptureKind.Region).Should().Be("20260307-090504");

    [Theory]
    [InlineData(CaptureKind.AllMonitors, null, "all")]
    [InlineData(CaptureKind.Monitor, "DISPLAY2", "DISPLAY2")]
    [InlineData(CaptureKind.Region, "DISPLAY1", "DISPLAY1")]
    [InlineData(CaptureKind.Region, null, "multi")]
    public void Monitor_token(CaptureKind kind, string? monitor, string expected) =>
        Expand("{monitor}", kind, monitor).Should().Be(expected);

    [Fact]
    public void Window_title_is_sanitized()
    {
        Expand("{window}", CaptureKind.Window, window: "*Untitled - Notepad").Should().Be("Untitled - Notepad");
        Expand("shot {window}", CaptureKind.Window, window: "C:\\data\\report?.txt <draft>").Should().Be("shot C_data_report_.txt _draft");
    }

    [Fact]
    public void Empty_window_token_does_not_leave_doubled_separators() =>
        Expand("Capture_{window}_{HH}", CaptureKind.Region).Should().Be("Capture_09");

    [Fact]
    public void Counter_is_four_digits_and_only_drawn_when_used()
    {
        var calls = 0;
        int Next() => ++calls + 41;

        FileNameTemplate.Expand("n{counter}", Context(CaptureKind.Region), Next).Should().Be("n0042");
        FileNameTemplate.Expand(FileNameTemplate.Default, Context(CaptureKind.Region), Next);

        calls.Should().Be(1, "a template without {counter} must not advance it");
        FileNameTemplate.Expand("{counter}", Context(CaptureKind.Region), () => 123456).Should().Be("123456");
    }

    [Fact]
    public void Unknown_tokens_are_kept_literally() =>
        Expand("a{nope}b", CaptureKind.Region).Should().Be("a{nope}b");

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul.backup", "_nul.backup")]
    [InlineData("   ", "Capture")]
    [InlineData("...", "Capture")]
    [InlineData("a/b\\c:d", "a_b_c_d")]
    [InlineData("  trailing dots...  ", "trailing dots")]
    public void Sanitize(string input, string expected) => FileNameTemplate.Sanitize(input).Should().Be(expected);

    [Fact]
    public void Long_names_are_capped()
    {
        var name = FileNameTemplate.Sanitize(new string('x', 400));

        name.Length.Should().Be(FileNameTemplate.MaxLength);
    }

    [Theory]
    [InlineData(FileNameTemplate.Default)]
    [InlineData("{counter}")]
    [InlineData("plain name")]
    [InlineData("{yyyy}{MM}{dd}{HH}{mm}{ss}{kind}{monitor}{window}")]
    public void Valid_templates(string template)
    {
        FileNameTemplate.TryValidate(template, out var error).Should().BeTrue();
        error.Should().BeNull();
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("  ", "empty")]
    [InlineData("a{yyyy", "never closed")]
    [InlineData("a}b", "Unexpected '}'")]
    [InlineData("{YYYY}", "Unknown token {YYYY}")]
    [InlineData("{date}", "Unknown token {date}")]
    public void Invalid_templates(string template, string message)
    {
        FileNameTemplate.TryValidate(template, out var error).Should().BeFalse();
        error.Should().Contain(message);
    }

    [Fact]
    public void Uses_detects_tokens() =>
        FileNameTemplate.Uses("x_{monitor}", "monitor").Should().BeTrue();

    private static string Expand(string template, CaptureKind kind, string? monitor = null, string? window = null) =>
        FileNameTemplate.Expand(template, Context(kind, monitor, window));

    private static FileNameContext Context(CaptureKind kind, string? monitor = null, string? window = null) =>
        new(Time, kind, monitor, window);
}
