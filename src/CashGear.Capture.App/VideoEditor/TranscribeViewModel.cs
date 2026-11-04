using System.ComponentModel;
using System.Globalization;
using System.IO;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Transcription;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>A sound track to listen to (null: all of them). Its name is what screen readers read for it.</summary>
public sealed record SoundChoice(string? TrackId, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A language to listen for (null: found out from the speech).</summary>
public sealed record LanguageChoice(string? Code, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A speech model as the window lists it.</summary>
public sealed record ModelChoice(WhisperModel Model, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A transcript made for one media file.</summary>
public sealed record MadeTranscript(string AssetId, MediaTranscript Transcript);

/// <summary>
/// Captions from Speech: which sound track (the microphone first), the language, the model (downloaded the first
/// time it is used) and whether to use the graphics card; then the sound is mixed, transcribed in the background with
/// its progress, and the captions handed to the editor as a new caption track. Cancel stops it.
/// </summary>
public sealed partial class TranscribeViewModel : ObservableObject
{
    private readonly VideoProject _project;
    private readonly TimeRange? _range;
    private readonly ITranscriber _transcriber;
    private readonly WhisperModelStore _models;
    private readonly IAppStateStore _state;
    private readonly ILogger _logger;
    private readonly Action<IReadOnlyList<CaptionItem>, string> _done;
    private readonly Action<IReadOnlyList<MadeTranscript>>? _transcripts;
    private readonly SoundLevelStore? _levels;
    private CancellationTokenSource? _cancel;

    /// <param name="done">Gets the captions and the language heard (on the caller's thread), when there are some.</param>
    /// <param name="transcripts">
    /// When given, the window makes transcripts instead of captions: of each media file on the track chosen, on its own clock,
    /// whatever the timeline does with it. Gets them on the caller's thread, when there are some.
    /// </param>
    /// <param name="levels">Keeps the sound's levels, for the transcripts' word times.</param>
    public TranscribeViewModel(VideoProject project, TimeRange? range, ITranscriber transcriber, WhisperModelStore models, IAppStateStore state, ILogger logger, Action<IReadOnlyList<CaptionItem>, string> done, Action<IReadOnlyList<MadeTranscript>>? transcripts = null, SoundLevelStore? levels = null)
    {
        _transcripts = transcripts;
        _levels = levels;
        _project = project;
        _range = range;
        _transcriber = transcriber;
        _models = models;
        _state = state;
        _logger = logger;
        _done = done;
        // The microphone's track first: tracks are named after a recording's sound streams, whose titles stay English.
        var sounds = project.Tracks.Where(t => t.HasSound).OrderBy(t => t.Name.Contains(MicrophoneTitle, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        Sounds = [.. sounds.Select(t => new SoundChoice(t.Id, TrackNames.Display(t.Name))), .. sounds.Count > 1 && transcripts is null ? [new SoundChoice(null, VideoEditorText.AllSoundTracks)] : Array.Empty<SoundChoice>()];
        _sound = Sounds.FirstOrDefault();
        var remembered = state.Current.VideoEditor ?? new VideoEditorState();
        Models = [.. WhisperModels.All.Select(m => new ModelChoice(m, models.Has(m) ? VideoEditorText.ModelDownloaded(m.Name, m.About) : VideoEditorText.ModelToDownload(m.Name, m.About, m.SizeText)))];
        _model = Models.FirstOrDefault(m => m.Model.Id == remembered.TranscriptionModel) ?? Models.First(m => m.Model == WhisperModels.Small);
        _language = Languages.FirstOrDefault(l => l.Code == remembered.TranscriptionLanguage) ?? Languages[0];
        _useGpu = remembered.TranscriptionGpu;
        _keepFillers = remembered.TranscriptionKeepFillers;
        _onlyRange = range is not null;
    }

    /// <summary>Whether it makes a transcript rather than captions.</summary>
    public bool MakesTranscript => _transcripts is not null;

    public string TitleText => MakesTranscript ? VideoEditorText.TranscriptMakeTitle : VideoEditorText.TranscribeTitle;

    public string IntroText => MakesTranscript ? VideoEditorText.TranscriptMakeIntro : VideoEditorText.TranscribeIntro;

    public string StartText => MakesTranscript ? VideoEditorText.TranscriptMakeStart : VideoEditorText.MakeCaptions;

    /// <summary>Ask Whisper to write down "um" and "uh".</summary>
    [ObservableProperty]
    private bool _keepFillers;

    public IReadOnlyList<SoundChoice> Sounds { get; }

    public IReadOnlyList<ModelChoice> Models { get; }

    public IReadOnlyList<LanguageChoice> Languages { get; } = [new(null, VideoEditorText.FindOutLanguage), new("en", VideoEditorText.LanguageEnglish), new("ar", VideoEditorText.LanguageArabic)];

    public bool HasSound => Sounds.Count > 0;

    public bool HasRange => _range is not null;

    public string RangeText => _range is { } range
        ? VideoEditorText.TranscribeRange(range.Length)
        : "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private SoundChoice? _sound;

    [ObservableProperty]
    private ModelChoice _model;

    [ObservableProperty]
    private LanguageChoice _language;

    [ObservableProperty]
    private bool _useGpu;

    [ObservableProperty]
    private bool _onlyRange;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _status = "";

    [ObservableProperty]
    private string? _error;

    public bool IsIdle => !IsRunning;

    /// <summary>The window should close.</summary>
    public event EventHandler? CloseRequested;

    private bool CanStart() => !IsRunning && Sound is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        Error = null;
        IsRunning = true;
        Progress = 0;
        _cancel = new CancellationTokenSource();
        var ct = _cancel.Token;
        var model = Model.Model;
        var language = Language.Code;
        var gpu = UseGpu;
        var range = OnlyRange ? _range : null;
        var tracks = Sound?.TrackId is { } id ? new[] { id } : null;
        try
        {
            await _state.UpdateAsync(s => s with
            {
                VideoEditor = (s.VideoEditor ?? new VideoEditorState()) with { TranscriptionModel = model.Id, TranscriptionLanguage = language, TranscriptionGpu = gpu, TranscriptionKeepFillers = MakesTranscript ? KeepFillers : (s.VideoEditor ?? new VideoEditorState()).TranscriptionKeepFillers },
            }, ct);

            // A tenth of the bar for the sound, the rest for listening; a download first has a bar of its own.
            Status = _models.Has(model) ? "" : VideoEditorText.DownloadingModel(model.Name, model.SizeText);
            var path = await _models.EnsureAsync(model, new Progress<double>(p =>
            {
                Progress = p;
                Status = VideoEditorText.DownloadingModelProgress(model.Name, model.SizeText, p);
            }), ct);
            Progress = 0;
            if (_transcripts is { } hand)
            {
                await MakeTranscriptsAsync(path, language, gpu, hand, ct);
                return;
            }

            Status = VideoEditorText.GettingSound;
            var project = range is { } r ? ProjectRange.Extract(_project, r) : _project;
            var samples = await Task.Run(() => TranscriptAudio.Render(project, tracks, new Progress<double>(p => Progress = p * 0.1), ct), ct);
            Status = VideoEditorText.Listening;
            var result = await _transcriber.TranscribeAsync(samples, new TranscriptionOptions(path, language, gpu), new Progress<double>(p =>
            {
                Progress = 0.1 + (p * 0.9);
                Status = VideoEditorText.ListeningProgress(p);
            }), ct);
            var captions = CaptionEdits.FromTranscript(result.Segments, range?.From ?? 0);
            LogDone(captions.Count, result.Language, result.Runtime);
            if (captions.Count == 0)
            {
                Error = VideoEditorText.NoSpeechHeard;
                Status = "";
                return;
            }

            _done(captions, result.Language);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = VideoEditorText.TranscribeCancelled;
            Progress = 0;
        }
        catch (Exception e)
        {
            // Downloads, files, and whisper.cpp's own errors (a model it can't load, no runtime): said, never a crash.
            LogFailed(e);
            Error = e is System.Net.Http.HttpRequestException ? VideoEditorText.ModelNotDownloaded(e.Message) : e.Message;
            Status = "";
            Progress = 0;
        }
        finally
        {
            IsRunning = false;
            _cancel.Dispose();
            _cancel = null;
        }
    }

    /// <summary>
    /// A transcript of each media file on the track chosen. Its sound is heard on its own clock; its levels are kept and
    /// bring the words' times nearer.
    /// </summary>
    private async Task MakeTranscriptsAsync(string path, string? language, bool gpu, Action<IReadOnlyList<MadeTranscript>> hand, CancellationToken ct)
    {
        var track = _project.Track(Sound!.TrackId!)!;
        var streams = track.Clips.Where(c => !c.IsFreeze).Select(c => (c.AssetId, c.Stream)).Distinct().ToList();
        var made = new List<MadeTranscript>();
        for (var i = 0; i < streams.Count; i++)
        {
            var (assetId, stream) = streams[i];
            if (_project.Asset(assetId) is not { } asset)
            {
                continue;
            }

            var share = 1.0 / streams.Count;
            var before = i * share;
            Status = VideoEditorText.GettingSound;
            var samples = await Task.Run(() => TranscriptAudio.RenderStream(asset, stream, new Progress<double>(p => Progress = before + (p * 0.1 * share)), ct), ct);
            var levels = await Task.Run(() => _levels?.Keep(asset, stream, samples, ct) ?? SoundLevels.FromSamples(samples, ct), ct);
            Status = VideoEditorText.Listening;
            var result = await _transcriber.TranscribeAsync(samples, new TranscriptionOptions(path, language, gpu, KeepFillers), new Progress<double>(p =>
            {
                Progress = before + ((0.1 + (p * 0.9)) * share);
                Status = VideoEditorText.ListeningProgress(p);
            }), ct);
            var transcript = TranscriptBuilder.FromSegments(stream, result.Language, result.Segments, levels);
            LogDone(transcript.Words.Count, result.Language, result.Runtime);
            if (transcript.Words.Count > 0)
            {
                made.Add(new MadeTranscript(assetId, transcript));
            }
        }

        if (made.Count == 0)
        {
            Error = VideoEditorText.NoSpeechHeard;
            Status = "";
            return;
        }

        hand(made);
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsRunning)
        {
            _cancel?.Cancel();
        }
        else
        {
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Stops a running transcription (the window is closing).</summary>
    public void Stop() => _cancel?.Cancel();

    /// <summary>
    /// A language's name for a track ("Captions (Arabic)"), in the UI language for the languages offered; another
    /// language Whisper heard is named in English in the English UI, else in its own language (.NET's localized names
    /// follow Windows' language, not the app's).
    /// </summary>
    public static string LanguageName(string? code) => code switch
    {
        "en" => VideoEditorText.LanguageEnglish,
        "ar" => VideoEditorText.LanguageArabic,
        null or "" => "",
        _ => CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(c => c.TwoLetterISOLanguageName == code) is { } culture
            ? (CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "en" ? culture.EnglishName : culture.NativeName)
            : code,
    };

    /// <summary>The microphone's sound stream title in recordings, matched as stored (never translated).</summary>
    [Localizable(false)]
    private const string MicrophoneTitle = "Microphone";

    [LoggerMessage(Level = LogLevel.Information, Message = "Captions from speech: {Count} captions ({Language}, {Runtime})")]
    private partial void LogDone(int count, string language, string runtime);

    [LoggerMessage(Level = LogLevel.Error, Message = "Captions from speech failed")]
    private partial void LogFailed(Exception e);
}
