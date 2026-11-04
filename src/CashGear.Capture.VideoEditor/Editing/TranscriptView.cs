using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.VideoEditor.Editing;

/// <summary>A word of a transcript as the timeline has it.</summary>
/// <param name="AssetId">The media it is from.</param>
/// <param name="Index">Its place in that media's transcript.</param>
/// <param name="ClipId">The sound clip it is heard in; null when no clip has it: it was cut.</param>
/// <param name="Start">When it starts on the timeline (a cut word: where it was cut out).</param>
/// <param name="End">When it ends on the timeline (a cut word: the same as its start).</param>
/// <param name="Paragraph">The paragraph it is shown in, counting from 0.</param>
public sealed record ShownWord(string AssetId, int Index, SpokenWord Word, string? ClipId, double Start, double End, int Paragraph)
{
    public bool IsCut => ClipId is null;
}

/// <summary>
/// The transcripts of a project's media, seen through its clips: each word on the timeline at the time its clip plays
/// it, in timeline order, and the words no clip plays between their neighbours. A pure function of the project, so a cut
/// on the timeline shows at once and undo restores it.
/// </summary>
public sealed class TranscriptView
{
    /// <summary>A pause this long in the speech, in seconds, starts a new paragraph.</summary>
    public const double ParagraphPause = 0.8;

    /// <summary>After this many words a paragraph ends at the next end of a sentence, pause or none.</summary>
    private const int LongParagraph = 90;

    /// <summary>The ends of sentences in the languages the app knows.</summary>
    private const string SentenceEnds = ".?!…؟";

    /// <summary>Only the words the timeline plays, by position in <see cref="Words"/>, in timeline order.</summary>
    private readonly int[] _shown;

    private TranscriptView(IReadOnlyList<ShownWord> words)
    {
        Words = words;
        _shown = [.. words.Select((w, i) => (w, i)).Where(p => !p.w.IsCut).Select(p => p.i)];
    }

    public IReadOnlyList<ShownWord> Words { get; }

    public bool IsEmpty => Words.Count == 0;

    /// <summary>The sound clips a transcript of <paramref name="asset"/> is heard through: the first sound track's with the stream, in time order.</summary>
    public static IReadOnlyList<Clip> ClipsOf(VideoProject project, MediaAsset asset)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Transcript is not { } transcript)
        {
            return [];
        }

        var track = project.Tracks.FirstOrDefault(t => t.HasSound && t.Clips.Any(c => c.AssetId == asset.Id && c.Stream == transcript.Stream));
        return track is null ? [] : [.. track.Clips.Where(c => c.AssetId == asset.Id && c.Stream == transcript.Stream && !c.IsFreeze).OrderBy(c => c.Start)];
    }

    public static TranscriptView Build(VideoProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var shown = new List<ShownWord>();
        var cutRuns = new List<(MediaAsset Asset, int From, int To)>();
        var seen = new HashSet<(string Asset, int Index)>();
        foreach (var asset in project.Assets.Where(a => a.Transcript is { Words.Count: > 0 }))
        {
            var words = asset.Transcript!.Words;
            var has = new bool[words.Count];
            foreach (var clip in ClipsOf(project, asset))
            {
                foreach (var index in IndicesInside(words, clip))
                {
                    // A word in the join of two clips is said once.
                    if (has[index] && shown.Count > 0 && shown[^1] is { } last && last.AssetId == asset.Id && last.Index == index)
                    {
                        continue;
                    }

                    has[index] = true;
                    var word = words[index];
                    var start = clip.TimelineAt(Math.Max(word.Start, clip.SourceIn));
                    var end = clip.TimelineAt(Math.Min(word.End, clip.SourceOut));
                    shown.Add(new ShownWord(asset.Id, index, word, clip.Id, Times.Round(start), Times.Round(Math.Max(start, end)), 0));
                    seen.Add((asset.Id, index));
                }
            }

            for (var i = 0; i < words.Count; i++)
            {
                if (!has[i])
                {
                    var j = i;
                    while (j + 1 < words.Count && !has[j + 1])
                    {
                        j++;
                    }

                    cutRuns.Add((asset, i, j));
                    i = j;
                }
            }
        }

        var ordered = shown.OrderBy(w => w.Start).ThenBy(w => w.Index).ToList();
        return new TranscriptView(Paragraphs(WithCutWords(project, ordered, cutRuns)));
    }

    /// <summary>The position in <see cref="Words"/> of the word being said at <paramref name="time"/> (the one before a short gap too), or -1.</summary>
    public int IndexAt(double time, double holdThroughGap = 0.3)
    {
        var low = 0;
        var high = _shown.Length - 1;
        var found = -1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (Words[_shown[middle]].Start <= time + Times.Epsilon)
            {
                found = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return found < 0 || time > Words[_shown[found]].End + holdThroughGap ? -1 : _shown[found];
    }

    /// <summary>The nearest word before position <paramref name="position"/> that the timeline plays, or null.</summary>
    public ShownWord? ShownBefore(int position)
    {
        for (var i = position - 1; i >= 0; i--)
        {
            if (!Words[i].IsCut)
            {
                return Words[i];
            }
        }

        return null;
    }

    /// <summary>The nearest word after position <paramref name="position"/> that the timeline plays, or null.</summary>
    public ShownWord? ShownAfter(int position)
    {
        for (var i = position + 1; i < Words.Count; i++)
        {
            if (!Words[i].IsCut)
            {
                return Words[i];
            }
        }

        return null;
    }

    /// <summary>The words whose middle is inside what the clip plays, by binary search.</summary>
    private static IEnumerable<int> IndicesInside(IReadOnlyList<SpokenWord> words, Clip clip)
    {
        var low = 0;
        var high = words.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (words[middle].Middle < clip.SourceIn)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        for (var i = low; i < words.Count && words[i].Middle < clip.SourceOut; i++)
        {
            yield return i;
        }
    }

    /// <summary>Each run of cut words after the word before it (or before the word after it, when nothing is before it).</summary>
    private static List<ShownWord> WithCutWords(VideoProject project, List<ShownWord> shown, List<(MediaAsset Asset, int From, int To)> runs)
    {
        if (runs.Count == 0)
        {
            return shown;
        }

        var after = new Dictionary<(string Asset, int Index), List<ShownWord>>();
        var before = new Dictionary<(string Asset, int Index), List<ShownWord>>();
        var result = new List<ShownWord>(shown.Count);
        var trailing = new List<ShownWord>();
        foreach (var (asset, from, to) in runs)
        {
            var words = asset.Transcript!.Words;
            var previous = shown.LastOrDefault(w => w.AssetId == asset.Id && w.Index == from - 1);
            var next = previous is null ? shown.FirstOrDefault(w => w.AssetId == asset.Id && w.Index == to + 1) : null;
            var point = previous is not null && project.Find(previous.ClipId!) is { Item: Clip ending } a ? TranscriptEdits.EdgeAfter(a.Track, ending)
                : next is not null && project.Find(next.ClipId!) is { Item: Clip starting } b ? TranscriptEdits.EdgeBefore(b.Track, starting)
                : project.Duration;
            var run = Enumerable.Range(from, to - from + 1).Select(i => new ShownWord(asset.Id, i, words[i], null, Times.Round(point), Times.Round(point), 0)).ToList();
            if (previous is not null)
            {
                after[(asset.Id, previous.Index)] = run;
            }
            else if (next is not null)
            {
                before[(asset.Id, next.Index)] = run;
            }
            else
            {
                trailing.AddRange(run);
            }
        }

        foreach (var word in shown)
        {
            if (before.Remove((word.AssetId, word.Index), out var ahead))
            {
                result.AddRange(ahead);
            }

            result.Add(word);
            if (after.Remove((word.AssetId, word.Index), out var behind))
            {
                result.AddRange(behind);
            }
        }

        result.AddRange(trailing);
        return result;
    }

    /// <summary>The words' paragraphs: a new one at a pause in the speech, at another media, at a jump back, and in long stretches at the end of a sentence.</summary>
    private static List<ShownWord> Paragraphs(List<ShownWord> words)
    {
        var paragraph = 0;
        var inParagraph = 0;
        var result = new List<ShownWord>(words.Count);
        for (var i = 0; i < words.Count; i++)
        {
            var word = words[i];
            if (i > 0)
            {
                var previous = words[i - 1];
                var sentenceEnded = previous.Word.Text.Length > 0 && SentenceEnds.Contains(previous.Word.Text[^1]);
                if (previous.AssetId != word.AssetId
                    || word.Index < previous.Index
                    || word.Word.Start - previous.Word.End >= ParagraphPause
                    || (inParagraph >= LongParagraph && sentenceEnded))
                {
                    paragraph++;
                    inParagraph = 0;
                }
            }

            inParagraph++;
            result.Add(word with { Paragraph = paragraph });
        }

        return result;
    }
}
