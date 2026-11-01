using System.Security.Cryptography;
using CashGear.Capture.Inference.Resources;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.Inference;

/// <summary>A model file: where it comes from (a pinned address, so it never changes under the app), its size and SHA-256.</summary>
public sealed record InferenceModel(string Id, string Name, string File, long Bytes, string Sha256, Uri Source)
{
    public override string ToString() => Name;
}

public static class InferenceModels
{
    /// <summary>
    /// MediaPipe Selfie Segmentation (Google, Apache 2.0), the square general model as ONNX from onnx-community at a
    /// fixed commit: 256 × 256 RGB in, the person's matte out, under half a megabyte.
    /// </summary>
    public static InferenceModel PersonFinder { get; } = new(
        "selfie",
        InferenceStrings.ModelPersonFinder,
        "mediapipe-selfie-segmentation.onnx",
        462_352,
        "3241ac4ad8aa35bdaf33946776db29f7c283a413aa0b0dacb9483594b4531aad",
        new Uri("https://huggingface.co/onnx-community/mediapipe_selfie_segmentation/resolve/be49485c8e027524be38591817fc5cd31bd9d00e/onnx/model.onnx"));

    public static IReadOnlyList<InferenceModel> All { get; } = [PersonFinder];
}

/// <summary>Model files on disk: found in a folder (and, for tests and portable copies, extra folders), downloaded when missing.</summary>
public sealed partial class InferenceModelStore(string folder, HttpClient http, ILogger<InferenceModelStore> logger, Uri? source = null, IReadOnlyList<string>? alsoIn = null)
{
    private const int Chunk = 1 << 16;

    /// <summary>Where downloaded models are kept.</summary>
    public string Folder { get; } = folder;

    /// <summary>The model's file if it is here in full (by its size), else null.</summary>
    public string? PathOf(InferenceModel model)
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

    public bool Has(InferenceModel model) => PathOf(model) is not null;

    /// <summary>
    /// The model's file, downloaded first when it isn't here (as <c>.partial</c>, checked against its SHA-256 and
    /// renamed; nothing is kept if it is cancelled or doesn't match). Progress is the part downloaded, 0 to 1.
    /// </summary>
    /// <exception cref="InvalidDataException">The download isn't the model it should be.</exception>
    /// <exception cref="HttpRequestException">It can't be downloaded.</exception>
    public async Task<string> EnsureAsync(InferenceModel model, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (PathOf(model) is { } found)
        {
            return found;
        }

        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, model.File);
        var partial = path + ".partial";
        var url = source is null ? model.Source : new Uri(source, model.File);
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
                throw new InvalidDataException(InferenceStrings.ModelDamaged(model.Name));
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloading the model {File} from {Url}")]
    private partial void LogDownloading(string file, Uri url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Downloaded the model {File}")]
    private partial void LogDownloaded(string file);
}
