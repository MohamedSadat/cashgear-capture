using System.Text.Json;
using System.Text.Json.Serialization;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Selection;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Core.Settings;

/// <summary>
/// What the app remembers between runs that isn't a setting: the last capture (for Repeat Last Capture), and where
/// tool windows were. Kept in <c>state.json</c> in the data folder, apart from the settings the user edits.
/// </summary>
public sealed record AppState
{
    public RepeatableCapture? LastCapture { get; init; }

    /// <summary>Where the capture toolbar was (null: never moved).</summary>
    public Geometry.ToolbarPlacement? Toolbar { get; init; }
}

public interface IAppStateStore
{
    /// <summary>The state now (loaded on first use; defaults when the file is missing or unreadable).</summary>
    AppState Current { get; }

    /// <summary>Changes the state and writes it (the file is replaced whole, never half-written).</summary>
    Task UpdateAsync(Func<AppState, AppState> update, CancellationToken ct = default);
}

public sealed partial class JsonAppStateStore(AppPaths paths, ILogger<JsonAppStateStore> logger) : IAppStateStore
{
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly Lock _gate = new();
    private AppState? _current;

    public string FilePath => Path.Combine(paths.DataDirectory, "state.json");

    public AppState Current
    {
        get
        {
            lock (_gate)
            {
                return _current ??= Load();
            }
        }
    }

    public async Task UpdateAsync(Func<AppState, AppState> update, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            AppState next;
            lock (_gate)
            {
                next = update(_current ??= Load());
                _current = next;
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, StateJsonContext.Default.AppState);
            Directory.CreateDirectory(paths.DataDirectory);
            var temp = FilePath + ".tmp";
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogWriteFailed(ex, FilePath); // kept in memory; the next change tries again
        }
        finally
        {
            _write.Release();
        }
    }

    private AppState Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize(File.ReadAllBytes(FilePath), StateJsonContext.Default.AppState) ?? new AppState()
                : new AppState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            LogReadFailed(ex, FilePath);
            return new AppState();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read {Path}; starting from nothing remembered")]
    private partial void LogReadFailed(Exception ex, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not write {Path}")]
    private partial void LogWriteFailed(Exception ex, string path);
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [typeof(PixelRectConverter), typeof(PixelPointConverter), typeof(CaptureShapeConverter)])]
[JsonSerializable(typeof(AppState))]
[JsonSerializable(typeof(ShapeData))]
internal sealed partial class StateJsonContext : JsonSerializerContext;

/// <summary>A <see cref="CaptureShape"/> as stored: corners and rectangles as flat lists of numbers.</summary>
internal sealed record ShapeData(ShapeKind Kind, int[] Bounds, int Radius = 0, int[]? Points = null, int[]? Rectangles = null);

/// <summary><c>[x, y, width, height]</c>.</summary>
internal sealed class PixelRectConverter : JsonConverter<PixelRect>
{
    public override PixelRect Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Numbers(ref reader) is [var x, var y, var w, var h] ? new PixelRect(x, y, w, h) : throw new JsonException("A rectangle is [x, y, width, height].");

    public override void Write(Utf8JsonWriter writer, PixelRect value, JsonSerializerOptions options) =>
        WriteNumbers(writer, [value.X, value.Y, value.Width, value.Height]);

    internal static int[] Numbers(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            throw new JsonException("Expected an array of numbers.");
        }

        var numbers = new List<int>(4);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            numbers.Add(reader.GetInt32());
        }

        return [.. numbers];
    }

    internal static void WriteNumbers(Utf8JsonWriter writer, IEnumerable<int> numbers)
    {
        writer.WriteStartArray();
        foreach (var n in numbers)
        {
            writer.WriteNumberValue(n);
        }

        writer.WriteEndArray();
    }
}

/// <summary><c>[x, y]</c>.</summary>
internal sealed class PixelPointConverter : JsonConverter<PixelPoint>
{
    public override PixelPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        PixelRectConverter.Numbers(ref reader) is [var x, var y] ? new PixelPoint(x, y) : throw new JsonException("A point is [x, y].");

    public override void Write(Utf8JsonWriter writer, PixelPoint value, JsonSerializerOptions options) =>
        PixelRectConverter.WriteNumbers(writer, [value.X, value.Y]);
}

internal sealed class CaptureShapeConverter : JsonConverter<CaptureShape>
{
    public override CaptureShape? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var data = JsonSerializer.Deserialize(ref reader, StateJsonContext.Default.ShapeData) ?? throw new JsonException("Missing shape.");
        return data.Kind switch
        {
            ShapeKind.RoundedRectangle when data.Bounds is [var x, var y, var w, var h] => CaptureShape.RoundedRectangle(new PixelRect(x, y, w, h), data.Radius),
            ShapeKind.Polygon when data.Points is { Length: >= 6 } p && p.Length % 2 == 0 =>
                CaptureShape.Polygon(Enumerable.Range(0, p.Length / 2).Select(i => new PixelPoint(p[2 * i], p[(2 * i) + 1])).ToArray()),
            ShapeKind.Rectangles when data.Rectangles is { Length: >= 4 } r && r.Length % 4 == 0 =>
                CaptureShape.Union(Enumerable.Range(0, r.Length / 4).Select(i => new PixelRect(r[4 * i], r[(4 * i) + 1], r[(4 * i) + 2], r[(4 * i) + 3])).ToArray()),
            _ => throw new JsonException($"Not a valid {data.Kind} shape."),
        };
    }

    public override void Write(Utf8JsonWriter writer, CaptureShape value, JsonSerializerOptions options)
    {
        var b = value.Bounds;
        var data = new ShapeData(
            value.Kind,
            [b.X, b.Y, b.Width, b.Height],
            value.Radius,
            value.Kind == ShapeKind.Polygon ? [.. value.Points.SelectMany(p => new[] { p.X, p.Y })] : null,
            value.Kind == ShapeKind.Rectangles ? [.. value.Rectangles.SelectMany(r => new[] { r.X, r.Y, r.Width, r.Height })] : null);
        JsonSerializer.Serialize(writer, data, StateJsonContext.Default.ShapeData);
    }
}
