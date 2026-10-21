using CashGear.Capture.Core.Transcripts;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Resources;

namespace CashGear.Capture.VideoEditor.Editing;

/// <summary>How much a caption made from speech may hold: lines of so many letters, and how long it may last.</summary>
public sealed record CaptionLimits(int LineLength = 42, int Lines = 2, double LongestTime = 7, double ShortestTime = 0.8);

/// <summary>Making captions (from speech, from subtitle files, by hand), splitting and merging them.</summary>
public static class CaptionEdits
{
    private const string SentenceEnds = ".?!…؟";

    /// <summary>
    /// Captions from a transcript whose times start at <paramref name="offset"/> on the timeline. Each stretch of
    /// speech is cut into captions of at most <see cref="CaptionLimits.Lines"/> lines of
    /// <see cref="CaptionLimits.LineLength"/> letters and <see cref="CaptionLimits.LongestTime"/> seconds, between
    /// words, by the words' times (a sentence that ends half way through a caption ends it); without word times, the
    /// text is cut at spaces and the time shared out by letters. Captions never overlap, and last at least
    /// <see cref="CaptionLimits.ShortestTime"/> where there is room.
    /// </summary>
    public static IReadOnlyList<CaptionItem> FromTranscript(IEnumerable<TranscriptSegment> segments, double offset = 0, CaptionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(segments);
        limits ??= new CaptionLimits();
        var most = limits.LineLength * limits.Lines;
        var parts = new List<(double Start, double End, string Text, List<TranscriptWord>? Words)>();
        foreach (var segment in segments.OrderBy(s => s.Start))
        {
            var text = segment.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            var words = segment.Words?.Where(w => w.Text.Length > 0).ToList();
            if (words is { Count: > 0 })
            {
                var current = new List<TranscriptWord>();
                foreach (var word in words)
                {
                    // A caption ends only between words (a piece of a word joins the one before).
                    var startsWord = word.Text[0] == ' ';
                    var joined = Join(current) + word.Text;
                    var full = joined.Trim().Length > most || word.End - current.FirstOrDefault()?.Start > limits.LongestTime;
                    var sentence = Join(current).TrimEnd() is { Length: > 0 } sofar && SentenceEnds.Contains(sofar[^1]) && sofar.Trim().Length >= limits.LineLength / 2;
                    if (current.Count > 0 && startsWord && (full || sentence))
                    {
                        parts.Add((current[0].Start, current[^1].End, Join(current).Trim(), current));
                        current = [];
                    }

                    current.Add(word);
                }

                if (current.Count > 0)
                {
                    parts.Add((current[0].Start, current[^1].End, Join(current).Trim(), current));
                }

                continue;
            }

            var pieces = Pieces(text, most);
            var letters = pieces.Sum(p => p.Length);
            var at = segment.Start;
            foreach (var piece in pieces)
            {
                var length = (segment.End - segment.Start) * piece.Length / letters;
                parts.Add((at, at + length, piece, null));
                at += length;
            }
        }

        var captions = new List<CaptionItem>();
        for (var i = 0; i < parts.Count; i++)
        {
            var (start, end, text, words) = parts[i];
            start = Math.Max(start, captions.Count > 0 ? captions[^1].End - offset : 0);
            var next = i + 1 < parts.Count ? parts[i + 1].Start : double.PositiveInfinity;
            end = Math.Min(Math.Max(end, start + limits.ShortestTime), Math.Max(end, next));
            if (end - start < TimelineEdits.ShortestItem)
            {
                continue;
            }

            var from = Times.Round(start + offset);
            captions.Add(new CaptionItem
            {
                Id = Ids.New(),
                Start = from,
                Length = Times.Round(end - start),
                Text = text,
                Words = words?.Select(w => new CaptionWord(Times.Round(Math.Max(0, w.Start - start)), Times.Round(Math.Max(0, w.End - start)), w.Text)).ToList(),
            });
        }

        return captions;
    }

    /// <summary>Captions from a subtitle file's cues (no word times).</summary>
    public static IReadOnlyList<CaptionItem> FromCues(IEnumerable<SubtitleCue> cues)
    {
        ArgumentNullException.ThrowIfNull(cues);
        return [.. cues.Where(c => c.End - c.Start >= TimelineEdits.ShortestItem && c.Text.Trim().Length > 0).OrderBy(c => c.Start).Select(c => new CaptionItem
        {
            Id = Ids.New(),
            Start = Times.Round(Math.Max(0, c.Start)),
            Length = Times.Round(c.End - Math.Max(0, c.Start)),
            Text = c.Text.Trim(),
        })];
    }

    /// <summary>
    /// A caption track named <paramref name="name"/> (null: "Captions" in the UI language) with <paramref name="captions"/>,
    /// on top of the others.
    /// </summary>
    public static (VideoProject Project, string TrackId) AddTrack(VideoProject project, IEnumerable<CaptionItem> captions, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var track = new Track(Ids.New(), TrackKind.Caption, name ?? VideoEditorStrings.TrackCaptions, []).WithItems(captions);
        return (project with { Tracks = [.. project.Tracks, track] }, track.Id);
    }

    /// <summary>A caption from <paramref name="at"/> on the first unlocked caption track with room for it (a new one on top if none has).</summary>
    public static (VideoProject Project, string Id) Add(VideoProject project, double at, double length, string text)
    {
        ArgumentNullException.ThrowIfNull(project);
        var item = new CaptionItem { Id = Ids.New(), Start = Times.Round(Math.Max(0, at)), Length = Times.Round(Math.Max(TimelineEdits.ShortestItem, length)), Text = text };
        var free = project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Caption && !t.Locked && !t.Items.Any(i => i.Overlaps(item.Start, item.End)));
        if (free is not null)
        {
            return (project.WithTrack(free.WithItems([.. free.Items, item])), item.Id);
        }

        var (added, _) = AddTrack(project, [item]);
        return (added, item.Id);
    }

    /// <summary>
    /// The caption cut in two at <paramref name="time"/>: its words shared out by when they are said, or (written
    /// by hand) its text at the space nearest the same part of its letters. The second part gets a new id.
    /// </summary>
    public static (VideoProject Project, string? SecondId) Split(VideoProject project, string id, double time)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Find(id) is not ({ } track, CaptionItem caption) || time <= caption.Start + TimelineEdits.ShortestItem || time >= caption.End - TimelineEdits.ShortestItem)
        {
            return (project, null);
        }

        var first = (CaptionItem)caption.Slice(caption.Start, time);
        var second = (CaptionItem)caption.Slice(time, caption.End) with { Id = Ids.New() };
        if (caption.Words is null)
        {
            var (before, after) = SplitText(caption.Text, (time - caption.Start) / caption.Length);
            first = first with { Text = before };
            second = second with { Text = after };
        }

        return (project.WithTrack(track.WithItems([.. track.Items.Where(i => i.Id != id), first, second])), second.Id);
    }

    /// <summary>The caption joined with the next one on its track: from its start to the next one's end, both texts (and words).</summary>
    public static VideoProject MergeWithNext(VideoProject project, string id)
    {
        ArgumentNullException.ThrowIfNull(project);
        if (project.Find(id) is not ({ } track, CaptionItem caption)
            || track.Items.OfType<CaptionItem>().Where(c => c.Id != id && c.Start >= caption.Start + Times.Epsilon).OrderBy(c => c.Start).FirstOrDefault() is not { } next)
        {
            return project;
        }

        var shift = next.Start - caption.Start;
        var words = caption.Words is not null && next.Words is not null
            ? caption.Words.Concat(next.Words.Select(w => w with { Start = Times.Round(w.Start + shift), End = Times.Round(w.End + shift) })).ToList()
            : null;
        var merged = caption with
        {
            Length = Times.Round(next.End - caption.Start),
            Words = words,
            Text = words is not null ? CaptionItem.Join(words) : $"{caption.Text.Trim()} {next.Text.Trim()}".Trim(),
        };
        return project.WithTrack(track.WithItems(track.Items.Where(i => i.Id != next.Id).Select(i => i.Id == id ? merged : i)));
    }

    /// <summary>The text cut at the space nearest <paramref name="fraction"/> of its letters (all of it in both when it has no space).</summary>
    public static (string Before, string After) SplitText(string text, double fraction)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        var target = (int)Math.Round(text.Length * Math.Clamp(fraction, 0, 1));
        var best = -1;
        for (var i = 0; i < text.Length; i++)
        {
            if (char.IsWhiteSpace(text[i]) && (best < 0 || Math.Abs(i - target) < Math.Abs(best - target)))
            {
                best = i;
            }
        }

        return best < 0 ? (text, text) : (text[..best].Trim(), text[(best + 1)..].Trim());
    }

    private static string Join(IEnumerable<TranscriptWord> words) => string.Concat(words.Select(w => w.Text));

    /// <summary>The text cut at spaces into pieces of at most <paramref name="most"/> letters (a longer word is a piece of its own).</summary>
    private static List<string> Pieces(string text, int most)
    {
        var pieces = new List<string>();
        var current = "";
        foreach (var word in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && candidate.Length > most)
            {
                pieces.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Length > 0)
        {
            pieces.Add(current);
        }

        return pieces;
    }
}
