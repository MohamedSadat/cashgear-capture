using System.Globalization;
using System.IO;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Transcription;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>A sound track to listen to (null: all of them).</summary>
public sealed record SoundChoice(string? TrackId, string Name);

/// <summary>A language to listen for (null: found out from the speech).</summary>
public sealed record LanguageChoice(string? Code, string Name);

/// <summary>A speech model as the window lists it.</summary>
public sealed record ModelChoice(WhisperModel Model, string Name);

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
    private CancellationTokenSource? _cancel;

    /// <param name="done">Gets the captions and the language heard (on the caller's thread), when there are some.</param>
    public TranscribeViewModel(VideoProject project, TimeRange? range, ITranscriber transcriber, WhisperModelStore models, IAppStateStore state, ILogger logger, Action<IReadOnlyList<CaptionItem>, string> done)
    {
        _project = project;
        _range = range;
        _transcriber = transcriber;
        _models = models;
        _state = state;
        _logger = logger;
        _done = done;
        var sounds = project.Tracks.Where(t => t.HasSound).OrderBy(t => t.Name.Contains("Microphone", StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
        Sounds = [.. sounds.Select(t => new SoundChoice(t.Id, t.Name)), .. sounds.Count > 1 ? [new SoundChoice(null, "All sound tracks")] : Array.Empty<SoundChoice>()];
        _sound = Sounds.FirstOrDefault();
        var remembered = state.Current.VideoEditor ?? new VideoEditorState();
        Models = [.. WhisperModels.All.Select(m => new ModelChoice(m, $"{m.Name}: {m.About} ({(models.Has(m) ? "downloaded" : $"{m.SizeText} to download")})"))];
        _model = Models.FirstOrDefault(m => m.Model.Id == remembered.TranscriptionModel) ?? Models.First(m => m.Model == WhisperModels.Small);
        _language = Languages.FirstOrDefault(l => l.Code == remembered.TranscriptionLanguage) ?? Languages[0];
        _useGpu = remembered.TranscriptionGpu;
        _onlyRange = range is not null;
    }

    public IReadOnlyList<SoundChoice> Sounds { get; }

    public IReadOnlyList<ModelChoice> Models { get; }

    public IReadOnlyList<LanguageChoice> Languages { get; } = [new(null, "Find out from the speech"), new("en", "English"), new("ar", "Arabic")];

    public bool HasSound => Sounds.Count > 0;

    public bool HasRange => _range is not null;

    public string RangeText => _range is { } range
        ? string.Create(CultureInfo.CurrentCulture, $"Only the marked range ({range.Length:0.0} s)")
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
                VideoEditor = (s.VideoEditor ?? new VideoEditorState()) with { TranscriptionModel = model.Id, TranscriptionLanguage = language, TranscriptionGpu = gpu },
            }, ct);

            // A tenth of the bar for the sound, the rest for listening; a download first has a bar of its own.
            Status = _models.Has(model) ? "" : $"Downloading the {model.Name} model ({model.SizeText}), once…";
            var path = await _models.EnsureAsync(model, new Progress<double>(p =>
            {
                Progress = p;
                Status = string.Create(CultureInfo.CurrentCulture, $"Downloading the {model.Name} model ({model.SizeText}), once… {p:P0}");
            }), ct);
            Progress = 0;
            Status = "Getting the sound…";
            var project = range is { } r ? ProjectRange.Extract(_project, r) : _project;
            var samples = await Task.Run(() => TranscriptAudio.Render(project, tracks, new Progress<double>(p => Progress = p * 0.1), ct), ct);
            Status = "Listening…";
            var result = await _transcriber.TranscribeAsync(samples, new TranscriptionOptions(path, language, gpu), new Progress<double>(p =>
            {
                Progress = 0.1 + (p * 0.9);
                Status = string.Create(CultureInfo.CurrentCulture, $"Listening… {p:P0}");
            }), ct);
            var captions = CaptionEdits.FromTranscript(result.Segments, range?.From ?? 0);
            LogDone(captions.Count, result.Language, result.Runtime);
            if (captions.Count == 0)
            {
                Error = "No speech was heard in that sound track.";
                Status = "";
                return;
            }

            _done(captions, result.Language);
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            Status = "Cancelled.";
            Progress = 0;
        }
        catch (Exception e)
        {
            // Downloads, files, and whisper.cpp's own errors (a model it can't load, no runtime): said, never a crash.
            LogFailed(e);
            Error = e is System.Net.Http.HttpRequestException ? $"The model couldn't be downloaded: {e.Message}" : e.Message;
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

    /// <summary>A language's name for a track ("Captions (Arabic)").</summary>
    public static string LanguageName(string? code) => code switch
    {
        "en" => "English",
        "ar" => "Arabic",
        null or "" => "",
        _ => CultureInfo.GetCultures(CultureTypes.NeutralCultures).FirstOrDefault(c => c.TwoLetterISOLanguageName == code)?.EnglishName ?? code,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Captions from speech: {Count} captions ({Language}, {Runtime})")]
    private partial void LogDone(int count, string language, string runtime);

    [LoggerMessage(Level = LogLevel.Error, Message = "Captions from speech failed")]
    private partial void LogFailed(Exception e);
}
