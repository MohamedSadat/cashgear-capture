using System.Text.Json.Nodes;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Core.Tests.Settings;

public sealed class SettingsMigrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public SettingsMigrationTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "data", "logs"), Path.Combine(_root, "captures"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Version_1_copy_to_clipboard_moves_to_the_workflow()
    {
        var root = (JsonObject)JsonNode.Parse("""{ "schemaVersion": 1, "output": { "format": "Jpeg", "copyToClipboard": false } }""")!;

        SettingsMigration.Upgrade(root).Should().Be(1);

        root["output"]!.AsObject().ContainsKey("copyToClipboard").Should().BeFalse();
        Steps(root).Should().Contain(("clipboard", false));
        root["output"]!["format"]!.GetValue<string>().Should().Be("Jpeg", "unrelated values are untouched");
    }

    [Fact]
    public void A_version_1_file_keeps_saving_and_copying_without_the_editor()
    {
        var root = (JsonObject)JsonNode.Parse("""{ "schemaVersion": 1, "output": { "format": "Png" } }""")!;

        SettingsMigration.Upgrade(root);

        Steps(root).Should().Equal([("file", true), ("clipboard", true), ("editor", false)], "version 1 had no editor; the new default must not switch it on");
    }

    [Fact]
    public void Missing_schema_version_counts_as_1_and_keys_match_case_insensitively()
    {
        var root = (JsonObject)JsonNode.Parse("""{ "Output": { "CopyToClipboard": false } }""")!;

        SettingsMigration.Upgrade(root).Should().Be(1);

        Steps(root).Should().Contain(("clipboard", false));
    }

    [Fact]
    public void An_explicit_after_capture_value_wins()
    {
        var root = (JsonObject)JsonNode.Parse("""{ "output": { "copyToClipboard": false }, "afterCapture": { "copyToClipboard": true } }""")!;

        SettingsMigration.Upgrade(root);

        Steps(root).Should().Contain(("clipboard", true));
    }

    [Fact]
    public void Current_version_is_left_alone()
    {
        var json = """{"schemaVersion":2,"output":{"copyToClipboard":false}}""";
        var root = (JsonObject)JsonNode.Parse(json)!;

        SettingsMigration.Upgrade(root).Should().Be(2);

        root.ToJsonString().Should().Be(json);
    }

    [Fact]
    public void Old_file_is_upgraded_on_load_and_rewritten()
    {
        Directory.CreateDirectory(_paths.SettingsDirectory);
        File.WriteAllText(_paths.SettingsFile, """{ "schemaVersion": 1, "capture": { "hideDelayMs": 300 }, "output": { "copyToClipboard": false } }""");

        using var service = new JsonSettingsService(_paths, NullLogger<JsonSettingsService>.Instance, TimeProvider.System, watchForExternalChanges: false);

        service.Current.Destinations.IsEnabled(DestinationIds.Clipboard).Should().BeFalse();
        service.Current.Destinations.IsEnabled(DestinationIds.File).Should().BeTrue();
        service.Current.Capture.HideDelayMs.Should().Be(300);
        var rewritten = File.ReadAllText(_paths.SettingsFile);
        rewritten.Should().Contain("\"schemaVersion\": 5").And.Contain("\"workflow\"").And.NotContain("\"afterCapture\"").And.NotContain("\"output\": {\n    \"copyToClipboard\"");
        JsonNode.Parse(rewritten)!["output"]!.AsObject().ContainsKey("copyToClipboard").Should().BeFalse();
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public void Version_4_switches_become_the_workflow_in_the_order_they_ran(bool save, bool copy, bool editor)
    {
        var root = (JsonObject)JsonNode.Parse(
            $$"""{ "schemaVersion": 4, "afterCapture": { "save": {{Json(save)}}, "copyToClipboard": {{Json(copy)}}, "openEditor": {{Json(editor)}} } }""")!;

        SettingsMigration.Upgrade(root).Should().Be(4);

        root.ContainsKey("afterCapture").Should().BeFalse();
        Steps(root).Should().Equal([("file", save), ("clipboard", copy), ("editor", editor)]);
    }

    [Fact]
    public void A_version_4_file_reads_back_doing_exactly_what_it_did()
    {
        Directory.CreateDirectory(_paths.SettingsDirectory);
        File.WriteAllText(_paths.SettingsFile, """{ "schemaVersion": 4, "afterCapture": { "save": true, "openEditor": false } }""");

        using var service = new JsonSettingsService(_paths, NullLogger<JsonSettingsService>.Instance, TimeProvider.System, watchForExternalChanges: false);

        var destinations = service.Current.Destinations;
        destinations.IsEnabled(DestinationIds.File).Should().BeTrue();
        destinations.IsEnabled(DestinationIds.Clipboard).Should().BeFalse("a missing switch had its default (off)");
        destinations.IsEnabled(DestinationIds.Editor).Should().BeFalse();
        destinations.DoesAnything.Should().BeTrue();
        destinations.RecordingWorkflow.Should().BeEmpty();
    }

    [Fact]
    public void A_workflow_already_in_the_file_wins_over_the_old_switches()
    {
        var root = (JsonObject)JsonNode.Parse(
            """{ "schemaVersion": 4, "afterCapture": { "save": true }, "destinations": { "workflow": [ { "destination": "printer" } ] } }""")!;

        SettingsMigration.Upgrade(root);

        root.ContainsKey("afterCapture").Should().BeFalse();
        root["destinations"]!["workflow"]!.AsArray().Should().ContainSingle();
    }

    [Fact]
    public void Without_old_switches_the_default_workflow_stays()
    {
        var root = (JsonObject)JsonNode.Parse("""{ "schemaVersion": 4 }""")!;

        SettingsMigration.Upgrade(root);

        root.ContainsKey("destinations").Should().BeFalse("nothing to carry over, so the new default applies");
    }

    private static string Json(bool value) => value ? "true" : "false";

    /// <summary>The migrated workflow as (destination, enabled) pairs.</summary>
    private static List<(string, bool)> Steps(JsonObject root) =>
        [.. root["destinations"]!["workflow"]!.AsArray().Select(s => (s!["destination"]!.GetValue<string>(), s["enabled"]!.GetValue<bool>()))];
}

public sealed class CaptureCounterTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Counts_up_and_survives_a_restart()
    {
        var paths = Paths();
        new FileCaptureCounter(paths, NullLogger<FileCaptureCounter>.Instance).Next().Should().Be(1);
        new FileCaptureCounter(paths, NullLogger<FileCaptureCounter>.Instance).Next().Should().Be(2);

        var counter = new FileCaptureCounter(paths, NullLogger<FileCaptureCounter>.Instance);
        counter.Next().Should().Be(3);
        counter.Next().Should().Be(4);
        File.ReadAllText(Path.Combine(paths.DataDirectory, "counter.txt")).Should().Be("4");
    }

    [Fact]
    public void A_damaged_counter_file_restarts_at_1()
    {
        var paths = Paths();
        Directory.CreateDirectory(paths.DataDirectory);
        File.WriteAllText(Path.Combine(paths.DataDirectory, "counter.txt"), "not a number");

        new FileCaptureCounter(paths, NullLogger<FileCaptureCounter>.Instance).Next().Should().Be(1);
    }

    [Fact]
    public void Data_directory_defaults_to_the_parent_of_the_log_directory()
    {
        var paths = Paths();

        paths.DataDirectory.Should().Be(Path.Combine(_root, "data"));
        (paths with { DataDirectoryOverride = @"X:\state" }).DataDirectory.Should().Be(@"X:\state");
    }

    private AppPaths Paths() => new(Path.Combine(_root, "s"), Path.Combine(_root, "data", "logs"), Path.Combine(_root, "c"));
}
