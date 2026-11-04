using System.IO;
using System.Text.RegularExpressions;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Media.Tests;
using CashGear.Capture.Tests;
using CashGear.Capture.Transcription;
using CashGear.Capture.Transcription.Tests;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using Microsoft.Extensions.Logging.Abstractions;
using SoundLevels = CashGear.Capture.VideoEditor.Playback.SoundLevels;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>
/// Phase 21's acceptance, on speech: Windows' own voice talking (it marks when each word starts, which is the truth to
/// check against), written into a real recording, heard by Whisper. A real voice is by hand (TESTING.md).
/// </summary>
public sealed partial class TranscriptAcceptanceTests : IDisposable
{
    /// <summary>About five minutes of a talk-over, said at the voice's normal rate.</summary>
    internal const string Talk = """
        Today I want to show you how the new editor works. First we open a recording from the tray, and the timeline shows every track of it. The screen is on top, with the webcam next to it, and the sound from the microphone sits below them. If you press the space bar the preview plays, and the playhead moves along the ruler. Now look at the settings on the right. We can choose a background, make the corners round, and add a soft shadow under the picture. Most people leave these alone, because the default look is good enough. Later on we will talk about zooms, which follow the pointer around the screen. Remember that nothing here ever changes the original file, so you can always go back and try something else.

        Let's start with the simplest job, which is taking out a mistake. Suppose I stumble in the middle of a sentence. I find the place on the timeline, mark the start with the letter I, mark the end with the letter O, and press delete. Everything after the mark moves back to close the gap, on every track at once. That keeps the sound and the picture together, even when the recording has several tracks. If the track is locked, it stays exactly where it was. You can undo that with control Z, as many times as you want, and bring it back again with control Y.

        The next thing most people want is a title at the beginning. Choose insert and then text, and a box appears over the picture. Type the words you want, and drag the box to where it looks right. The panel on the right sets the font, the size and the colour. Arabic text joins its letters and reads from right to left, just as it should. A title can fade in and out, and it can move across the screen with a few keyframes. If it covers something important, drag it somewhere else, or shorten the time it stays on the screen.

        Captions are a little different, because they come from what you say. The editor listens to the microphone track on this computer, and nothing is ever sent anywhere. The first time, it has to download a speech model, which can take a minute or two on a slow connection. After that, it makes the captions by itself, with the time of each word. You can correct a word by typing, split a caption at the playhead, or join it to the next one. When you export the video, the captions can be burned into the picture, or saved beside it as a separate file.

        Sound deserves some attention as well. Each track has its own volume, and each clip can fade in and out. If there is music underneath, keep it low, so that the voice stays clear. A pair of headphones helps you hear small problems, like a fan in the background or a click from the keyboard. The meter on the screen shows how loud the mix is. Try to keep the loudest parts just under the top, and never let them touch it for long. A short test export is the quickest way to hear what your viewers will hear.

        When everything looks right, it is time to export. The window offers a few presets, and for most recordings the first one is fine. A higher quality takes longer and makes a larger file, but the picture stays sharper when things move quickly. The vertical preset turns the same recording into a phone video, and it follows the pointer so the action stays in the picture. You can export only the marked range, which is handy for a short clip on social media. While it runs, you can keep working in another window, because the export has its own copy of the project.

        Zooms are where a recording starts to feel finished. Select a stretch of the timeline, choose insert and then zoom, and the picture moves in on the place you were working. With auto zoom, the editor does this by itself, from your clicks and your typing, and follows the pointer on a soft spring. If a zoom is too close, drag the corner of its frame on the preview, or lower its scale in the panel. A little motion blur makes the movement look natural, especially at a low frame rate. Between two zooms that are close together, the camera pans across instead of pulling all the way back out.

        One last thing before I stop. Save the project often, even though the editor keeps a copy for you. The project file does not contain the video itself, only the list of what you did, so it stays small. If you want to move it to another computer, choose save with media, and the recordings go inside the file. That is everything you need for your first video. Take your time, try things out, and remember that every change can be undone. Thank you for watching, and I will see you in the next one.
        """;

    /// <summary>A hand-labelled clip: the voice says these, the fillers among them written out as it should say them.</summary>
    internal const string FillerTalk = """
        So, um, today I want to show you, uh, how the editor works. Um, the first thing is the timeline, and, uh, it shows every track. If you, um, press the space bar, the preview plays. Uh, now look at the settings. We can, um, choose a background, and, uh, make the corners round. Most people, um, leave these alone, because, uh, the default look is good enough. Um, later on we will talk about zooms. They follow the, uh, pointer around the screen. Nothing here, um, ever changes the original file. So, uh, you can always go back. Um, let's start with the simplest job. I find the place, uh, on the timeline, and, um, mark the start. Then, uh, I press delete. Everything, um, moves back to close the gap. That, uh, keeps the sound and the picture together. Um, you can undo that. And, uh, you can bring it back. The next thing, um, is a title. Uh, choose insert and then text. Type the words, um, you want. Uh, thank you for watching.
        """;

    private readonly TestMediaFiles _media = new("cashgear-transcript-");

    public void Dispose() => _media.Dispose();

    private static string Models => Path.Combine(FindRepository(), "tools", "whisper");

    private static string FindRepository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CashGear.Capture.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository wasn't found above the tests.");
    }

    private static string Model(WhisperModel model)
    {
        var path = Path.Combine(Models, model.File);
        Assert.SkipUnless(File.Exists(path) && new FileInfo(path).Length == model.Bytes, $"{model.File} isn't in tools\\whisper (run tools\\get-whisper-models.ps1).");
        return path;
    }

    private static string[] Plain(string text) => [.. WordPattern().Split(text.ToLowerInvariant()).Where(w => w.Length > 0)];

    [GeneratedRegex(@"[^\p{L}\p{N}']+")]
    private static partial Regex WordPattern();

    /// <summary>A recording whose microphone says <paramref name="speech"/>, with numbered pictures.</summary>
    private string RecordingOf(string name, float[] speech)
    {
        var path = Path.Combine(_media.Root, name);
        new StampedRecording
        {
            Path = path,
            Seconds = (speech.Length / 16_000.0) + 0.2,
            Format = new VideoFormat(320, 180, 30),
            Tracks = ["Microphone"],
            SoundAt = sample =>
            {
                var position = sample / 3.0;
                var k = (long)position;
                var f = (float)(position - k);
                return k + 1 < speech.Length ? (speech[k] * (1 - f)) + (speech[k + 1] * f) : 0;
            },
        }.Write();
        return path;
    }

    /// <summary>Pairs of (position in the heard words, position in the true words) that are the same word, in order.</summary>
    private static List<(int Heard, int True)> Matched(IReadOnlyList<string> heard, IReadOnlyList<string> truth)
    {
        var table = new int[heard.Count + 1, truth.Count + 1];
        for (var i = heard.Count - 1; i >= 0; i--)
        {
            for (var j = truth.Count - 1; j >= 0; j--)
            {
                table[i, j] = heard[i] == truth[j] ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var pairs = new List<(int, int)>();
        for (int i = 0, j = 0; i < heard.Count && j < truth.Count;)
        {
            if (heard[i] == truth[j])
            {
                pairs.Add((i, j));
                i++;
                j++;
            }
            else if (table[i + 1, j] >= table[i, j + 1])
            {
                i++;
            }
            else
            {
                j++;
            }
        }

        return pairs;
    }

    /// <summary>The true words as plain words, each with when it starts; a mark may hold two (a number, "don't").</summary>
    private static List<(string Word, double Start)> TrueWords(SaidSpeech speech) =>
        [.. speech.Words.SelectMany(w => Plain(w.Text).Select(p => (p, w.Start)))];

    private static WhisperTranscriber Whisper() => new(NullLogger<WhisperTranscriber>.Instance);

    [Fact]
    public async Task Deleting_ten_words_of_a_five_minute_recording_cuts_exactly_those_words_without_a_click()
    {
        var model = Model(WhisperModels.Small);
        TestMedia.Require();
        var speech = await Speech.SayWithWordsAsync(Talk);
        (speech.Samples.Length / 16_000.0).Should().BeGreaterThan(280, "about five minutes");
        var asset = ProjectFactory.ReadAsset(RecordingOf("talk.mp4", speech.Samples));
        var project = ProjectFactory.FromRecording(asset);
        var stream = project.Tracks.First(t => t.HasSound).Clips.First().Stream;
        var samples = TranscriptAudio.RenderStream(asset, stream, ct: TestContext.Current.CancellationToken);
        var levels = SoundLevels.FromSamples(samples, TestContext.Current.CancellationToken);
        var result = await MachineTurn.RunAsync(() => Whisper().TranscribeAsync(samples, new TranscriptionOptions(model, "en", UseGpu: true), ct: TestContext.Current.CancellationToken));
        var transcript = TranscriptBuilder.FromSegments(stream, result.Language, result.Segments, levels);
        project = TranscriptEdits.WithTranscript(project, asset.Id, transcript);
        var view = TranscriptView.Build(project);
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"{speech.Samples.Length / 16_000.0:0.0} s, {speech.Words.Count} words said, {view.Words.Count} heard, on {result.Runtime}");

        // Words of at least two letters with both neighbours heard right, evenly spaced through the recording.
        var heardPlain = view.Words.Select(w => string.Concat(Plain(w.Word.Text))).ToList();
        var truth = TrueWords(speech);
        var pairs = Matched(heardPlain, [.. truth.Select(t => t.Word)]);
        var candidates = Enumerable.Range(1, pairs.Count - 2)
            .Where(i => pairs[i - 1].Heard == pairs[i].Heard - 1 && pairs[i + 1].Heard == pairs[i].Heard + 1 && pairs[i - 1].True == pairs[i].True - 1 && pairs[i + 1].True == pairs[i].True + 1 && heardPlain[pairs[i].Heard].Length >= 2)
            .ToList();
        var chosen = Enumerable.Range(0, 10).Select(i => candidates[(i * candidates.Count / 10) + (candidates.Count / 20)]).ToList();
        var positions = chosen.Select(i => pairs[i].Heard).ToList();
        output?.WriteLine("Cutting: " + string.Join(", ", positions.Select(p => view.Words[p].Word.Text)));

        // Where each cut's ends fall against the true words, with the sound's own silence around them.
        var ranges = TranscriptEdits.RangesOf(project, view, positions, (_, _) => levels);
        ranges.Should().HaveCount(10);
        var worst = 0.0;
        for (var i = 0; i < 10; i++)
        {
            var at = chosen[i];
            var wordStart = truth[pairs[at].True].Start;
            var nextStart = truth[pairs[at + 1].True].Start;
            var (previousEnd, wordEnd) = (EndBefore(levels, wordStart), EndBefore(levels, nextStart));
            var intoPrevious = Math.Max(0, previousEnd - ranges[i].From);
            var intoWord = Math.Max(0, ranges[i].From - wordStart);
            var wordLeft = Math.Max(0, wordEnd - ranges[i].To);
            var intoNext = Math.Max(0, ranges[i].To - nextStart);
            worst = Math.Max(worst, Math.Max(intoPrevious, intoNext));
            output?.WriteLine($"  {view.Words[pairs[at].Heard].Word.Text,-12} cut {ranges[i].From:0.000} to {ranges[i].To:0.000}; true gap before it {previousEnd:0.000} to {wordStart:0.000}, after it {wordEnd:0.000} to {nextStart:0.000}; into the word before {intoPrevious * 1000:0} ms, into the next {intoNext * 1000:0} ms, of this word left {Math.Max(intoWord, wordLeft) * 1000:0} ms");
        }

        output?.WriteLine($"The furthest a cut goes into a neighbour: {worst * 1000:0} ms");

        var cut = TranscriptEdits.Cut(project, ranges);
        cut.Duration.Should().BeApproximately(project.Duration - ranges.Sum(r => r.Length), 1e-3);
        TranscriptView.Build(cut).Words.Where(w => w.IsCut).Select(w => w.Index).Should().Equal(positions.Select(p => view.Words[p].Index));

        // No click: the biggest sample jump within a cut's crossfade is no bigger than those of the sound around it.
        var mixed = new float[(int)Math.Ceiling(cut.Duration * 48_000) * 2];
        using (var mixer = new TimelineMixer())
        {
            const int block = 48_000;
            for (long start = 0; start < mixed.Length / 2; start += block)
            {
                mixer.Mix(cut, start, mixed.AsSpan((int)(start * 2), (int)Math.Min(block * 2, mixed.Length - (start * 2))));
            }
        }

        var removed = 0.0;
        foreach (var range in ranges)
        {
            var center = (int)((range.From - removed) * 48_000);
            removed += range.Length;
            var jump = Jump(mixed, center - 480, center + 480);
            var around = Math.Max(Jump(mixed, center - 4800, center - 480), Jump(mixed, center + 480, center + 4800));
            output?.WriteLine($"  jump at the cut {jump:0.0000}, around it {around:0.0000}");
            jump.Should().BeLessThanOrEqualTo(Math.Max(around * 1.5, 0.02), "the cut at {0:0.000} s doesn't click", range.From);
        }

        // Heard again: the neighbours are all there, and the words cut are gone.
        var cutSound = TranscriptAudio.Render(cut, ct: TestContext.Current.CancellationToken);
        var again = await MachineTurn.RunAsync(() => Whisper().TranscribeAsync(cutSound, new TranscriptionOptions(model, "en", UseGpu: true), ct: TestContext.Current.CancellationToken));
        var heardAgain = Plain(string.Join(' ', again.Segments.Select(s => s.Text)));
        var expected = heardPlain.Where((_, i) => !positions.Contains(i)).ToList();
        var kept = Matched(heardAgain, expected).Count;
        output?.WriteLine($"Heard again: {kept} of the {expected.Count} words that should be there, {heardAgain.Length} in all.");
        kept.Should().BeGreaterThan(expected.Count * 97 / 100, "no neighbour lost");
        foreach (var position in positions)
        {
            var before = heardPlain[position - 1];
            var after = heardPlain[position + 1];
            var word = heardPlain[position];
            var together = Enumerable.Range(0, heardAgain.Length - 1).Any(i => heardAgain[i] == before && heardAgain[i + 1] == after);
            var stillThere = Enumerable.Range(0, heardAgain.Length - 2).Any(i => heardAgain[i] == before && heardAgain[i + 1] == word && heardAgain[i + 2] == after);
            var heardThere = string.Join(' ', Enumerable.Range(0, heardAgain.Length - 2).Where(i => heardAgain[i] == before).Select(i => string.Join(' ', heardAgain[i..Math.Min(i + 4, heardAgain.Length)])).Take(3));
            output?.WriteLine($"  {before} [{word}] {after}: {(together ? "joined" : $"not found together; heard {heardThere}")}{(stillThere ? ", STILL THERE" : "")}");
            stillThere.Should().BeFalse("«{0}» was cut", word);
        }

        worst.Should().BeLessThan(0.15, "no syllable of a neighbour is lost");
    }

    /// <summary>The end of the sound before <paramref name="start"/>: the silence just before it, if there is one.</summary>
    private static double EndBefore(SoundLevels levels, double start)
    {
        var frame = levels.FrameAt(start) - 4;
        while (frame > 0 && levels.IsQuiet(frame - 1))
        {
            frame--;
        }

        return Math.Min(start, (double)(frame + 2) / SoundLevels.PerSecond);
    }

    /// <summary>The biggest change from one sample to the next, in a stretch of interleaved stereo.</summary>
    private static double Jump(float[] interleaved, int from, int to)
    {
        var most = 0.0;
        for (var i = Math.Max(1, from); i < Math.Min(to, interleaved.Length / 2); i++)
        {
            most = Math.Max(most, Math.Abs(interleaved[2 * i] - interleaved[2 * (i - 1)]));
        }

        return most;
    }

    [Fact]
    public void A_cut_through_a_loud_tone_doesnt_click_with_the_join_and_does_without()
    {
        TestMedia.Require();
        var path = _media.Stamped("tone.mp4", 6, tracks: ["Microphone"], tones: new HashSet<int> { 0 });
        var asset = ProjectFactory.ReadAsset(path);
        var project = ProjectFactory.FromRecording(asset);

        // Cut so the tone jumps a quarter of a cycle (440 Hz): from where it crosses zero to where it is at its height.
        var from = 2.0;
        var to = 3.0 + (0.25 / 440);
        var joined = TranscriptEdits.Cut(project, [new CutRange(from, to)]);
        var hard = TimelineEdits.DeleteRange(project, from, to);

        static float[] Mix(VideoProject p)
        {
            var samples = new float[48_000 * 2 * 4];
            using var mixer = new TimelineMixer();
            mixer.Mix(p, 0, samples);
            return samples;
        }

        var center = (int)(from * 48_000);
        var normal = Jump(Mix(project), 48_000, 96_000);
        var withJoin = Jump(Mix(joined), center - 960, center + 960);
        var without = Jump(Mix(hard), center - 960, center + 960);
        TestContext.Current.TestOutputHelper?.WriteLine($"The tone's own biggest step {normal:0.0000}; at the cut with the join {withJoin:0.0000}, without {without:0.0000}");
        without.Should().BeGreaterThan(normal * 5, "a cut like this clicks");
        withJoin.Should().BeLessThan(normal * 1.5, "the crossfade takes the click away");
        joined.Duration.Should().BeApproximately(project.Duration - (to - from), 1e-5);
    }

    [Fact]
    public async Task Shortening_the_silences_of_ten_minutes_keeps_sound_and_picture_together()
    {
        TestMedia.Require();
        var path = _media.Stamped("ten-minutes.mp4", 600);
        var asset = ProjectFactory.ReadAsset(path);
        var project = ProjectFactory.FromRecording(asset);
        var track = SilenceFinder.VoiceTrack(project)!;
        var stream = track.Clips.First().Stream;
        var levels = SoundLevels.FromSamples(TranscriptAudio.RenderStream(asset, stream, ct: TestContext.Current.CancellationToken), TestContext.Current.CancellationToken);
        var silences = SilenceFinder.Find(project, track, (_, _) => levels);
        silences.Should().HaveCountGreaterThan(290, "a beep every 2 s");
        var cut = TranscriptEdits.Cut(project, silences.Select(s => s.CutFor(SilenceFinder.DefaultShortenTo)));
        var cutDuration = cut.Duration;
        TestContext.Current.TestOutputHelper?.WriteLine($"{silences.Count} silences shortened: {project.Duration:0.0} s to {cutDuration:0.0} s");
        cutDuration.Should().BeLessThan(project.Duration * 0.25);

        // The clips say so: at the very end, sound and picture show the same moment.
        var picture = cut.Tracks.First(t => t.HasPictures).Clips.Last();
        var sound = cut.Tracks.First(t => t.HasSound).Clips.Last();
        Math.Abs(picture.SourceAt(cutDuration - 0.1) - sound.SourceAt(cutDuration - 0.1)).Should().BeLessThan(1 / 30.0);

        // The file says so: every flash has its beep within a frame, all the way to the end.
        var output = Path.Combine(_media.Root, "shortened.mp4");
        await MachineTurn.RunAsync(() => new ProjectExporter(new SoftwareEncoders(), NullLogger<ProjectExporter>.Instance).ExportAsync(new ExportRequest(cut, ExportPresets.Mp4H264Project, QualityPreset.Balanced, output, EncoderPresets.Software), ct: TestContext.Current.CancellationToken));

        var exported = ExportedFile.Read(output);
        exported.Flashes.Count.Should().BeGreaterThan(290);
        exported.Beeps.Count.Should().Be(exported.Flashes.Count);
        var offsets = exported.Beeps.Zip(exported.Flashes, (beep, flash) => beep - (flash / 30.0)).ToList();
        TestContext.Current.TestOutputHelper?.WriteLine($"Flash to beep: {offsets.Min() * 1000:0.0} to {offsets.Max() * 1000:0.0} ms over {offsets.Count} pairs; the last {offsets[^1] * 1000:0.0} ms");
        offsets.Should().OnlyContain(o => Math.Abs(o) < 1 / 30.0, "sound and picture stay together");
    }

    [Fact]
    public async Task An_export_with_two_languages_saves_a_file_for_each()
    {
        TestMedia.Require();
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(_media.Stamped("languages.mp4", 2)));
        var (one, _) = CaptionEdits.AddTrack(project, [new CaptionItem { Id = "e", Start = 0.5, Length = 1, Text = "Hello" }], "Captions (English)");
        var (two, arabic) = CaptionEdits.AddTrack(one, [new CaptionItem { Id = "a", Start = 0.5, Length = 1, Text = "مرحبا" }], "Captions (Arabic)");
        var output = Path.Combine(_media.Root, "languages-out.mp4");
        var result = await new ProjectExporter(new SoftwareEncoders(), NullLogger<ProjectExporter>.Instance).ExportAsync(
            new ExportRequest(two, ExportPresets.Mp4H264Project, QualityPreset.Balanced, output, EncoderPresets.Software, CaptionFiles: CaptionFiles.Srt | CaptionFiles.Vtt, BurnTrack: arabic),
            ct: TestContext.Current.CancellationToken);
        result.CaptionPaths!.Select(Path.GetFileName).Should().BeEquivalentTo("languages-out.Captions (English).srt", "languages-out.Captions (Arabic).srt", "languages-out.Captions (English).vtt", "languages-out.Captions (Arabic).vtt");
        File.ReadAllText(Path.Combine(_media.Root, "languages-out.Captions (Arabic).srt")).Should().Contain("مرحبا").And.NotContain("Hello");
        File.ReadAllText(Path.Combine(_media.Root, "languages-out.Captions (English).vtt")).Should().Contain("Hello").And.NotContain("مرحبا");
    }

    /// <summary>What was found of the labelled clip: how many of its fillers, and how many things that weren't.</summary>
    private static (int Found, int Said, int Wrong, string List) Score(SaidSpeech speech, SoundLevels levels, MediaTranscript transcript)
    {
        var asset = new MediaAsset("clip", @"C:\nowhere\clip.mp4", speech.Samples.Length / 16_000.0, [new AssetStream(1, StreamKind.Audio, "Microphone", 0, speech.Samples.Length / 16_000.0)], Transcript: transcript);
        var project = ProjectFactory.AddSound(VideoProject.Empty(new ProjectCanvas(320, 180, 30)), asset, 0, "Microphone");
        var view = TranscriptView.Build(project);
        var fillers = FillerFinder.Find(project, view, (_, _) => levels);

        // The truth: each "um" and "uh" said, from when it starts to when the next word does.
        var said = new List<(double From, double To)>();
        for (var i = 0; i < speech.Words.Count; i++)
        {
            if (Plain(speech.Words[i].Text) is ["um" or "uh"])
            {
                said.Add((speech.Words[i].Start, i + 1 < speech.Words.Count ? speech.Words[i + 1].Start : speech.Words[i].Start + 0.5));
            }
        }

        var found = said.Count(s => fillers.Any(f => f.Start < s.To + 0.05 && f.End > s.From - 0.05));
        var wrong = fillers.Count(f => !said.Any(s => f.Start < s.To + 0.05 && f.End > s.From - 0.05));
        return (found, said.Count, wrong, string.Join(", ", fillers.GroupBy(f => f.Kind).Select(g => $"{g.Key} {g.Count()}")));
    }

    [Fact]
    public async Task Filler_words_are_found_in_the_labelled_clip()
    {
        var model = Model(WhisperModels.Small);
        var speech = await Speech.SayWithWordsAsync(FillerTalk);
        var levels = SoundLevels.FromSamples(speech.Samples, TestContext.Current.CancellationToken);
        var output = TestContext.Current.TestOutputHelper;
        var scores = new Dictionary<bool, (int Found, int Said, int Wrong, string List)>();
        foreach (var prompt in new[] { false, true })
        {
            var result = await MachineTurn.RunAsync(() => Whisper().TranscribeAsync(speech.Samples, new TranscriptionOptions(model, "en", UseGpu: true, KeepFillers: prompt), ct: TestContext.Current.CancellationToken));
            var transcript = TranscriptBuilder.FromSegments(1, "en", result.Segments, levels);
            var score = Score(speech, levels, transcript);
            scores[prompt] = score;
            output?.WriteLine($"With the prompt: {prompt}. Written fillers {transcript.Words.Count(w => FillerFinder.IsFiller(w.Text))}; found {score.Found} of {score.Said}, {score.Wrong} wrong ({score.List}).");
            output?.WriteLine("  " + string.Join(' ', transcript.Words.Select(w => w.Text)));
        }

        var best = scores[true];
        var all = best.Found + best.Wrong;
        (best.Found * 100 / best.Said).Should().BeGreaterThanOrEqualTo(80, "at least 80 % found");
        (best.Wrong * 100 / Math.Max(1, all)).Should().BeLessThanOrEqualTo(10, "at most 10 % false");
    }
}
