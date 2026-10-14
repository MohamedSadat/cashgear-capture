using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CashGear.Capture.Core.Settings;

/// <summary>Source-generated serializer for the settings file (trim/AOT friendly). Lenient on read since users edit it.</summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext
{
    private static SettingsJsonContext? _readable;

    /// <summary>
    /// For writing: the file is meant to be read and edited by people, so characters such as '+' in
    /// <c>Ctrl+PrintScreen</c> are written as-is instead of HTML-safe escapes. Created on first use, after the
    /// generated <see cref="Default"/> context exists.
    /// </summary>
    public static SettingsJsonContext Readable => _readable ??= new(new JsonSerializerOptions(Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
