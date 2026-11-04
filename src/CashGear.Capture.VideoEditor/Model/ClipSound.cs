namespace CashGear.Capture.VideoEditor.Model;

/// <summary>Named settings of a clip's equalizer.</summary>
public enum EqPreset
{
    /// <summary>Nothing changed.</summary>
    Flat,

    /// <summary>Rumble gone, a little clearer.</summary>
    Voice,

    /// <summary>Fuller and softer.</summary>
    Warm,

    /// <summary>Thin, like a phone call.</summary>
    Phone,
}

/// <summary>A clip's equalizer: a high-pass and three bands (<see cref="Core.Audio.Equalizer"/>).</summary>
/// <param name="HighPassHz">Sound below this goes (0: nothing does).</param>
/// <param name="LowDb">The lows up or down, −24 to 24 dB.</param>
/// <param name="MidDb">The middle up or down.</param>
/// <param name="HighDb">The highs up or down.</param>
public sealed record SoundEq(double HighPassHz = 0, double LowDb = 0, double MidDb = 0, double HighDb = 0)
{
    public bool IsFlat => HighPassHz <= 0 && LowDb == 0 && MidDb == 0 && HighDb == 0;

    public static SoundEq Of(EqPreset preset) => preset switch
    {
        EqPreset.Voice => new SoundEq(80, -2, 1, 3),
        EqPreset.Warm => new SoundEq(50, 3, -1, -2),
        EqPreset.Phone => new SoundEq(300, -12, 6, -18),
        _ => new SoundEq(),
    };

    /// <summary>The preset with these settings (null: other settings).</summary>
    public EqPreset? Preset => Enum.GetValues<EqPreset>().Cast<EqPreset?>().FirstOrDefault(p => Of(p!.Value) == this);
}

/// <summary>
/// What is done to a clip's sound on its way into the mix, on the source's clock, in this order: noise removal, equalizer,
/// noise gate, gain, compressor, limiter. Worked out as it plays by <see cref="Playback.ClipSoundReader"/>, in
/// <see cref="Playback.TimelineMixer"/>, so the preview and the export are the same samples. Null on a clip: as recorded.
/// </summary>
/// <param name="GainDb">Turned up or down, in dB, whatever else is on (what Even Out sets).</param>
/// <param name="NoiseStrength">How much noise removal is used, 0 to 1 (the rest is the original).</param>
public sealed record ClipSound(
    double GainDb = 0,
    bool NoiseRemoval = false,
    double NoiseStrength = 1,
    bool Gate = false,
    double GateThresholdDb = -45,
    bool Compressor = false,
    double CompressorThresholdDb = -24,
    double CompressorRatio = 3,
    bool Limiter = false,
    double LimiterCeilingDb = -1,
    SoundEq? Eq = null)
{
    /// <summary>The range of <see cref="GainDb"/>.</summary>
    public const double MostGainDb = 24;

    /// <summary>Whether it needs samples worked on one after another, as opposed to only a change of level.</summary>
    public bool HasChain => NoiseRemoval || Gate || Compressor || Limiter || Eq is { IsFlat: false };

    /// <summary>Whether it changes anything at all.</summary>
    public bool IsActive => GainDb != 0 || HasChain;
}
