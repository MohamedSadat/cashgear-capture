using System.Text.Json;
using System.Text.Json.Serialization;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Recording;

/// <summary>Whether an encoder worked when it was tried on this PC.</summary>
/// <param name="Detail">Which device encodes (e.g. "NVIDIA H.264 Encoder MFT"), or why it doesn't work.</param>
/// <param name="QualityMode">False when a Media Foundation encoder ignored its quality setting (a bit rate is used then).</param>
public sealed record EncoderProbe(string Id, bool Works, string? Detail = null, bool QualityMode = true);

/// <summary>The detection results file.</summary>
/// <param name="Key">What the results depend on (FFmpeg build, graphics adapters and drivers, Windows build).</param>
public sealed record EncoderCacheFile(string Key, DateTimeOffset DetectedAt, IReadOnlyList<EncoderProbe> Encoders);

/// <summary>
/// Detection results in <c>%LocalAppData%\CashGear\Capture\encoders.json</c>. They describe this PC, so they're kept
/// with the machine's data rather than in the (roaming) settings file, and they only count while the key they were
/// made under still matches: a new driver, GPU or FFmpeg build means detecting again.
/// </summary>
public sealed class EncoderCache(AppPaths paths)
{
    public string FilePath { get; } = Path.Combine(paths.DataDirectory, "encoders.json");

    /// <summary>The results saved under <paramref name="key"/>, or null (none, another key, or unreadable).</summary>
    public EncoderCacheFile? Load(string key)
    {
        try
        {
            using var stream = File.OpenRead(FilePath);
            var file = JsonSerializer.Deserialize(stream, RecordingJsonContext.Default.EncoderCacheFile);
            return file is not null && file.Key == key && file.Encoders is not null ? file : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void Save(EncoderCacheFile file)
    {
        Directory.CreateDirectory(paths.DataDirectory);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(file, RecordingJsonContext.Default.EncoderCacheFile));
        File.Move(temp, FilePath, overwrite: true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(EncoderCacheFile))]
[JsonSerializable(typeof(RecordingManifest))]
internal sealed partial class RecordingJsonContext : JsonSerializerContext;
