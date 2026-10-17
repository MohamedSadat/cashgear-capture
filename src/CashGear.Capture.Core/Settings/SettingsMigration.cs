using System.Text.Json.Nodes;
using CashGear.Capture.Core.Recording;

namespace CashGear.Capture.Core.Settings;

/// <summary>
/// Upgrades older settings JSON before it is deserialized, so values survive when a setting moves. Works on the
/// JSON tree (property names matched case-insensitively, like the serializer) and never drops anything it does
/// not understand.
/// </summary>
public static class SettingsMigration
{
    /// <summary>Upgrades <paramref name="root"/> in place. Returns the schema version it started at.</summary>
    public static int Upgrade(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var version = Property(root, "schemaVersion") is JsonValue v && v.TryGetValue(out int n) ? n : 1;

        if (version < 2)
        {
            // v2: output.copyToClipboard -> afterCapture.copyToClipboard. Version 1 always saved, copied unless told
            // not to, and had no editor: spell that out, so newer defaults (open the editor) don't change what an
            // old file does.
            var copy = Property(root, "output") is JsonObject output ? Remove(output, "copyToClipboard") : null;
            if (Property(root, "afterCapture") is not JsonObject after)
            {
                after = [];
                root["afterCapture"] = after;
            }

            SetIfMissing(after, "save", JsonValue.Create(true));
            SetIfMissing(after, "copyToClipboard", copy ?? JsonValue.Create(true));
            SetIfMissing(after, "openEditor", JsonValue.Create(false));
        }

        if (version < 3 && Property(root, "recording") is JsonObject recording)
        {
            // v3: the 15-35 quality number (x264's scale, when ffmpeg.exe recorded) becomes the nearest preset. The
            // encoder's old values "Auto" and "Software" are valid ids already.
            var quality = Remove(recording, "quality");
            if (quality is JsonValue number && number.TryGetValue(out int legacy))
            {
                recording["quality"] = JsonValue.Create(EncoderPresets.FromLegacyQuality(legacy).ToString());
            }
            else if (quality is not null)
            {
                recording["quality"] = quality;
            }
        }

        return version;
    }

    private static JsonNode? Property(JsonObject obj, string name) =>
        obj.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static void SetIfMissing(JsonObject obj, string name, JsonNode value)
    {
        if (Property(obj, name) is null)
        {
            obj[name] = value;
        }
    }

    private static JsonNode? Remove(JsonObject obj, string name)
    {
        var key = obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        if (key is null)
        {
            return null;
        }

        var value = obj[key];
        obj.Remove(key);
        return value;
    }
}
