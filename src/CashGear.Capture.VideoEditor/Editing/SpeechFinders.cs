using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;

namespace CashGear.Capture.VideoEditor.Editing;

/// <summary>A pause on the timeline: when it starts and ends.</summary>
public sealed record Silence(double Start, double End)
{
    public double Length => End - Start;

    /// <summary>What to take out of its middle so <paramref name="shortenTo"/> seconds are left, half each side.</summary>
    public CutRange CutFor(double shortenTo) => new(Times.Round(Start + (shortenTo / 2)), Times.Round(End - (shortenTo / 2)));
}

/// <summary>Finds the pauses in the voice: quiet in the sound, and no word there.</summary>
public static class SilenceFinder
{
    public const double DefaultLongest = 0.75;

    public const double DefaultShortenTo = 0.3;

    /// <summary>Two stretches this close on the timeline, in seconds, are one (across a cut's join).</summary>
    private const double SameTime = TranscriptEdits.LongestJoin;

    /// <summary>
    /// The sound track the voice is on: the one a transcript was heard from, else the microphone's, else the first with
    /// clips. Null when there is none.
    /// </summary>
    public static Track? VoiceTrack(VideoProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        foreach (var asset in project.Assets.Where(a => a.Transcript is not null))
        {
            if (project.Tracks.FirstOrDefault(t => t.HasSound && t.Clips.Any(c => c.AssetId == asset.Id && c.Stream == asset.Transcript!.Stream)) is { } track)
            {
                return track;
            }
        }

        return project.Tracks.FirstOrDefault(t => t.HasSound && t.Name.Contains(MicrophoneTitle, StringComparison.OrdinalIgnoreCase) && t.Clips.Any())
            ?? project.Tracks.FirstOrDefault(t => t.HasSound && t.Clips.Any());
    }

    /// <summary>
    /// The pauses of at least <paramref name="longer"/> seconds on the timeline on a voice track, in time order: stretches of its
    /// clips where the sound is quiet (when its levels are known) and no word of a transcript is being said. Without
    /// the levels, the gaps between words; with neither, none.
    /// </summary>
    public static IReadOnlyList<Silence> Find(VideoProject project, Track track, Func<MediaAsset, int, SoundLevels?>? levels, double longer = DefaultLongest)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(track);
        var runs = new List<Silence>();
        foreach (var clip in track.Clips.Where(c => !c.IsFreeze).OrderBy(c => c.Start))
        {
            if (project.Asset(clip.AssetId) is not { } asset)
            {
                continue;
            }

            var known = levels?.Invoke(asset, clip.Stream);
            var words = asset.Transcript is { } transcript && transcript.Stream == clip.Stream ? transcript.Words : [];
            foreach (var (from, to) in QuietInClip(clip, known, words))
            {
                runs.Add(new Silence(Times.Round(clip.TimelineAt(from)), Times.Round(clip.TimelineAt(to))));
            }
        }

        // Quiet either side of a cut is one pause on the timeline.
        var merged = new List<Silence>();
        foreach (var run in runs.OrderBy(r => r.Start))
        {
            if (merged.Count > 0 && run.Start <= merged[^1].End + SameTime)
            {
                merged[^1] = merged[^1] with { End = Math.Max(merged[^1].End, run.End) };
            }
            else
            {
                merged.Add(run);
            }
        }

        return [.. merged.Where(s => s.Length >= longer - Times.Epsilon)];
    }

    /// <summary>Quiet stretches inside what the clip plays, on the source's clock, without any words.</summary>
    private static List<(double From, double To)> QuietInClip(Clip clip, SoundLevels? levels, IReadOnlyList<SpokenWord> words)
    {
        var spans = new List<(double From, double To)>();
        if (levels is not null)
        {
            var first = levels.FrameAt(clip.SourceIn);
            var last = levels.FrameAt(clip.SourceOut);
            var start = -1;
            for (var frame = first; frame <= last + 1; frame++)
            {
                if (frame <= last && levels.IsQuiet(frame))
                {
                    if (start < 0)
                    {
                        start = frame;
                    }
                }
                else if (start >= 0)
                {
                    spans.Add((Math.Max(clip.SourceIn, (double)start / SoundLevels.PerSecond), Math.Min(clip.SourceOut, (double)frame / SoundLevels.PerSecond)));
                    start = -1;
                }
            }
        }
        else if (words.Count > 0)
        {
            spans.Add((clip.SourceIn, clip.SourceOut));
        }

        if (words.Count == 0)
        {
            return spans;
        }

        // Whatever a word covers isn't a pause: a whisper is quiet too.
        var result = new List<(double From, double To)>();
        var index = 0;
        foreach (var (spanFrom, spanTo) in spans)
        {
            var from = spanFrom;
            while (index < words.Count && words[index].End <= from)
            {
                index++;
            }

            for (var i = index; i < words.Count && words[i].Start < spanTo; i++)
            {
                if (words[i].Start > from)
                {
                    result.Add((from, words[i].Start));
                }

                from = Math.Max(from, words[i].End);
            }

            if (spanTo > from)
            {
                result.Add((from, spanTo));
            }
        }

        return result;
    }

    [Localizable(false)]
    private const string MicrophoneTitle = "Microphone";
}

public enum FillerKind
{
    /// <summary>A word that is only a filler: um, uh, اممم.</summary>
    Word,

    /// <summary>A word said twice in a row: the first of "the the".</summary>
    Repeat,

    /// <summary>Voiced sound that has no word: possibly a filler Whisper left out.</summary>
    Voiced,
}

/// <summary>A possible filler on the timeline.</summary>
/// <param name="Position">Its word's place in the <see cref="TranscriptView"/> it was found in (none for sound without a word).</param>
public sealed record Filler(FillerKind Kind, double Start, double End, string Text, int? Position = null)
{
    public double Length => End - Start;
}

/// <summary>Finds filler words in a transcript, and voiced sound between its words that has no word.</summary>
public static partial class FillerFinder
{
    /// <summary>The shortest voiced sound without a word that is worth a look, in seconds.</summary>
    public const double ShortestVoiced = 0.12;

    /// <summary>The longest. Anything longer is speech Whisper missed, or another voice, rather than a filler.</summary>
    public const double LongestVoiced = 2;

    /// <summary>Quiet this long, in seconds, separates two sounds (a stop inside a word is shorter).</summary>
    public const double SeparatingQuiet = 0.1;

    /// <summary>How much quiet is taken with a voiced sound each side, when there is that much, in seconds.</summary>
    private const double Margin = 0.03;

    /// <summary>A repeated word this far after the first, in seconds, is said again on purpose.</summary>
    private const double RepeatWithin = 0.6;

    /// <summary>"يعني" (it also is a real word: "means"), only when asked.</summary>
    [Localizable(false)]
    private const string Yani = "يعني";

    /// <summary>Whether <paramref name="word"/> is only a filler.</summary>
    public static bool IsFiller(string word, bool yani = false)
    {
        ArgumentNullException.ThrowIfNull(word);
        var plain = Plain(word);
        return plain.Length > 0 && (Fillers().IsMatch(plain) || (yani && plain == Yani));
    }

    /// <summary>
    /// The possible fillers of a transcript view in timeline order: filler words, the first of two words said twice in
    /// a row, and sounds with a pitch between words that no word covers, of <see cref="ShortestVoiced"/> to
    /// <see cref="LongestVoiced"/> seconds.
    /// </summary>
    public static IReadOnlyList<Filler> Find(VideoProject project, TranscriptView view, Func<MediaAsset, int, SoundLevels?>? levels = null, bool yani = false)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(view);
        var found = new List<Filler>();
        ShownWord? previous = null;
        for (var position = 0; position < view.Words.Count; position++)
        {
            var word = view.Words[position];
            if (word.IsCut)
            {
                continue;
            }

            var next = view.ShownAfter(position);
            if (!word.Word.IsSentence)
            {
                if (IsFiller(word.Word.Text, yani))
                {
                    found.Add(new Filler(FillerKind.Word, word.Start, word.End, word.Word.Text, position));
                }
                else if (next is { Word.IsSentence: false } && next.Start - word.End <= RepeatWithin && Plain(word.Word.Text) is { Length: > 0 } plain
                    && Plain(next.Word.Text) == plain && !char.IsDigit(plain[0]))
                {
                    found.Add(new Filler(FillerKind.Repeat, word.Start, word.End, word.Word.Text, position));
                }
            }
            else if (IsFiller(word.Word.Text, yani))
            {
                found.Add(new Filler(FillerKind.Word, word.Start, word.End, word.Word.Text, position));
            }

            if (levels is not null && !word.Word.IsSentence && project.Find(word.ClipId!) is { Item: Clip clip } && project.Asset(word.AssetId) is { Transcript: { } transcript } asset
                && levels(asset, transcript.Stream) is { } known)
            {
                var after = previous is not null && previous.ClipId == word.ClipId ? previous.Word.End : clip.SourceIn;
                foreach (var (from, to) in VoicedBefore(known, after, word.Word.End))
                {
                    found.Add(new Filler(FillerKind.Voiced, Times.Round(clip.TimelineAt(from)), Times.Round(clip.TimelineAt(to)), ""));
                }
            }

            previous = word;
        }

        return [.. found.OrderBy(f => f.Start)];
    }

    /// <summary>
    /// The sounds with a pitch from <paramref name="from"/> up to the sound of the word ending at <paramref name="wordEnd"/>:
    /// everything between quiet stretches that long there except the last, which is the word.
    /// </summary>
    internal static IEnumerable<(double From, double To)> VoicedBefore(SoundLevels levels, double from, double wordEnd)
    {
        foreach (var (first, last) in Islands(levels, levels.FrameAt(from), levels.FrameAt(wordEnd)).SkipLast(1))
        {
            var voiced = 0;
            for (var frame = first; frame <= last; frame++)
            {
                voiced += levels.IsVoiced(frame) ? 1 : 0;
            }

            var length = (double)(last - first + 1) / SoundLevels.PerSecond;
            if (length is >= ShortestVoiced and <= LongestVoiced && voiced >= (last - first + 1) * 0.6)
            {
                yield return (Math.Max(from, ((double)first / SoundLevels.PerSecond) - Margin), Math.Min(wordEnd, ((double)last / SoundLevels.PerSecond) + Margin));
            }
        }
    }

    /// <summary>The stretches of sound in the frames, those parted by less than <see cref="SeparatingQuiet"/> of quiet being one.</summary>
    internal static List<(int First, int Last)> Islands(SoundLevels levels, int from, int to)
    {
        var islands = new List<(int First, int Last)>();
        var separating = (int)Math.Round(SeparatingQuiet * SoundLevels.PerSecond);
        var start = -1;
        var quiet = 0;
        for (var frame = from; frame <= to; frame++)
        {
            if (levels.IsQuiet(frame))
            {
                quiet++;
                if (start >= 0 && quiet >= separating)
                {
                    islands.Add((start, frame - quiet));
                    start = -1;
                }
            }
            else
            {
                quiet = 0;
                if (start < 0)
                {
                    start = frame;
                }
            }
        }

        if (start >= 0)
        {
            islands.Add((start, to - quiet));
        }

        return islands;
    }

    /// <summary>A word without punctuation, case or Arabic's short vowels.</summary>
    internal static string Plain(string word)
    {
        var text = new StringBuilder(word.Length);
        foreach (var letter in word.Normalize(NormalizationForm.FormC))
        {
            var kind = CharUnicodeInfo.GetUnicodeCategory(letter);
            if (kind is UnicodeCategory.LowercaseLetter or UnicodeCategory.UppercaseLetter or UnicodeCategory.OtherLetter or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.ModifierLetter && letter != 'ـ')
            {
                text.Append(char.ToLowerInvariant(letter));
            }
        }

        return text.ToString();
    }

    /// <summary>
    /// English: um, uh, er, erm, ah, hmm and how they get written out. Arabic: اممم and إممم (three or more م when
    /// it starts with ا, as "امم" and "أمم" are words), آه and اه. Without the bare "أم" and "ام", real words.
    /// </summary>
    [GeneratedRegex("^(u+m+|u+h+|e+r+m*|a+h+|h+m+|m+h*m+|[اٱ]م{3,}|إم{2,}|آ+ه*|ا+ه+)$", RegexOptions.CultureInvariant)]
    private static partial Regex Fillers();
}
