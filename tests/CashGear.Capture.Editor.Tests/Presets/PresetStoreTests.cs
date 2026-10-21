using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Effects;
using CashGear.Capture.Editor.Presets;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.Editor.Tests.Presets;

public sealed class PresetStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", "presets-" + Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public PresetStoreTests()
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

    private JsonPresetStore Store() => new(_paths, NullLogger<JsonPresetStore>.Instance);

    /// <summary>Every kind of effect, with values other than the defaults.</summary>
    private static IReadOnlyList<ImageEffect> EveryEffect() =>
    [
        new ShadowEffect(5, -3, 7, 30, new SKColor(0x10, 0x20, 0x30, 0x80)),
        new BorderEffect(2, SKColors.Blue),
        new TornEdgeEffect(Edges.Top | Edges.Left, 8, 30, 42),
        new FadeEdgeEffect(Edges.Bottom, 25),
        new RoundedCornersEffect(9),
        new PerspectiveEffect(-15, PerspectiveAxis.Vertical),
        new AdjustEffect(10, -20),
        new GrayscaleEffect(),
        new SharpenEffect(70),
        new WatermarkEffect(new Watermark(Text: "سري", Color: SKColors.Red, Position: Anchor.Top, Tile: true, Rotation: 15)),
    ];

    [Fact]
    public void Without_a_file_the_built_in_presets_are_offered_and_nothing_is_written()
    {
        var store = Store();

        store.Current.EffectPresets.Should().NotBeEmpty();
        store.Current.WatermarkPresets.Should().ContainSingle();
        File.Exists(_paths.PresetsFile).Should().BeFalse();
    }

    [Fact]
    public async Task Every_effect_and_watermark_survives_a_restart()
    {
        var store = Store();
        var changed = 0;
        store.Changed += (_, _) => changed++;
        var effects = new EffectPreset("Everything", EveryEffect());
        var watermark = new WatermarkPreset("Logo", new Watermark(Kind: WatermarkKind.Image, ImagePath: @"C:\logo.png", Scale: 40));

        await store.UpdateAsync(p => p.WithEffects(effects).WithWatermark(watermark), TestContext.Current.CancellationToken);

        changed.Should().Be(1);
        var reloaded = Store().Current;
        reloaded.FindEffects("everything").Should().Be(effects);
        reloaded.FindWatermark("Logo").Should().Be(watermark);
        reloaded.Should().Be(store.Current);
    }

    [Fact]
    public async Task The_file_is_readable_json_with_a_type_for_each_effect()
    {
        var store = Store();

        await store.UpdateAsync(p => p.WithEffects(new EffectPreset("Mine", EveryEffect())), TestContext.Current.CancellationToken);

        var json = await File.ReadAllTextAsync(_paths.PresetsFile, TestContext.Current.CancellationToken);
        json.Should().Contain("\"type\": \"shadow\"").And.Contain("\"type\": \"watermark\"").And.Contain("\"type\": \"tornEdge\"");
        json.Should().Contain("\"#80102030\"").And.Contain("\"#0000FF\"");
        json.Should().Contain("\"edges\": \"Top, Left\"");
        json.Should().Contain("سري", "Arabic stays readable in the file");
    }

    [Fact]
    public void A_hand_edited_file_with_the_type_last_and_comments_is_read()
    {
        Directory.CreateDirectory(_paths.SettingsDirectory);
        File.WriteAllText(_paths.PresetsFile, """
            {
              // edited by hand
              "effects": [
                { "name": "Soft", "effects": [ { "blur": 20, "type": "shadow" }, { "type": "grayscale" }, ] }
              ]
            }
            """);

        var library = Store().Current;

        library.FindEffects("Soft")!.Effects.Should().Equal(new ShadowEffect(Blur: 20), new GrayscaleEffect());
        library.WatermarkPresets.Should().BeEmpty("the file says which presets there are");
    }

    [Fact]
    public void A_damaged_file_is_kept_aside_and_the_built_ins_are_used()
    {
        Directory.CreateDirectory(_paths.SettingsDirectory);
        File.WriteAllText(_paths.PresetsFile, "{ \"effects\": [ { \"name\": \"x\", \"effects\": [ { \"type\": \"nonsense\" } ] } ] }");

        var library = Store().Current;

        library.Should().Be(PresetLibrary.Defaults.Normalize());
        File.Exists(_paths.PresetsFile).Should().BeFalse();
        Directory.GetFiles(_paths.SettingsDirectory, "presets.json.corrupt-*").Should().ContainSingle();
    }

    [Fact]
    public void Names_are_cleaned_and_empty_or_repeated_presets_dropped()
    {
        var library = new PresetLibrary(
            Effects:
            [
                new(" Shadow ", [new ShadowEffect(Blur: 999)]),
                new("shadow", [new GrayscaleEffect()]),
                new("  ", [new GrayscaleEffect()]),
                new("Nothing", []),
            ],
            Watermarks: [new("Mark", new Watermark(Opacity: 200)), new("MARK", new Watermark())]);

        var clean = library.Normalize();

        clean.EffectPresets.Should().ContainSingle().Which.Should().Be(new EffectPreset("Shadow", [new ShadowEffect(Blur: ShadowEffect.MaxBlur)]));
        clean.WatermarkPresets.Should().ContainSingle().Which.Watermark.Opacity.Should().Be(100);
    }

    [Fact]
    public async Task Presets_are_replaced_by_name_and_deleted()
    {
        var store = Store();
        await store.UpdateAsync(p => p.WithEffects(new EffectPreset("Mine", [new GrayscaleEffect()])), TestContext.Current.CancellationToken);

        await store.UpdateAsync(p => p.WithEffects(new EffectPreset("MINE", [new SharpenEffect()])), TestContext.Current.CancellationToken);
        store.Current.EffectPresets.Count(e => e.Name.Equals("mine", StringComparison.OrdinalIgnoreCase)).Should().Be(1);
        store.Current.FindEffects("mine")!.Effects.Should().Equal(new SharpenEffect());

        await store.UpdateAsync(p => p.WithoutEffects("mine"), TestContext.Current.CancellationToken);
        store.Current.FindEffects("mine").Should().BeNull();
        Store().Current.FindEffects("mine").Should().BeNull();
    }
}
