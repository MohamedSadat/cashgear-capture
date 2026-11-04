using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace CashGear.Capture.Inference;

/// <summary>Turns text into another language. One text at a time.</summary>
public interface ITranslator : IDisposable
{
    /// <summary>The text in the other language, each of its sentences by itself.</summary>
    string Translate(string text, CancellationToken ct = default);
}

/// <summary>Gets a translator ready, downloading its model when it isn't here.</summary>
public interface ITranslators
{
    /// <summary>Whether its model is here already.</summary>
    bool IsReady { get; }

    /// <summary>How much there is to download, in bytes.</summary>
    long DownloadSize { get; }

    /// <summary>English to Arabic. Progress is the part of the download done, 0 to 1.</summary>
    Task<ITranslator> OpenEnglishToArabicAsync(IProgress<double>? progress = null, CancellationToken ct = default);
}

/// <summary>Opus-MT's English to Arabic from the model store: on the processor, whose quantized model it is.</summary>
public sealed class OpusTranslators(InferenceModelStore models) : ITranslators
{
    public bool IsReady => InferenceModels.EnglishToArabic.All(models.Has);

    public long DownloadSize => InferenceModels.EnglishToArabic.Where(m => !models.Has(m)).Sum(m => m.Bytes);

    public async Task<ITranslator> OpenEnglishToArabicAsync(IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var files = InferenceModels.EnglishToArabic;
        var total = (double)Math.Max(1, files.Sum(m => m.Bytes));
        var done = 0L;
        var paths = new List<string>();
        foreach (var file in files)
        {
            var before = done;
            var path = await models.EnsureAsync(file, new Progress<double>(p => progress?.Report((before + (p * file.Bytes)) / total)), ct).ConfigureAwait(false);
            paths.Add(path);
            done += file.Bytes;
        }

        return await Task.Run(() => new MarianTranslator(paths[0], paths[1], paths[2], paths[3], MarianTranslator.Arabic), ct).ConfigureAwait(false);
    }
}

/// <summary>
/// A Marian translation model (Opus-MT) as ONNX: an encoder, a decoder and the tokens it knows. The text goes through
/// SentencePiece's pieces (<see cref="SentencePieces"/>) to tokens, the encoder reads them once with the target language's
/// token first, and the decoder is asked for one token at a time, the likeliest each time. Without a cache of the
/// decoder's past, so a long sentence takes longer, and captions are short. On the processor.
/// </summary>
[Localizable(false)]
public sealed partial class MarianTranslator : ITranslator
{
    /// <summary>The token asking the model for Modern Standard Arabic.</summary>
    [Localizable(false)]
    public const string Arabic = ">>ara<<";

    /// <summary>The most tokens read from a sentence (longer ones are cut there), and the most made from them.</summary>
    private const int LongestInput = 200;

    private const int LongestOutput = 256;

    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly SentencePieces _pieces;
    private readonly Dictionary<string, int> _vocabulary;
    private readonly string[] _tokens;
    private readonly int _language;
    private readonly int _end;
    private readonly int _pad;
    private readonly int _unknown;

    /// <param name="language">The token asking for the language wanted.</param>
    /// <exception cref="InvalidDataException">The token isn't one the model knows.</exception>
    public MarianTranslator(string encoderPath, string decoderPath, string piecesPath, string vocabularyPath, string language)
    {
        OrtEnv.Instance().DisableTelemetryEvents();
        _pieces = SentencePieces.Load(piecesPath);
        using (var file = File.OpenRead(vocabularyPath))
        {
            _vocabulary = JsonSerializer.Deserialize<Dictionary<string, int>>(file) ?? throw new InvalidDataException(vocabularyPath);
        }

        _tokens = new string[_vocabulary.Values.Max() + 1];
        foreach (var (token, id) in _vocabulary)
        {
            _tokens[id] = token;
        }

        _language = Token(language);
        _end = Token("</s>");
        _pad = Token("<pad>");
        _unknown = Token("<unk>");
        using var options = new SessionOptions { LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR, GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
        _encoder = new InferenceSession(encoderPath, options);
        _decoder = new InferenceSession(decoderPath, options);
    }

    public string Translate(string text, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        var translated = new List<string>();
        foreach (var sentence in Sentences().Split(text.Trim()).Where(s => s.Trim().Length > 0))
        {
            ct.ThrowIfCancellationRequested();
            translated.Add(TranslateSentence(sentence.Trim(), ct));
        }

        return string.Join(' ', translated.Where(t => t.Length > 0));
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
    }

    private int Token(string token) => _vocabulary.TryGetValue(token, out var id) ? id : throw new InvalidDataException($"The model has no token {token}.");

    private string TranslateSentence(string sentence, CancellationToken ct)
    {
        var input = new List<long> { _language };
        input.AddRange(_pieces.Split(sentence).Take(LongestInput).Select(piece => (long)(_vocabulary.TryGetValue(piece, out var id) ? id : _unknown)));
        input.Add(_end);
        var shape = new[] { 1, input.Count };
        var ids = new DenseTensor<long>(input.ToArray(), shape);
        var mask = new DenseTensor<long>(Enumerable.Repeat(1L, input.Count).ToArray(), shape);
        using var encoded = _encoder.Run([NamedOnnxValue.CreateFromTensor("input_ids", ids), NamedOnnxValue.CreateFromTensor("attention_mask", mask)]);
        var hidden = encoded.First().AsTensor<float>();

        var output = new List<long> { _pad };
        var longest = Math.Min(LongestOutput, (input.Count * 4) + 16);
        while (output.Count < longest)
        {
            ct.ThrowIfCancellationRequested();
            var so = new DenseTensor<long>(output.ToArray(), [1, output.Count]);
            using var decoded = _decoder.Run([
                NamedOnnxValue.CreateFromTensor("encoder_attention_mask", mask),
                NamedOnnxValue.CreateFromTensor("input_ids", so),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden),
            ]);
            var logits = decoded.First().AsTensor<float>();
            var next = Likeliest(logits, output.Count - 1);
            if (next == _end || RepeatsItself(output, next))
            {
                break;
            }

            output.Add(next);
        }

        return Text(output.Skip(1));
    }

    /// <summary>The likeliest token at the last place, never the padding.</summary>
    private int Likeliest(Tensor<float> logits, int place)
    {
        var count = logits.Dimensions[2];
        var best = -1;
        var highest = float.NegativeInfinity;
        for (var i = 0; i < count; i++)
        {
            var value = logits[0, place, i];
            if (i != _pad && value > highest)
            {
                highest = value;
                best = i;
            }
        }

        return best;
    }

    /// <summary>Saying the same few tokens over and over: the model is lost, so it stops there.</summary>
    internal static bool RepeatsItself(IReadOnlyList<long> output, long next)
    {
        const int times = 4;
        var count = output.Count + 1;
        long At(int index) => index == output.Count ? next : output[index];
        for (var length = 1; length <= 6; length++)
        {
            if (count < length * times)
            {
                continue;
            }

            var same = true;
            for (var i = count - 1; i >= count - (length * (times - 1)) && same; i--)
            {
                same = At(i) == At(i - length);
            }

            if (same)
            {
                return true;
            }
        }

        return false;
    }

    private string Text(IEnumerable<long> tokens)
    {
        var text = new StringBuilder();
        foreach (var token in tokens)
        {
            var piece = _tokens[token];
            if (piece is not null && !(piece.StartsWith(">>", StringComparison.Ordinal) && piece.EndsWith("<<", StringComparison.Ordinal)) && piece != "<unk>")
            {
                text.Append(piece);
            }
        }

        return text.ToString().Replace(SentencePieces.Space, ' ').Trim();
    }

    [GeneratedRegex(@"(?<=[.?!…])\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Sentences();
}

/// <summary>
/// A SentencePiece model read from its file: its pieces and their scores. Text is cut into the pieces with the highest
/// total score (a unigram model's way), after the same clean-up for ordinary text: compatibility forms, and single spaces
/// written as its own space sign.
/// </summary>
[Localizable(false)]
public sealed class SentencePieces
{
    /// <summary>What a space is written as in pieces.</summary>
    public const char Space = '▁';

    private readonly Dictionary<string, float> _scores;
    private readonly int _longest;
    private readonly float _unknownScore;

    private SentencePieces(Dictionary<string, float> scores)
    {
        _scores = scores;
        _longest = scores.Keys.Max(k => k.Length);
        _unknownScore = scores.Values.Min() - 10;
    }

    public int Count => _scores.Count;

    /// <exception cref="InvalidDataException">It isn't a SentencePiece model.</exception>
    public static SentencePieces Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var scores = new Dictionary<string, float>();
        var at = 0;
        while (at < bytes.Length)
        {
            var key = ReadVarint(bytes, ref at);
            var field = key >> 3;
            var wire = key & 7;
            if (wire == 2)
            {
                var length = (int)ReadVarint(bytes, ref at);
                if (field == 1)
                {
                    ReadPiece(bytes.AsSpan(at, length), scores);
                }

                at += length;
            }
            else if (wire == 0)
            {
                ReadVarint(bytes, ref at);
            }
            else if (wire == 5)
            {
                at += 4;
            }
            else if (wire == 1)
            {
                at += 8;
            }
            else
            {
                throw new InvalidDataException($"{path} isn't a SentencePiece model.");
            }
        }

        return scores.Count > 0 ? new SentencePieces(scores) : throw new InvalidDataException($"{path} has no pieces.");
    }

    /// <summary>The text as pieces, with the highest total score.</summary>
    public IReadOnlyList<string> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var clean = Space + string.Join(Space, text.Normalize(NormalizationForm.FormKC).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var best = new float[clean.Length + 1];
        var from = new int[clean.Length + 1];
        Array.Fill(best, float.NegativeInfinity);
        best[0] = 0;
        for (var end = 1; end <= clean.Length; end++)
        {
            for (var start = Math.Max(0, end - _longest); start < end; start++)
            {
                if (float.IsNegativeInfinity(best[start]))
                {
                    continue;
                }

                var length = end - start;
                var score = _scores.TryGetValue(clean.Substring(start, length), out var known) ? known : length == 1 ? _unknownScore : float.NegativeInfinity;
                if (best[start] + score > best[end])
                {
                    best[end] = best[start] + score;
                    from[end] = start;
                }
            }
        }

        var pieces = new List<string>();
        for (var end = clean.Length; end > 0; end = from[end])
        {
            pieces.Add(clean[from[end]..end]);
        }

        pieces.Reverse();
        return pieces;
    }

    private static void ReadPiece(ReadOnlySpan<byte> bytes, Dictionary<string, float> scores)
    {
        string? piece = null;
        float score = 0;
        var at = 0;
        while (at < bytes.Length)
        {
            var key = ReadVarint(bytes, ref at);
            switch (key)
            {
                case (1 << 3) | 2:
                    var length = (int)ReadVarint(bytes, ref at);
                    piece = Encoding.UTF8.GetString(bytes.Slice(at, length));
                    at += length;
                    break;
                case (2 << 3) | 5:
                    score = BitConverter.ToSingle(bytes.Slice(at, 4));
                    at += 4;
                    break;
                default:
                    if ((key & 7) == 0)
                    {
                        ReadVarint(bytes, ref at);
                    }
                    else
                    {
                        throw new InvalidDataException(string.Create(CultureInfo.InvariantCulture, $"An unknown field {key} in a piece."));
                    }

                    break;
            }
        }

        if (piece is not null)
        {
            scores[piece] = score;
        }
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> bytes, ref int at)
    {
        ulong value = 0;
        var shift = 0;
        while (true)
        {
            var next = bytes[at++];
            value |= (ulong)(next & 0x7F) << shift;
            if ((next & 0x80) == 0)
            {
                return value;
            }

            shift += 7;
        }
    }
}
