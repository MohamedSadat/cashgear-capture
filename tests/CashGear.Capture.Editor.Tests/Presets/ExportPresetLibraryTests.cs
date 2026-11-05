using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Presets;

namespace CashGear.Capture.Editor.Tests.Presets;

/// <summary>The export window's presets by name, in presets.json.</summary>
public sealed class ExportPresetLibraryTests
{
    private static NamedExport Named(string name, string preset = "mp4", QualityPreset quality = QualityPreset.High) =>
        new(name.ToLowerInvariant(), name, new ExportChoices(preset, quality));

    [Fact]
    public void A_preset_is_added_replaced_by_its_name_and_removed()
    {
        var library = new PresetLibrary().WithExport(Named("One")).WithExport(Named("Two"));
        library.SavedExports.Select(e => e.Name).Should().Equal("One", "Two");

        library = library.WithExport(Named("ONE", "gif"));
        library.SavedExports.Should().HaveCount(2);
        library.SavedExports[0].Choices.Preset.Should().Be("gif");

        library.WithoutExport("two").SavedExports.Should().ContainSingle();
    }

    [Fact]
    public void They_go_through_the_file_with_Arabic_names_intact()
    {
        var library = new PresetLibrary().WithExport(Named("يوتيوب", "mp4-1080", QualityPreset.Small) with { Choices = new ExportChoices("gif", GifFps: 12, GifMaxMegabytes: 10, SendTo: "youtube") });

        var json = library.ToJson();
        var read = PresetLibrary.FromJson(json);

        json.Should().Contain("يوتيوب");
        read.SavedExports.Should().ContainSingle().Which.Should().Be(library.Normalize().SavedExports[0]);
        read.Should().Be(library.Normalize());
    }

    [Fact]
    public void A_file_from_before_has_none_and_nameless_or_repeated_ones_are_dropped()
    {
        PresetLibrary.FromJson("""{ "effects": [] }""").SavedExports.Should().BeEmpty();

        var cleaned = new PresetLibrary(Exports: [Named(" A "), Named("a"), Named("  ")]).Normalize();
        cleaned.SavedExports.Should().ContainSingle().Which.Name.Should().Be("A");
    }

    [Fact]
    public void An_imported_file_adds_its_presets_to_these()
    {
        var mine = new PresetLibrary().WithExport(Named("Mine")).WithExport(Named("Both", "gif"));
        var theirs = new PresetLibrary().WithExport(Named("Both", "mp4")).WithExport(Named("Theirs"));

        var merged = mine.MergedWith(theirs);

        merged.SavedExports.Select(e => e.Name).Should().BeEquivalentTo("Mine", "Both", "Theirs");
        merged.SavedExports.Single(e => e.Name == "Both").Choices.Preset.Should().Be("mp4");
    }

    [Fact]
    public void Libraries_that_differ_only_in_export_presets_differ()
    {
        new PresetLibrary().WithExport(Named("A")).Normalize().Should().NotBe(new PresetLibrary().Normalize());
    }
}
