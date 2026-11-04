using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Licensing;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.VideoEditor;

/// <summary>A sound clip's settings in the panel, ducking, the export window's sound and the preview's device, through the view model.</summary>
public sealed class SoundPanelTests : VideoEditorTestBase
{
    private sealed class OnlyFree : IFeatureGate
    {
        public bool IsEnabled(ProFeature feature) => feature != ProFeature.AudioPost;
    }

    private sealed class TwoDevices : IAudioDevices
    {
        public IReadOnlyList<AudioDevice> Microphones() => [];

        public IReadOnlyList<AudioDevice> Outputs() => [new("{one}", "Speakers"), new("{two}", "Headphones")];

        public AudioDevice? DefaultOutput() => new("{one}", "Speakers");
    }

    private VideoEditorViewModel Sounding(VideoEditorServices? services = null)
    {
        TestMedia.Require();
        var recording = Recording();
        return new VideoEditorViewModel(ProjectFactory.FromRecording(ProjectFactory.ReadAsset(recording)), null, services ?? Services, Dialogs);
    }

    private static Clip FirstSound(VideoEditorViewModel vm) => vm.Project.Tracks.First(t => t.HasSound).Clips.First();

    private static Clip Current(VideoEditorViewModel vm, Clip clip) => (Clip)vm.Project.Find(clip.Id)!.Value.Item;

    private static T Setting<T>(VideoEditorViewModel vm, string label)
        where T : EditParameter => vm.Properties.OfType<T>().First(p => p.Label == label);

    private static ActionParameter Button(VideoEditorViewModel vm, string label) => vm.Properties.OfType<ActionParameter>().First(p => p.Label == label);

    /// <summary>A sound file of a voice in stretches and a track of its own for music, in a project of their own.</summary>
    private VideoEditorViewModel WithMusic()
    {
        TestMedia.Require();
        List<(double From, double To)> speech = [(1, 3), (5, 7)];
        var voicePath = Path.Combine(Root, "voice.wav");
        var musicPath = Path.Combine(Root, "music.wav");
        WriteWav(voicePath, 9, t => speech.Any(s => t >= s.From && t < s.To) ? (0.3 * Math.Sin(2 * Math.PI * 140 * t)) + (0.1 * Math.Sin(2 * Math.PI * 280 * t)) : 0.0005 * Math.Sin(2 * Math.PI * 50 * t));
        WriteWav(musicPath, 9, t => 0.2 * Math.Sin(2 * Math.PI * 440 * t));
        var project = VideoProject.Empty(new ProjectCanvas(320, 180, 30));
        project = ProjectFactory.AddSound(project, ProjectFactory.ReadAsset(voicePath, "voice"), 0, "Microphone");
        project = ProjectFactory.AddSound(project, ProjectFactory.ReadAsset(musicPath, "music"), 0, "Score");
        return new VideoEditorViewModel(project, null, Services, Dialogs);
    }

    private static void WriteWav(string path, double seconds, Func<double, double> sample)
    {
        const int rate = 48_000;
        var frames = (int)(seconds * rate);
        using var file = new BinaryWriter(File.Create(path));
        file.Write("RIFF"u8);
        file.Write(36 + (frames * 4));
        file.Write("WAVEfmt "u8);
        file.Write(16);
        file.Write((short)1);
        file.Write((short)2);
        file.Write(rate);
        file.Write(rate * 4);
        file.Write((short)4);
        file.Write((short)16);
        file.Write("data"u8);
        file.Write(frames * 4);
        for (var i = 0; i < frames; i++)
        {
            var value = (short)Math.Round(Math.Clamp(sample((double)i / rate), -1, 1) * 32767);
            file.Write(value);
            file.Write(value);
        }
    }

    [Fact]
    public void A_sound_clip_has_its_settings_in_the_panel_and_a_picture_doesnt()
    {
        using var vm = Sounding();
        var clip = FirstSound(vm);
        vm.Timeline.Select(clip.Id);
        var labels = vm.Properties.Select(p => p.Label).ToList();
        labels.Should().Contain([VideoEditorText.VolumeKeysTitle, VideoEditorText.SoundEffectsTitle, VideoEditorText.EvenOut, VideoEditorText.Equalizer, VideoEditorText.NoiseGate, VideoEditorText.Compressor, VideoEditorText.Limiter, VideoEditorText.DuckingTitle, VideoEditorText.BackgroundTrack, VideoEditorText.DuckThisTrack]);

        vm.Timeline.Select(vm.Project.Tracks.First(t => t.HasPictures).Clips.First().Id);
        vm.Properties.Select(p => p.Label).Should().NotContain(VideoEditorText.SoundEffectsTitle);
    }

    [Fact]
    public void Each_effect_is_one_undo_step_and_its_settings_appear_when_it_is_on()
    {
        using var vm = Sounding();
        var clip = FirstSound(vm);
        vm.Timeline.Select(clip.Id);

        var threshold = Setting<NumberParameter>(vm, VideoEditorText.GateThreshold);
        threshold.IsVisible.Should().BeFalse();
        Setting<SwitchParameter>(vm, VideoEditorText.NoiseGate).Value = true;
        threshold.IsVisible.Should().BeTrue();
        Current(vm, clip).Sound!.Gate.Should().BeTrue();
        threshold.Value = -30;
        threshold.Value = -33;
        Current(vm, clip).Sound!.GateThresholdDb.Should().Be(-33);
        vm.History.Undo();
        Current(vm, clip).Sound!.GateThresholdDb.Should().Be(new ClipSound().GateThresholdDb, "a slider's drag is one step");
        vm.History.Undo();
        Current(vm, clip).Sound.Should().BeNull("with nothing on it is as recorded");

        vm.Timeline.Select(null);
        vm.Timeline.Select(clip.Id);
        Setting<SwitchParameter>(vm, VideoEditorText.Compressor).Value = true;
        Setting<NumberParameter>(vm, VideoEditorText.CompressorRatio).Value = 6;
        Setting<SwitchParameter>(vm, VideoEditorText.Limiter).Value = true;
        Setting<NumberParameter>(vm, VideoEditorText.LimiterCeiling).Value = -3;
        Setting<NumberParameter>(vm, VideoEditorText.SoundGain).Value = -6;
        Current(vm, clip).Sound.Should().Be(new ClipSound(GainDb: -6, Compressor: true, CompressorRatio: 6, Limiter: true, LimiterCeilingDb: -3));
    }

    [Fact]
    public void The_equalizers_presets_set_its_bands_and_a_band_is_then_your_own()
    {
        using var vm = Sounding();
        var clip = FirstSound(vm);
        vm.Timeline.Select(clip.Id);
        var preset = Setting<ChoiceParameter>(vm, VideoEditorText.Equalizer);
        preset.Selected.Value.Should().Be(EqPreset.Flat);
        var low = Setting<NumberParameter>(vm, VideoEditorText.EqLow);
        low.IsVisible.Should().BeFalse();

        preset.Selected = preset.Choices.First(c => Equals(c.Value, EqPreset.Warm));
        Current(vm, clip).Sound!.Eq.Should().Be(SoundEq.Of(EqPreset.Warm));
        low.IsVisible.Should().BeTrue();
        low.Value.Should().Be(SoundEq.Of(EqPreset.Warm).LowDb);

        low.Value = 9;
        Current(vm, clip).Sound!.Eq!.LowDb.Should().Be(9);
        vm.Timeline.Select(null);
        vm.Timeline.Select(clip.Id);
        Setting<ChoiceParameter>(vm, VideoEditorText.Equalizer).Selected.Label.Should().Be(VideoEditorText.EqCustom);

        preset = Setting<ChoiceParameter>(vm, VideoEditorText.Equalizer);
        preset.Selected = preset.Choices.First(c => Equals(c.Value, EqPreset.Flat));
        Current(vm, clip).Sound.Should().BeNull();
    }

    [Fact]
    public void Volume_keyframes_are_made_at_the_playhead_and_gone_through()
    {
        using var vm = Sounding();
        var clip = FirstSound(vm);
        vm.Timeline.Select(clip.Id);
        vm.Seek(clip.Start + 0.5);
        Setting<NumberParameter>(vm, VideoEditorText.VolumeAtPlayhead).Value = 50;
        Current(vm, clip).VolumeKeys.Should().ContainSingle().Which.Value.Should().Be(0.5);

        vm.Seek(clip.Start + 1.5);
        Setting<NumberParameter>(vm, VideoEditorText.VolumeAtPlayhead).Value.Should().Be(50, "the panel shows the level at the playhead");
        Button(vm, VideoEditorText.AddVolumeKey).Command.Execute(null);
        Setting<NumberParameter>(vm, VideoEditorText.VolumeAtPlayhead).Value = 100;
        Current(vm, clip).VolumeKeys!.Select(k => k.Value).Should().Equal(0.5, 1);

        Button(vm, VideoEditorText.PreviousKeyframe).Command.Execute(null);
        vm.Timeline.Playhead.Should().BeApproximately(clip.Start + 0.5, 1e-3);
        Button(vm, VideoEditorText.NextKeyframe).Command.Execute(null);
        vm.Timeline.Playhead.Should().BeApproximately(clip.Start + 1.5, 1e-3);

        var pace = Setting<ChoiceParameter>(vm, VideoEditorText.VolumeKeyEasing);
        pace.Selected = pace.Choices.First(c => Equals(c.Value, MotionEasing.EaseOut));
        Current(vm, clip).VolumeKeys![1].Easing.Should().Be(MotionEasing.EaseOut);

        Button(vm, VideoEditorText.RemoveVolumeKey).Command.Execute(null);
        Current(vm, clip).VolumeKeys.Should().ContainSingle();
    }

    [Fact]
    public async Task Even_out_measures_the_clip_and_is_one_undo_step()
    {
        using var vm = WithMusic();
        var clip = vm.Project.Tracks.Single(t => t.Name == "Score").Clips.Single();
        await vm.EvenOutAsync(clip.Id);
        var evened = Current(vm, clip);
        evened.Sound!.GainDb.Should().NotBe(0);
        vm.WorkText.Should().Contain("LUFS");
        vm.History.Undo();
        Current(vm, clip).Sound.Should().BeNull();
    }

    [Fact]
    public async Task Ducking_from_the_menu_makes_keyframes_on_the_music_and_only_there()
    {
        using var vm = WithMusic();
        var music = vm.Project.Tracks.Single(t => t.Name == "Score").Clips.Single();
        var voice = vm.Project.Tracks.Single(t => t.Name == "Microphone").Clips.Single();

        // Not yet marked: nothing to duck, and it says what to do.
        await vm.DuckMusicCommand.ExecuteAsync(null);
        Dialogs.Errors.Should().ContainSingle(e => e == VideoEditorText.NoBackgroundTrack);
        Dialogs.Errors.Clear();

        vm.Timeline.Select(music.Id);
        Setting<SwitchParameter>(vm, VideoEditorText.BackgroundTrack).Value = true;
        vm.Project.Track(vm.Project.Find(music.Id)!.Value.Track.Id)!.Background.Should().BeTrue();

        await vm.DuckMusicCommand.ExecuteAsync(null);
        Dialogs.Errors.Should().BeEmpty();
        var keys = Current(vm, music).VolumeKeys!;
        VolumeCurve.At(keys, 2).Should().BeApproximately(new DuckOptions().Level, 1e-3);
        VolumeCurve.At(keys, 4.2).Should().Be(1);
        Current(vm, voice).VolumeKeys.Should().BeNull();

        // One undo step, and every key an ordinary one.
        vm.History.Undo();
        Current(vm, music).VolumeKeys.Should().BeNull();
    }

    [Fact]
    public async Task Ducking_from_the_panel_uses_its_settings()
    {
        using var vm = WithMusic();
        var music = vm.Project.Tracks.Single(t => t.Name == "Score").Clips.Single();
        var voice = vm.Project.Tracks.Single(t => t.Name == "Microphone").Clips.Single();
        vm.Timeline.Select(music.Id);
        Setting<NumberParameter>(vm, VideoEditorText.DuckAmount).Value = -20;
        Setting<NumberParameter>(vm, VideoEditorText.DuckRelease).Value = 1;
        await vm.DuckClipsTrackAsync(music.Id);
        var keys = Current(vm, music).VolumeKeys!;
        VolumeCurve.At(keys, 2).Should().BeApproximately(0.1, 1e-3);
        VolumeCurve.At(keys, 3.5).Should().BeApproximately(0.55, 0.05, "half way through a second's release");

        // The voice's own track can't go under itself.
        Dialogs.Errors.Clear();
        vm.History.Undo();
        var first = vm.Project.Tracks.Single(t => t.Name == "Microphone");
        await vm.DuckClipsTrackAsync(first.Clips.Single().Id);
        Current(vm, voice).VolumeKeys.Should().NotBeNull("the music is the other sound there, so the voice's track went under it, as asked");
    }

    [Fact]
    public void Without_the_licence_a_sound_clip_has_only_its_volume_and_fades_and_ducking_says_why()
    {
        var closed = Services with { Features = new OnlyFree() };
        using var vm = Sounding(closed);
        var clip = FirstSound(vm);
        vm.Timeline.Select(clip.Id);
        var labels = vm.Properties.Select(p => p.Label).ToList();
        labels.Should().Contain(VideoEditorText.Volume);
        labels.Should().NotContain(VideoEditorText.SoundEffectsTitle).And.NotContain(VideoEditorText.DuckingTitle);
        vm.DuckMusicCommand.Execute(null);
        Dialogs.Errors.Should().ContainSingle();

        var export = new ExportViewModel(vm.Project, "Name", Root, Services.Exporter, Settings, State, Shell, NullLogger.Instance, features: new OnlyFree());
        export.HasSoundChoices.Should().BeFalse();
        export.ChoosesTracks.Should().BeFalse();
    }

    [Fact]
    public async Task The_export_window_offers_loudness_and_tracks_for_a_file_with_sound_and_remembers_them()
    {
        using var vm = Sounding();
        var export = new ExportViewModel(vm.Project, "Name", Root, Services.Exporter, Settings, State, Shell, NullLogger.Instance);
        export.Loudness.Lufs.Should().Be(ExportLoudness.Online, "speech online is the default");
        export.Loudnesses.Select(l => l.Lufs).Should().Equal(0, -16, -14, -23);
        export.HasSoundChoices.Should().BeTrue();
        export.ChoosesTracks.Should().BeTrue();
        export.TracksOutChoices.Select(t => t.Value).Should().Equal(SoundTracksOut.Mix, SoundTracksOut.Streams, SoundTracksOut.WavFiles);

        // A GIF has no sound; a file of sound alone holds one stream.
        export.SelectedPreset = ExportPresets.Gif;
        export.HasSoundChoices.Should().BeFalse();
        export.SelectedPreset = ExportPresets.Mkv;
        export.TracksOut = export.TracksOutChoices.Single(t => t.Value == SoundTracksOut.Streams);
        export.SelectedPreset = ExportPresets.Wav;
        export.TracksOutChoices.Select(t => t.Value).Should().Equal(SoundTracksOut.Mix, SoundTracksOut.WavFiles);
        export.TracksOut.Value.Should().Be(SoundTracksOut.Mix);
        export.Path.Should().EndWith(".wav");

        export.TracksOut = export.TracksOutChoices.Single(t => t.Value == SoundTracksOut.WavFiles);
        export.Loudness = export.Loudnesses.Single(l => l.Lufs == ExportLoudness.YouTube);

        // The last line of progress can come in just after the words at the end, off the window's thread: any of them says it.
        var said = new List<string>();
        export.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExportViewModel.ProgressText))
            {
                lock (said)
                {
                    said.Add(export.ProgressText);
                }
            }
        };
        await export.StartCommand.ExecuteAsync(null);
        export.Error.Should().BeNull();
        export.Result.Should().NotBeNull();
        lock (said)
        {
            said.Should().Contain(s => s.Contains("LUFS", StringComparison.Ordinal));
        }

        var sounds = vm.Project.Tracks.Where(t => t.HasSound).ToList();
        foreach (var track in sounds)
        {
            File.Exists(Path.ChangeExtension(export.Result!, null) + "." + track.Name + ".wav").Should().BeTrue();
        }

        State.Current.VideoEditor!.ExportLoudness.Should().Be(ExportLoudness.YouTube);
        State.Current.VideoEditor!.ExportTracksOut.Should().Be(nameof(SoundTracksOut.WavFiles));
        var again = new ExportViewModel(vm.Project, "Name", Root, Services.Exporter, Settings, State, Shell, NullLogger.Instance);
        again.Loudness.Lufs.Should().Be(ExportLoudness.YouTube);
        again.TracksOut.Value.Should().Be(SoundTracksOut.WavFiles);
    }

    [Fact]
    public async Task The_preview_can_play_on_another_device_and_remembers_it()
    {
        var made = new List<string?>();
        var withDevices = Services with
        {
            AudioDevices = new TwoDevices(),
            AudioOutput = () =>
            {
                made.Add(State.Current.VideoEditor?.PreviewOutput);
                return new NoSound();
            },
        };
        using var vm = Sounding(withDevices);
        vm.OutputDevices.Select(d => d.Name).Should().Equal(VideoEditorText.SoundOutputDefault, "Speakers", "Headphones");
        vm.OutputDevices.Single(d => d.IsChosen).Name.Should().Be(VideoEditorText.SoundOutputDefault);

        vm.Engine.Play(0);
        made.Should().Equal((string?)null);
        await vm.ChooseOutputAsync("{two}");
        made.Should().Equal(null, "{two}");
        vm.Engine.IsPlaying.Should().BeTrue("it goes on playing there");
        vm.OutputDevices.Single(d => d.IsChosen).Name.Should().Be("Headphones");
        State.Current.VideoEditor!.PreviewOutput.Should().Be("{two}");
        vm.Engine.Pause();

        // A device that has gone: the default again, and the window says so.
        await State.UpdateAsync(s => s with { VideoEditor = s.VideoEditor! with { PreviewOutput = "{gone}" } }, TestContext.Current.CancellationToken);
        vm.RefreshOutputDevices();
        vm.OutputDevices.Single(d => d.IsChosen).Name.Should().Be(VideoEditorText.SoundOutputDefault);

        vm.OutputDevices.First().Choose.Execute(null);
    }

    private sealed class NoSound : IAudioOutput
    {
        public long Played => 0;

        public void Start(AudioPull pull)
        {
        }

        public void Stop()
        {
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void A_crossfade_on_sound_can_be_either_shape_and_music_is_marked_when_added()
    {
        using var vm = Sounding();
        var track = vm.Project.Tracks.First(t => t.HasSound);
        var clip = track.Clips.First();
        vm.Timeline.Select(clip.Id);
        vm.Properties.OfType<ChoiceParameter>().Should().NotContain(p => p.Label == VideoEditorText.CrossfadeShape, "there is no crossfade yet");
        vm.Seek(1);
        vm.SplitCommand.Execute(null);
        var first = vm.Project.Track(track.Id)!.Clips.OrderBy(c => c.Start).First();
        vm.CrossfadeFrom(first.Id);
        var coming = vm.Project.Track(track.Id)!.Clips.OrderBy(c => c.Start).Last();
        coming.EqualPowerIn.Should().BeTrue();
        vm.Timeline.Select(null);
        vm.Timeline.Select(coming.Id);
        var shape = Setting<ChoiceParameter>(vm, VideoEditorText.CrossfadeShape);
        shape.Selected.Value.Should().Be(true);
        shape.Selected = shape.Choices.First(c => Equals(c.Value, false));
        Current(vm, coming).EqualPowerIn.Should().BeFalse();
    }

    [Fact]
    public void A_ramped_sound_clip_says_whether_it_keeps_its_pitch()
    {
        using var vm = Sounding();
        var clip = FirstSound(vm);
        vm.Timeline.Select(clip.Id);
        var keep = Setting<SwitchParameter>(vm, VideoEditorText.KeepPitch);
        keep.IsVisible.Should().BeFalse();
        Setting<SwitchParameter>(vm, VideoEditorText.SpeedRamp).Value = true;
        keep.IsVisible.Should().BeTrue();
        Setting<NumberParameter>(vm, VideoEditorText.SpeedAtEnd).Value = 2;
        Current(vm, clip).KeepPitch.Should().BeTrue();
        keep.Value = false;
        Current(vm, clip).KeepPitch.Should().BeFalse();
    }
}
