using System.Globalization;
using System.Text;
using CashGear.Capture.Media;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Resources;

namespace CashGear.Capture.VideoEditor.Export;

/// <summary>
/// The project's markers as chapters: in the file, as a WebVTT file, and as times in a YouTube description. All three
/// come from the one list, so they say the same. Chapters start at 0, as players and YouTube expect: when the first marker
/// is later, a first chapter covers what comes before it.
/// </summary>
public static class ProjectChapters
{
    /// <summary>What YouTube needs to show chapters at all.</summary>
    public const int YouTubeFewest = 3;

    public const double YouTubeShortest = 10;

    /// <summary>The chapters of the timeline as it is (a range already on its own clock). None without markers.</summary>
    public static IReadOnlyList<ExportChapter> From(VideoProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        var duration = project.Duration;
        var markers = (project.Markers ?? [])
            .Where(m => m.Time >= 0 && m.Time < duration)
            .OrderBy(m => m.Time)
            .GroupBy(m => Math.Round(m.Time * 1000))
            .Select(g => g.First())
            .ToList();
        if (markers.Count == 0)
        {
            return [];
        }

        var starts = new List<(double Time, string Title)>();
        if (markers[0].Time >= 0.001)
        {
            starts.Add((0, VideoEditorStrings.ChapterFirst));
        }

        var before = starts.Count;
        starts.AddRange([.. markers.Select((m, i) => (m.Time, m.Name.Trim().Length > 0 ? m.Name.Trim() : VideoEditorStrings.ChapterNumbered(before + i + 1)))]);
        return [.. starts.Select((c, i) => new ExportChapter(c.Time, i + 1 < starts.Count ? starts[i + 1].Time : duration, c.Title))];
    }

    /// <summary>The time as YouTube reads it in a description: <c>0:00</c>, <c>12:34</c>, <c>1:02:03</c>.</summary>
    public static string Stamp(double seconds)
    {
        var whole = (long)Math.Floor(seconds + 1e-6);
        var (hours, minutes, rest) = (whole / 3600, whole / 60 % 60, whole % 60);
        return hours > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}:{minutes:00}:{rest:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{minutes}:{rest:00}");
    }

    /// <summary>A line per chapter, the time first, for a YouTube description.</summary>
    public static string Description(IReadOnlyList<ExportChapter> chapters)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        return string.Join('\n', chapters.Select(c => $"{Stamp(c.Start)} {c.Title}"));
    }

    /// <summary>A description with the chapters' times after what it says already.</summary>
    public static string WithDescription(string? description, IReadOnlyList<ExportChapter> chapters)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        var text = (description ?? string.Empty).TrimEnd();
        return chapters.Count == 0 ? text : text.Length == 0 ? Description(chapters) : $"{text}\n\n{Description(chapters)}";
    }

    /// <summary>Why YouTube wouldn't show these as chapters, or null when it would.</summary>
    public static string? YouTubeProblem(IReadOnlyList<ExportChapter> chapters)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        if (chapters.Count == 0)
        {
            return null;
        }

        return chapters.Count < YouTubeFewest
            ? VideoEditorStrings.ChaptersTooFew(YouTubeFewest)
            : chapters.Any(c => c.End - c.Start < YouTubeShortest)
                ? VideoEditorStrings.ChaptersTooShort((long)YouTubeShortest)
                : null;
    }

    /// <summary>The WebVTT chapters file's name, beside the export.</summary>
    public static string VttPath(string exportPath) => Path.ChangeExtension(exportPath, null) + ".chapters.vtt";

    /// <summary>Writes the WebVTT chapters file: a cue per chapter, for a player's <c>kind="chapters"</c> track.</summary>
    public static void SaveVtt(string path, IReadOnlyList<ExportChapter> chapters)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        File.WriteAllText(path, SubtitleFiles.Write(chapters.Select(c => new SubtitleCue(c.Start, c.End, c.Title)), SubtitleFormat.Vtt), new UTF8Encoding(false));
    }
}
