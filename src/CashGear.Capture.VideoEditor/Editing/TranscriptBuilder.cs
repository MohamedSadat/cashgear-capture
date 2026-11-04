using CashGear.Capture.Core.Transcripts;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;

namespace CashGear.Capture.VideoEditor.Editing;

/// <summary>Transcripts from what a speech recognizer heard, and from Phase 17's caption tracks.</summary>
public static class TranscriptBuilder
{
    /// <summary>How far before an aligned word's end a boundary between words may be looked for, in seconds.</summary>
    private const double SearchBack = 0.05;

    /// <summary>How far after it. Alignment tends to end a word early.</summary>
    private const double SearchAhead = 0.08;

    /// <summary>What moving away from the aligned time costs, in decibels a second: the quietest moment wins unless it is far.</summary>
    private const double CostOfMoving = 60;

    /// <summary>The level below which all that is left is noise, in decibels: going lower doesn't make a place better.</summary>
    private const double Inaudible = -60;

    /// <summary>
    /// A transcript of what was heard from one stream: pieces of words put together, and each boundary between words
    /// moved to the quiet moment nearest it (<see cref="Refine"/>) when the stream's <paramref name="levels"/> are known.
    /// A segment without word times is one unit, cut as a sentence.
    /// </summary>
    public static MediaTranscript FromSegments(int stream, string language, IEnumerable<TranscriptSegment> segments, SoundLevels? levels = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        var words = new List<SpokenWord>();
        foreach (var segment in segments.OrderBy(s => s.Start))
        {
            if (segment.Words is { Count: > 0 } pieces)
            {
                words.AddRange(Merge(pieces));
            }
            else if (segment.Text.Trim().Length > 0)
            {
                words.Add(new SpokenWord(Times.Round(segment.Start), Times.Round(segment.End), segment.Text.Trim(), IsSentence: true));
            }
        }

        return new MediaTranscript(stream, language, levels is null ? words : Refine(words, levels));
    }

    /// <summary>
    /// Whole words from recognized pieces: a piece that starts with a space starts a word, and the others join the one
    /// before. Punctuation joins it too, without taking time.
    /// </summary>
    public static IReadOnlyList<SpokenWord> Merge(IEnumerable<TranscriptWord> pieces)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        var words = new List<SpokenWord>();
        foreach (var piece in pieces)
        {
            var text = piece.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var joins = words.Count > 0 && piece.Text[0] != ' ';
            if (!joins)
            {
                words.Add(new SpokenWord(piece.Start, Math.Max(piece.Start, piece.End), text));
                continue;
            }

            var word = words[^1];
            words[^1] = word with { Text = word.Text + text, End = text.All(char.IsPunctuation) ? word.End : Math.Max(word.End, piece.End) };
        }

        return words;
    }

    /// <summary>
    /// Word times moved to the sound. Each boundary goes to the quietest moment near the time the recognizer gave (a
    /// word's alignment time is near its end), and a silence there becomes the gap between the words; the first word
    /// starts, and the last ends, where the sound around them does. Order is kept.
    /// </summary>
    public static IReadOnlyList<SpokenWord> Refine(IReadOnlyList<SpokenWord> words, SoundLevels levels)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(levels);
        if (words.Count == 0 || levels.Count == 0)
        {
            return words;
        }

        var result = words.ToArray();
        for (var i = 0; i + 1 < result.Length; i++)
        {
            var (end, start) = Boundary(levels, result[i], result[i + 1]);
            result[i] = result[i] with { End = end };
            result[i + 1] = result[i + 1] with { Start = start };
        }

        // Before the first word and after the last: out to the silence around them.
        var firstWord = result[0];
        var firstEnd = levels.FrameAt(firstWord.End);
        var at = levels.FrameAt(firstWord.Start);
        while (at < firstEnd && levels.IsQuiet(at))
        {
            at++;
        }

        while (at > 0 && !levels.IsQuiet(at - 1))
        {
            at--;
        }

        result[0] = firstWord with { Start = Times.Round((double)at / SoundLevels.PerSecond) };
        var lastWord = result[^1];
        var lastStart = levels.FrameAt(lastWord.Start);
        at = levels.FrameAt(lastWord.End);
        while (at > lastStart && levels.IsQuiet(at))
        {
            at--;
        }

        var limit = levels.FrameAt(lastWord.End + 0.5);
        while (at < limit && !levels.IsQuiet(at))
        {
            at++;
        }

        result[^1] = result[^1] with { End = Times.Round(Math.Max(result[^1].Start, (double)at / SoundLevels.PerSecond)) };

        // A sound of its own at a word's start, parted from the rest of it by quiet, isn't that word ("um" left unwritten).
        for (var i = 0; i < result.Length; i++)
        {
            if (!result[i].IsSentence)
            {
                var islands = FillerFinder.Islands(levels, levels.FrameAt(result[i].Start), levels.FrameAt(result[i].End));
                if (islands.Count > 1)
                {
                    result[i] = result[i] with { Start = Times.Round(Math.Min(result[i].End, (double)islands[^1].First / SoundLevels.PerSecond)) };
                }
            }
        }

        return result;
    }

    /// <summary>Where <paramref name="before"/> ends and <paramref name="after"/> starts.</summary>
    private static (double End, double Start) Boundary(SoundLevels levels, SpokenWord before, SpokenWord after)
    {
        var lower = before.Start + 0.02;
        var upper = after.End - 0.02;
        var near = before.IsSentence ? (before.End + after.Start) / 2 : before.End;
        var center = levels.FrameAt(Math.Clamp(near, Math.Min(lower, upper), Math.Max(lower, upper)));
        var from = levels.FrameAt(Math.Max(lower, near - SearchBack));
        var to = levels.FrameAt(Math.Min(upper, near + SearchAhead));
        var best = center;
        var lowest = double.MaxValue;
        for (var frame = from; frame <= to; frame++)
        {
            var cost = Math.Max(levels.SharpLevel(frame), Inaudible) + (CostOfMoving * Math.Abs(frame - center) / SoundLevels.PerSecond);
            if (cost < lowest)
            {
                lowest = cost;
                best = frame;
            }
        }

        // A silence there is the gap between them, all of it.
        var first = best;
        var last = best;
        if (levels.IsQuiet(best))
        {
            var lowerFrame = levels.FrameAt(lower);
            var upperFrame = levels.FrameAt(upper);
            while (first > lowerFrame && levels.IsQuiet(first - 1))
            {
                first--;
            }

            while (last < upperFrame && levels.IsQuiet(last + 1))
            {
                last++;
            }
        }

        var end = Times.Round(Math.Max(before.Start, (double)first / SoundLevels.PerSecond));
        return (end, Times.Round(Math.Max(end, (double)last / SoundLevels.PerSecond)));
    }

    /// <summary>The caption tracks with words: what can be offered as the transcript of a project that has none.</summary>
    public static IReadOnlyList<Track> CaptionTracksWithWords(VideoProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return [.. project.Tracks.Where(t => t.Kind == TrackKind.Caption && t.Items.OfType<CaptionItem>().Any(c => c.Words is { Count: > 0 }))];
    }

    /// <summary>
    /// The project with a caption track's words as the transcript of the media under them. Each word is said at its
    /// timeline time, which the sound clip there turns into a time on its stream's clock; a caption that has no words is a
    /// sentence. Captions over no sound are left out. <paramref name="levels"/>, when known, refine the times.
    /// </summary>
    public static VideoProject FromCaptions(VideoProject project, string trackId, string language = "", Func<MediaAsset, int, SoundLevels?>? levels = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Track(trackId) is not { Kind: TrackKind.Caption } track)
        {
            return project;
        }

        var heard = new Dictionary<(string Asset, int Stream), List<TranscriptWord>>();
        var sentences = new Dictionary<(string Asset, int Stream), List<SpokenWord>>();
        foreach (var caption in track.Items.OfType<CaptionItem>().OrderBy(c => c.Start))
        {
            if (caption.Words is { Count: > 0 } words)
            {
                foreach (var word in words)
                {
                    var from = caption.Start + word.Start;
                    var to = caption.Start + Math.Max(word.Start, word.End);
                    if (SoundUnder(project, (from + to) / 2) is { } clip)
                    {
                        var key = (clip.AssetId, clip.Stream);
                        if (!heard.TryGetValue(key, out var list))
                        {
                            heard[key] = list = [];
                        }

                        list.Add(new TranscriptWord(Times.Round(clip.SourceAt(from)), Times.Round(clip.SourceAt(to)), word.Text));
                    }
                }
            }
            else if (caption.Text.Trim().Length > 0 && SoundUnder(project, (caption.Start + caption.End) / 2) is { } clip)
            {
                var key = (clip.AssetId, clip.Stream);
                if (!sentences.TryGetValue(key, out var list))
                {
                    sentences[key] = list = [];
                }

                list.Add(new SpokenWord(Times.Round(clip.SourceAt(caption.Start)), Times.Round(clip.SourceAt(caption.End)), caption.Text.Trim(), IsSentence: true));
            }
        }

        var result = project;
        foreach (var key in heard.Keys.Union(sentences.Keys).ToList())
        {
            if (result.Asset(key.Asset) is not { } asset)
            {
                continue;
            }

            var words = heard.TryGetValue(key, out var pieces) ? Merge(pieces).ToList() : [];
            if (sentences.TryGetValue(key, out var whole))
            {
                words.AddRange(whole);
            }

            words = [.. words.OrderBy(w => w.Start)];
            var known = levels?.Invoke(asset, key.Stream);
            result = result.WithAsset(asset with { Transcript = new MediaTranscript(key.Stream, language, known is null ? words : Refine(words, known)) });
        }

        return result;
    }

    /// <summary>The clip whose sound is heard at <paramref name="time"/>: on the first sound track with one, the microphone first.</summary>
    private static Clip? SoundUnder(VideoProject project, double time) =>
        project.Tracks.Where(t => t.HasSound).OrderBy(t => t.Name.Contains(MicrophoneTitle, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .SelectMany(t => t.Clips).FirstOrDefault(c => !c.IsFreeze && c.Covers(time));

    /// <summary>The microphone's sound stream title in recordings, matched as stored (never translated).</summary>
    [System.ComponentModel.Localizable(false)]
    private const string MicrophoneTitle = "Microphone";
}
