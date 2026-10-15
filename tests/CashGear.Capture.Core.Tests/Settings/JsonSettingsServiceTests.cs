using System.Text.Json;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Core.Tests.Settings;

public sealed class JsonSettingsServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public JsonSettingsServiceTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "logs"), Path.Combine(_root, "captures"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Missing_file_gives_defaults_and_writes_them()
    {
        using var service = Create();

        service.Current.Should().Be(new AppSettings());
        File.Exists(_paths.SettingsFile).Should().BeTrue();
        File.ReadAllText(_paths.SettingsFile).Should().Contain("\"backend\": \"Auto\"");
        File.ReadAllText(_paths.SettingsFile).Should().Contain("\"captureFullScreen\": \"Ctrl+PrintScreen\"", "the file is meant for people: '+' must not be escaped")
            .And.NotContain("doesAnything");
    }

    [Fact]
    public void Defaults_are_sensible()
    {
        var defaults = new AppSettings();

        defaults.Capture.Backend.Should().Be(CaptureBackend.Auto);
        defaults.Capture.HideDelayMs.Should().Be(200);
        defaults.Capture.IncludeCursor.Should().BeFalse();
        defaults.Capture.FullScreenOutput.Should().Be(FullScreenOutput.AllAndEachMonitor);
        defaults.Output.Format.Should().Be(ImageFileFormat.Png);
        defaults.Output.JpegQuality.Should().Be(90);
        defaults.AfterCapture.Should().Be(new AfterCaptureSettings(Save: false, CopyToClipboard: false, OpenEditor: true), "the spec: open the editor by default");
        defaults.Output.FileNameTemplate.Should().Be(CashGear.Capture.Core.Output.FileNameTemplate.Default);
        defaults.Appearance.Theme.Should().Be(AppTheme.System);
        defaults.Capture.DelaySeconds.Should().Be(0);
        defaults.Output.Folder.Should().BeNull();
    }

    [Fact]
    public async Task Update_round_trips_through_a_new_instance()
    {
        using (var first = Create())
        {
            await first.UpdateAsync(
                s => s with
                {
                    Capture = s.Capture with { Backend = CaptureBackend.Gdi, IncludeCursor = true },
                    Output = s.Output with { Format = ImageFileFormat.Jpeg, JpegQuality = 75, Folder = @"D:\Shots" },
                },
                Ct);
        }

        using var second = Create();

        second.Current.Capture.Backend.Should().Be(CaptureBackend.Gdi);
        second.Current.Capture.IncludeCursor.Should().BeTrue();
        second.Current.Output.Format.Should().Be(ImageFileFormat.Jpeg);
        second.Current.Output.JpegQuality.Should().Be(75);
        second.Current.Output.Folder.Should().Be(@"D:\Shots");
    }

    [Fact]
    public void Partial_json_keeps_defaults_for_missing_values()
    {
        WriteFile("""{ "capture": { "includeCursor": true } }""");

        using var service = Create();

        service.Current.Capture.IncludeCursor.Should().BeTrue();
        service.Current.Capture.HideDelayMs.Should().Be(200);
        service.Current.Output.Should().Be(new OutputSettings());
    }

    [Fact]
    public void Lenient_reading_comments_trailing_commas_case_and_unknown_properties()
    {
        WriteFile("""
            {
              // user comment
              "Capture": { "BACKEND": "gdi", "futureSetting": 42, },
              "somethingNew": { "x": 1 },
            }
            """);

        using var service = Create();

        service.Current.Capture.Backend.Should().Be(CaptureBackend.Gdi);
    }

    [Fact]
    public void Null_section_is_replaced_with_defaults()
    {
        WriteFile("""{ "capture": null, "output": null }""");

        using var service = Create();

        service.Current.Capture.Should().Be(new CaptureSettings());
        service.Current.Output.Should().Be(new OutputSettings());
    }

    [Fact]
    public void Out_of_range_values_are_clamped()
    {
        WriteFile("""{ "capture": { "hideDelayMs": -50, "wgcFrameTimeoutMs": 999999 }, "output": { "jpegQuality": 0, "folder": "  " } }""");

        using var service = Create();

        service.Current.Capture.HideDelayMs.Should().Be(0);
        service.Current.Capture.WgcFrameTimeoutMs.Should().Be(CaptureSettings.MaxFrameTimeoutMs);
        service.Current.Output.JpegQuality.Should().Be(1);
        service.Current.Output.Folder.Should().BeNull();
    }

    [Fact]
    public void Corrupt_file_is_backed_up_and_replaced_with_defaults()
    {
        WriteFile("{ this is not json");

        using var service = Create();

        service.Current.Should().Be(new AppSettings());
        var backups = Directory.GetFiles(_paths.SettingsDirectory, "settings.json.corrupt-*");
        backups.Should().ContainSingle();
        File.ReadAllText(backups[0]).Should().Be("{ this is not json");
        JsonDocument.Parse(File.ReadAllText(_paths.SettingsFile)).Should().NotBeNull();
    }

    [Fact]
    public async Task Update_raises_Changed_only_when_something_changed()
    {
        using var service = Create();
        var raised = 0;
        service.Changed += (_, _) => raised++;

        await service.UpdateAsync(s => s, Ct);
        await service.UpdateAsync(s => s with { Capture = s.Capture with { HideDelayMs = 500 } }, Ct);

        raised.Should().Be(1);
        service.Current.Capture.HideDelayMs.Should().Be(500);
    }

    [Fact]
    public async Task Writes_leave_no_temp_file_behind()
    {
        using var service = Create();

        await service.UpdateAsync(s => s with { Output = s.Output with { JpegQuality = 50 } }, Ct);
        await service.SaveAsync(Ct);

        Directory.GetFiles(_paths.SettingsDirectory).Select(Path.GetFileName).Should().Equal("settings.json");
    }

    [Fact]
    public async Task Concurrent_updates_are_serialized()
    {
        using var service = Create();

        await Task.WhenAll(Enumerable.Range(1, 20).Select(i =>
            service.UpdateAsync(s => s with { Capture = s.Capture with { HideDelayMs = s.Capture.HideDelayMs + 1 } }, Ct)));

        service.Current.Capture.HideDelayMs.Should().Be(220);
        using var reloaded = Create();
        reloaded.Current.Capture.HideDelayMs.Should().Be(220);
    }

    [Fact]
    public void Default_capture_directory_is_used_when_folder_unset()
    {
        _paths.ResolveCaptureDirectory(new OutputSettings()).Should().Be(_paths.DefaultCaptureDirectory);
        _paths.ResolveCaptureDirectory(new OutputSettings { Folder = @"E:\x" }).Should().Be(@"E:\x");
    }

    private JsonSettingsService Create() =>
        new(_paths, NullLogger<JsonSettingsService>.Instance, new FixedTimeProvider(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero)));

    private void WriteFile(string json)
    {
        Directory.CreateDirectory(_paths.SettingsDirectory);
        File.WriteAllText(_paths.SettingsFile, json);
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
