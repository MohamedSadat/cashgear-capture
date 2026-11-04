using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using CashGear.Capture.VideoEditor.Resources;

namespace CashGear.Capture.VideoEditor.Model;

/// <summary>
/// The JSON of <c>project.json</c> in a <c>.cgcap</c>: camelCase, enums as names, nulls left out, items tagged with
/// their <c>type</c>. Readable and hand-editable (comments and trailing commas are accepted).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(VideoProject))]
internal sealed partial class ProjectJsonContext : JsonSerializerContext
{
    private static ProjectJsonContext? _readable;

    /// <summary>Names and text written as they are (Arabic too), not as escapes.</summary>
    public static ProjectJsonContext Readable => _readable ??= new(new JsonSerializerOptions(Default.Options)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}

public static class ProjectJson
{
    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };

    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    public static byte[] Serialize(VideoProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return JsonSerializer.SerializeToUtf8Bytes(project, ProjectJsonContext.Readable.VideoProject);
    }

    /// <summary>A project from its JSON, of this version or brought up to it (<see cref="Migrate"/>).</summary>
    /// <exception cref="InvalidDataException">Not a project this version of the app can read.</exception>
    public static VideoProject Deserialize(ReadOnlySpan<byte> json)
    {
        VideoProject? project;
        try
        {
            var root = JsonNode.Parse(json, NodeOptions, DocumentOptions);
            if (root is not JsonObject document)
            {
                throw new InvalidDataException(VideoEditorStrings.NotAProject);
            }

            if (VersionOf(document) > VideoProject.CurrentVersion)
            {
                throw new InvalidDataException(VideoEditorStrings.NewerProject);
            }

            Migrate(document);
            project = document.Deserialize(ProjectJsonContext.Readable.VideoProject);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException(VideoEditorStrings.NotAProjectBecause(e.Message), e);
        }

        if (project?.Canvas is null || project.Assets is null || project.Tracks is null)
        {
            throw new InvalidDataException(VideoEditorStrings.NotAProject);
        }

        return project with { Version = VideoProject.CurrentVersion };
    }

    /// <summary>
    /// Brings the JSON of an older project up to this version, one version at a time.
    /// 1 → 2 (Phase 20): the canvas's <c>background</c> colour string becomes a background record. Everything else
    /// Phase 20 added is new properties whose defaults draw what 1.1.1 drew.
    /// 2 → 3 (Phase 21): nothing to change: a transcript on an asset is new and optional, and whether a caption style's
    /// missing box means "the default box" stays as it was for what older versions wrote. Their caption words become a
    /// transcript only when the user takes up the offer (<see cref="Editing.TranscriptBuilder.FromCaptions"/>).
    /// 3 → 4 (Phase 22): nothing to change: pictures, colours, motion, transitions, effects, templates, groups and
    /// markers are all new and optional, and without them a project draws what it drew.
    /// 4 → 5 (Phase 23): nothing to change: clip effects, volume keyframes and background tracks are new and optional, and the
    /// two changes to how sound is mixed are switches a clip must have on (<see cref="Clip.EqualPowerIn"/>,
    /// <see cref="Clip.KeepPitch"/>), which older clips haven't, so a project mixes the samples it mixed.
    /// </summary>
    internal static void Migrate(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var version = VersionOf(document);
        if (version < 2)
        {
            if (document["canvas"] is JsonObject canvas && canvas["background"] is JsonValue color && color.TryGetValue<string>(out var text))
            {
                canvas["background"] = new JsonObject { ["kind"] = nameof(BackgroundKind.Color), ["color"] = text };
            }

            version = 2;
        }

        if (version < 3)
        {
            version = 3;
        }

        if (version < 4)
        {
            version = 4;
        }

        if (version < 5)
        {
            version = 5;
        }

        document["version"] = version;
    }

    private static int VersionOf(JsonObject document) =>
        document["version"] is JsonValue value && value.TryGetValue<int>(out var version) ? version : 1;
}
