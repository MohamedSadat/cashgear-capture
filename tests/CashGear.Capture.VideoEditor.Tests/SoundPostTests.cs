using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CashGear.Capture.Audio;
using CashGear.Capture.Core.Audio;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.Transcription.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using CashGear.Capture.VideoEditor.Timeline;
using Microsoft.Extensions.Logging.Abstractions;
using SoundLevels = CashGear.Capture.VideoEditor.Playback.SoundLevels;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>Phase 23: what is done to a clip's sound, volume keyframes, loudness, ducking, tracks out, equal power, a ramp's pitch.</summary>
public sealed class SoundPostTests(ITestOutputHelper output) : IDisposable
{
    private const int Rate = 48_000;

    private readonly TestMediaFiles _media = new("cashgear-sound-");

    public void Dispose() => _media.Dispose();

    private static void RequireNoiseRemoval() =>
        Assert.SkipUnless(RnnoiseLibrary.IsAvailable, RnnoiseLibrary.Problem ?? "RNNoise isn't available (run tools\\get-rnnoise.ps1).");

    private string Wav(string name, double seconds, Func<int, double, (double Left, double Right)> sample)
    {
        var path = Path.Combine(_media.Root, name);
        if (!File.Exists(path))
        {
            Phase22Fixture.WriteWav(path, seconds, sample);
        }

        return path;
    }

    private string Tone(string name, double seconds, double hertz = 1000, double level = 0.3) =>
        Wav(name, seconds, (_, t) => (level * Math.Sin(2 * Math.PI * hertz * t), level * Math.Sin(2 * Math.PI * hertz * t)));

    /// <summary>Syllables, then quiet, a little noise under them, each channel its own: what a chain has something to do with.</summary>
    private string Busy(string name, double seconds) => Wav(name, seconds, (i, t) =>
    {
        var random = new Random(i);
        var syllable = Math.Pow(Math.Max(0, Math.Sin(2 * Math.PI * 1.7 * t)), 2) * (Math.Sin(2 * Math.PI * 0.13 * t) > -0.6 ? 1 : 0);
        var phase = 2 * Math.PI * (150 + (40 * Math.Sin(2 * Math.PI * 0.6 * t))) * t;
        var voice = syllable * ((0.25 * Math.Sin(phase)) + (0.1 * Math.Sin(2 * phase)));
        return (voice + ((random.NextDouble() - 0.5) * 0.01), (voice * 0.7) + ((random.NextDouble() - 0.5) * 0.01));
    });

    private static Clip ClipOf(MediaAsset asset, double seconds, string id = "c", double start = 0, double sourceIn = 0) =>
        new() { Id = id, AssetId = asset.Id, Stream = asset.Streams.First(s => s.Kind == StreamKind.Audio).Index, Start = start, SourceIn = sourceIn, SourceOut = sourceIn + seconds };

    private static VideoProject Project(MediaAsset asset, params Clip[] clips) =>
        new(VideoProject.CurrentVersion, new ProjectCanvas(320, 180, 30), [asset], [new Track("sound", TrackKind.Audio, "Sound", clips)]);

    private static float[] MixAll(VideoProject project, int blockFrames = 1024, long? upTo = null, long from = 0)
    {
        var total = (upTo ?? TimelineMixer.Sample(project.Duration)) - from;
        var result = new float[total * 2];
        using var mixer = new TimelineMixer();
        for (var at = 0L; at < total; at += blockFrames)
        {
            mixer.Mix(project, from + at, result.AsSpan((int)(at * 2), (int)Math.Min(blockFrames, total - at) * 2));
        }

        return result;
    }

    private static double Rms(ReadOnlySpan<float> samples, double from, double to)
    {
        var (first, last) = ((int)(from * Rate) * 2, Math.Min((int)(to * Rate) * 2, samples.Length));
        var sum = 0.0;
        for (var i = first; i < last; i++)
        {
            sum += samples[i] * samples[i];
        }

        return Math.Sqrt(sum / Math.Max(1, last - first));
    }

    private static double Db(double ratio) => 20 * Math.Log10(Math.Max(ratio, 1e-9));

    private static double Worst(ReadOnlySpan<float> one, ReadOnlySpan<float> other)
    {
        var worst = 0.0;
        for (var i = 0; i < Math.Min(one.Length, other.Length); i++)
        {
            worst = Math.Max(worst, Math.Abs(one[i] - other[i]));
        }

        return worst;
    }

    private static float[] ReadAll(string path, int stream = -1)
    {
        using var reader = new AudioStreamReader(path, stream);
        var samples = new List<float>();
        var block = new float[4096];
        while (reader.Read(block) is var read and > 0)
        {
            samples.AddRange(block.Take(read * 2));
        }

        return [.. samples];
    }

    private static ProjectExporter Exporter() => new(new SoftwareEncoders(), NullLogger<ProjectExporter>.Instance);

    private string OutFile(string name) => Path.Combine(_media.Root, name);

    private static async Task<ExportResult> Export(VideoProject project, ExportPreset preset, string path, double? loudness = null, SoundTracksOut tracks = SoundTracksOut.Mix) =>
        await Exporter().ExportAsync(new ExportRequest(project, preset, QualityPreset.High, path, LoudnessTarget: loudness, TracksOut: tracks), ct: TestContext.Current.CancellationToken);

    // --- Equalizer -------------------------------------------------------------------------------------------------
    [Fact]
    public void The_equalizer_does_what_its_bands_say()
    {
        new Equalizer(0, 0, 0, 0).ResponseDb(1000).Should().BeApproximately(0, 1e-9);
        var bands = new Equalizer(100, 6, -6, 9);
        bands.ResponseDb(20).Should().BeLessThan(-18, "the high-pass takes away what is well below it");
        new Equalizer(0, 6, 0, 0).ResponseDb(30).Should().BeApproximately(6, 0.3);
        new Equalizer(0, 0, -6, 0).ResponseDb(Equalizer.MidHz).Should().BeApproximately(-6, 0.1);
        new Equalizer(0, 0, 0, 9).ResponseDb(18000).Should().BeApproximately(9, 0.5);

        // A tone through it comes out at that level, measured rather than worked out.
        var block = new float[Rate * 2];
        for (var i = 0; i < Rate; i++)
        {
            block[2 * i] = block[(2 * i) + 1] = (float)(0.25 * Math.Sin(2 * Math.PI * Equalizer.MidHz * i / Rate));
        }

        new Equalizer(0, 0, -6, 0).Process(block);
        Db(Rms(block, 0.5, 1) / (0.25 / Math.Sqrt(2))).Should().BeApproximately(-6, 0.1);
    }

    [Fact]
    public void Equalizer_presets_are_found_again_from_their_settings()
    {
        foreach (var preset in Enum.GetValues<EqPreset>())
        {
            SoundEq.Of(preset).Preset.Should().Be(preset);
        }

        new SoundEq(90).Preset.Should().BeNull();
        SoundEq.Of(EqPreset.Flat).IsFlat.Should().BeTrue();
        new ClipSound(Eq: SoundEq.Of(EqPreset.Flat)).HasChain.Should().BeFalse();
        new ClipSound(Eq: SoundEq.Of(EqPreset.Phone)).HasChain.Should().BeTrue();
    }

    // --- Volume keyframes ------------------------------------------------------------------------------------------
    [Fact]
    public void Volume_keyframes_set_the_level_and_ease_between_them()
    {
        TestMedia.Require();
        var asset = ProjectFactory.ReadAsset(Tone("keys.wav", 4), "tone");
        var clip = ClipOf(asset, 4) with { VolumeKeys = [new(1, 1), new(2, 0.25), new(3, 0.25, MotionEasing.EaseInOut), new(3.5, 1)] };
        var mixed = MixAll(Project(asset, clip));
        var full = 0.3 / Math.Sqrt(2);
        Rms(mixed, 0.2, 0.9).Should().BeApproximately(full, full * 0.01, "held before the first key");
        Rms(mixed, 2.2, 2.8).Should().BeApproximately(full * 0.25, full * 0.01);
        Rms(mixed, 3.6, 3.9).Should().BeApproximately(full, full * 0.01, "held after the last key");
        Rms(mixed, 1.45, 1.55).Should().BeApproximately(full * 0.625, full * 0.03, "a straight line half way");
    }

    [Fact]
    public void A_curve_of_keys_is_found_by_halving_as_the_motion_lanes_read_it()
    {
        IReadOnlyList<MotionKey> keys = [new(0.5, 0, MotionEasing.EaseIn), new(1, 1, MotionEasing.Spring), new(2, 0.5, MotionEasing.Bezier, [0.4, 0, 0.2, 1]), new(3, 2), new(3, 0.3)];
        for (var t = 0.0; t < 4; t += 0.013)
        {
            VolumeCurve.At(keys, t).Should().BeApproximately(ItemMotion.Value(keys, t, 1), 1e-9);
        }

        VolumeCurve.At(null, 1).Should().Be(1);
        VolumeCurve.At([], 1).Should().Be(1);
    }

    [Fact]
    public void Splits_and_trims_keep_each_moment_at_its_level()
    {
        var asset = TestProjects.Recording(60);
        var clip = new Clip { Id = "c", AssetId = "rec", Stream = 1, Start = 2, SourceIn = 0, SourceOut = 10, VolumeKeys = [new(1, 1), new(3, 0.5, MotionEasing.EaseInOut), new(5, 1)] };
        var project = Project(asset, clip);
        var split = TimelineEdits.Split(project, 5);
        var parts = split.Tracks[0].Clips.OrderBy(c => c.Start).ToList();
        parts.Should().HaveCount(2);
        for (var time = 2.1; time < 12; time += 0.31)
        {
            var part = parts.Single(p => p.Covers(time));
            VolumeCurve.At(part.VolumeKeys, time - part.Start).Should().BeApproximately(VolumeCurve.At(clip.VolumeKeys, time - clip.Start), 0.02, "at {0} s", time);
        }

        var trimmed = (Clip)TimelineEdits.Trim(project, "c", ItemEdge.Start, 4).Find("c")!.Value.Item;
        VolumeCurve.At(trimmed.VolumeKeys, 5 - trimmed.Start).Should().BeApproximately(VolumeCurve.At(clip.VolumeKeys, 5 - clip.Start), 0.02);
        var grown = (Clip)TimelineEdits.Trim(TimelineEdits.Trim(project, "c", ItemEdge.Start, 4), "c", ItemEdge.Start, 3).Find("c")!.Value.Item;
        VolumeCurve.At(grown.VolumeKeys, 6 - grown.Start).Should().BeApproximately(VolumeCurve.At(trimmed.VolumeKeys, 6 - trimmed.Start), 1e-3, "lengthened at its start, what was there stays where it was");
    }

    [Fact]
    public void Volume_keys_are_added_set_moved_and_removed()
    {
        var clip = new Clip { Id = "c", AssetId = "a", SourceOut = 10 };
        var one = SoundEdits.SetVolumeAt(clip, 2, 0.5, 30);
        one.VolumeKeys.Should().ContainSingle().Which.Value.Should().Be(0.5);
        var two = SoundEdits.AddVolumeKey(one, 4, 30);
        two.VolumeKeys![1].Value.Should().Be(0.5, "a key added says what the level is there, so nothing changes yet");
        SoundEdits.SetVolumeAt(two, 4, 5, 30).VolumeKeys![1].Value.Should().Be(SoundEdits.LoudestKey);
        var moved = SoundEdits.MoveVolumeKey(two, 4, 1, 0.75, 30);
        moved.VolumeKeys!.Select(k => (k.Time, k.Value)).Should().Equal((1, 0.75), (2, 0.5));
        SoundEdits.MoveVolumeKey(two, 4, 2, null, 30).VolumeKeys.Should().ContainSingle("a key already at that time is replaced");
        SoundEdits.RemoveVolumeKey(SoundEdits.RemoveVolumeKey(two, 2, 30), 4, 30).VolumeKeys.Should().BeNull();
        SoundEdits.SetVolumeEasing(two, 2.5, MotionEasing.EaseOut, 30).VolumeKeys![0].Easing.Should().Be(MotionEasing.EaseOut);
    }

    [Fact]
    public void Volume_keys_show_in_the_timelines_lane_and_a_diamond_is_dragged_there()
    {
        var clip = new Clip { Id = "c", AssetId = "rec", Stream = 1, Start = 0, SourceOut = 10, VolumeKeys = [new(2, 1), new(4, 0.5)] };
        var project = Project(TestProjects.Recording(60), clip);
        MotionEdits.KeyTimes(clip).Should().Equal(2, 4);
        var row = TimelineView.Rows(project).Single();
        row.Lane.Should().BePositive();
        var moved = (Clip)MotionEdits.MoveKeyframesOf(clip, 4, 5, 30);
        moved.VolumeKeys![1].Time.Should().Be(5);

        var history = new ProjectHistory(project);
        var view = new TimelineView { Width = 800, Height = 300 };
        var session = new TimelineSession(history, view);
        var y = view.RowTop(row) + row.Height - 4;
        session.PointerDown(view.X(4), y, shift: false);
        session.PointerMove(view.X(6), y, alt: true);
        session.PointerUp(view.X(6), y);
        ((Clip)history.Project.Find("c")!.Value.Item).VolumeKeys![1].Time.Should().BeApproximately(6, 1e-3);
        history.CanUndo.Should().BeTrue();
    }

    // --- Effects: the same samples however they are reached ---------------------------------------------------------
    private static ClipSound Everything(bool noise) =>
        new(GainDb: 3, NoiseRemoval: noise, Gate: true, GateThresholdDb: -35, Compressor: true, Limiter: true, Eq: SoundEq.Of(EqPreset.Voice));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_clips_effects_give_the_same_samples_however_they_are_reached(bool noise)
    {
        TestMedia.Require();
        if (noise)
        {
            RequireNoiseRemoval();
        }

        var asset = ProjectFactory.ReadAsset(Busy("busy.wav", 17), "busy");
        var clip = ClipOf(asset, 16.5, sourceIn: 0.25) with { Sound = Everything(noise) };
        var project = Project(asset, clip);
        var whole = MixAll(project);
        Db(Rms(whole, 0, whole.Length / 2.0 / Rate)).Should().BeGreaterThan(-40, "there is sound in it");

        // Other block sizes: what a preview asks for, and an encoder's.
        Worst(MixAll(project, 480), whole).Should().Be(0);
        Worst(MixAll(project, 960), whole).Should().Be(0);

        // Seeks, on and off the stretches' edges, and into the blend at the end of one.
        foreach (var at in new[] { 1.0, 4.74, 4.745, 4.9, 5.0, 7.31, 9.76, 10.5 })
        {
            var from = TimelineMixer.Sample(at);
            var part = MixAll(project, 1024, from + (Rate * 2), from);
            var worst = Worst(part, whole.AsSpan((int)from * 2, part.Length));
            worst.Should().Be(0, "at {0} s the mix is what it is when played from the start", at);
        }

        // A split doesn't change a sample.
        var split = TimelineEdits.Split(TimelineEdits.Split(project, 5.5), 11);
        split.Tracks[0].Clips.Should().HaveCount(3);
        Worst(MixAll(split), whole).Should().Be(0);
    }

    [Fact]
    public void A_stretchs_edge_doesnt_click()
    {
        TestMedia.Require();
        RequireNoiseRemoval();

        // Steady speech-like sound over several stretches: the biggest step from sample to sample at their edges is no bigger than anywhere.
        var asset = ProjectFactory.ReadAsset(Busy("edges.wav", 22), "edges");
        var mixed = MixAll(Project(asset, ClipOf(asset, 22) with { Sound = new ClipSound(NoiseRemoval: true, Compressor: true, Eq: SoundEq.Of(EqPreset.Voice)) }));
        double Steepest(int from, int to)
        {
            var most = 0.0;
            for (var i = from; i < to; i++)
            {
                most = Math.Max(most, Math.Abs(mixed[2 * i] - mixed[2 * (i - 1)]));
            }

            return most;
        }

        var anywhere = Steepest(1, mixed.Length / 2);
        foreach (var segment in new[] { 1, 2, 3, 4 })
        {
            var edge = segment * ClipSoundReader.SegmentFrames;
            Steepest(edge - 960, edge + 960).Should().BeLessThanOrEqualTo(anywhere, "the edge at {0} s", segment * 5);
        }
    }

    [Fact]
    public void A_clip_with_effects_starting_soon_is_got_ready_beside_the_mixing()
    {
        TestMedia.Require();
        var asset = ProjectFactory.ReadAsset(Busy("ahead.wav", 12), "ahead");
        var sound = new ClipSound(Compressor: true, Limiter: true);
        var clip = ClipOf(asset, 4, "later", start: 2, sourceIn: 3.3) with { Sound = sound };
        var project = Project(asset, clip);
        var whole = MixAll(project);

        // Read as a preview does: the block before it starts makes it ready, and it plays without a seam.
        var reading = new float[whole.Length];
        using var mixer = new TimelineMixer();
        for (var at = 0; at < whole.Length / 2; at += 1024)
        {
            mixer.Mix(project, at, reading.AsSpan(at * 2, Math.Min(1024, (whole.Length / 2) - at) * 2));
        }

        mixer.Repositions.Should().Be(1, "reading forward needs one voice for it");
        Worst(reading, whole).Should().Be(0);
        Rms(whole, 2.5, 5.5).Should().BePositive();
        Rms(whole, 0, 1.9).Should().Be(0);
    }

    [Fact]
    public void A_level_change_while_playing_doesnt_break_the_sound()
    {
        TestMedia.Require();
        var asset = ProjectFactory.ReadAsset(Tone("edit.wav", 4), "edit");
        var clip = ClipOf(asset, 4);
        using var mixer = new TimelineMixer();
        var (one, two) = (new float[2048], new float[2048]);
        mixer.Mix(Project(asset, clip), 0, one);
        mixer.Mix(Project(asset, clip with { Volume = 1, Sound = new ClipSound(GainDb: 0) }), 1024, two);
        var joined = one.Concat(two).ToArray();
        for (var i = 1; i < joined.Length / 2; i++)
        {
            Math.Abs(joined[2 * i] - joined[2 * (i - 1)]).Should().BeLessThan(0.05f);
        }
    }

    [Fact]
    public void The_chains_delay_is_taken_out()
    {
        TestMedia.Require();
        RequireNoiseRemoval();

        // A burst at 3 s, on noise RNNoise would rather take out: where the burst is must not move.
        var path = Wav("burst.wav", 6, (i, t) =>
        {
            var random = new Random(i);
            var burst = t is >= 3 and < 3.3 ? 0.4 * Math.Sin(2 * Math.PI * 220 * t) : 0;
            return (burst + ((random.NextDouble() - 0.5) * 0.002), burst + ((random.NextDouble() - 0.5) * 0.002));
        });
        var asset = ProjectFactory.ReadAsset(path, "burst");
        var plain = MixAll(Project(asset, ClipOf(asset, 6)));
        var worked = MixAll(Project(asset, ClipOf(asset, 6) with { Sound = new ClipSound(NoiseRemoval: true, Limiter: true, Compressor: false) }));
        static int Onset(float[] samples)
        {
            for (var i = 0; i < samples.Length / 2; i++)
            {
                if (Math.Abs(samples[2 * i]) > 0.1)
                {
                    return i;
                }
            }

            return -1;
        }

        Math.Abs(Onset(worked) - Onset(plain)).Should().BeLessThan(480, "within RNNoise's own 10 ms frame of where it was");
        worked.Length.Should().Be(plain.Length);
    }

    [Fact]
    public void Sound_ends_where_the_stream_does_and_past_it_is_silence()
    {
        TestMedia.Require();
        var asset = ProjectFactory.ReadAsset(Tone("short.wav", 2), "short");
        var clip = ClipOf(asset, 3) with { Sound = new ClipSound(Limiter: true) };
        var mixed = MixAll(Project(asset, clip));
        Rms(mixed, 1.5, 1.95).Should().BeGreaterThan(0.1);
        Rms(mixed, 2.1, 3).Should().Be(0, "nothing comes after the end of the file");
    }

    [Fact]
    public void Gain_works_with_or_without_a_chain_and_goes_before_the_limiter()
    {
        TestMedia.Require();
        var asset = ProjectFactory.ReadAsset(Tone("gain.wav", 2, level: 0.1), "gain");
        var plain = Rms(MixAll(Project(asset, ClipOf(asset, 2))), 0.5, 1.5);
        Db(Rms(MixAll(Project(asset, ClipOf(asset, 2) with { Sound = new ClipSound(GainDb: 6) })), 0.5, 1.5) / plain).Should().BeApproximately(6, 0.01);
        Db(Rms(MixAll(Project(asset, ClipOf(asset, 2) with { Sound = new ClipSound(GainDb: 6, Eq: new SoundEq(20)) })), 0.5, 1.5) / plain).Should().BeApproximately(6, 0.1);

        var limited = MixAll(Project(asset, ClipOf(asset, 2) with { Sound = new ClipSound(GainDb: 24, Limiter: true, LimiterCeilingDb: -6) }));
        limited.Skip(Rate).Max().Should().BeLessThanOrEqualTo((float)Math.Pow(10, -6.0 / 20) + 1e-3f, "the limiter holds what the gain pushed up");
    }

    // --- Noise removal: the acceptance ------------------------------------------------------------------------------
    /// <summary>
    /// Real speech from Windows' voice with a fan and a keyboard under it, as the plan's test clip: three sentences parted by
    /// two and a half seconds of the noise alone. The spans of speech and of the gaps are returned with it.
    /// </summary>
    private async Task<(string Path, float[] Speech, List<(double From, double To)> Said, List<(double From, double To)> Gaps)> NoisyClip()
    {
        string[] sentences =
        [
            "The quick brown fox jumps over the lazy dog near the river bank.",
            "Please open the settings window and choose another sound device.",
            "We measured how much of the noise was gone in the quiet parts of this recording.",
        ];
        var clean = new List<float>();
        var said = new List<(double From, double To)>();
        var gaps = new List<(double From, double To)>();
        void Silence(double seconds) => clean.AddRange(new float[(int)(seconds * Rate)]);
        Silence(1.5);
        foreach (var sentence in sentences)
        {
            var words = await Speech.SayAsync(sentence);
            var start = (double)clean.Count / Rate;
            for (var i = 0; i < words.Length; i++)
            {
                // 16 kHz to 48 kHz, in straight lines: speech has little up there.
                var next = words[Math.Min(i + 1, words.Length - 1)];
                clean.Add(words[i]);
                clean.Add(words[i] + ((next - words[i]) / 3));
                clean.Add(words[i] + (2 * (next - words[i]) / 3));
            }

            said.Add((start, (double)clean.Count / Rate));
            Silence(2.5);
        }

        for (var i = 0; i < said.Count; i++)
        {
            gaps.Add((i == 0 ? 0.5 : said[i - 1].To + 0.4, said[i].From - 0.1));
        }

        gaps.Add((said[^1].To + 0.4, ((double)clean.Count / Rate) - 0.1));
        var peak = clean.Max(Math.Abs);
        var speech = clean.Select(s => s / peak * 0.5f).ToArray();

        var random = new Random(23);
        var samples = new short[speech.Length * 2];
        var fan = 0.0;
        var keystroke = 0.0;
        var next2 = 0;
        for (var i = 0; i < speech.Length; i++)
        {
            // A fan: rumble and hiss, with its hum. Keys: a short burst of noise now and then, dying away.
            var t = (double)i / Rate;
            fan += (((random.NextDouble() * 2) - 1) - fan) * 0.08;
            var hiss = ((random.NextDouble() * 2) - 1) * 0.15;
            var hum = (0.5 * Math.Sin(2 * Math.PI * 120 * t)) + (0.3 * Math.Sin(2 * Math.PI * 240 * t));
            if (i >= next2)
            {
                keystroke = 1;
                next2 = i + random.Next(Rate / 10, Rate / 3);
            }

            keystroke *= 0.998;
            var key = keystroke * ((random.NextDouble() * 2) - 1) * 0.06;
            var noise = (((fan * 1.2) + hiss + (hum * 0.25)) * 0.03) + key;
            var value = (short)Math.Round(Math.Clamp(speech[i] + noise, -1, 1) * 32767);
            samples[2 * i] = samples[(2 * i) + 1] = value;
        }

        var path = OutFile("noisy.wav");
        Phase22Fixture.WriteWav(path, samples);
        return (path, speech, said, gaps);
    }

    [Fact]
    public async Task RNNoise_on_a_clip_takes_the_noise_floor_down_with_the_speech_intact_and_preview_and_export_match()
    {
        TestMedia.Require();
        RequireNoiseRemoval();
        var (path, speech, said, gaps) = await NoisyClip();
        var asset = ProjectFactory.ReadAsset(path, "noisy");
        var seconds = (double)speech.Length / Rate;
        var plain = Project(asset, ClipOf(asset, seconds));
        var cleaned = Project(asset, ClipOf(asset, seconds) with { Sound = new ClipSound(NoiseRemoval: true) });
        var before = MixAll(plain);
        var after = MixAll(cleaned);

        foreach (var (from, to) in gaps)
        {
            var (a, b) = (Rms(before, from, to), Rms(after, from, to));
            output.WriteLine($"Gap {from:0.0}-{to:0.0} s: {Db(a):0.0} dB to {Db(b):0.0} dB, {Db(b / a):0.0} dB");
            Db(b / a).Should().BeLessThanOrEqualTo(-15, "the noise floor in the gap from {0} s", from);
        }

        // The speech is the speech: as loud, and the same wave.
        foreach (var (from, to) in said)
        {
            var (first, count) = ((int)(from * Rate), (int)((to - from) * Rate));
            double together = 0, cleanPower = 0, afterPower = 0;
            for (var i = first; i < first + count; i++)
            {
                var (x, y) = (speech[i], after[2 * i]);
                together += x * y;
                cleanPower += x * x;
                afterPower += y * y;
            }

            var correlation = together / Math.Sqrt(cleanPower * afterPower);
            var level = 10 * Math.Log10(afterPower / cleanPower);
            output.WriteLine($"Speech {from:0.0}-{to:0.0} s: correlation with the clean speech {correlation:0.000}, level {level:0.0} dB");
            correlation.Should().BeGreaterThan(0.9, "the sentence at {0} s comes through, in its place", from);
            level.Should().BeInRange(-3, 1.5, "the sentence at {0} s is as loud", from);
        }

        // Preview (the mixer, a block at a time, after a seek too) and export (the file, read back) are the same samples.
        var file = OutFile("cleaned.wav");
        await Export(cleaned, ExportPresets.Wav, file);
        var exported = ReadAll(file);
        exported.Length.Should().Be(after.Length);
        Worst(exported, after).Should().BeLessThan(1.5 / 8_388_608, "the file holds 24 bits of the preview's samples");
        var flac = OutFile("cleaned.flac");
        await Export(cleaned, ExportPresets.Flac, flac);
        Worst(ReadAll(flac), exported).Should().Be(0, "lossless is lossless");

        var seek = TimelineMixer.Sample(gaps[1].From);
        var part = MixAll(cleaned, 1024, seek + (2 * Rate), seek);
        Worst(part, exported.AsSpan((int)seek * 2, part.Length)).Should().BeLessThan(1.5 / 8_388_608, "after a seek too");
    }

    [Fact]
    public void Noise_removal_keeps_up_with_playing()
    {
        TestMedia.Require();
        RequireNoiseRemoval();
        using var turn = new CashGear.Capture.Tests.MachineTurn();
        var asset = ProjectFactory.ReadAsset(Busy("speed.wav", 30), "speed");
        var project = Project(asset, ClipOf(asset, 30) with { Sound = new ClipSound(NoiseRemoval: true, Gate: true, Compressor: true, Limiter: true, Eq: SoundEq.Of(EqPreset.Voice)) });
        var watch = Stopwatch.StartNew();
        var mixed = MixAll(project);
        watch.Stop();
        var factor = 30 / watch.Elapsed.TotalSeconds;

        // The longest wait there is: a seek to the last of a stretch, which goes through the stretch and its pre-roll.
        var seekWatch = Stopwatch.StartNew();
        using var mixer = new TimelineMixer();
        mixer.Mix(project, TimelineMixer.Sample(14.99), new float[2048]);
        seekWatch.Stop();
        output.WriteLine($"All effects on two different channels: {factor:0.0} times as fast as playing; a seek to the end of a stretch takes {seekWatch.ElapsedMilliseconds} ms");
        factor.Should().BeGreaterThan(4, "two clips can play at once with room to spare");
        seekWatch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1.5));
        mixed.Length.Should().Be(30 * Rate * 2);
    }

    [Fact]
    public void Without_RNNoise_noise_removal_passes_the_sound_and_says_so()
    {
        // The reader says whether it can; the panel says so to the user. Without the library the chain is the rest of it.
        ClipSoundReader.CanRemoveNoise.Should().Be(RnnoiseLibrary.IsAvailable);
    }

    // --- Equal power --------------------------------------------------------------------------------------------------
    [Fact]
    public void A_crossfade_made_now_is_equal_power_and_an_old_one_stays_straight()
    {
        TestMedia.Require();

        // Two sounds that have nothing to do with each other, each steady: equal power keeps the sum's power.
        var one = ProjectFactory.ReadAsset(Tone("one.wav", 4, 440), "one");
        var other = ProjectFactory.ReadAsset(Tone("other.wav", 4, 611), "other");
        Track Fade(bool equal) => new("sound", TrackKind.Audio, "Sound", [
            new Clip { Id = "a", AssetId = "one", Stream = 0, SourceOut = 3 },
            new Clip { Id = "b", AssetId = "other", Stream = 0, Start = 2, SourceOut = 3, EqualPowerIn = equal },
        ]);
        var canvas = new ProjectCanvas(320, 180, 30);
        var equal = MixAll(new VideoProject(VideoProject.CurrentVersion, canvas, [one, other], [Fade(true)]));
        var straight = MixAll(new VideoProject(VideoProject.CurrentVersion, canvas, [one, other], [Fade(false)]));
        var steady = Rms(equal, 0.5, 1.5);
        Db(Rms(equal, 2.45, 2.55) / steady).Should().BeApproximately(0, 0.2, "half way through, the two together are as loud as one alone");
        Db(Rms(straight, 2.45, 2.55) / steady).Should().BeApproximately(-3, 0.3, "a straight line dips by 3 dB half way");

        TimelineMixer.Envelope(new Clip { Id = "x", AssetId = "", SourceOut = 4 }, 0.5, overlapIn: 1, equalIn: true).Should().BeApproximately(Math.Sin(Math.PI / 4), 1e-9);
        TimelineMixer.Envelope(new Clip { Id = "x", AssetId = "", SourceOut = 4 }, 3.5, overlapOut: 1, equalOut: true).Should().BeApproximately(Math.Sin(Math.PI / 4), 1e-9);
    }

    [Fact]
    public void The_crossfade_command_and_a_transition_make_it_equal_power_on_the_sound()
    {
        var project = TestProjects.Project();
        var split = TimelineEdits.Split(project, 10);
        var sound = split.Tracks.First(t => t.HasSound).Clips.OrderBy(c => c.Start).First();
        var faded = TransitionEdits.Crossfade(split, sound.Id, 1);
        var coming = faded.Tracks.First(t => t.HasSound).Clips.OrderBy(c => c.Start).Last();
        coming.EqualPowerIn.Should().BeTrue();
        coming.Start.Should().BeApproximately(9, 1e-6);
        faded.Tracks.Where(t => t.HasSound).SelectMany(t => t.Clips).Count(c => c.EqualPowerIn).Should().Be(2, "both of a recording's sound tracks");
        faded.Tracks.Where(t => t.HasPictures).SelectMany(t => t.Clips).Should().OnlyContain(c => !c.EqualPowerIn);
    }

    [Fact]
    public void A_cuts_join_stays_a_straight_line_whatever_its_clip_says()
    {
        TestMedia.Require();
        var asset = ProjectFactory.ReadAsset(Tone("join.wav", 4), "join");
        Clip[] Joined(bool equal) =>
        [
            ClipOf(asset, 2 + TranscriptEdits.JoinLength, "a"),
            ClipOf(asset, 2, "b", start: 2, sourceIn: 2 + TranscriptEdits.JoinLength) with { EqualPowerIn = equal },
        ];
        TranscriptEdits.JoinLength.Should().BeLessThanOrEqualTo(TranscriptEdits.LongestJoin);
        Worst(MixAll(Project(asset, Joined(true))), MixAll(Project(asset, Joined(false)))).Should().Be(0);

        // Cutting by the transcript and crossfading next to it leave its sound as it was.
        var clip = ClipOf(asset, 4) with { EqualPowerIn = true };
        ((Clip)clip.Slice(2, 4)).EqualPowerIn.Should().BeFalse("the part after a split has no overlap of its own");
        ((Clip)clip.Slice(0, 2)).EqualPowerIn.Should().BeTrue();
    }

    // --- A ramp keeps its pitch --------------------------------------------------------------------------------------
    [Theory]
    [InlineData(0.5, 2)]
    [InlineData(2, 0.5)]
    [InlineData(0.25, 0.75)]
    [InlineData(1, 4)]
    public void A_ramped_clip_keeps_its_pitch_within_ten_cents(double from, double to)
    {
        TestMedia.Require();
        var hertz = 997.0;
        var asset = ProjectFactory.ReadAsset(Tone("ramp.wav", 40, hertz), "ramp");
        var clip = ClipOf(asset, 10) with { Speed = from, EndSpeed = to, KeepPitch = true };
        var project = Project(asset, clip);
        var mixed = MixAll(project);
        var worst = 0.0;
        var duration = clip.Duration;
        for (var at = 0.2; at < duration - 0.3; at += 0.25)
        {
            var window = mixed.AsSpan((int)(at * Rate) * 2, Rate / 10 * 2);
            var found = TestMediaFiles.Frequency(window);
            worst = Math.Max(worst, Math.Abs(1200 * Math.Log2(found / hertz)));
        }

        output.WriteLine($"{from}x to {to}x, {duration:0.0} s: at worst {worst:0.00} cents off");
        worst.Should().BeLessThan(10);
    }

    [Theory]
    [InlineData(0.5, 2)]
    [InlineData(2, 0.5)]
    public void A_ramped_clips_sound_stays_with_its_pictures(double from, double to)
    {
        TestMedia.Require();
        var asset = ProjectFactory.ReadAsset(Wav("ticks.wav", 12, (_, t) =>
        {
            var tick = (t % 1) < 0.05 ? 0.5 * Math.Sin(2 * Math.PI * 800 * t) : 0;
            return (tick, tick);
        }), "ticks");
        var clip = ClipOf(asset, 10) with { Speed = from, EndSpeed = to, KeepPitch = true };
        var mixed = MixAll(Project(asset, clip));
        var worst = 0.0;
        for (var source = 1; source < 10; source++)
        {
            // Each tick on the timeline where the pictures have that moment of the source.
            var expected = clip.TimelineAt(source);
            var (first, last) = (Math.Max(0, (int)((expected - 0.2) * Rate)), Math.Min(mixed.Length / 2, (int)((expected + 0.2) * Rate)));
            var found = -1;
            for (var i = first; i < last; i++)
            {
                if (Math.Abs(mixed[2 * i]) > 0.15)
                {
                    found = i;
                    break;
                }
            }

            found.Should().BeGreaterThan(0, "the tick at {0} s of the source is heard near {1} s", source, expected);
            worst = Math.Max(worst, Math.Abs(((double)found / Rate) - expected));
        }

        output.WriteLine($"{from}x to {to}x: at worst {worst * 1000:0} ms from its picture");
        worst.Should().BeLessThan(0.04, "a piece may start 11 ms from its place, and a tick's first piece fades in over 21 ms");
    }

    [Theory]
    [InlineData(0.5, 2, false)]
    [InlineData(0.25, 8, false)]
    [InlineData(0.5, 2, true)]
    public void A_ramped_clip_is_as_long_as_the_clip_and_steady_in_level(double from, double to, bool effects)
    {
        TestMedia.Require();
        var asset = ProjectFactory.ReadAsset(Tone("ramp-long.wav", 40, 613), "ramp");
        var clip = ClipOf(asset, 10) with { Speed = from, EndSpeed = to, KeepPitch = true, Sound = effects ? new ClipSound(Limiter: true, LimiterCeilingDb: -3) : null };
        var mixed = MixAll(Project(asset, clip));
        var steady = 0.3 / Math.Sqrt(2);
        for (var at = 0.1; at < clip.Duration - 0.1; at += 0.2)
        {
            Db(Rms(mixed, at, at + 0.1) / steady).Should().BeApproximately(0, 0.5, "a tone stays a tone at {0} s", at);
        }

        // Read in one go, a block at a time, or in other blocks: the same.
        Worst(MixAll(Project(asset, clip), 480), mixed).Should().Be(0);
    }

    [Fact]
    public void A_seek_into_a_ramp_lands_on_the_same_moment()
    {
        TestMedia.Require();
        var asset = ProjectFactory.ReadAsset(Wav("seek-ticks.wav", 12, (_, t) =>
        {
            var tick = (t % 1) < 0.05 ? 0.5 * Math.Sin(2 * Math.PI * 800 * t) : 0;
            return (tick, tick);
        }), "ticks");
        var clip = ClipOf(asset, 10) with { Speed = 0.5, EndSpeed = 2, KeepPitch = true };
        var project = Project(asset, clip);
        var whole = MixAll(project);
        var from = TimelineMixer.Sample(2.2);
        var part = MixAll(project, 1024, from + (Rate * 3), from);
        Db(Rms(part, 0, 3)).Should().BeApproximately(Db(Rms(whole.AsSpan((int)from * 2, part.Length), 0, 3)), 0.5);
        var expected = clip.TimelineAt(4) - 2.2;
        var found = Array.FindIndex(part, (int)((expected - 0.2) * Rate) * 2, s => Math.Abs(s) > 0.15) / 2.0 / Rate;
        found.Should().BeApproximately(expected, 0.04);
    }

    [Theory]
    [InlineData(0.5, 2)]
    [InlineData(0.25, 1)]
    public void A_ramp_keeps_the_pitch_of_a_voice_too(double from, double to)
    {
        TestMedia.Require();

        // A low voice with harmonics, not a tone with a whole number of samples to its wave.
        var asset = ProjectFactory.ReadAsset(Wav("hum.wav", 30, (_, t) =>
        {
            var phase = 2 * Math.PI * 131.7 * t;
            var value = (0.3 * Math.Sin(phase)) + (0.1 * Math.Sin(2 * phase)) + (0.05 * Math.Sin(3 * phase));
            return (value, value);
        }), "hum");
        var clip = ClipOf(asset, 10) with { Speed = from, EndSpeed = to, KeepPitch = true };
        var mixed = MixAll(Project(asset, clip));
        var worst = 0.0;
        for (var at = 0.2; at < clip.Duration - 0.5; at += 0.3)
        {
            worst = Math.Max(worst, Math.Abs(1200 * Math.Log2(TestMediaFiles.Frequency(mixed.AsSpan((int)(at * Rate) * 2, Rate / 5 * 2)) / 131.7)));
        }

        output.WriteLine($"{from}x to {to}x: at worst {worst:0.00} cents off");
        worst.Should().BeLessThan(10);
    }

    [Fact]
    public void Setting_a_ramp_keeps_its_pitch_and_an_old_ramp_is_still_resampled()
    {
        var project = TestProjects.Project();
        var clip = project.Tracks.First(t => t.HasSound).Clips.First();
        var ramped = TransitionEdits.SetSpeed(project, clip.Id, 1, 2);
        ((Clip)ramped.Find(clip.Id)!.Value.Item).KeepPitch.Should().BeTrue();

        // A project from before (the switch off) goes on being resampled: its pitch follows the speed.
        TestMedia.Require();
        var asset = ProjectFactory.ReadAsset(Tone("old-ramp.wav", 12, 440), "old");
        var old = ClipOf(asset, 6) with { Speed = 1, EndSpeed = 3 };
        var mixed = MixAll(Project(asset, old));
        var middle = mixed.AsSpan((TimelineMixer.Sample(old.Duration / 2) is var at ? (int)at : 0) * 2, 9600);
        TestMediaFiles.Frequency(middle).Should().BeApproximately(880, 30);
    }

    // --- Loudness ----------------------------------------------------------------------------------------------------
    [Fact]
    public void The_meter_reads_what_FFmpeg_does_and_the_standards_tone()
    {
        TestMedia.Require();

        // BS.1770's own check: a 997 Hz sine at -23 dBFS on both channels is -23 LUFS.
        var level = Math.Pow(10, -23.0 / 20);
        var path = Tone("r128.wav", 12, 997, level);
        var meter = new LoudnessMeter();
        meter.Add(ReadAll(path));
        meter.IntegratedLufs.Should().BeApproximately(-23, 0.1);
        meter.PeakDb.Should().BeApproximately(-23, 0.1);
        new LoudnessMeter().IntegratedLufs.Should().Be(double.NegativeInfinity);

        var busy = Busy("meter.wav", 20);
        var mine = new LoudnessMeter();
        mine.Add(ReadAll(busy));
        var theirs = Loudness.Measure(busy, ct: TestContext.Current.CancellationToken)[0];
        output.WriteLine($"This meter {mine.IntegratedLufs:0.00} LUFS, FFmpeg {theirs.IntegratedLufs:0.00} LUFS");
        mine.IntegratedLufs.Should().BeApproximately(theirs.IntegratedLufs, 0.5);
    }

    [Fact]
    public void Even_out_brings_a_clip_to_the_usual_level()
    {
        TestMedia.Require();
        var asset = ProjectFactory.ReadAsset(Busy("quiet.wav", 20), "quiet");
        var clip = ClipOf(asset, 20) with { Volume = 1.5, FadeIn = 3, VolumeKeys = [new(0, 0.1)] };
        var measured = SoundEdits.Measure(asset, clip, ct: TestContext.Current.CancellationToken);
        var evened = SoundEdits.EvenOut(clip, measured.IntegratedLufs, measured.PeakDb);
        evened.Sound!.GainDb.Should().BeApproximately(SoundEdits.EvenOutLufs - measured.IntegratedLufs, 0.1);
        evened.Volume.Should().Be(1.5, "the level the user set is theirs, on top");

        var bare = evened with { Volume = 1, FadeIn = 0, VolumeKeys = null };
        var meter = new LoudnessMeter();
        meter.Add(MixAll(Project(asset, bare)));
        meter.IntegratedLufs.Should().BeApproximately(SoundEdits.EvenOutLufs, 1);

        SoundEdits.EvenOut(clip, double.NegativeInfinity, double.NegativeInfinity).Should().Be(clip, "silence stays as it is");
        SoundEdits.EvenOut(clip, -70, -60).Should().Be(clip);
        SoundEdits.EvenOut(clip, -40, -2).Sound!.Limiter.Should().BeTrue("24 dB up would clip its peaks");
        SoundEdits.EvenOut(clip, -18, -20).Sound!.Limiter.Should().BeFalse();
        SoundEdits.EvenOut(clip, -100.5, 0).Should().Be(clip);
        SoundEdits.EvenOut(clip, -50, -40).Sound!.GainDb.Should().Be(ClipSound.MostGainDb);
    }

    [Theory]
    [InlineData(ExportLoudness.Online)]
    [InlineData(ExportLoudness.YouTube)]
    [InlineData(ExportLoudness.Broadcast)]
    public async Task An_exports_loudness_is_within_one_LU_of_its_target(double target)
    {
        TestMedia.Require();
        var voice = ProjectFactory.ReadAsset(Busy("loud-voice.wav", 20), "voice");
        var music = ProjectFactory.ReadAsset(Tone("loud-music.wav", 20, 330, 0.05), "music");
        var project = new VideoProject(VideoProject.CurrentVersion, new ProjectCanvas(320, 180, 30), [voice, music], [
            new Track("v", TrackKind.Audio, "Voice", [ClipOf(voice, 20)]),
            new Track("m", TrackKind.Audio, "Music", [ClipOf(music, 20, "m")]),
        ]);
        foreach (var preset in new[] { ExportPresets.M4a, ExportPresets.Wav, ExportPresets.Mp3 })
        {
            var path = OutFile(FormattableString.Invariant($"loud{target}{preset.Extension}"));
            var result = await Export(project, preset, path, target);
            var (id, measured) = Loudness.Measure(path, ct: TestContext.Current.CancellationToken).Single();
            output.WriteLine($"{preset.Id}: target {target}, the mix was {result.MeasuredLufs:0.0}, gain {result.LoudnessGainDb:0.0} dB, the file is {measured.IntegratedLufs:0.00} LUFS with peaks at {measured.TruePeakDb:0.0} dBTP");
            measured.IntegratedLufs.Should().BeApproximately(target, 1, "{0} is at its target (stream {1})", preset.Id, id);
            result.MeasuredLufs.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task A_loudness_target_holds_the_peaks_and_makes_up_for_it()
    {
        TestMedia.Require();

        // Quiet but for a few short knocks: turned up to the target, they would go far past full scale.
        var asset = ProjectFactory.ReadAsset(Wav("knocks.wav", 20, (_, t) =>
        {
            var knock = (t % 5) < 0.002 ? 0.9 : 0;
            var bed = 0.05 * Math.Sin(2 * Math.PI * 1000 * t);
            return (knock + bed, knock + bed);
        }), "knocks");
        var project = Project(asset, ClipOf(asset, 20));
        var path = OutFile("knocks-out.wav");
        var result = await Export(project, ExportPresets.Wav, path, ExportLoudness.Online);
        var samples = ReadAll(path);
        samples.Max(Math.Abs).Should().BeLessThanOrEqualTo((float)Math.Pow(10, ExportLoudness.CeilingDb / 20) + 1e-3f);
        var (_, measured) = Loudness.Measure(path, ct: TestContext.Current.CancellationToken).Single();
        output.WriteLine($"Knocks: mix {result.MeasuredLufs:0.0} LUFS, gain {result.LoudnessGainDb:0.0} dB, file {measured.IntegratedLufs:0.00} LUFS, peak {Db(samples.Max(Math.Abs)):0.0} dBFS");
        measured.IntegratedLufs.Should().BeApproximately(ExportLoudness.Online, 1);
        result.LoudnessGainDb.Should().BeGreaterThan(3, "turning it up is what this test is about");
        samples.Length.Should().Be(20 * Rate * 2, "the limiter's delay is taken out and nothing is lost");
    }

    [Fact]
    public async Task Silence_stays_silent_under_a_loudness_target()
    {
        TestMedia.Require();
        var asset = ProjectFactory.ReadAsset(Wav("silence.wav", 3, (_, _) => (0, 0)), "silence");
        var path = OutFile("silence-out.wav");
        var result = await Export(Project(asset, ClipOf(asset, 3)), ExportPresets.Wav, path, ExportLoudness.Online);
        result.LoudnessGainDb.Should().BeNull();
        ReadAll(path).Max(Math.Abs).Should().Be(0);
    }

    // --- Ducking -----------------------------------------------------------------------------------------------------
    private (VideoProject Project, List<(double From, double To)> Speech) DuckingProject()
    {
        List<(double From, double To)> speech = [(1, 3), (5, 6.5), (6.8, 7.5), (10, 12)];
        var voice = ProjectFactory.ReadAsset(Wav("duck-voice.wav", 14, (i, t) =>
        {
            var on = speech.Any(s => t >= s.From && t < s.To);
            var noise = (new Random(i).NextDouble() - 0.5) * 0.002;
            var phase = 2 * Math.PI * 140 * t;
            var said = on ? (0.3 * Math.Sin(phase)) + (0.12 * Math.Sin(2 * phase)) + (0.05 * Math.Sin(3 * phase)) : 0;
            return (said + noise, said + noise);
        }), "voice");
        var music = ProjectFactory.ReadAsset(Tone("duck-music.wav", 14, 440, 0.2), "music");
        var project = new VideoProject(VideoProject.CurrentVersion, new ProjectCanvas(320, 180, 30), [voice, music], [
            new Track("voice", TrackKind.Audio, "Microphone", [ClipOf(voice, 14, "v")]),
            new Track("music", TrackKind.Audio, "Music", [ClipOf(music, 14, "m")], Background: true),
        ]);
        return (project, speech);
    }

    private static Func<MediaAsset, int, SoundLevels?> Levels() =>
        (asset, stream) => SoundLevels.FromSamples(TranscriptAudio.RenderStream(asset, stream, null, default));

    [Fact]
    public void Ducking_finds_the_voice_and_makes_keyframes_on_the_music()
    {
        TestMedia.Require();
        var (project, _) = DuckingProject();
        var voice = SilenceFinder.VoiceTrack(project)!;
        voice.Id.Should().Be("voice");
        Ducking.BackgroundTracks(project, voice).Select(t => t.Id).Should().Equal("music");
        var spans = Ducking.SpeechSpans(project, voice, Levels());
        spans.Should().HaveCount(3, "a breath of 0.3 s isn't a pause");
        spans[0].From.Should().BeApproximately(1, 0.06);
        spans[0].To.Should().BeApproximately(3, 0.06);

        var ducked = Ducking.Duck(project, ["music"], spans);
        var keys = ((Clip)ducked.Find("m")!.Value.Item).VolumeKeys!;
        keys[0].Should().BeEquivalentTo(new MotionKey(0, 1));
        keys.Should().OnlyContain(k => k.Value >= new DuckOptions().Level - 1e-3 && k.Value <= 1);
        ((Clip)ducked.Find("v")!.Value.Item).VolumeKeys.Should().BeNull("the voice isn't touched");
        keys.Select(k => k.Time).Should().BeInAscendingOrder();

        // Each is an ordinary keyframe: one changed by hand changes the sound there, and the rest stays.
        var edited = SoundEdits.SetVolumeAt((Clip)ducked.Find("m")!.Value.Item, 2, 0.5, 30);
        VolumeCurve.At(edited.VolumeKeys, 2).Should().Be(0.5);
    }

    [Fact]
    public void Music_goes_at_least_10_dB_down_under_speech_and_is_back_within_the_release()
    {
        TestMedia.Require();
        var (project, _) = DuckingProject();
        var voice = SilenceFinder.VoiceTrack(project)!;
        var options = new DuckOptions(-12, 0.3, 0.5);
        var ducked = Ducking.Duck(project, ["music"], Ducking.SpeechSpans(project, voice, Levels(), options), options);
        var music = MixAll(ProjectExporter.Solo(ducked, "music"));
        var open = 0.2 / Math.Sqrt(2);
        foreach (var (from, to) in new[] { (1.1, 2.9), (5.1, 7.4), (10.1, 11.9) })
        {
            var down = Db(Rms(music, from, to) / open);
            output.WriteLine($"{from}-{to} s: music {down:0.0} dB");
            down.Should().BeLessThanOrEqualTo(-10, "under the voice from {0} s", from);
        }

        // 0.3 s before speech it hasn't started down; the release after it, it is back.
        Db(Rms(music, 0.1, 0.65) / open).Should().BeApproximately(0, 0.1);
        foreach (var end in new[] { 3.0, 12.0 })
        {
            Db(Rms(music, end + options.Release + 0.06, end + options.Release + 0.3) / open).Should().BeApproximately(0, 0.2, "back {0} s after the voice stops at {1} s", options.Release, end);
        }
    }

    [Fact]
    public void Ducking_works_across_cuts_and_the_keys_go_with_the_music()
    {
        TestMedia.Require();
        var (project, _) = DuckingProject();
        var voice = SilenceFinder.VoiceTrack(project)!;
        var ducked = Ducking.Duck(project, ["music"], Ducking.SpeechSpans(project, voice, Levels()));
        var cut = TimelineEdits.DeleteRange(ducked, 3.5, 4.5);
        var level = (Clip clip, double t) => VolumeCurve.At(clip.VolumeKeys, t - clip.Start);
        var after = cut.Tracks.First(t => t.Id == "music").Clips.Single(c => c.Covers(5));
        level(after, 5).Should().BeApproximately(new DuckOptions().Level, 1e-3, "what was at 6 s, under the voice, is at 5 s now");
        var moved = TimelineEdits.Move(cut, after.Id, after.Start + 1, withLinked: false);
        var there = (Clip)moved.Find(after.Id)!.Value.Item;
        level(there, 6).Should().BeApproximately(new DuckOptions().Level, 1e-3, "the keys go with their clip");
    }

    [Fact]
    public void Ducking_a_clip_the_voice_never_touches_clears_its_keys_and_replaces_older_ones()
    {
        var clip = new Clip { Id = "m", AssetId = "a", SourceOut = 5, Start = 20, VolumeKeys = [new(0, 0.1)] };
        Ducking.Keys(clip, [(1.0, 3.0)], new DuckOptions()).Should().BeNull();
        var keys = Ducking.Keys(clip with { Start = 0 }, [(1.0, 3.0)], new DuckOptions(-12, 0.25, 0.5))!;
        keys.Select(k => (k.Time, k.Value)).Should().Equal((0, 1), (0.75, 1), (1, new DuckOptions(-12).Level.RoundedForKeys()), (3, new DuckOptions(-12).Level.RoundedForKeys()), (3.5, 1), (5, 1));

        // A gap the music can't come back in stays down.
        var close = Ducking.Keys(clip with { Start = 0 }, [(1.0, 2.0), (2.5, 3.0)], new DuckOptions(-12, 0.25, 0.5))!;
        close.Where(k => k.Time is > 2 and < 2.5).Should().BeEmpty();
        VolumeCurve.At(close, 2.25).Should().BeApproximately(new DuckOptions(-12).Level, 1e-3);
    }

    // --- Tracks out --------------------------------------------------------------------------------------------------
    private VideoProject Tones()
    {
        var file = _media.Stamped("tracks.mp4", 3, tracks: ["Microphone", "System audio"], tones: new HashSet<int> { 0, 1 });
        return ProjectFactory.FromRecording(ProjectFactory.ReadAsset(file));
    }

    private static string Ffprobe(string path)
    {
        var tool = Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffprobe.exe");
        Assert.SkipUnless(File.Exists(tool), "ffprobe isn't in tools\\ffmpeg.");
        var start = new ProcessStartInfo(tool, ["-v", "error", "-show_entries", "stream=index,codec_type,codec_name:stream_tags=title,handler_name", "-of", "json", path])
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start)!;
        var text = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return text;
    }

    private static List<(string Kind, string Title, string Codec)> Streams(string path)
    {
        using var json = JsonDocument.Parse(Ffprobe(path));
        return [.. json.RootElement.GetProperty("streams").EnumerateArray().Select(s =>
        {
            var title = "";
            if (s.TryGetProperty("tags", out var tags))
            {
                foreach (var tag in tags.EnumerateObject())
                {
                    if (tag.Name.Equals("title", StringComparison.OrdinalIgnoreCase) || tag.Name.Equals("handler_name", StringComparison.OrdinalIgnoreCase))
                    {
                        title = tag.Value.GetString() ?? "";
                    }
                }
            }

            return (s.GetProperty("codec_type").GetString()!, title, s.GetProperty("codec_name").GetString()!);
        })];
    }

    public static TheoryData<string> Containers => new() { "mp4", "mkv", "webm-vp9" };

    [Theory]
    [MemberData(nameof(Containers))]
    public async Task Each_track_is_a_titled_stream_that_matches_a_solo_export_of_it(string presetId)
    {
        TestMedia.Require();
        await CashGear.Capture.Tests.MachineTurn.RunAsync(async () => // VP9 works every core
        {
            await TitledStreams(presetId);
            return 0;
        });
    }

    private async Task TitledStreams(string presetId)
    {
        var project = Tones();
        var preset = ExportPresets.Find(presetId)!;
        var path = OutFile("streams-" + presetId + preset.Extension);
        await Export(project, preset, path, tracks: SoundTracksOut.Streams);
        var streams = Streams(path);
        var sounds = project.Tracks.Where(t => t.HasSound).ToList();
        sounds.Should().HaveCountGreaterThanOrEqualTo(2);
        streams.Where(s => s.Kind == "video").Should().ContainSingle();
        var audio = streams.Where(s => s.Kind == "audio").ToList();
        audio.Select(s => s.Title).Should().Equal(["Mix", .. sounds.Select(t => t.Name)]);

        var mix = ReadAll(path, 1);
        for (var i = 0; i < sounds.Count; i++)
        {
            var solo = OutFile($"solo-{presetId}-{i}{preset.Extension}");
            await Export(ProjectExporter.Solo(project, sounds[i].Id), preset, solo);
            var own = ReadAll(path, 2 + i);
            var alone = ReadAll(solo, 1);
            own.Length.Should().Be(alone.Length);
            Worst(own, alone).Should().BeLessThan(0.01, "stream {0} is what exporting {1} alone gives", 2 + i, sounds[i].Name);
            Rms(own, 0.5, 2.5).Should().BeGreaterThan(0.001, "{0} has its sound", sounds[i].Name);
        }

        var mixAlone = OutFile("mix-" + presetId + preset.Extension);
        await Export(project, preset, mixAlone);
        Worst(mix, ReadAll(mixAlone, 1)).Should().BeLessThan(0.01, "the first sound stream is the mix, as a plain export has it");
    }

    [Fact]
    public async Task Each_track_is_a_WAV_file_beside_the_export()
    {
        TestMedia.Require();
        var project = Tones();
        var path = OutFile("stems.mp4");
        var result = await Export(project, ExportPresets.Mp4H264Project, path, tracks: SoundTracksOut.WavFiles);
        var sounds = project.Tracks.Where(t => t.HasSound).ToList();
        result.TrackPaths.Should().HaveCount(sounds.Count);
        Streams(path).Count(s => s.Kind == "audio").Should().Be(1, "the file itself has the mix");
        for (var i = 0; i < sounds.Count; i++)
        {
            var file = result.TrackPaths![i];
            Path.GetFileName(file).Should().Be($"stems.{sounds[i].Name}.wav");
            File.Exists(file).Should().BeTrue();
            File.Exists(file + ".partial").Should().BeFalse();
            var own = ReadAll(file);
            var alone = MixAll(ProjectExporter.Solo(project, sounds[i].Id));
            own.Length.Should().Be(alone.Length);
            Worst(own, alone).Should().BeLessThan(1.5 / 8_388_608, "the stem is the mixer's samples for that track");
            Streams(file).Single().Codec.Should().Be("pcm_s24le");
        }
    }

    [Fact]
    public async Task A_muted_track_goes_out_as_it_is_heard_and_a_sound_only_file_can_have_stems()
    {
        TestMedia.Require();
        var project = Tones();
        var first = project.Tracks.First(t => t.HasSound);
        project = project.WithTrack(first with { Muted = true });
        ProjectExporter.HeardTracks(project).Should().NotContain(t => t.Id == first.Id);
        var path = OutFile("heard.flac");
        var result = await Export(project, ExportPresets.Flac, path, tracks: SoundTracksOut.WavFiles);
        result.TrackPaths.Should().HaveCount(project.Tracks.Count(t => t.HasSound) - 1);
        Streams(path).Single().Codec.Should().Be("flac");

        // A file of sound alone holds one stream, whatever is asked.
        var m4a = OutFile("one.m4a");
        await Export(project, ExportPresets.M4a, m4a, tracks: SoundTracksOut.Streams);
        Streams(m4a).Count.Should().Be(1);
    }

    [Fact]
    public async Task A_cancelled_export_leaves_no_track_files()
    {
        TestMedia.Require();
        var project = Tones();
        var path = OutFile("cancelled.mp4");
        using var cancel = new CancellationTokenSource();
        var running = Exporter().ExportAsync(new ExportRequest(project, ExportPresets.Mp4H264Project, QualityPreset.High, path, TracksOut: SoundTracksOut.WavFiles), new Progress<ExportProgress>(_ => cancel.Cancel()), cancel.Token);
        await cancel.CancelAsync();
        var act = async () => await running;
        await act.Should().ThrowAsync<OperationCanceledException>();
        Directory.GetFiles(_media.Root, "cancelled*").Should().BeEmpty();
    }

    [Fact]
    public async Task Sound_only_WAV_and_FLAC_are_what_the_mixer_mixed()
    {
        TestMedia.Require();
        var project = Tones();
        var mixed = MixAll(project);
        foreach (var preset in new[] { ExportPresets.Wav, ExportPresets.Flac })
        {
            var path = OutFile("alone" + preset.Extension);
            var result = await Export(project, preset, path);
            result.Encoder.Should().BeNull();
            result.Frames.Should().Be(0);
            var read = ReadAll(path);
            read.Length.Should().Be(mixed.Length);
            Worst(read, mixed).Should().BeLessThan(1.5 / 8_388_608, "{0} keeps 24 bits of every sample", preset.Id);
        }
    }

    [Fact]
    public void The_presets_are_listed_and_know_what_they_can_hold()
    {
        ExportPresets.All.Should().Contain(p => p.Id == "wav").And.Contain(p => p.Id == "flac").And.Contain(p => p.Id == "mkv");
        ExportPresets.Wav.CanHoldTracks.Should().BeFalse();
        ExportPresets.Mkv.CanHoldTracks.Should().BeTrue();
        ExportPresets.Find("mkv")!.Extension.Should().Be(".mkv");
        ExportPresets.Wav.HasVideo.Should().BeFalse();
    }

    // --- Keeping projects --------------------------------------------------------------------------------------------
    [Fact]
    public void Everything_new_is_saved_and_read_again()
    {
        var clip = new Clip
        {
            Id = "c",
            AssetId = "rec",
            Stream = 1,
            SourceOut = 10,
            KeepPitch = true,
            EqualPowerIn = true,
            VolumeKeys = [new(1, 0.5, MotionEasing.EaseOut)],
            Sound = new ClipSound(GainDb: -3.5, NoiseRemoval: true, NoiseStrength: 0.8, Gate: true, GateThresholdDb: -50, Compressor: true, CompressorThresholdDb: -20, CompressorRatio: 4, Limiter: true, LimiterCeilingDb: -2, Eq: new SoundEq(90, 1, 2, 3)),
        };
        var project = new VideoProject(VideoProject.CurrentVersion, new ProjectCanvas(320, 180, 30), [TestProjects.Recording()], [new Track("t", TrackKind.Audio, "Music", [clip], Background: true)]);
        var again = ProjectJson.Deserialize(ProjectJson.Serialize(project));
        var read = (Clip)again.Find("c")!.Value.Item;
        read.Sound.Should().Be(clip.Sound);
        read.VolumeKeys.Should().BeEquivalentTo(clip.VolumeKeys);
        read.KeepPitch.Should().BeTrue();
        read.EqualPowerIn.Should().BeTrue();
        again.Track("t")!.Background.Should().BeTrue();
        ProjectJson.Serialize(again).Should().Equal(ProjectJson.Serialize(project));
    }

    [Fact]
    public void A_version_4_project_comes_up_to_version_5_with_nothing_changed()
    {
        const string old = """{ "version": 4, "canvas": { "width": 320, "height": 180, "fps": 30 }, "assets": [], "tracks": [ { "id": "t", "kind": "Audio", "name": "Voice", "items": [ { "type": "clip", "id": "c", "assetId": "a", "sourceOut": 2, "speed": 1, "endSpeed": 2, "volume": 1 } ] } ] }""";
        var project = ProjectJson.Deserialize(System.Text.Encoding.UTF8.GetBytes(old));
        project.Version.Should().Be(VideoProject.CurrentVersion);
        VideoProject.CurrentVersion.Should().Be(5);
        var clip = (Clip)project.Find("c")!.Value.Item;
        clip.KeepPitch.Should().BeFalse("its ramp stays resampled");
        clip.EqualPowerIn.Should().BeFalse("its crossfades stay straight");
        clip.Sound.Should().BeNull();
        clip.VolumeKeys.Should().BeNull();
        project.Track("t")!.Background.Should().BeFalse();
    }

    // --- The preview's device ----------------------------------------------------------------------------------------
    [Fact]
    public void The_preview_plays_on_a_chosen_device_and_a_gone_one_gives_way_to_the_default()
    {
        using var devices = new NAudio.CoreAudioApi.MMDeviceEnumerator();
        var active = devices.EnumerateAudioEndPoints(NAudio.CoreAudioApi.DataFlow.Render, NAudio.CoreAudioApi.DeviceState.Active).Select(d => d.ID).ToList();
        Assert.SkipWhen(active.Count == 0, "Windows has no output device.");

        // Silence goes out, so nothing is heard; the clock moves only when a device takes it.
        foreach (var id in active.Take(2).Append("{gone}"))
        {
            using var chosen = new WasapiAudioOutput(id);
            chosen.Start(block => block.Clear());
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (chosen.Played < 960 && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(5);
            }

            chosen.Stop();
            chosen.Played.Should().BeGreaterThanOrEqualTo(960, "device {0} took the sound", id);
            chosen.Failure.Should().BeNull();
        }
    }

    [Fact]
    public void Changing_the_output_goes_on_playing_from_the_same_place()
    {
        TestMedia.Require();
        var asset = ProjectFactory.ReadAsset(Tone("change.wav", 10), "change");
        var project = Project(asset, ClipOf(asset, 10));
        var made = 0;
        using var engine = new PlaybackEngine(() => project, () =>
        {
            made++;
            return new CountingOutput();
        }, NullLogger.Instance);
        engine.Play(2);
        Thread.Sleep(50);
        engine.ChangeOutput();
        engine.IsPlaying.Should().BeTrue();
        made.Should().Be(2, "the next sound goes to a new device");
        engine.Position.Should().BeInRange(2, 3);
        engine.Pause();
        engine.ChangeOutput();
        engine.IsPlaying.Should().BeFalse();
        made.Should().Be(2, "it opens when it next plays");
    }

    private sealed class CountingOutput : IAudioOutput
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
}

internal static class DuckingTestValues
{
    /// <summary>A level as the keys hold it.</summary>
    public static double RoundedForKeys(this double level) => Math.Round(level, 4);
}
