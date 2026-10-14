using System.Text.Json.Nodes;

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
            // v2: output.copyToClipboard -> afterCapture.copyToClipboard
            if (Property(root, "output") is JsonObject output && Remove(output, "copyToClipboard") is { } copy)
            {
                var after = Property(root, "afterCapture") as JsonObject;
                if (after is null)
                {
                    after = [];
                    root["afterCapture"] = after;
                }

                if (Property(after, "copyToClipboard") is null)
                {
                    after["copyToClipboard"] = copy;
                }
            }
        }

        return version;
    }

    private static JsonNode? Property(JsonObject obj, string name) =>
        obj.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

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
