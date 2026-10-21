using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CashGear.Capture.App.Diagnostics;

/// <summary>
/// What a diagnostics zip leaves out of the files it copies: the text that names a server, an account, an address
/// or a person. Values are replaced by <see cref="Removed"/>, so the zip still shows that a setting was set.
/// </summary>
internal static class DiagnosticsRedaction
{
    public const string Removed = "(removed)";

    /// <summary>The settings whose text is left out; <c>[]</c> stands for every item of a list (names as in the file, any case).</summary>
    private static readonly HashSet<string> SettingsPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "destinations.servers[].name",
        "destinations.servers[].host",
        "destinations.servers[].userName",
        "destinations.servers[].remoteFolder",
        "destinations.servers[].publicUrl",
        "destinations.servers[].trustedFingerprint",
        "destinations.servers[].keyFile",
        "destinations.uploads[].name",
        "destinations.uploads[].url",
        "destinations.uploads[].fields[].value",
        "destinations.uploads[].headers[].value",
        "destinations.uploads[].authHeader",
        "destinations.apps[].path",
        "destinations.apps[].arguments",
        "destinations.word.appendPath",
        "destinations.powerPoint.appendPath",
        "destinations.print.printer",
        "updates.feedUrl",
        "crashReports.dsn",
    };

    /// <summary>What the app remembers that names windows and files.</summary>
    private static readonly HashSet<string> StatePaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "lastCapture.windowTitle",
        "videoEditor.recentProjects[]",
        "videoEditor.lastRecording",
        "videoEditor.exportFolder",
    };

    private static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // Arabic and other text stays readable
    };

    /// <summary>settings.json without its addresses, user names, URLs, header values and paths of other apps.</summary>
    public static string Settings(string json) => Redact(json, SettingsPaths);

    /// <summary>state.json without window titles and recent files.</summary>
    public static string State(string json) => Redact(json, StatePaths);

    /// <summary>
    /// <paramref name="text"/> with the user's profile folder (as written, with JSON's doubled backslashes or with
    /// forward slashes, in any case) shown as <c>%USERPROFILE%</c>: it holds the Windows user name.
    /// </summary>
    public static string WithoutProfile(string text, string profile)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrEmpty(profile))
        {
            return text;
        }

        profile = profile.TrimEnd('\\', '/');
        return text
            .Replace(profile.Replace("\\", "\\\\", StringComparison.Ordinal), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
            .Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase)
            .Replace(profile.Replace('\\', '/'), "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    private static string Redact(string json, HashSet<string> paths)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (JsonException e)
        {
            // Copying a file that can't be read could copy what should be left out.
            return $"(not readable as JSON, so left out: {e.Message})";
        }

        if (root is null)
        {
            return "null";
        }

        Walk(root, string.Empty, paths);
        return root.ToJsonString(Indented);
    }

    private static void Walk(JsonNode node, string path, HashSet<string> paths)
    {
        switch (node)
        {
            case JsonObject item:
                foreach (var (name, child) in item.ToList())
                {
                    var at = path.Length == 0 ? name : path + "." + name;
                    if (child is JsonValue && paths.Contains(at))
                    {
                        item[name] = Blank(child);
                    }
                    else if (child is not null)
                    {
                        Walk(child, at, paths);
                    }
                }

                break;
            case JsonArray list:
                var each = path + "[]";
                for (var i = 0; i < list.Count; i++)
                {
                    if (list[i] is JsonValue value && paths.Contains(each))
                    {
                        list[i] = Blank(value);
                    }
                    else if (list[i] is { } child)
                    {
                        Walk(child, each, paths);
                    }
                }

                break;
        }
    }

    /// <summary>Empty text stays empty (it shows the setting wasn't used); anything else becomes <see cref="Removed"/>.</summary>
    private static JsonNode Blank(JsonNode value) =>
        value.GetValueKind() == JsonValueKind.String && value.GetValue<string>().Length == 0 ? JsonValue.Create(string.Empty) : JsonValue.Create(Removed);
}
