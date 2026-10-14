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
internal sealed partial class SettingsJsonContext : JsonSerializerContext;
