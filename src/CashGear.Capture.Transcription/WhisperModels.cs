using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Transcription;

/// <summary>
/// A Whisper model: one of whisper.cpp's quantized ggml files of OpenAI's Whisper (MIT), its size and SHA-256.
/// Larger models are slower and more accurate (Arabic needs at least Small).
/// </summary>
public sealed record WhisperModel(string Id, string Name, string File, long Bytes, string Sha256, string About)
{
    public string SizeText => $"{Bytes / 1_000_000} MB";

    public override string ToString() => $"{Name} ({SizeText})";
}

public static class WhisperModels
{
    /// <summary>Where the models are downloaded from.</summary>
    public static Uri Source { get; } = new("https://huggingface.co/ggerganov/whisper.cpp/resolve/main/");

    public static WhisperModel Base { get; } = new("base", "Base", "ggml-base-q5_1.bin", 59_707_625, "422f1ae452ade6f30a004d7e5c6a43195e4433bc370bf23fac9cc591f01a8898", "fast; good for clear English");

    public static WhisperModel Small { get; } = new("small", "Small", "ggml-small-q5_1.bin", 190_085_487, "ae85e4a935d7a567bd102fe55afc16bb595bdb618e11b2fc7591bc08120411bb", "better; the least for Arabic");

    public static WhisperModel Medium { get; } = new("medium", "Medium", "ggml-medium-q5_0.bin", 539_212_467, "19fea4b380c3a618ec4723c3eef2eb785ffba0d0538cf43f8f235e7b3b34220f", "best; slow without a graphics card");

    public static IReadOnlyList<WhisperModel> All { get; } = [Base, Small, Medium];

    public static WhisperModel? Find(string? id) => All.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Model files on disk: found in a folder (and, for tests and portable copies, extra folders), downloaded when missing.</summary>
public sealed partial class WhisperModelStore(string folder, HttpClient http, ILogger<WhisperModelStore> logger, Uri? source = null, IReadOnlyList<string>? alsoIn = null)
{
    private const int Chunk = 1 << 16;

    /// <summary>Where downloaded models are kept.</summary>
    public string Folder { get; } = folder;

    /// <summary>The model's file if it is here in full (by its size), else null.</summary>
    public string? PathOf(WhisperModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        foreach (var directory in (alsoIn ?? []).Prepend(Folder))
        {
            var path = Path.Combine(directory, model.File);
            if (File.Exists(path) && new FileInfo(path).Length == model.Bytes)
            {
                return path;
            }
        }

        return null;
    }

    public bool Has(WhisperModel model) => PathOf(model) is not null;

    /// <summary>
    /// The model's file, downloaded first when it isn't here (as <c>.partial</c>, checked against its SHA-256 and
    /// renamed; nothing is kept if it is cancelled or doesn't match). Progress is the part downloaded, 0 to 1.
    /// </summary>
    /// <exception cref="InvalidDataException">The download isn't the model it should be.</exception>
    /// <exception cref="HttpRequestException">It can't be downloaded.</exception>
    public async Task<string> EnsureAsync(WhisperModel model, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (PathOf(model) is { } found)
        {
            return found;
        }

        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, model.File);
        var partial = path + ".partial";
        var url = new Uri(source ?? WhisperModels.Source, model.File);
        LogDownloading(model.File, url);
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, Chunk, useAsync: true))
            {
                var buffer = new byte[Chunk];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    total += read;
                    progress?.Report(Math.Min(1, (double)total / model.Bytes));
                }
            }

            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, model.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"The download of the {model.Name} model is damaged (its checksum doesn't match). Try again.");
            }

            File.Move(partial, path, overwrite: true);
            LogDownloaded(model.File);
            return path;
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloading the speech model {File} from {Url}")]
    private partial void LogDownloading(string file, Uri url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloaded the speech model {File}")]
    private partial void LogDownloaded(string file);
}
