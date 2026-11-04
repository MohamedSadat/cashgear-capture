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

    /// <summary>Opus-MT English to Arabic (Helsinki-NLP, CC-BY 4.0) as quantized ONNX from Xenova at a fixed commit: 113 MB.</summary>
    public static IReadOnlyList<InferenceModel> EnglishToArabic { get; } =
    [
        new("en-ar-encoder", InferenceStrings.ModelEnglishToArabicEncoder, "opus-mt-en-ar-encoder.onnx", 51_773_854, "53c354d87ced44b39085fe8a46a25f09ea4578d442dbce9663f3977228f8a04e", new Uri(OpusSource + "onnx/encoder_model_quantized.onnx")),
        new("en-ar-decoder", InferenceStrings.ModelEnglishToArabicDecoder, "opus-mt-en-ar-decoder.onnx", 58_707_418, "94e6bdf90866151787d014b7b77be0adb0f036418ee04d235ec01238aa8426b9", new Uri(OpusSource + "onnx/decoder_model_quantized.onnx")),
        new("en-ar-pieces", InferenceStrings.ModelEnglishToArabicPieces, "opus-mt-en-ar-source.spm", 801_074, "81ac8f4d4ee271f4109eac67560a31f9c2d555463ba5eb95c32c6deee8ae819d", new Uri(OpusSource + "source.spm")),
        new("en-ar-words", InferenceStrings.ModelEnglishToArabicWords, "opus-mt-en-ar-vocab.json", 2_243_581, "8b74af9e4c1ec122b8d06362db6c38795e10ca2b0156b2110cf08d4716744b76", new Uri(OpusSource + "vocab.json")),
    ];

    public static IReadOnlyList<InferenceModel> All { get; } = [PersonFinder, .. EnglishToArabic];

    private const string OpusSource = "https://huggingface.co/Xenova/opus-mt-en-ar/resolve/034a684356c19021a187cda4d7f823298b913921/";
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
