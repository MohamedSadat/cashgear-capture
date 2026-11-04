using System.ComponentModel;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Resources;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CommunityToolkit.Mvvm.Input;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>A device the preview's sound can play on, as the menu lists it (null id: Windows' default).</summary>
public sealed record OutputChoice(string? Id, string Name, bool IsChosen, System.Windows.Input.ICommand Choose)
{
    public override string ToString() => Name;
}

/// <summary>
/// What a sound clip has in the properties panel beyond its volume and fades, as the same parameters as everything else there, so
/// they read right to left, take the keyboard, have their names for screen readers and follow high contrast as the rest does:
/// its volume keyframes, Even Out, its effects, how it comes in over a crossfade, and ducking.
/// </summary>
public static class SoundProperties
{
    /// <summary>What the panel needs of the window for sound.</summary>
    /// <param name="Playhead">The playhead's time.</param>
    /// <param name="Seek">Going to a time (the next keyframe).</param>
    /// <param name="Fps">The project's frames a second, the nearness of keyframes.</param>
    /// <param name="EvenOut">Measures a clip and sets its gain (by its id).</param>
    /// <param name="Duck">Puts a track under the voice (by its id).</param>
    /// <param name="SetBackground">Marks a track as background (by its id).</param>
    /// <param name="Ducking">The ducking settings now, kept while the window is open.</param>
    /// <param name="SetDucking">Keeps other ducking settings.</param>
    /// <param name="NoiseProblem">Why noise removal can't be offered, or null.</param>
    public sealed record Actions(
        Func<double> Playhead,
        Action<double> Seek,
        int Fps,
        Action<string> EvenOut,
        Action<string> Duck,
        Action<string, bool> SetBackground,
        Func<DuckOptions> Ducking,
        Action<DuckOptions> SetDucking,
        string? NoiseProblem);

    /// <summary>The list's row for settings that are no preset.</summary>
    private static readonly object CustomEq = new();

    /// <summary>The parts of the keys that merge a setting's undo steps (never shown).</summary>
    [Localizable(false)]
    private const string VolumeKeysKey = "volume keys";

    [Localizable(false)]
    private const string SoundKey = "sound";

    [Localizable(false)]
    private const string CrossfadeKey = "crossfade shape";

    private delegate void SoundChange(EditParameter parameter, [Localizable(false)] string key, Func<ClipSound, ClipSound> update);

    /// <summary>Adds what a sound clip has to the panel's list.</summary>
    public static void Add(Clip clip, Track track, IList<EditParameter> list, Func<VideoProject> current, ItemProperties.Apply apply, Actions actions)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(apply);
        ArgumentNullException.ThrowIfNull(actions);
        AddVolumeKeys(clip, list, current, apply, actions);
        AddEffects(clip, list, current, apply, actions);
        AddCrossfade(clip, track, list, current, apply);
        AddDucking(clip, track, list, actions);
    }

    private static void AddVolumeKeys(Clip clip, IList<EditParameter> list, Func<VideoProject> current, ItemProperties.Apply apply, Actions actions)
    {
        list.Add(new NoteParameter(VideoEditorText.VolumeKeysTitle));
        var into = actions.Playhead() - clip.Start;
        void Change([Localizable(false)] string key, Func<Clip, double, Clip> update)
        {
            if (current().Find(clip.Id)?.Item is Clip now)
            {
                apply(update(now, actions.Playhead() - now.Start), VideoEditorText.UndoVolumeKeys, $"property {clip.Id} {VolumeKeysKey} {key} {Math.Round(actions.Playhead() * 1000)}");
            }
        }

        var level = new NumberParameter(VideoEditorText.VolumeAtPlayhead, 0, SoundEdits.LoudestKey * 100, Math.Round(VolumeCurve.At(clip.VolumeKeys, into) * 100), 1, " %");
        level.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Value")
            {
                Change("level", (c, at) => SoundEdits.SetVolumeAt(c, at, level.Value / 100, actions.Fps));
            }
        };
        list.Add(level);
        list.Add(new NoteParameter(VideoEditorText.VolumeKeysNote));
        list.Add(new ActionParameter(VideoEditorText.AddVolumeKey, new RelayCommand(() => Change("add", (c, at) => SoundEdits.AddVolumeKey(c, at, actions.Fps)))));
        list.Add(new ActionParameter(VideoEditorText.RemoveVolumeKey, new RelayCommand(() => Change("remove", (c, at) => SoundEdits.RemoveVolumeKey(c, at, actions.Fps)))));
        void Jump(bool backward)
        {
            if (current().Find(clip.Id)?.Item is { } now && MotionEdits.NextKeyframe(now, actions.Playhead() - now.Start, backward, actions.Fps) is { } time)
            {
                actions.Seek(now.Start + time);
            }
        }

        list.Add(new ActionParameter(VideoEditorText.PreviousKeyframe, new RelayCommand(() => Jump(backward: true))));
        list.Add(new ActionParameter(VideoEditorText.NextKeyframe, new RelayCommand(() => Jump(backward: false))));
        var pace = clip.VolumeKeys?.LastOrDefault(k => k.Time <= into + (0.5 / actions.Fps))?.Easing ?? MotionEasing.Linear;
        var easing = new ChoiceParameter(
            VideoEditorText.VolumeKeyEasing,
            [
                new(MotionEasing.Linear, VideoEditorText.EasingLinear),
                new(MotionEasing.EaseIn, VideoEditorText.EasingIn),
                new(MotionEasing.EaseOut, VideoEditorText.EasingOut),
                new(MotionEasing.EaseInOut, VideoEditorText.EasingInOut),
            ],
            pace);
        easing.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Selected")
            {
                Change("easing", (c, at) => SoundEdits.SetVolumeEasing(c, at, easing.As<MotionEasing>(), actions.Fps));
            }
        };
        list.Add(easing);
    }

    private static void AddEffects(Clip clip, IList<EditParameter> list, Func<VideoProject> current, ItemProperties.Apply apply, Actions actions)
    {
        var sound = clip.Sound ?? new ClipSound();
        list.Add(new NoteParameter(VideoEditorText.SoundEffectsTitle));
        void Change(EditParameter parameter, [Localizable(false)] string key, Func<ClipSound, ClipSound> update)
        {
            parameter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is "Value" or "Selected" && current().Find(clip.Id)?.Item is Clip now)
                {
                    apply(SoundEdits.WithSound(now, update), VideoEditorText.UndoSoundEffect, $"property {clip.Id} {SoundKey} {key}");
                }
            };
            list.Add(parameter);
        }

        list.Add(new ActionParameter(VideoEditorText.EvenOut, new RelayCommand(() => actions.EvenOut(clip.Id))));
        var gain = new NumberParameter(VideoEditorText.SoundGain, -ClipSound.MostGainDb, ClipSound.MostGainDb, sound.GainDb, 0.5, VideoEditorText.DecibelsUnit);
        Change(gain, "gain", s => s with { GainDb = Math.Round(gain.Value, 1) });

        var noise = new SwitchParameter(VideoEditorText.NoiseRemoval, sound.NoiseRemoval);
        var strength = new NumberParameter(VideoEditorText.NoiseStrength, 0, 100, sound.NoiseStrength * 100, 1, " %") { IsVisible = sound.NoiseRemoval };
        if (actions.NoiseProblem is { } problem)
        {
            list.Add(new NoteParameter(problem));
        }
        else
        {
            noise.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == "Value")
                {
                    strength.IsVisible = noise.Value;
                }
            };
            Change(noise, "noise", s => s with { NoiseRemoval = noise.Value });
            Change(strength, "noise strength", s => s with { NoiseStrength = strength.Value / 100 });
        }

        AddEq(sound, list, Change);

        var gate = new SwitchParameter(VideoEditorText.NoiseGate, sound.Gate);
        var threshold = new NumberParameter(VideoEditorText.GateThreshold, -80, 0, sound.GateThresholdDb, 1, VideoEditorText.DecibelsUnit) { IsVisible = sound.Gate };
        gate.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Value")
            {
                threshold.IsVisible = gate.Value;
            }
        };
        Change(gate, "gate", s => s with { Gate = gate.Value });
        Change(threshold, "gate threshold", s => s with { GateThresholdDb = threshold.Value });

        var compressor = new SwitchParameter(VideoEditorText.Compressor, sound.Compressor);
        var compressAt = new NumberParameter(VideoEditorText.CompressorThreshold, -60, 0, sound.CompressorThresholdDb, 1, VideoEditorText.DecibelsUnit) { IsVisible = sound.Compressor };
        var ratio = new NumberParameter(VideoEditorText.CompressorRatio, 1, 20, sound.CompressorRatio, 0.5, ":1") { IsVisible = sound.Compressor };
        compressor.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Value")
            {
                compressAt.IsVisible = ratio.IsVisible = compressor.Value;
            }
        };
        Change(compressor, "compressor", s => s with { Compressor = compressor.Value });
        Change(compressAt, "compressor threshold", s => s with { CompressorThresholdDb = compressAt.Value });
        Change(ratio, "compressor ratio", s => s with { CompressorRatio = ratio.Value });

        var limiter = new SwitchParameter(VideoEditorText.Limiter, sound.Limiter);
        var ceiling = new NumberParameter(VideoEditorText.LimiterCeiling, -24, 0, sound.LimiterCeilingDb, 0.5, VideoEditorText.DecibelsUnit) { IsVisible = sound.Limiter };
        limiter.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Value")
            {
                ceiling.IsVisible = limiter.Value;
            }
        };
        Change(limiter, "limiter", s => s with { Limiter = limiter.Value });
        Change(ceiling, "limiter ceiling", s => s with { LimiterCeilingDb = ceiling.Value });
    }

    private static void AddEq(ClipSound sound, IList<EditParameter> list, SoundChange change)
    {
        var eq = sound.Eq ?? new SoundEq();
        List<Choice> choices =
        [
            new(EqPreset.Flat, VideoEditorText.EqFlat),
            new(EqPreset.Voice, VideoEditorText.EqVoice),
            new(EqPreset.Warm, VideoEditorText.EqWarm),
            new(EqPreset.Phone, VideoEditorText.EqPhone),
            new(CustomEq, VideoEditorText.EqCustom),
        ];
        var preset = new ChoiceParameter(VideoEditorText.Equalizer, choices, eq.Preset is { } named ? named : CustomEq);
        var bands = !eq.IsFlat;
        var highPass = new NumberParameter(VideoEditorText.EqHighPass, 0, 500, eq.HighPassHz, 10, VideoEditorText.HertzUnit) { IsVisible = bands };
        var low = new NumberParameter(VideoEditorText.EqLow, -24, 24, eq.LowDb, 0.5, VideoEditorText.DecibelsUnit) { IsVisible = bands };
        var mid = new NumberParameter(VideoEditorText.EqMid, -24, 24, eq.MidDb, 0.5, VideoEditorText.DecibelsUnit) { IsVisible = bands };
        var high = new NumberParameter(VideoEditorText.EqHigh, -24, 24, eq.HighDb, 0.5, VideoEditorText.DecibelsUnit) { IsVisible = bands };
        var showing = false;
        preset.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Selected" && preset.Selected.Value is EqPreset chosen)
            {
                // The sliders show what the preset is, without each being a change of its own.
                var settings = SoundEq.Of(chosen);
                showing = true;
                highPass.Value = settings.HighPassHz;
                low.Value = settings.LowDb;
                mid.Value = settings.MidDb;
                high.Value = settings.HighDb;
                showing = false;
                highPass.IsVisible = low.IsVisible = mid.IsVisible = high.IsVisible = chosen != EqPreset.Flat;
            }
        };
        change(preset, "eq", s => preset.Selected.Value is EqPreset chosen ? s with { Eq = chosen == EqPreset.Flat ? null : SoundEq.Of(chosen) } : s);
        Func<ClipSound, ClipSound> Band(Func<SoundEq, SoundEq> update) => s => showing ? s : s with { Eq = update(s.Eq ?? new SoundEq()) };
        change(highPass, "eq high pass", Band(e => e with { HighPassHz = highPass.Value }));
        change(low, "eq low", Band(e => e with { LowDb = low.Value }));
        change(mid, "eq mid", Band(e => e with { MidDb = mid.Value }));
        change(high, "eq high", Band(e => e with { HighDb = high.Value }));
    }

    /// <summary>How it comes in over a crossfade from the clip before: only when there is one, and not over a cut's short join.</summary>
    private static void AddCrossfade(Clip clip, Track track, IList<EditParameter> list, Func<VideoProject> current, ItemProperties.Apply apply)
    {
        if (!track.Clips.Any(o => o.Id != clip.Id && o.Start < clip.Start - Times.Epsilon && o.End > clip.Start + TranscriptEdits.LongestJoin))
        {
            return;
        }

        var shape = new ChoiceParameter(VideoEditorText.CrossfadeShape, [new(true, VideoEditorText.CrossfadeEqualPower), new(false, VideoEditorText.CrossfadeStraight)], clip.EqualPowerIn);
        shape.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Selected" && current().Find(clip.Id)?.Item is Clip now)
            {
                apply(now with { EqualPowerIn = shape.As<bool>() }, VideoEditorText.UndoCrossfadeShape, $"property {clip.Id} {CrossfadeKey}");
            }
        };
        list.Add(shape);
    }

    private static void AddDucking(Clip clip, Track track, IList<EditParameter> list, Actions actions)
    {
        list.Add(new NoteParameter(VideoEditorText.DuckingTitle));
        var background = new SwitchParameter(VideoEditorText.BackgroundTrack, track.Background);
        background.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "Value")
            {
                actions.SetBackground(track.Id, background.Value);
            }
        };
        list.Add(background);
        var options = actions.Ducking();
        var amount = new NumberParameter(VideoEditorText.DuckAmount, -40, 0, options.AmountDb, 1, VideoEditorText.DecibelsUnit);
        var attack = new NumberParameter(VideoEditorText.DuckAttack, 0.05, 2, options.Attack, 0.05, VideoEditorText.SecondsUnit);
        var release = new NumberParameter(VideoEditorText.DuckRelease, 0.05, 3, options.Release, 0.05, VideoEditorText.SecondsUnit);
        foreach (var parameter in new[] { amount, attack, release })
        {
            parameter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == "Value")
                {
                    actions.SetDucking(actions.Ducking() with { AmountDb = amount.Value, Attack = Math.Round(attack.Value, 2), Release = Math.Round(release.Value, 2) });
                }
            };
            list.Add(parameter);
        }

        list.Add(new NoteParameter(VideoEditorText.DuckingNote));
        list.Add(new ActionParameter(VideoEditorText.DuckThisTrack, new RelayCommand(() => actions.Duck(clip.Id))));
    }
}
