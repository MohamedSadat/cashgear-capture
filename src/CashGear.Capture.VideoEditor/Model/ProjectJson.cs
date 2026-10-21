using System.Text.Encodings.Web;
using System.Text.Json;
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
    public static byte[] Serialize(VideoProject project)
    {
        ArgumentNullException.ThrowIfNull(project);
        return JsonSerializer.SerializeToUtf8Bytes(project, ProjectJsonContext.Readable.VideoProject);
    }

    /// <exception cref="InvalidDataException">Not a project this version of the app can read.</exception>
    public static VideoProject Deserialize(ReadOnlySpan<byte> json)
    {
        VideoProject? project;
        try
        {
            project = JsonSerializer.Deserialize(json, ProjectJsonContext.Readable.VideoProject);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException(VideoEditorStrings.NotAProjectBecause(e.Message), e);
        }

        if (project?.Canvas is null || project.Assets is null || project.Tracks is null)
        {
            throw new InvalidDataException(VideoEditorStrings.NotAProject);
        }

        if (project.Version > VideoProject.CurrentVersion)
        {
            throw new InvalidDataException(VideoEditorStrings.NewerProject);
        }

        return project;
    }
}
