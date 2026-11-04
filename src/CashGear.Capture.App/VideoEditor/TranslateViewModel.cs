using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Inference;
using CashGear.Capture.Transcription;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>What can be translated: a caption track into Arabic, or Arabic speech on a sound track into English.</summary>
public sealed record TranslationChoice(string TrackId, bool IsSpeech, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Translate: captions in English into Arabic with Opus-MT, or Arabic speech into English with Whisper's translate task
/// (no new model); downloaded when first used, in the background, with progress and Cancel. The result is a caption track
/// named for its language, in one undo step. Nothing leaves the PC.
/// </summary>
public sealed partial class TranslateViewModel : ObservableObject
{
    private readonly VideoProject _project;
    private readonly ITranslators? _translators;
    private readonly ITranscriber? _transcriber;
    private readonly WhisperModelStore? _models;
    private readonly IAppStateStore _state;
    private readonly ILogger _logger;
    private readonly Action<IReadOnlyList<CaptionItem>, string> _done;
    private CancellationTokenSource? _cancel;

    /// <param name="done">Gets the captions and the language they are in (two letters), on the caller's thread.</param>
    public TranslateViewModel(VideoProject project, ITranslators? translators, ITranscriber? transcriber, WhisperModelStore? models, IAppStateStore state, ILogger logger, Action<IReadOnlyList<CaptionItem>, string> done)
    {
        _project = project;
        _translators = translators;
        _transcriber = transcriber;
        _models = models;
        _state = state;
        _logger = logger;
        _done = done;
        var choices = new List<TranslationChoice>();
        if (translators is not null)
        {
            choices.AddRange(project.Tracks.Where(t => t.Kind == TrackKind.Caption && t.Items.OfType<CaptionItem>().Any(c => c.Text.Trim().Length > 0) && !IsArabic(t))
                .Select(t => new TranslationChoice(t.Id, false, VideoEditorText.TranslateCaptionsChoice(TrackNames.Display(t.Name)))));
        }

        if (transcriber is not null && models is not null)
        {
            choices.AddRange(project.Tracks.Where(t => t.HasSound && t.Items.Count > 0)
                .Select(t => new TranslationChoice(t.Id, true, VideoEditorText.TranslateSpeechChoice(TrackNames.Display(t.Name)))));
        }

        Choices = choices;
        _choice = choices.FirstOrDefault();
        _useGpu = (state.Current.VideoEditor ?? new VideoEditorState()).TranscriptionGpu;
    }

    public IReadOnlyList<TranslationChoice> Choices { get; }

    /// <summary>Whether a caption track's text is Arabic already: most of its letters are.</summary>
    private static bool IsArabic(Track track)
    {
        var text = string.Concat(track.Items.OfType<CaptionItem>().Select(c => c.Text));
        var letters = text.Count(char.IsLetter);
        return letters > 0 && text.Count(c => c is >= '\u0600' and <= '\u06FF') * 2 > letters;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Note))]
    [NotifyPropertyChangedFor(nameof(ShowsGpu))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private TranslationChoice? _choice;

    [ObservableProperty]
    private bool _useGpu;

    public bool ShowsGpu => Choice is { IsSpeech: true };

    /// <summary>What is downloaded for the choice, when it is the first time.</summary>
    public string Note => Choice is { IsSpeech: false } && _translators is { IsReady: false } translators
        ? VideoEditorText.TranslatorToDownload(CommonText.Megabytes(translators.DownloadSize / 1_000_000.0))
        : "";

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

    private bool CanStart() => !IsRunning && Choice is not null;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        Error = null;
        IsRunning = true;
        Progress = 0;
        _cancel = new CancellationTokenSource();
        var ct = _cancel.Token;
        try
        {
            var (captions, language) = Choice!.IsSpeech ? await FromSpeechAsync(Choice.TrackId, ct) : (await FromCaptionsAsync(Choice.TrackId, ct), "ar");
            LogDone(captions.Count, language);
            if (captions.Count == 0)
            {
                Error = VideoEditorText.NoSpeechHeard;
                Status = "";
                return;
            }

            _done(captions, language);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = VideoEditorText.TranscribeCancelled;
            Progress = 0;
        }
        catch (Exception e)
        {
            // Downloads, files, and the runtimes' own errors: said, never a crash.
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

    /// <summary>A caption's text in Arabic for each caption of the track, at the same times.</summary>
    private async Task<IReadOnlyList<CaptionItem>> FromCaptionsAsync(string trackId, CancellationToken ct)
    {
        Status = _translators!.IsReady ? "" : VideoEditorText.DownloadingTranslator(0);
        using var translator = await _translators.OpenEnglishToArabicAsync(new Progress<double>(p =>
        {
            Progress = p;
            Status = VideoEditorText.DownloadingTranslator(p);
        }), ct);
        var captions = _project.Track(trackId)!.Items.OfType<CaptionItem>().Where(c => c.Text.Trim().Length > 0).ToList();
        Progress = 0;
        Status = VideoEditorText.Translating(0);
        IProgress<double> report = new Progress<double>(done =>
        {
            Progress = done;
            Status = VideoEditorText.Translating(done);
        });
        return await Task.Run(() =>
        {
            var list = new List<CaptionItem>(captions.Count);
            for (var i = 0; i < captions.Count; i++)
            {
                list.Add(new CaptionItem { Id = Ids.New(), Start = captions[i].Start, Length = captions[i].Length, Text = translator.Translate(captions[i].Text, ct) });
                report.Report((double)(i + 1) / captions.Count);
            }

            return list;
        }, ct);
    }

    /// <summary>Speech on the sound track, as the timeline plays it, in English.</summary>
    private async Task<(IReadOnlyList<CaptionItem> Captions, string Language)> FromSpeechAsync(string trackId, CancellationToken ct)
    {
        var remembered = _state.Current.VideoEditor ?? new VideoEditorState();
        var model = WhisperModels.Find(remembered.TranscriptionModel) ?? WhisperModels.Small;
        Status = _models!.Has(model) ? "" : VideoEditorText.DownloadingModel(model.Name, model.SizeText);
        var path = await _models.EnsureAsync(model, new Progress<double>(p =>
        {
            Progress = p;
            Status = VideoEditorText.DownloadingModelProgress(model.Name, model.SizeText, p);
        }), ct);
        Progress = 0;
        Status = VideoEditorText.GettingSound;
        var samples = await Task.Run(() => TranscriptAudio.Render(_project, [trackId], new Progress<double>(p => Progress = p * 0.1), ct), ct);
        Status = VideoEditorText.Listening;
        var gpu = UseGpu;
        var result = await _transcriber!.TranscribeAsync(samples, new TranscriptionOptions(path, "ar", gpu, Translate: true), new Progress<double>(p =>
        {
            Progress = 0.1 + (p * 0.9);
            Status = VideoEditorText.ListeningProgress(p);
        }), ct);
        return (CaptionEdits.FromTranscript(result.Segments, 0), "en");
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

    /// <summary>Stops a running translation (the window is closing).</summary>
    public void Stop() => _cancel?.Cancel();

    [LoggerMessage(Level = LogLevel.Information, Message = "Translated: {Count} captions ({Language})")]
    private partial void LogDone(int count, string language);

    [LoggerMessage(Level = LogLevel.Error, Message = "Translating failed")]
    private partial void LogFailed(Exception e);
}
