using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using CashGear.Capture.Core.Capture;

namespace CashGear.Capture.Core.Output;

/// <summary>What a file name is built from.</summary>
/// <param name="Time">When the capture was taken.</param>
/// <param name="MonitorName">Short monitor name (<c>DISPLAY1</c>) when the capture sits on one monitor.</param>
/// <param name="WindowTitle">Title of the captured window, for window captures.</param>
public sealed record FileNameContext(DateTimeOffset Time, CaptureKind Kind, string? MonitorName = null, string? WindowTitle = null);

/// <summary>
/// Expands file-name templates such as <c>Capture_{yyyy}-{MM}-{dd}_{HH}-{mm}-{ss}_{kind}</c> and makes the result a
/// valid Windows file name (no extension; the format adds it).
/// </summary>
public static partial class FileNameTemplate
{
    public const string Default = "Capture_{yyyy}-{MM}-{dd}_{HH}-{mm}-{ss}_{kind}";

    /// <summary>Longest name produced, leaving room for the folder, a " (n)" suffix and the extension.</summary>
    public const int MaxLength = 150;

    private const string Fallback = "Capture";

    /// <summary>Every token with a one-line description (for the settings window).</summary>
    public static IReadOnlyList<(string Token, string Description)> Tokens { get; } =
    [
        ("{yyyy}", "Year, e.g. 2026"),
        ("{MM}", "Month, 01–12"),
        ("{dd}", "Day, 01–31"),
        ("{HH}", "Hour, 00–23"),
        ("{mm}", "Minute, 00–59"),
        ("{ss}", "Second, 00–59"),
        ("{counter}", "Running number, 0001, 0002, … (kept across restarts)"),
        ("{kind}", "fullscreen, monitor, region, window, scrolling or recording"),
        ("{monitor}", "Monitor name, e.g. DISPLAY1 (all for the whole screen, multi when spanning)"),
        ("{window}", "Title of the captured window (empty otherwise)"),
    ];

    private static readonly HashSet<string> KnownTokens = new(
        ["yyyy", "MM", "dd", "HH", "mm", "ss", "counter", "kind", "monitor", "window"], StringComparer.Ordinal);

    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"],
        StringComparer.OrdinalIgnoreCase);

    private static readonly char[] InvalidChars = Path.GetInvalidFileNameChars();

    /// <summary>Whether the template uses <paramref name="token"/> (name without braces, e.g. <c>monitor</c>).</summary>
    public static bool Uses(string template, string token) =>
        template.Contains("{" + token + "}", StringComparison.Ordinal);

    /// <summary>
    /// Checks the template: not blank, braces balanced, only known tokens (case-sensitive: <c>{MM}</c> is the month,
    /// <c>{mm}</c> the minute).
    /// </summary>
    public static bool TryValidate(string? template, out string? error)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            error = "The file name template is empty.";
            return false;
        }

        for (var i = 0; i < template.Length; i++)
        {
            if (template[i] == '}')
            {
                error = $"Unexpected '}}' at position {i + 1}.";
                return false;
            }

            if (template[i] != '{')
            {
                continue;
            }

            var close = template.IndexOf('}', i + 1);
            if (close < 0)
            {
                error = $"'{{' at position {i + 1} is never closed.";
                return false;
            }

            var token = template[(i + 1)..close];
            if (!KnownTokens.Contains(token))
            {
                error = $"Unknown token {{{token}}}.";
                return false;
            }

            i = close;
        }

        error = null;
        return true;
    }

    /// <summary>
    /// Expands the tokens and sanitizes the result. <paramref name="nextCounter"/> is only called when the template
    /// uses <c>{counter}</c>, so the counter advances only for files that show it. Unknown tokens stay as written.
    /// </summary>
    public static string Expand(string template, FileNameContext context, Func<int>? nextCounter = null)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(context);

        var result = new StringBuilder(template.Length + 32);
        for (var i = 0; i < template.Length; i++)
        {
            var close = template[i] == '{' ? template.IndexOf('}', i + 1) : -1;
            if (close < 0)
            {
                result.Append(template[i]);
                continue;
            }

            var token = template[(i + 1)..close];
            if (Value(token, context, nextCounter) is { } value)
            {
                result.Append(value);
            }
            else
            {
                result.Append(template, i, close - i + 1);
            }

            i = close;
        }

        return Sanitize(result.ToString());
    }

    /// <summary>
    /// Makes any text a usable file name: invalid characters become '_', separators left doubled by empty tokens
    /// collapse, leading/trailing separators, dots and spaces go, reserved device names get a '_' prefix, and the
    /// length is capped. Never returns an empty string.
    /// </summary>
    public static string Sanitize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var chars = name.Select(c => Array.IndexOf(InvalidChars, c) >= 0 ? '_' : c).ToArray();
        var cleaned = RepeatedSeparators().Replace(new string(chars), "$1");
        cleaned = cleaned.Trim(' ', '.', '_', '-');

        if (cleaned.Length > MaxLength)
        {
            cleaned = cleaned[..MaxLength].TrimEnd(' ', '.', '_', '-');
        }

        if (cleaned.Length == 0)
        {
            return Fallback;
        }

        var stem = cleaned.Split('.')[0];
        return ReservedNames.Contains(stem) ? "_" + cleaned : cleaned;
    }

    private static string? Value(string token, FileNameContext c, Func<int>? nextCounter) => token switch
    {
        "yyyy" => c.Time.ToString("yyyy", CultureInfo.InvariantCulture),
        "MM" => c.Time.ToString("MM", CultureInfo.InvariantCulture),
        "dd" => c.Time.ToString("dd", CultureInfo.InvariantCulture),
        "HH" => c.Time.ToString("HH", CultureInfo.InvariantCulture),
        "mm" => c.Time.ToString("mm", CultureInfo.InvariantCulture),
        "ss" => c.Time.ToString("ss", CultureInfo.InvariantCulture),
        "counter" => (nextCounter?.Invoke() ?? 1).ToString("D4", CultureInfo.InvariantCulture),
        "kind" => c.Kind switch
        {
            CaptureKind.AllMonitors => "fullscreen",
            CaptureKind.Monitor => "monitor",
            CaptureKind.Window => "window",
            CaptureKind.Scrolling => "scrolling",
            CaptureKind.Recording => "recording",
            _ => "region",
        },
        "monitor" => c.MonitorName ?? (c.Kind == CaptureKind.AllMonitors ? "all" : "multi"),
        "window" => c.WindowTitle ?? string.Empty,
        _ => null,
    };

    // The same separator repeated ("__", "--", "  "), typically left by an empty {window}. Mixed runs such as
    // " - " are kept: they are deliberate in window titles.
    [GeneratedRegex(@"([_\- ])\1+")]
    private static partial Regex RepeatedSeparators();
}
