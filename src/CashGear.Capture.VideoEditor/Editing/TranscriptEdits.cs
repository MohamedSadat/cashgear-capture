using CashGear.Capture.Core.Transcripts;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;

namespace CashGear.Capture.VideoEditor.Editing;

/// <summary>A stretch of the timeline to take out.</summary>
public readonly record struct CutRange(double From, double To)
{
    public double Length => To - From;
}

/// <summary>
/// Editing by the transcript, as functions from a project to a new one: cutting words (the timeline's range delete,
/// snapped to the silence between them, with a short crossfade on the sound), putting cut words back, and correcting their
/// spelling, which changes the transcript and the captions and never the media.
/// </summary>
public static class TranscriptEdits
{
    /// <summary>How long the sound crossfades at a cut, in seconds: at most 20 ms, so a word next to it isn't touched, and long enough never to click.</summary>
    public const double JoinLength = 0.016;

    /// <summary>Overlaps up to this long, in seconds, are joins made by these edits rather than crossfades someone asked for.</summary>
    public const double LongestJoin = 0.03;

    /// <summary>A clip that starts this close before a word, in seconds, was cut there: cutting the word goes back to the cut.</summary>
    private const double NearCut = 0.5;

    /// <summary>Two times this close, in seconds, are the same place on the timeline or in the source.</summary>
    private const double SameTime = 0.002;

    /// <summary>The project with a transcript for its asset (one undo step for whoever applies it).</summary>
    public static VideoProject WithTranscript(VideoProject project, string assetId, MediaTranscript? transcript)
    {
        ArgumentNullException.ThrowIfNull(project);
        return project.Asset(assetId) is { } asset ? project.WithAsset(asset with { Transcript = transcript }) : project;
    }

    /// <summary>Where the selected words of <paramref name="view"/> (positions in its list) are on the timeline: the stretches to cut for them.</summary>
    public static IReadOnlyList<CutRange> RangesOf(VideoProject project, TranscriptView view, IEnumerable<int> positions, Func<MediaAsset, int, SoundLevels?>? levels = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(positions);
        var chosen = positions.Where(p => p >= 0 && p < view.Words.Count && !view.Words[p].IsCut).Distinct().Order().ToList();
        var ranges = new List<CutRange>();
        for (var i = 0; i < chosen.Count; i++)
        {
            // Words next to each other (cut words between them don't count) are one stretch.
            var last = i;
            while (last + 1 < chosen.Count && view.ShownAfter(chosen[last]) is { } following && ReferenceEquals(following, view.Words[chosen[last + 1]]))
            {
                last++;
            }

            var from = StartOf(project, view, chosen[i], levels);
            var to = EndOf(project, view, chosen[last], levels);
            if (to > from + Times.Epsilon)
            {
                ranges.Add(new CutRange(from, to));
            }

            i = last;
        }

        return ranges;
    }

    /// <summary>The ripple cut of the stretches, on every unlocked track, from the last to the first, each join crossfading on the sound.</summary>
    public static VideoProject Cut(VideoProject project, IEnumerable<CutRange> ranges)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(ranges);
        var result = project;
        foreach (var range in Merged(ranges).OrderByDescending(r => r.From))
        {
            result = Join(TimelineEdits.DeleteRange(result, range.From, range.To), range.From);
        }

        return result;
    }

    /// <summary>The selected words cut (<see cref="RangesOf"/>, <see cref="Cut(VideoProject, IEnumerable{CutRange})"/>).</summary>
    public static VideoProject CutWords(VideoProject project, IEnumerable<int> positions, Func<MediaAsset, int, SoundLevels?>? levels = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        return Cut(project, RangesOf(project, TranscriptView.Build(project), positions, levels));
    }

    /// <summary>
    /// Where sound clips meet at <paramref name="at"/> on the timeline: each goes on by half a join into what was cut, and
    /// the mixer crossfades them over the overlap. The pictures stay as they are, and the timeline is no longer. Nothing
    /// changes on a track with no more sound to read at either side.
    /// </summary>
    public static VideoProject Join(VideoProject project, double at)
    {
        ArgumentNullException.ThrowIfNull(project);
        var half = JoinLength / 2;
        at = Times.Round(at);
        return project with
        {
            Tracks = [.. project.Tracks.Select(track =>
            {
                if (track.Locked || !track.HasSound)
                {
                    return track;
                }

                var before = track.Clips.FirstOrDefault(c => Math.Abs(c.End - at) <= SameTime && !c.IsFreeze);
                var after = track.Clips.FirstOrDefault(c => Math.Abs(c.Start - at) <= SameTime && !c.IsFreeze && c.Id != before?.Id);
                if (before is null || after is null || project.Asset(before.AssetId) is not { } first
                    || before.SourceOut + (half * (before.EndSpeed ?? before.Speed)) > first.Duration
                    || after.SourceIn - (half * after.Speed) < 0)
                {
                    return track;
                }

                return track.WithItems(track.Items.Select(item =>
                    item.Id == before.Id ? TimelineEdits.Retime(before, ItemEdge.End, Times.Round(before.End + half))
                    : item.Id == after.Id ? TimelineEdits.Retime(after, ItemEdge.Start, Times.Round(after.Start - half))
                    : item));
            })],
        };
    }

    /// <summary>Takes out the joins at <paramref name="at"/>: the clips meet end to start again.</summary>
    private static VideoProject Unjoin(VideoProject project, double at) => project with
    {
        Tracks = [.. project.Tracks.Select(track =>
        {
            if (track.Locked || !track.HasSound)
            {
                return track;
            }

            return track.WithItems(track.Items.Select(item =>
            {
                if (item is not Clip clip)
                {
                    return item;
                }

                var endsInJoin = clip.End > at + Times.Epsilon && clip.End <= at + LongestJoin && track.Clips.Any(c => c.Id != clip.Id && c.Start < at && c.Start >= at - LongestJoin);
                var startsInJoin = clip.Start < at - Times.Epsilon && clip.Start >= at - LongestJoin && track.Clips.Any(c => c.Id != clip.Id && c.End > at && c.End <= at + LongestJoin);
                return endsInJoin ? clip.Slice(clip.Start, at) : startsInJoin ? clip.Slice(at, clip.End) : item;
            }));
        })],
    };

    /// <summary>
    /// Cut words back on the timeline where they were: for each run of them, the time they took goes back between the
    /// clips around it (on every unlocked track with that media), what follows moves on, and the captions over that place
    /// get those words again. Clips that meet with nothing cut between them become one. What was cut with the words on other
    /// tracks (annotations, say) stays cut: undo brings those back.
    /// </summary>
    public static VideoProject Restore(VideoProject project, IEnumerable<int> positions, Func<MediaAsset, int, SoundLevels?>? levels = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(positions);
        var view = TranscriptView.Build(project);
        var chosen = positions.Where(p => p >= 0 && p < view.Words.Count && view.Words[p].IsCut).Distinct().Order().ToList();

        // Each run, the last first: the places of those before it stay right.
        var runs = new List<(string Asset, int From, int To, double At)>();
        foreach (var position in chosen)
        {
            var word = view.Words[position];
            if (runs.Count > 0 && runs[^1].Asset == word.AssetId && runs[^1].To == word.Index - 1 && Math.Abs(runs[^1].At - word.Start) <= SameTime)
            {
                runs[^1] = runs[^1] with { To = word.Index };
            }
            else
            {
                runs.Add((word.AssetId, word.Index, word.Index, word.Start));
            }
        }

        var result = project;
        foreach (var (asset, from, to, at) in Enumerable.Reverse(runs))
        {
            result = RestoreRun(result, asset, from, to, at, levels);
        }

        return result;
    }

    private static VideoProject RestoreRun(VideoProject project, string assetId, int from, int to, double at, Func<MediaAsset, int, SoundLevels?>? levels)
    {
        if (project.Asset(assetId) is not { Transcript: { } transcript } asset)
        {
            return project;
        }

        var unjoined = Unjoin(project, at);
        var words = transcript.Words;
        var known = levels?.Invoke(asset, transcript.Stream);
        var voice = TranscriptView.ClipsOf(unjoined, asset);
        var before = voice.LastOrDefault(c => Math.Abs(c.End - at) <= SameTime);
        var after = voice.FirstOrDefault(c => Math.Abs(c.Start - at) <= SameTime);

        // The time the words took: to the middle of the silence each side, or what the clips around them left of it.
        var source0 = from > 0 ? Middle(known, words[from - 1].End, words[from].Start) : Math.Max(0, words[from].Start - 0.05);
        var source1 = to + 1 < words.Count ? Middle(known, words[to].End, words[to + 1].Start) : Math.Min(asset.Duration, words[to].End + 0.05);
        if (before is not null && (source0 < before.SourceOut || source0 - before.SourceOut < NearCut / 10))
        {
            source0 = before.SourceOut;
        }

        if (after is not null && (source1 > after.SourceIn || after.SourceIn - source1 < NearCut / 10))
        {
            source1 = after.SourceIn;
        }

        var neighbour = before ?? after;
        var speed = neighbour is null ? 1 : before is not null ? before.EndSpeed ?? before.Speed : after!.Speed;
        var length = Times.Round((source1 - source0) / speed);
        if (length <= Times.Epsilon)
        {
            return project;
        }

        var inserted = InsertTime(unjoined, at, length);
        var link = neighbour?.Link;
        var tracks = new List<Track>();
        foreach (var track in inserted.Tracks)
        {
            if (track.Locked || !(track.HasPictures || track.HasSound))
            {
                tracks.Add(track);
                continue;
            }

            tracks.Add(track.WithItems(WithSourceBack(track, assetId, at, length, source0, source1, speed)));
        }

        var result = inserted with { Tracks = tracks };
        result = WithCaptionWords(result, words, from, to, at, source0, speed);

        // What still meets across a cut crossfades again.
        result = JoinIfCut(result, Times.Round(at + length));
        result = JoinIfCut(result, at);
        return result;
    }

    /// <summary>What a picture or sound track has after the time from <paramref name="source0"/> to <paramref name="source1"/> goes back at <paramref name="at"/>.</summary>
    private static IEnumerable<TimelineItem> WithSourceBack(Track track, string assetId, double at, double length, double source0, double source1, double speed)
    {
        var clips = track.Clips.Where(c => c.AssetId == assetId && !c.IsFreeze).ToList();
        var before = clips.FirstOrDefault(c => Math.Abs(c.End - at) <= SameTime);
        var after = clips.FirstOrDefault(c => Math.Abs(c.Start - (at + length)) <= SameTime);
        var items = track.Items.ToList();
        if (before is null && after is null)
        {
            return items;
        }

        if (before is { IsRamp: false } && Math.Abs(before.SourceOut - source0) <= SameTime)
        {
            var longer = before with { SourceOut = Times.Round(source1) };
            items[items.IndexOf(before)] = longer;
            if (after is { IsRamp: false } && Math.Abs(after.SourceIn - source1) <= SameTime && SameLook(longer, after))
            {
                // Nothing is cut between them any more: one clip again.
                items.Remove(after);
                items[items.IndexOf(longer)] = longer with { SourceOut = after.SourceOut, FadeOut = after.FadeOut };
            }

            return items;
        }

        if (after is { IsRamp: false } && Math.Abs(after.SourceIn - source1) <= SameTime)
        {
            items[items.IndexOf(after)] = after with { Start = Times.Round(at), SourceIn = Times.Round(source0) };
            return items;
        }

        var model = before ?? after!;
        items.Add(model with
        {
            Id = Ids.New(),
            Start = Times.Round(at),
            SourceIn = Times.Round(source0),
            SourceOut = Times.Round(source1),
            Speed = speed,
            EndSpeed = null,
            FadeIn = 0,
            FadeOut = 0,
        });
        return items;
    }

    /// <summary>Two clips that differ only in where they are and what they play.</summary>
    private static bool SameLook(Clip one, Clip other) =>
        one.AssetId == other.AssetId && one.Stream == other.Stream && Math.Abs(one.Speed - other.Speed) < 1e-9 && one.Volume == other.Volume
        && Equals(one.Placement, other.Placement) && Equals(one.Background, other.Background);

    /// <summary>
    /// Room for <paramref name="length"/> seconds at <paramref name="at"/> on every unlocked track: what starts there or
    /// later moves on, clips across it are cut, and anything else across it lasts that much longer.
    /// </summary>
    private static VideoProject InsertTime(VideoProject project, double at, double length)
    {
        var split = TimelineEdits.Split(project, at, [.. project.Tracks.Where(t => t.HasPictures || t.HasSound).Select(t => t.Id)]);
        return split with
        {
            Tracks = [.. split.Tracks.Select(track => track.Locked ? track : track.WithItems(track.Items.Select(item =>
            {
                if (item.Start >= at - SameTime)
                {
                    return item with { Start = Times.Round(item.Start + length) };
                }

                if (item.End <= at + SameTime)
                {
                    return item;
                }

                return item is CaptionItem caption ? LongerCaption(caption, at, length) : item.Lengthen(ItemEdge.End, length);
            })))],
        };
    }

    /// <summary>A caption across <paramref name="at"/> with room for <paramref name="length"/> seconds there: the words after it move on.</summary>
    private static CaptionItem LongerCaption(CaptionItem caption, double at, double length)
    {
        var into = at - caption.Start;
        return caption with
        {
            Length = Times.Round(caption.Length + length),
            Words = caption.Words?.Select(w => w.Start >= into - SameTime ? w with { Start = Times.Round(w.Start + length), End = Times.Round(w.End + length) } : w).ToList(),
        };
    }

    /// <summary>The words back in the captions with words over that place, or as a caption of their own where nothing else is.</summary>
    private static VideoProject WithCaptionWords(VideoProject project, IReadOnlyList<SpokenWord> words, int from, int to, double at, double source0, double speed)
    {
        var back = Enumerable.Range(from, to - from + 1).Select(i => words[i]).Where(w => !w.IsSentence).ToList();
        if (back.Count == 0)
        {
            return project;
        }

        return project with
        {
            Tracks = [.. project.Tracks.Select(track =>
            {
                if (track.Locked || track.Kind != TrackKind.Caption || !track.Items.OfType<CaptionItem>().Any(c => c.Words is not null))
                {
                    return track;
                }

                var caption = track.Items.OfType<CaptionItem>().FirstOrDefault(c => c.Words is not null && c.Start <= at + SameTime && c.End > at + SameTime);
                if (caption is null)
                {
                    var start = Times.Round(at);
                    var said = back.Select(w => new CaptionWord(Times.Round((w.Start - source0) / speed), Times.Round((w.End - source0) / speed), " " + w.Text)).ToList();
                    var length = Times.Round(Math.Max(said[^1].End, TimelineEdits.ShortestItem));
                    if (track.Items.Any(i => i.Overlaps(start, start + length)))
                    {
                        return track;
                    }

                    return track.WithItems([.. track.Items, new CaptionItem { Id = Ids.New(), Start = start, Length = length, Words = said, Text = CaptionItem.Join(said) }]);
                }

                var into = at - caption.Start;
                var words2 = caption.Words!.Concat(back.Select(w => new CaptionWord(Times.Round(into + ((w.Start - source0) / speed)), Times.Round(into + ((w.End - source0) / speed)), " " + w.Text))).OrderBy(w => w.Start).ToList();
                return track.WithItems(track.Items.Select(i => i.Id == caption.Id ? caption with { Words = words2, Text = CaptionItem.Join(words2) } : i));
            })],
        };
    }

    /// <summary>A join at <paramref name="at"/> on sound tracks where the two clips don't simply carry on from one to the other.</summary>
    private static VideoProject JoinIfCut(VideoProject project, double at)
    {
        var cut = project.Tracks.Where(t => t.HasSound && !t.Locked).Any(track =>
            track.Clips.FirstOrDefault(c => Math.Abs(c.End - at) <= SameTime) is { } before
            && track.Clips.FirstOrDefault(c => Math.Abs(c.Start - at) <= SameTime && c.Id != before.Id) is { } after
            && Math.Abs(before.SourceOut - after.SourceIn) > SameTime);
        return cut ? Join(project, at) : project;
    }

    /// <summary>
    /// The word's text changed: the transcript's, and the captions saying it at that time. The media doesn't change.
    /// Back to what was heard, it no longer counts as corrected. An empty text changes nothing (Delete cuts a word).
    /// </summary>
    public static VideoProject Correct(VideoProject project, string assetId, int index, string text)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        if (text.Length == 0 || project.Asset(assetId) is not { Transcript: { } transcript } asset || index < 0 || index >= transcript.Words.Count)
        {
            return project;
        }

        var word = transcript.Words[index];
        if (word.Text == text)
        {
            return project;
        }

        var heard = word.Heard ?? word.Text;
        var corrected = word with { Text = text, Heard = heard == text ? null : heard };
        var list = transcript.Words.ToList();
        list[index] = corrected;
        var result = project.WithAsset(asset with { Transcript = transcript with { Words = list } });
        foreach (var shown in TranscriptView.Build(result).Words.Where(w => w.AssetId == assetId && w.Index == index && !w.IsCut))
        {
            result = CorrectCaptions(result, shown, word.Text, text);
        }

        return result;
    }

    private static VideoProject CorrectCaptions(VideoProject project, ShownWord shown, string old, string text) => project with
    {
        Tracks = [.. project.Tracks.Select(track =>
        {
            if (track.Locked || track.Kind != TrackKind.Caption)
            {
                return track;
            }

            var changed = false;
            var items = track.Items.Select(item =>
            {
                if (item is not CaptionItem caption || !caption.Overlaps(shown.Start, Math.Max(shown.End, shown.Start + Times.Epsilon)))
                {
                    return item;
                }

                var fixedCaption = CorrectCaption(caption, shown, old, text);
                changed |= !ReferenceEquals(fixedCaption, caption);
                return fixedCaption;
            }).ToList();
            return changed ? track.WithItems(items) : track;
        })],
    };

    /// <summary>A caption with the word corrected: the word it has at that time, or its text when the word is in it just once.</summary>
    private static CaptionItem CorrectCaption(CaptionItem caption, ShownWord shown, string old, string text)
    {
        var middle = ((shown.Start + shown.End) / 2) - caption.Start;
        if (caption.Words is { Count: > 0 } words)
        {
            // The word nearest that time that spells what was there (a word in pieces is left to its first piece).
            var best = words.Select((w, i) => (Word: w, Index: i))
                .Where(p => Normal(p.Word.Text) == Normal(old) || Normal(old).StartsWith(Normal(p.Word.Text), StringComparison.Ordinal) || Normal(p.Word.Text).StartsWith(Normal(old), StringComparison.Ordinal))
                .OrderBy(p => Math.Abs(((p.Word.Start + p.Word.End) / 2) - middle))
                .Select(p => (int?)p.Index)
                .FirstOrDefault();
            if (best is not { } found)
            {
                return caption;
            }

            var list = words.ToList();
            var word = list[found];
            var space = word.Text.StartsWith(' ') ? " " : "";

            // Punctuation on the piece stays unless the correction has its own.
            list[found] = word with { Text = space + text };

            // The rest of a word in pieces goes with it.
            for (var i = found + 1; i < list.Count && !list[i].Text.StartsWith(' ') && Normal(old).Length > Normal(word.Text).Length; i++)
            {
                list[i] = list[i] with { Text = "" };
            }

            return caption with { Words = list, Text = CaptionItem.Join(list) };
        }

        var parts = caption.Text.Split(' ');
        var matches = parts.Select((p, i) => (p, i)).Where(p => p.p == old).ToList();
        if (matches.Count != 1)
        {
            return caption;
        }

        parts[matches[0].i] = text;
        return caption with { Text = string.Join(' ', parts) };
    }

    private static string Normal(string text) => text.Trim();

    /// <summary>Captions of the words the timeline plays, for this cut: a new track with the look of the first caption track there is, on top of the others.</summary>
    public static (VideoProject Project, string TrackId) CaptionsFromTranscript(VideoProject project, string? name = null, CaptionLimits? limits = null)
    {
        ArgumentNullException.ThrowIfNull(project);
        var view = TranscriptView.Build(project);
        var segments = new List<TranscriptSegment>();
        var current = new List<ShownWord>();
        void Flush()
        {
            if (current.Count > 0)
            {
                segments.Add(new TranscriptSegment(current[0].Start, current[^1].End, string.Join(' ', current.Select(w => w.Word.Text)), current[0].Word.IsSentence ? null : [.. current.Select(w => new TranscriptWord(w.Start, w.End, " " + w.Word.Text))]));
                current = [];
            }
        }

        foreach (var word in view.Words.Where(w => !w.IsCut))
        {
            // A sentence with no word times, and what comes after a pause, stand alone.
            if (current.Count > 0 && (word.Word.IsSentence || current[0].Word.IsSentence || word.Start - current[^1].End > TranscriptView.ParagraphPause))
            {
                Flush();
            }

            current.Add(word);
        }

        Flush();
        var style = project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Caption)?.Captions;
        var (added, id) = CaptionEdits.AddTrack(project, CaptionEdits.FromTranscript(segments, 0, limits), name);
        return (added.Track(id) is { } track ? added.WithTrack(track with { Captions = style }) : added, id);
    }

    /// <summary>The start of the time to cut for the word at <paramref name="position"/>.</summary>
    private static double StartOf(VideoProject project, TranscriptView view, int position, Func<MediaAsset, int, SoundLevels?>? levels)
    {
        var word = view.Words[position];
        var (clip, track) = ClipOf(project, word);
        var previous = view.ShownBefore(position);
        var known = KnownLevels(project, word, levels);
        if (previous is not null && previous.ClipId == word.ClipId)
        {
            return Times.Round(clip.TimelineAt(Middle(known, previous.Word.End, word.Word.Start)));
        }

        var edge = EdgeBefore(track, clip);
        return word.Word.Start - clip.SourceIn <= NearCut ? edge : Times.Round(clip.TimelineAt(Middle(known, clip.SourceIn, word.Word.Start)));
    }

    /// <summary>The end of the time to cut for the word at <paramref name="position"/>.</summary>
    private static double EndOf(VideoProject project, TranscriptView view, int position, Func<MediaAsset, int, SoundLevels?>? levels)
    {
        var word = view.Words[position];
        var (clip, track) = ClipOf(project, word);
        var next = view.ShownAfter(position);
        var known = KnownLevels(project, word, levels);
        if (next is not null && next.ClipId == word.ClipId)
        {
            return Times.Round(clip.TimelineAt(Middle(known, word.Word.End, next.Word.Start)));
        }

        var edge = EdgeAfter(track, clip);
        return clip.SourceOut - word.Word.End <= NearCut ? edge : Times.Round(clip.TimelineAt(Middle(known, word.Word.End, clip.SourceOut)));
    }

    private static (Clip Clip, Track Track) ClipOf(VideoProject project, ShownWord word)
    {
        var found = project.Find(word.ClipId!) ?? throw new ArgumentException($"No clip {word.ClipId}.", nameof(word));
        return ((Clip)found.Item, found.Track);
    }

    /// <summary>Where a clip's time starts, a join's overlap shared with the clip before it.</summary>
    internal static double EdgeBefore(Track track, Clip clip)
    {
        var joined = track.Clips.FirstOrDefault(c => c.Id != clip.Id && c.End > clip.Start + Times.Epsilon && c.End - clip.Start <= LongestJoin && c.Start < clip.Start);
        return joined is null ? clip.Start : Times.Round((joined.End + clip.Start) / 2);
    }

    /// <summary>Where a clip's time ends, a join's overlap shared with the clip after it.</summary>
    internal static double EdgeAfter(Track track, Clip clip)
    {
        var joined = track.Clips.FirstOrDefault(c => c.Id != clip.Id && c.Start < clip.End - Times.Epsilon && clip.End - c.Start <= LongestJoin && c.End > clip.End);
        return joined is null ? clip.End : Times.Round((clip.End + joined.Start) / 2);
    }

    private static SoundLevels? KnownLevels(VideoProject project, ShownWord word, Func<MediaAsset, int, SoundLevels?>? levels) =>
        project.Asset(word.AssetId) is { Transcript: { } transcript } asset ? levels?.Invoke(asset, transcript.Stream) : null;

    /// <summary>
    /// The middle of the silence between two source times: of the longest quiet stretch there when the sound's levels are
    /// known (a gap can hold a breath), else of the gap.
    /// </summary>
    public static double Middle(SoundLevels? levels, double from, double to)
    {
        if (to <= from)
        {
            return Times.Round((from + to) / 2);
        }

        if (levels is not null)
        {
            var first = levels.FrameAt(from);
            var last = levels.FrameAt(to);
            var bestStart = -1;
            var bestLength = 0;
            var runStart = -1;
            for (var frame = first; frame <= last + 1; frame++)
            {
                if (frame <= last && levels.IsQuiet(frame))
                {
                    if (runStart < 0)
                    {
                        runStart = frame;
                    }
                }
                else if (runStart >= 0)
                {
                    if (frame - runStart > bestLength)
                    {
                        bestLength = frame - runStart;
                        bestStart = runStart;
                    }

                    runStart = -1;
                }
            }

            if (bestLength > 0)
            {
                var middle = (bestStart + ((bestLength - 1) / 2.0)) / SoundLevels.PerSecond;
                return Times.Round(Math.Clamp(middle, from, to));
            }
        }

        return Times.Round((from + to) / 2);
    }

    /// <summary>Stretches that touch or overlap, as one.</summary>
    private static List<CutRange> Merged(IEnumerable<CutRange> ranges)
    {
        var merged = new List<CutRange>();
        foreach (var range in ranges.Where(r => r.To > r.From + Times.Epsilon).OrderBy(r => r.From))
        {
            if (merged.Count > 0 && range.From <= merged[^1].To + Times.Epsilon)
            {
                merged[^1] = new CutRange(merged[^1].From, Math.Max(merged[^1].To, range.To));
            }
            else
            {
                merged.Add(range);
            }
        }

        return merged;
    }
}
