using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Markup;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.App.Platform;

/// <summary>
/// The app's language: English or Arabic (docs\localization.md). Chosen once, at start-up, from the setting (or
/// Windows' display language), as the UI culture every string of the .resx files is looked up in. Windows of a
/// right-to-left language are laid out right to left; dates and numbers keep the user's regional format.
/// </summary>
internal static class AppLanguage
{
    public static CultureInfo English { get; } = CultureInfo.GetCultureInfo("en");

    public static CultureInfo Arabic { get; } = CultureInfo.GetCultureInfo("ar");

    /// <summary>The languages the app has, in the order Settings lists them.</summary>
    public static IReadOnlyList<UiLanguage> Languages { get; } = [UiLanguage.Automatic, UiLanguage.English, UiLanguage.Arabic];

    /// <summary>Windows' display language, as it was when the app started (Automatic follows it).</summary>
    public static CultureInfo Windows { get; private set; } = CultureInfo.CurrentUICulture;

    /// <summary>
    /// At start-up, before any window: remembers Windows' display language and makes the language set in
    /// <paramref name="settingsFile"/> (or Windows') the UI culture.
    /// </summary>
    public static void Start(string settingsFile)
    {
        Windows = CultureInfo.CurrentUICulture;
        Apply(Resolve(ReadSetting(settingsFile), Windows));
    }

    /// <summary>Whether <paramref name="language"/> is a different language from the one the app is showing.</summary>
    public static bool NeedsRestart(UiLanguage language) =>
        Resolve(language, Windows).TwoLetterISOLanguageName != CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;

    /// <summary>The direction windows are laid out in for the thread's UI culture.</summary>
    public static FlowDirection FlowDirection =>
        CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    public static bool IsRightToLeft => FlowDirection == FlowDirection.RightToLeft;

    /// <summary>The culture for <paramref name="language"/>; Automatic follows <paramref name="windows"/>, Windows' display language.</summary>
    public static CultureInfo Resolve(UiLanguage language, CultureInfo windows) => language switch
    {
        UiLanguage.English => English,
        UiLanguage.Arabic => Arabic,
        _ => windows.TwoLetterISOLanguageName == "ar" ? Arabic : English,
    };

    /// <summary>
    /// The language setting in <paramref name="settingsFile"/>, read before anything else starts (the settings
    /// service comes later). Automatic when the file or the setting is missing or can't be read.
    /// </summary>
    public static UiLanguage ReadSetting(string settingsFile)
    {
        try
        {
            if (!File.Exists(settingsFile))
            {
                return UiLanguage.Automatic;
            }

            var root = JsonNode.Parse(
                File.ReadAllText(settingsFile),
                new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var value = root?["appearance"]?["language"]?.GetValue<string>();
            return Enum.TryParse<UiLanguage>(value, ignoreCase: true, out var language) && Enum.IsDefined(language) ? language : UiLanguage.Automatic;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException)
        {
            return UiLanguage.Automatic;
        }
    }

    /// <summary>
    /// Makes <paramref name="ui"/> the UI culture of this thread and every thread started after. Dates in a
    /// regional format whose calendar isn't the Gregorian one (Saudi Arabia's Hijri, for example) are shown in the
    /// Gregorian calendar, as file names and logs use it.
    /// </summary>
    public static void Apply(CultureInfo ui)
    {
        CultureInfo.DefaultThreadCurrentUICulture = ui;
        CultureInfo.CurrentUICulture = ui;

        var regional = CultureInfo.CurrentCulture;
        if (regional.Calendar is not GregorianCalendar && regional.OptionalCalendars.OfType<GregorianCalendar>().FirstOrDefault() is { } gregorian)
        {
            var culture = (CultureInfo)regional.Clone();
            culture.DateTimeFormat.Calendar = gregorian;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.CurrentCulture = culture;
        }
    }

    /// <summary>
    /// Lays <paramref name="element"/> (a window) out in the UI language's direction, with the regional format for
    /// numbers in bindings. Windows that show the screen pixel for pixel (the region overlay, the ruler) stay left to
    /// right and don't call this.
    /// </summary>
    public static void ApplyTo(FrameworkElement element)
    {
        element.FlowDirection = FlowDirection;
        element.Language = XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag);
    }
}
