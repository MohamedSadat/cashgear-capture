using System.Globalization;
using CashGear.Capture.Core.Resources;

namespace CashGear.Capture.Core.Recording;

/// <summary>What a recording records.</summary>
public enum RecordingAreaKind
{
    /// <summary>Asks: drag an area, or click a window or a monitor.</summary>
    Ask,

    /// <summary>A whole monitor.</summary>
    Monitor,

    /// <summary>Every monitor, as Settings says: on one canvas, or a track each.</summary>
    AllMonitors,

    /// <summary>One window, however it moves.</summary>
    Window,
}

/// <summary>When a recording stops by itself; whichever comes first. With none it stops when it is told to.</summary>
/// <param name="After">The video's length, not counting pauses.</param>
/// <param name="At">A moment.</param>
/// <param name="AppClosed">An app, by its program file (<c>notepad.exe</c>): when it has run and none of it is left.</param>
public sealed record RecordingStop(TimeSpan? After = null, DateTimeOffset? At = null, string? AppClosed = null);

/// <summary>A recording to be made: everything not said comes from Settings.</summary>
/// <param name="Area">What it records (null: the profile's, else asks).</param>
/// <param name="ProfileId">The profile whose settings it has.</param>
/// <param name="StartAt">When it starts (null: now).</param>
/// <param name="Stop">When it stops by itself.</param>
/// <param name="Monitor">The monitor for <see cref="RecordingAreaKind.Monitor"/>, by device name (null: the primary one).</param>
/// <param name="WindowTitle">The window for <see cref="RecordingAreaKind.Window"/>: its title, found again when it starts.</param>
/// <param name="WindowHandle">That window's handle, when it was picked by hand and the app hasn't restarted since.</param>
public sealed record RecordingPlan(
    RecordingAreaKind? Area = null,
    string? ProfileId = null,
    DateTimeOffset? StartAt = null,
    RecordingStop? Stop = null,
    string? Monitor = null,
    string? WindowTitle = null,
    long WindowHandle = 0)
{
    /// <summary>How long before its time a scheduled recording starts getting ready, so that it starts on the second.</summary>
    public static readonly TimeSpan LeadTime = TimeSpan.FromSeconds(3);

    /// <summary>A scheduled recording found this late (the PC was asleep, or the app closed) isn't made: it says so.</summary>
    public static readonly TimeSpan TooLate = TimeSpan.FromMinutes(1);

    /// <summary>Whether a plan that was to start at <paramref name="startAt"/> has been missed by <paramref name="now"/>.</summary>
    public static bool IsMissed(DateTimeOffset startAt, DateTimeOffset now) => now - startAt > TooLate;

    /// <summary>The next time this clock time comes round after <paramref name="now"/>: today, or tomorrow.</summary>
    public static DateTimeOffset NextAt(TimeOnly time, DateTimeOffset now)
    {
        var today = new DateTimeOffset(DateOnly.FromDateTime(now.DateTime).ToDateTime(time), now.Offset);
        return today > now ? today : today.AddDays(1);
    }
}

/// <summary>
/// <c>--record</c> with how it should go, from the command line:
/// <c>--duration 00:30:00</c>, <c>--start-at 14:30</c>, <c>--stop-at 15:00</c>, <c>--until-closes notepad.exe</c>,
/// <c>--profile Tutorial</c>, <c>--all-monitors</c>, <c>--monitor 2</c>, <c>--window "Title"</c>.
/// Any of them means nobody is there to pick an area: the primary monitor, unless they say otherwise.
/// </summary>
public static class RecordingCommandLine
{
    private const string Duration = "duration";
    private const string StartAt = "start-at";
    private const string StopAt = "stop-at";
    private const string UntilCloses = "until-closes";
    private const string Profile = "profile";
    private const string Monitor = "monitor";
    private const string Window = "window";
    private const string AllMonitors = "all-monitors";

    /// <summary>Every option, for help and for telling them from other arguments.</summary>
    public static IReadOnlyCollection<string> Options { get; } = [Duration, StartAt, StopAt, UntilCloses, Profile, Monitor, Window, AllMonitors];

    /// <summary>What is to be recorded; <paramref name="plan"/> stays null when the arguments don't say (a plain <c>--record</c> toggles).</summary>
    /// <param name="monitorByNumber">The device name of the monitor numbered n, from 1, or null.</param>
    /// <returns>Why the arguments can't be followed, in words for the user, or null.</returns>
    public static string? Parse(IReadOnlyList<string> args, DateTimeOffset now, out RecordingPlan? plan, Func<int, string?>? monitorByNumber = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        plan = null;
        TimeSpan? after = null;
        DateTimeOffset? startAt = null;
        DateTimeOffset? stopAt = null;
        string? app = null;
        string? profile = null;
        string? monitor = null;
        string? window = null;
        var all = false;
        var any = false;
        for (var i = 0; i < args.Count; i++)
        {
            var name = args[i].TrimStart('-', '/').ToLowerInvariant();
            if (!args[i].StartsWith('-') && !args[i].StartsWith('/'))
            {
                continue;
            }

            if (name == AllMonitors)
            {
                all = true;
                any = true;
                continue;
            }

            if (!Options.Contains(name))
            {
                continue;
            }

            any = true;
            if (i + 1 >= args.Count)
            {
                return CoreStrings.RecordingOptionNeedsValue("--" + name);
            }

            var value = args[++i];
            switch (name)
            {
                case Duration:
                    if (!TryLength(value, out var length))
                    {
                        return CoreStrings.RecordingOptionBad("--" + name, value);
                    }

                    after = length;
                    break;
                case StartAt:
                    if (!TryMoment(value, now, out var start))
                    {
                        return CoreStrings.RecordingOptionBad("--" + name, value);
                    }

                    startAt = start;
                    break;
                case StopAt:
                    if (!TryMoment(value, startAt ?? now, out var stop))
                    {
                        return CoreStrings.RecordingOptionBad("--" + name, value);
                    }

                    stopAt = stop;
                    break;
                case UntilCloses:
                    app = value;
                    break;
                case Profile:
                    profile = value;
                    break;
                case Window:
                    window = value;
                    break;
                case Monitor:
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) || monitorByNumber?.Invoke(number) is not { } device)
                    {
                        return CoreStrings.RecordingOptionBad("--" + name, value);
                    }

                    monitor = device;
                    break;
            }
        }

        if (!any)
        {
            return null;
        }

        // A stop-at given before the start-at, and so read from now: from the start, it is the next one after it.
        if (stopAt is { } end && startAt is { } begin && end <= begin)
        {
            stopAt = end.AddDays(1);
        }

        RecordingAreaKind? area = all ? RecordingAreaKind.AllMonitors
            : window is not null ? RecordingAreaKind.Window
            : monitor is not null ? RecordingAreaKind.Monitor
            : profile is null ? RecordingAreaKind.Monitor
            : null;
        var stops = after is not null || stopAt is not null || app is not null ? new RecordingStop(after, stopAt, app) : null;
        plan = new RecordingPlan(area, profile, startAt, stops, monitor, window);
        return null;
    }

    /// <summary>Arabic-Indic digits as 0 to 9: what an Arabic keyboard types.</summary>
    private static string WesternDigits(string text) =>
        string.Create(text.Length, text, static (span, from) =>
        {
            for (var i = 0; i < span.Length; i++)
            {
                var c = from[i];
                span[i] = c is >= '\u0660' and <= '\u0669' ? (char)('0' + (c - '\u0660')) : c is >= '\u06F0' and <= '\u06F9' ? (char)('0' + (c - '\u06F0')) : c;
            }
        });

    /// <summary>A length: <c>00:30:00</c>, <c>30:00</c> (minutes and seconds), or <c>90</c> (seconds).</summary>
    public static bool TryLength(string text, out TimeSpan length)
    {
        length = default;
        var parts = WesternDigits(text).Split(':');
        var numbers = new int[parts.Length];
        if (parts.Length > 3)
        {
            return false;
        }

        for (var i = 0; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
            {
                return false;
            }
        }

        var seconds = numbers.Aggregate(0L, (sum, n) => (sum * 60) + n);
        length = TimeSpan.FromSeconds(seconds);
        return seconds > 0;
    }

    /// <summary>A clock time (<c>14:30</c>, <c>14:30:15</c>, the next one after <paramref name="after"/>), or a date and time.</summary>
    public static bool TryMoment(string text, DateTimeOffset after, out DateTimeOffset moment)
    {
        moment = default;
        text = WesternDigits(text);
        if (TimeOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            moment = RecordingPlan.NextAt(time, after);
            return true;
        }

        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var date))
        {
            moment = new DateTimeOffset(date, after.Offset);
            return true;
        }

        return false;
    }
}
