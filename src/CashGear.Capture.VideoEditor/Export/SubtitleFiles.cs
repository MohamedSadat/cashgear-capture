using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using CashGear.Capture.VideoEditor.Model;

namespace CashGear.Capture.VideoEditor.Export;

public enum SubtitleFormat
{
    /// <summary>SubRip (<c>.srt</c>): the most widely read.</summary>
    Srt,

    /// <summary>WebVTT (<c>.vtt</c>): for the web's &lt;track&gt;.</summary>
    Vtt,
}

/// <summary>Subtitle files an export writes next to its file.</summary>
[Flags]
public enum CaptionFiles
{
    None = 0,
    Srt = 1,
    Vtt = 2,
}

/// <summary>A caption in a subtitle file: from <see cref="Start"/> to <see cref="End"/> seconds (lines separated by <c>\n</c>).</summary>
public sealed record SubtitleCue(double Start, double End, string Text);

/// <summary>SRT and WebVTT files: written from a project's caption tracks, read into captions.</summary>
public static partial class SubtitleFiles
{
    public static string Extension(SubtitleFormat format) => format == SubtitleFormat.Vtt ? ".vtt" : ".srt";

    /// <summary>The captions with text on every caption track (shown or not), in time order.</summary>
    public static IReadOnlyList<SubtitleCue> Cues(VideoProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return [.. project.Tracks.Where(t => t.Kind == TrackKind.Caption)
            .SelectMany(t => t.Items.OfType<CaptionItem>())
            .Where(c => c.Text.Trim().Length > 0)
            .OrderBy(c => c.Start)
            .Select(c => new SubtitleCue(c.Start, c.End, c.Text.Trim().ReplaceLineEndings("\n")))];
    }

    /// <summary>A time as the format writes it: <c>01:02:03,456</c> (SRT) or <c>01:02:03.456</c> (VTT).</summary>
    public static string Time(double seconds, SubtitleFormat format)
    {
        var ms = (long)Math.Round(Math.Max(0, seconds) * 1000);
        var separator = format == SubtitleFormat.Vtt ? '.' : ',';
        return string.Create(CultureInfo.InvariantCulture, $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}{separator}{ms % 1000:000}");
    }

    public static string Write(IEnumerable<SubtitleCue> cues, SubtitleFormat format)
    {
        ArgumentNullException.ThrowIfNull(cues);
        var text = new StringBuilder();
        if (format == SubtitleFormat.Vtt)
        {
            text.Append("WEBVTT\n\n");
        }

        var number = 0;
        foreach (var cue in cues)
        {
            if (format == SubtitleFormat.Srt)
            {
                text.Append(CultureInfo.InvariantCulture, $"{++number}\n");
            }

            text.Append(CultureInfo.InvariantCulture, $"{Time(cue.Start, format)} --> {Time(cue.End, format)}\n");
            var lines = cue.Text.ReplaceLineEndings("\n").Split('\n').Where(l => l.Trim().Length > 0);
            foreach (var line in lines)
            {
                text.Append(format == SubtitleFormat.Vtt ? WebUtility.HtmlEncode(line.Trim()) : line.Trim()).Append('\n');
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>Writes the file in UTF-8 (with a byte-order mark for SRT, which some players need for Arabic).</summary>
    public static void Save(string path, IEnumerable<SubtitleCue> cues, SubtitleFormat format)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        File.WriteAllText(path, Write(cues, format), new UTF8Encoding(encoderShouldEmitUTF8Identifier: format == SubtitleFormat.Srt));
    }

    /// <exception cref="InvalidDataException">No captions could be read.</exception>
    public static IReadOnlyList<SubtitleCue> Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        return Read(File.ReadAllText(path));
    }

    /// <summary>The cues of an SRT or WebVTT file: numbers, notes and styles skipped, tags removed, entities decoded.</summary>
    /// <exception cref="InvalidDataException">No captions could be read.</exception>
    public static IReadOnlyList<SubtitleCue> Read(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var vtt = text.TrimStart('﻿').StartsWith("WEBVTT", StringComparison.Ordinal);
        var cues = new List<SubtitleCue>();
        foreach (var block in BlankLines().Split(text.ReplaceLineEndings("\n").Trim('﻿', '\n')))
        {
            var lines = block.Split('\n');
            var timing = Array.FindIndex(lines, l => l.Contains("-->", StringComparison.Ordinal));
            if (timing < 0)
            {
                continue;
            }

            var times = lines[timing].Split("-->", 2);
            if (ParseTime(times[0]) is not { } start || ParseTime(times[1].Trim().Split(' ', 2)[0]) is not { } end)
            {
                continue;
            }

            var body = string.Join('\n', lines.Skip(timing + 1).Select(l => Tags().Replace(l, "").Trim()).Where(l => l.Length > 0));
            if (vtt)
            {
                body = WebUtility.HtmlDecode(body);
            }

            if (body.Length > 0 && end > start)
            {
                cues.Add(new SubtitleCue(start, end, body));
            }
        }

        return cues.Count == 0 ? throw new InvalidDataException("This isn't an SRT or WebVTT file with captions in it.") : cues;
    }

    /// <summary><c>hh:mm:ss,mmm</c>, <c>hh:mm:ss.mmm</c> or <c>mm:ss.mmm</c>.</summary>
    private static double? ParseTime(string text)
    {
        var parts = text.Trim().Replace(',', '.').Split(':');
        if (parts.Length is < 2 or > 3 || !double.TryParse(parts[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            return null;
        }

        var minutes = int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var m) ? m : -1;
        var hours = parts.Length == 3 ? int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var h) ? h : -1 : 0;

        // In whole milliseconds, as the files have them, so a time read back is the time written.
        return minutes < 0 || hours < 0 ? null : ((((hours * 3600L) + (minutes * 60L)) * 1000) + (long)Math.Round(seconds * 1000)) / 1000.0;
    }

    [GeneratedRegex(@"\n[ \t]*\n+")]
    private static partial Regex BlankLines();

    /// <summary>The styling tags of SRT (<c>&lt;i&gt;</c>, <c>&lt;font …&gt;</c>) and WebVTT (voices, classes, timestamps).</summary>
    [GeneratedRegex(@"</?(?:i|b|u|c|v|font|ruby|rt|lang)(?:[ .][^>]*)?>|<\d[\d:.]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Tags();
}
