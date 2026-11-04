using System.IO;
using System.Text;
using System.Text.Json;
using CashGear.Capture.Tests;
using CashGear.Capture.Transcription;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.VideoEditor.Tests;

/// <summary>
/// What Whisper's word times are worth on real Arabic speech, measured when asked (TESTING.md): no clips of it are in the
/// repository, and this PC has no Arabic voice. Point <c>CASHGEAR_ARABIC_CLIPS</c> at a folder with sound clips and a
/// <c>clips.json</c> of <c>{ "clips": [ { "file", "text" } ] }</c>.
/// </summary>
public sealed class ArabicSpeechMeasurement
{
    private static string[] Plain(string text)
    {
        var words = new List<string>();
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var plain = FillerFinder.Plain(word);
            if (plain.Length > 0)
            {
                words.Add(plain);
            }
        }

        return [.. words];
    }

    private static int InOrder(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        var table = new int[a.Count + 1, b.Count + 1];
        for (var i = 1; i <= a.Count; i++)
        {
            for (var j = 1; j <= b.Count; j++)
            {
                table[i, j] = a[i - 1] == b[j - 1] ? table[i - 1, j - 1] + 1 : Math.Max(table[i - 1, j], table[i, j - 1]);
            }
        }

        return table[a.Count, b.Count];
    }

    [Fact(Explicit = true)]
    public async Task Word_times_on_real_arabic_speech()
    {
        var folder = Environment.GetEnvironmentVariable("CASHGEAR_ARABIC_CLIPS");
        Assert.SkipWhen(folder is null, "CASHGEAR_ARABIC_CLIPS isn't set.");
        CashGear.Capture.Media.Tests.TestMedia.Require();
        var model = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "whisper", Environment.GetEnvironmentVariable("CASHGEAR_ARABIC_MODEL") ?? WhisperModels.Small.File);
        using var list = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder!, "clips.json")));
        var log = new StringBuilder();
        var clips = new List<(float[] Samples, string Text)>();
        foreach (var clip in list.RootElement.GetProperty("clips").EnumerateArray())
        {
            var path = Path.Combine(folder!, clip.GetProperty("file").GetString()!);
            var asset = ProjectFactory.ReadAsset(path);
            var stream = asset.Streams.First(s => s.Kind == StreamKind.Audio).Index;
            clips.Add((TranscriptAudio.RenderStream(asset, stream, ct: TestContext.Current.CancellationToken), clip.GetProperty("text").GetString()!));
        }

        // All of them one after another, with a second of silence between: the clips' edges are known.
        var total = clips.Sum(c => c.Samples.Length + 16_000) + 16_000;
        var all = new float[total];
        var edges = new List<(double From, double To)>();
        var at = 16_000;
        foreach (var (samples, _) in clips)
        {
            samples.CopyTo(all, at);
            edges.Add((at / 16_000.0, (at + samples.Length) / 16_000.0));
            at += samples.Length + 16_000;
        }

        log.AppendLine(FormattableString.Invariant($"{clips.Count} clips, {total / 16_000.0:0.0} s"));
        var levels = SoundLevels.FromSamples(all, TestContext.Current.CancellationToken);
        var whisper = new WhisperTranscriber(NullLogger<WhisperTranscriber>.Instance);
        var result = await MachineTurn.RunAsync(() => whisper.TranscribeAsync(all, new TranscriptionOptions(model, "ar", UseGpu: true), ct: TestContext.Current.CancellationToken));
        var withWords = result.Segments.Count(s => s.Words is not null);
        log.AppendLine(FormattableString.Invariant($"{result.Segments.Count} segments on {result.Runtime}: {withWords} with word times ({withWords * 100.0 / Math.Max(1, result.Segments.Count):0}%)"));

        var said = Plain(string.Join(' ', clips.Select(c => c.Text)));
        var heardText = string.Join(' ', result.Segments.Select(s => s.Text));
        var heard = Plain(heardText);
        var matched = InOrder(said, heard);
        log.AppendLine(FormattableString.Invariant($"Words said {said.Length}, heard {heard.Length}, the same in order {matched} ({matched * 100.0 / Math.Max(1, said.Length):0}%)"));
        log.AppendLine("First segments:");
        foreach (var segment in result.Segments.Take(8))
        {
            log.AppendLine(FormattableString.Invariant($"  {segment.Start:0.00}-{segment.End:0.00} {segment.Text}"));
            log.AppendLine("    " + (segment.Words is null ? "(no words)" : string.Join('|', segment.Words.Select(w => FormattableString.Invariant($"{w.Text}@{w.End:0.00}")))));
        }

        var transcript = TranscriptBuilder.FromSegments(0, "ar", result.Segments, levels);
        var words = transcript.Words.Where(w => !w.IsSentence).ToList();
        log.AppendLine(FormattableString.Invariant($"{transcript.Words.Count} units: {words.Count} words, {transcript.Words.Count - words.Count} sentences"));

        // Against what is known: each clip's first word starts where its sound does, and its last word ends where its sound does.
        var starts = new List<double>();
        var ends = new List<double>();
        foreach (var (from, to) in edges)
        {
            var inside = words.Where(w => w.Middle >= from && w.Middle <= to).ToList();
            if (inside.Count == 0)
            {
                continue;
            }

            var soundFrom = levels.FrameAt(from);
            while (soundFrom < levels.FrameAt(to) && levels.IsQuiet(soundFrom))
            {
                soundFrom++;
            }

            var soundTo = levels.FrameAt(to);
            while (soundTo > soundFrom && levels.IsQuiet(soundTo))
            {
                soundTo--;
            }

            starts.Add(Math.Abs(inside[0].Start - ((double)soundFrom / SoundLevels.PerSecond)));
            ends.Add(Math.Abs(inside[^1].End - ((double)soundTo / SoundLevels.PerSecond)));
        }

        static string Stats(List<double> d)
        {
            d.Sort();
            return d.Count == 0 ? "none" : FormattableString.Invariant($"mean {d.Average() * 1000:0} ms, half within {d[d.Count / 2] * 1000:0} ms, 90% within {d[(int)(d.Count * 0.9)] * 1000:0} ms, worst {d[^1] * 1000:0} ms");
        }

        log.AppendLine($"First word's start against the clip's sound: {Stats(starts)}");
        log.AppendLine($"Last word's end against the clip's sound: {Stats(ends)}");

        // Inside a sentence: how many boundaries are in a dip of the sound, against the level of the words either side.
        var dips = 0;
        var inner = 0;
        for (var i = 0; i + 1 < words.Count; i++)
        {
            if (words[i + 1].Start - words[i].End > 0.3)
            {
                continue; // a pause
            }

            var boundary = levels.FrameAt((words[i].End + words[i + 1].Start) / 2);
            var around = Math.Max(levels.Level(levels.FrameAt(words[i].Middle)), levels.Level(levels.FrameAt(words[i + 1].Middle)));
            inner++;
            dips += levels.SharpLevel(boundary) < around - 6 ? 1 : 0;
        }

        log.AppendLine(FormattableString.Invariant($"Boundaries inside speech: {inner}; in a dip of at least 6 dB: {dips} ({dips * 100.0 / Math.Max(1, inner):0}%)"));

        // Heard again: ten words cut from the middle of sentences; what should be there is, and they are gone.
        var asset2 = new MediaAsset("clip", Path.Combine(folder!, "all.wav"), total / 16_000.0, [new AssetStream(0, StreamKind.Audio, "Microphone", 0, total / 16_000.0)], Transcript: transcript);
        WriteWave(asset2.Path, all);
        var project = ProjectFactory.AddSound(VideoProject.Empty(new ProjectCanvas(320, 180, 30)), asset2, 0, "Microphone");
        var view = TranscriptView.Build(project);
        var candidates = Enumerable.Range(1, view.Words.Count - 2).Where(i => !view.Words[i].Word.IsSentence && !view.Words[i - 1].Word.IsSentence && !view.Words[i + 1].Word.IsSentence).ToList();
        var chosen = Enumerable.Range(0, 10).Select(i => candidates[(i * candidates.Count / 10) + (candidates.Count / 20)]).ToList();
        var cut = TranscriptEdits.CutWords(project, chosen, (_, _) => levels);
        var sound = TranscriptAudio.Render(cut, ct: TestContext.Current.CancellationToken);
        var again = await MachineTurn.RunAsync(() => whisper.TranscribeAsync(sound, new TranscriptionOptions(model, "ar", UseGpu: true), ct: TestContext.Current.CancellationToken));
        var expected = view.Words.Where((_, i) => !chosen.Contains(i)).SelectMany(w => Plain(w.Word.Text)).ToList();
        var heardAgain = Plain(string.Join(' ', again.Segments.Select(s => s.Text)));
        log.AppendLine(FormattableString.Invariant($"Ten words cut; heard again: {InOrder(heardAgain, expected)} of the {expected.Count} that should be there ({heardAgain.Length} in all)."));
        foreach (var position in chosen)
        {
            var (before, word, after) = (FillerFinder.Plain(view.Words[position - 1].Word.Text), FillerFinder.Plain(view.Words[position].Word.Text), FillerFinder.Plain(view.Words[position + 1].Word.Text));
            var joined = Enumerable.Range(0, Math.Max(0, heardAgain.Length - 1)).Any(i => heardAgain[i] == before && heardAgain[i + 1] == after);
            var still = Enumerable.Range(0, Math.Max(0, heardAgain.Length - 2)).Any(i => heardAgain[i] == before && heardAgain[i + 1] == word && heardAgain[i + 2] == after);
            log.AppendLine($"  {before} [{word}] {after}: {(joined ? "joined" : "not together")}{(still ? ", STILL THERE" : "")}");
        }

        // With the fillers' prompt: read sentences have no fillers, so what is found is false, and how the words fare.
        var prompted = await MachineTurn.RunAsync(() => whisper.TranscribeAsync(all, new TranscriptionOptions(model, "ar", UseGpu: true, KeepFillers: true), ct: TestContext.Current.CancellationToken));
        var promptedHeard = Plain(string.Join(' ', prompted.Segments.Select(s => s.Text)));
        var promptedTranscript = TranscriptBuilder.FromSegments(0, "ar", prompted.Segments, levels);
        var promptedView = TranscriptView.Build(project.WithAsset(asset2 with { Transcript = promptedTranscript }));
        var written = FillerFinder.Find(project.WithAsset(asset2 with { Transcript = promptedTranscript }), promptedView, (_, _) => levels);
        log.AppendLine(FormattableString.Invariant($"With the fillers' prompt: {InOrder(said, promptedHeard)} of {said.Length} words the same in order; fillers flagged in sentences that have none: {written.Count(f => f.Kind == FillerKind.Word)} written, {written.Count(f => f.Kind == FillerKind.Repeat)} repeated, {written.Count(f => f.Kind == FillerKind.Voiced)} sounds without a word."));
        log.AppendLine("  " + string.Join(", ", written.Take(12).Select(f => FormattableString.Invariant($"{f.Kind} {f.Text} {f.Start:0.0}"))));

        // Whisper's translate task, into English.
        var english = await MachineTurn.RunAsync(() => whisper.TranscribeAsync(all, new TranscriptionOptions(model, "ar", UseGpu: true, Translate: true), ct: TestContext.Current.CancellationToken));
        log.AppendLine($"Into English: {english.Segments.Count} segments");
        foreach (var segment in english.Segments.Take(6))
        {
            log.AppendLine(FormattableString.Invariant($"  {segment.Start:0.00}-{segment.End:0.00} {segment.Text}"));
        }

        var report = Environment.GetEnvironmentVariable("CASHGEAR_ARABIC_REPORT") ?? Path.Combine(folder!, "report.txt");
        File.WriteAllText(report, log.ToString(), Encoding.UTF8);
        TestContext.Current.TestOutputHelper?.WriteLine(log.ToString());
    }

    private static void WriteWave(string path, float[] samples)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write("RIFF"u8);
        writer.Write(36 + (samples.Length * 2));
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(16_000);
        writer.Write(32_000);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(samples.Length * 2);
        foreach (var sample in samples)
        {
            writer.Write((short)Math.Clamp(sample * 32767, -32768, 32767));
        }
    }
}
