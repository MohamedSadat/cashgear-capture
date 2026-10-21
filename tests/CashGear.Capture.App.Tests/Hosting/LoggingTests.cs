using System.IO;
using System.Text.Json;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace CashGear.Capture.App.Tests.Hosting;

public sealed partial class LoggingTests
{
    [Fact]
    public void Each_event_is_one_line_of_json_with_the_run_and_its_scopes()
    {
        var lines = Lines(logger =>
        {
            using (LogScopes.Begin(logger, LogScopes.Recording, "3fa9c2d1"))
            {
                LogStarted(logger, "DISPLAY2", 30, "C:\\Videos\\a \"b\".mp4");
            }

            LogStarted(logger, "DISPLAY1", 60, null);
        });

        lines.Should().HaveCount(2);
        using var first = JsonDocument.Parse(lines[0]);
        var root = first.RootElement;
        DateTimeOffset.Parse(root.GetProperty("@t").GetString()!, System.Globalization.CultureInfo.InvariantCulture)
            .Should().BeCloseTo(DateTimeOffset.Now, TimeSpan.FromMinutes(1));
        root.GetProperty("@l").GetString().Should().Be("Information");
        root.GetProperty("@mt").GetString().Should().Be("Recording on {Monitor} at {Fps} fps to {Address}");

        // Strings without quotes in the message, as the text log had them; the values themselves keep their types.
        root.GetProperty("@m").GetString().Should().Be("Recording on DISPLAY2 at 30 fps to C:\\Videos\\a \"b\".mp4");
        root.GetProperty("Address").GetString().Should().Be("C:\\Videos\\a \"b\".mp4");
        root.GetProperty("Monitor").GetString().Should().Be("DISPLAY2");
        root.GetProperty("Fps").GetInt32().Should().Be(30);
        root.GetProperty("Recording").GetString().Should().Be("3fa9c2d1");
        root.GetProperty("Run").GetString().Should().Be(LoggingSetup.RunId).And.MatchRegex("^[0-9a-f]{8}$");
        root.GetProperty("SourceContext").GetString().Should().Be(typeof(LoggingTests).FullName);
        root.GetProperty("ProcessId").GetInt32().Should().Be(Environment.ProcessId);
        root.TryGetProperty("ThreadId", out _).Should().BeTrue();

        // Outside the scope the recording's id is gone.
        using var second = JsonDocument.Parse(lines[1]);
        second.RootElement.TryGetProperty("Recording", out _).Should().BeFalse();
        second.RootElement.GetProperty("Address").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void An_exception_is_written_whole_under_its_own_field()
    {
        var lines = Lines(logger =>
        {
            try
            {
                throw new InvalidOperationException("The disk is full\nsecond line");
            }
            catch (InvalidOperationException e)
            {
                LogFailed(logger, e);
            }
        });

        var line = lines.Should().ContainSingle().Subject;
        using var json = JsonDocument.Parse(line);
        var exception = json.RootElement.GetProperty("@x").GetString();
        exception.Should().StartWith("System.InvalidOperationException: The disk is full\nsecond line").And.Contain(nameof(An_exception_is_written_whole_under_its_own_field));
        json.RootElement.GetProperty("@l").GetString().Should().Be("Error");
    }

    [Fact]
    public void Scope_ids_are_eight_hex_digits_and_differ()
    {
        var ids = Enumerable.Range(0, 50).Select(_ => LogScopes.NewId()).ToList();

        ids.Should().AllSatisfy(id => id.Should().MatchRegex("^[0-9a-f]{8}$"));
        ids.Distinct().Should().HaveCount(50);
    }

    /// <summary>Logs through Microsoft.Extensions.Logging into the app's formatter, as the app does, and returns the lines.</summary>
    private static string[] Lines(Action<Microsoft.Extensions.Logging.ILogger> write)
    {
        var output = new StringWriter();
        using (var serilog = LoggingSetup.Configure(new LoggerConfiguration()).WriteTo.Sink(new FormattedSink(output)).CreateLogger())
        using (var factory = new SerilogLoggerFactory(serilog))
        {
            write(factory.CreateLogger<LoggingTests>());
        }

        var text = output.ToString();
        text.Should().EndWith("\n").And.NotContain("\r");
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private sealed class FormattedSink(TextWriter output) : Serilog.Core.ILogEventSink
    {
        private readonly JsonLinesFormatter _formatter = new();

        public void Emit(Serilog.Events.LogEvent logEvent) => _formatter.Format(logEvent, output);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording on {Monitor} at {Fps} fps to {Address}")]
    private static partial void LogStarted(Microsoft.Extensions.Logging.ILogger logger, string monitor, int fps, string? address);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording can't go on")]
    private static partial void LogFailed(Microsoft.Extensions.Logging.ILogger logger, Exception e);
}
