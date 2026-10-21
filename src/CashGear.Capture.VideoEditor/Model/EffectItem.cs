using System.Text.Json.Serialization;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.VideoEditor.Model;

public enum EffectKind
{
    /// <summary>An expanding ring at each mouse press.</summary>
    Clicks,

    /// <summary>The keys pressed, in a box (as the recording's metadata kept them).</summary>
    Keystrokes,

    /// <summary>The pointer: a halo or a spotlight, a bigger or smoother pointer, hidden when it stands still.</summary>
    Pointer,
}

/// <summary>
/// An effect drawn from a recording's metadata (<c>.cgmeta.json</c>) over the screen clips below its track while it
/// lasts: what happened at each clip's source time, so cuts, speed and freezes keep the clicks and keys on their
/// pictures. The settings are the recording overlays' own (Phase 12).
/// </summary>
public sealed record EffectItem : TimelineItem
{
    public EffectKind Kind { get; init; }

    public double Length { get; init; }

    public ClickOverlaySettings? Clicks { get; init; }

    public KeystrokeOverlaySettings? Keystrokes { get; init; }

    public PointerOverlaySettings? Pointer { get; init; }

    /// <summary>How much the pointer's path is smoothed, 0 to 1 (only when the app draws the pointer).</summary>
    public double Smoothing { get; init; }

    [JsonIgnore]
    public override double Duration => Length;

    /// <summary>The overlay settings that draw this effect, and nothing else.</summary>
    public OverlaySettings Settings() => new(
        BurnIn: true,
        WriteMetadata: false,
        Clicks: Kind == EffectKind.Clicks ? (Clicks ?? new ClickOverlaySettings()) with { Enabled = true, Sound = false } : new ClickOverlaySettings(Enabled: false),
        Pointer: Kind == EffectKind.Pointer ? Pointer ?? new PointerOverlaySettings() : new PointerOverlaySettings(),
        Keystrokes: Kind == EffectKind.Keystrokes ? (Keystrokes ?? new KeystrokeOverlaySettings()) with { Mode = KeystrokeMode.AllKeys } : new KeystrokeOverlaySettings(),
        Webcam: new WebcamSettings(Enabled: false));

    public override TimelineItem Slice(double from, double to)
    {
        from = Times.Round(Math.Max(from, Start));
        to = Times.Round(Math.Min(to, End));
        return this with { Start = from, Length = Math.Max(0, to - from) };
    }

    public override TimelineItem Lengthen(ItemEdge edge, double more) => edge == ItemEdge.Start
        ? this with { Start = Times.Round(Start - more), Length = Times.Round(Length + more) }
        : this with { Length = Times.Round(Length + more) };
}
