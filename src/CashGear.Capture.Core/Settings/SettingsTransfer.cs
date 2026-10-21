using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using CashGear.Capture.Core.Resources;

namespace CashGear.Capture.Core.Settings;

/// <summary>A settings file that can't be imported, with why in words for the user.</summary>
public sealed class SettingsImportException([Localizable(true)] string message, Exception? inner = null) : Exception(message, inner);

/// <summary>What an exported settings file holds.</summary>
/// <param name="Settings">The settings, brought up to this version's schema and normalized.</param>
/// <param name="PresetsJson">The image editor's presets as JSON (presets.json's content), or null when the file has none.</param>
/// <param name="AppVersion">The version that exported them.</param>
public sealed record ImportedSettings(AppSettings Settings, string? PresetsJson, string? AppVersion);

/// <summary>
/// Settings › General › Export and Import: one JSON file with the settings and the image editor's presets, to move
/// them to another PC or keep a copy. Passwords and tokens are never in it (they are kept apart, encrypted for this
/// Windows user, and can't be read anywhere else), so destinations that need one ask for it again.
/// </summary>
[Localizable(false)]
public static class SettingsTransfer
{
    /// <summary>The file's <c>format</c>: tells an exported settings file from any other JSON.</summary>
    public const string Format = "cashgear-capture-settings";

    public const int FormatVersion = 1;

    /// <summary>The usual file name extension.</summary>
    public const string Extension = ".json";

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The export file's text.</summary>
    public static string Write(AppSettings settings, string? presetsJson, string appVersion, DateTimeOffset exported)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var root = new JsonObject
        {
            ["format"] = Format,
            ["version"] = FormatVersion,
            ["appVersion"] = appVersion,
            ["exported"] = exported.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
            ["settings"] = JsonSerializer.SerializeToNode(settings.Normalize(), SettingsJsonContext.Readable.AppSettings),
            ["presets"] = presetsJson is null ? null : JsonNode.Parse(presetsJson),
        };
        return root.ToJsonString(Indented);
    }

    /// <summary>Reads an export file, bringing settings from an older version up to date.</summary>
    /// <exception cref="SettingsImportException">Not an exported settings file, or damaged.</exception>
    public static ImportedSettings Read(string text)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(
                text,
                new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
                ?? throw new SettingsImportException(CoreStrings.NotASettingsFile);
        }
        catch (JsonException ex)
        {
            throw new SettingsImportException(CoreStrings.NotASettingsFile, ex);
        }

        if (!string.Equals((root["format"] as JsonValue)?.GetValue<string>(), Format, StringComparison.Ordinal))
        {
            throw new SettingsImportException(CoreStrings.NotASettingsFile);
        }

        if ((root["settings"] as JsonObject)?.ToJsonString() is not { } settingsJson)
        {
            throw new SettingsImportException(CoreStrings.SettingsFileWithoutSettings);
        }

        AppSettings settings;
        try
        {
            settings = JsonSettingsService.Parse(System.Text.Encoding.UTF8.GetBytes(settingsJson), out _);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or InvalidOperationException)
        {
            throw new SettingsImportException(CoreStrings.SettingsFileDamaged(ex.Message), ex);
        }

        var presets = root["presets"] is JsonObject presetsNode ? presetsNode.ToJsonString() : null;
        var version = root["appVersion"] is JsonValue value && value.TryGetValue<string>(out var text2) ? text2 : null;
        return new ImportedSettings(settings, presets, version);
    }

    /// <summary>
    /// What an import or a reset changes, kept to this PC: the choice about crash reports (asked on each PC), and for
    /// a reset also the language.
    /// </summary>
    public static AppSettings KeepLocal(AppSettings incoming, AppSettings current, bool keepLanguage)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        ArgumentNullException.ThrowIfNull(current);
        return incoming with
        {
            Appearance = keepLanguage ? incoming.Appearance with { Language = current.Appearance.Language } : incoming.Appearance,
            CrashReports = current.CrashReports,
        };
    }
}
