using CashGear.Capture.Editor.Effects;
using SkiaSharp;

namespace CashGear.Capture.Editor.Presets;

/// <summary>A saved list of effects, applied in one click (Effects menu) or by the batch processor.</summary>
public sealed record EffectPreset(string Name, IReadOnlyList<ImageEffect> Effects)
{
    public bool Equals(EffectPreset? other) =>
        other is not null && Name == other.Name && Effects.SequenceEqual(other.Effects);

    public override int GetHashCode() => HashCode.Combine(Name, Effects.Count);
}

/// <summary>A saved watermark.</summary>
public sealed record WatermarkPreset(string Name, Watermark Watermark);

/// <summary>
/// What <c>presets.json</c> holds: the effect presets, the watermark presets, and the image tools' last choices.
/// Immutable; change it with <see cref="IPresetStore.UpdateAsync"/>.
/// </summary>
public sealed record PresetLibrary(IReadOnlyList<EffectPreset>? Effects = null, IReadOnlyList<WatermarkPreset>? Watermarks = null)
{
    /// <summary>Never null once normalized.</summary>
    public IReadOnlyList<EffectPreset>? Effects { get; init; } = Effects ?? [];

    public IReadOnlyList<WatermarkPreset>? Watermarks { get; init; } = Watermarks ?? [];

    /// <summary>What a new installation starts with.</summary>
    public static PresetLibrary Defaults { get; } = new(
        Effects:
        [
            new("Shadow", [new ShadowEffect()]),
            new("Torn bottom with a shadow", [new TornEdgeEffect(), new ShadowEffect()]),
            new("Rounded with a shadow", [new RoundedCornersEffect(), new ShadowEffect()]),
            new("Thin border and a shadow", [new BorderEffect(1, new SKColor(0xA0, 0xA0, 0xA0)), new ShadowEffect()]),
        ],
        Watermarks:
        [
            new("Confidential, across the image", new Watermark(
                Text: "CONFIDENTIAL", FontSize: 48, Color: new SKColor(0x80, 0x80, 0x80), Opacity: 25, Rotation: -30, Tile: true, Spacing: 120, Shadow: false)),
        ]);

    public IReadOnlyList<EffectPreset> EffectPresets => Effects ?? [];

    public IReadOnlyList<WatermarkPreset> WatermarkPresets => Watermarks ?? [];

    public EffectPreset? FindEffects(string? name) => EffectPresets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public WatermarkPreset? FindWatermark(string? name) => WatermarkPresets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A copy with <paramref name="preset"/> added, or replacing the one with its name.</summary>
    public PresetLibrary WithEffects(EffectPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return this with { Effects = Replace(EffectPresets, preset, p => p.Name) };
    }

    public PresetLibrary WithoutEffects(string name) =>
        this with { Effects = [.. EffectPresets.Where(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))] };

    public PresetLibrary WithWatermark(WatermarkPreset preset)
    {
        ArgumentNullException.ThrowIfNull(preset);
        return this with { Watermarks = Replace(WatermarkPresets, preset, p => p.Name) };
    }

    public PresetLibrary WithoutWatermark(string name) =>
        this with { Watermarks = [.. WatermarkPresets.Where(p => !string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))] };

    /// <summary>
    /// Names trimmed; presets without a name or without effects dropped; for a name used twice (in any case), the
    /// first kept; every value in range.
    /// </summary>
    public PresetLibrary Normalize() => this with
    {
        Effects = [.. Unique(
            EffectPresets
                .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new EffectPreset(p.Name.Trim(), [.. (p.Effects ?? []).Where(e => e is not null).Select(e => e.Normalize())]))
                .Where(p => p.Effects.Count > 0),
            p => p.Name)],
        Watermarks = [.. Unique(
            WatermarkPresets
                .Where(p => p is not null && !string.IsNullOrWhiteSpace(p.Name))
                .Select(p => new WatermarkPreset(p.Name.Trim(), (p.Watermark ?? new Watermark()).Normalize())),
            p => p.Name)],
    };

    public bool Equals(PresetLibrary? other) =>
        other is not null && EffectPresets.SequenceEqual(other.EffectPresets) && WatermarkPresets.SequenceEqual(other.WatermarkPresets);

    public override int GetHashCode() => HashCode.Combine(EffectPresets.Count, WatermarkPresets.Count);

    private static List<T> Replace<T>(IReadOnlyList<T> list, T item, Func<T, string> name)
    {
        var copy = list.ToList();
        var index = copy.FindIndex(p => string.Equals(name(p), name(item), StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            copy[index] = item;
        }
        else
        {
            copy.Add(item);
        }

        return copy;
    }

    private static IEnumerable<T> Unique<T>(IEnumerable<T> items, Func<T, string> name) =>
        items.DistinctBy(name, StringComparer.OrdinalIgnoreCase);
}
