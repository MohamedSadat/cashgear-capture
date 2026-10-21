using System.Globalization;
using System.IO;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Media;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>Picks where an export goes (behind an interface for tests).</summary>
public interface IExportPathPicker
{
    string? Pick(string filter, string fileName, string folder);
}

/// <summary>A quality as the export window lists it.</summary>
public sealed record QualityChoice(QualityPreset Value, string Name);

/// <summary>A GIF's dithering as the export window lists it.</summary>
public sealed record DitherChoice(GifDither Value, string Name);

/// <summary>A size a GIF must stay under (0: no limit).</summary>
public sealed record SizeLimitChoice(double Megabytes, string Name);

/// <summary>
/// The export window: the kind of file and its quality, where it goes, only the marked range or not, the captions
/// burned in and/or saved beside it, then the export running in the background with its progress and the time left
/// (Cancel stops it and leaves no file), and when it is done Open and Show in folder. The project is the one the
/// window was opened with: edits made meanwhile wait for the next export.
/// </summary>
public sealed partial class ExportViewModel : ObservableObject
{
    private readonly VideoProject _project;
    private readonly TimeRange? _range;
    private readonly ProjectExporter _exporter;
    private readonly ISettingsService _settings;
    private readonly IAppStateStore _state;
    private readonly IShell _shell;
    private readonly ILogger _logger;
    private CancellationTokenSource? _cancel;
    private string? _confirmed;

    public ExportViewModel(VideoProject project, string name, string folder, ProjectExporter exporter, ISettingsService settings, IAppStateStore state, IShell shell, ILogger logger, TimeRange? range = null)
    {
        _project = project;
        _range = range;
        _exporter = exporter;
        _settings = settings;
        _state = state;
        _shell = shell;
        _logger = logger;
        var remembered = state.Current.VideoEditor ?? new VideoEditorState();
        _selectedPreset = ExportPresets.Find(remembered.ExportPreset) ?? ExportPresets.Mp4H264Hd;
        _quality = Qualities.FirstOrDefault(q => q.Value == remembered.ExportQuality) ?? Qualities[2];
        _path = System.IO.Path.Combine(folder, Safe(name) + _selectedPreset.Extension);
        _onlyRange = range is not null;
        _burnCaptions = remembered.ExportBurnCaptions;
        _saveSrt = remembered.ExportSrt;
        _saveVtt = remembered.ExportVtt;
        var gif = GifChoices.From(remembered);
        GifWidths = [.. new[] { 320, 480, 640, 800, 960, 1280 }.Where(w => w < project.Canvas.Width).Append(project.Canvas.Width)];
        _gifFps = GifRates.Contains(gif.Fps) ? gif.Fps : 15;
        _gifWidth = GifWidths.Where(w => w <= gif.Width).DefaultIfEmpty(GifWidths[0]).Max();
        _gifLoop = gif.Loop;
        _gifDithering = GifDithers.FirstOrDefault(d => d.Value == gif.Dither) ?? GifDithers[0];
        _gifLimit = GifLimits.FirstOrDefault(l => Math.Abs(l.Megabytes - remembered.GifMaxMegabytes) < 0.01) ?? GifLimits[0];
    }

    /// <summary>The kind of file is a GIF: its own choices instead of the quality.</summary>
    public bool IsGif => SelectedPreset.IsGif;

    public bool HasQuality => !SelectedPreset.IsGif;

    public IReadOnlyList<int> GifRates { get; } = [10, 12, 15, 20, 25, 30];

    /// <summary>Widths up to the project's own.</summary>
    public IReadOnlyList<int> GifWidths { get; }

    public IReadOnlyList<DitherChoice> GifDithers { get; } =
    [
        new(GifDither.Pattern, "A fine pattern (smaller; best for screens)"),
        new(GifDither.Diffusion, "Diffusion (smoother gradients and video)"),
        new(GifDither.None, "None (flat colours only)"),
    ];

    public IReadOnlyList<SizeLimitChoice> GifLimits { get; } =
    [
        new(0, "No limit"),
        new(1, "1 MB"),
        new(2, "2 MB"),
        new(5, "5 MB"),
        new(8, "8 MB"),
        new(10, "10 MB"),
        new(25, "25 MB"),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private int _gifFps;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private int _gifWidth;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private bool _gifLoop;

    [ObservableProperty]
    private DitherChoice _gifDithering;

    [ObservableProperty]
    private SizeLimitChoice _gifLimit;

    /// <summary>The GIF choices as export options.</summary>
    public GifOptions GifOptions => new(GifFps, GifWidth, GifLoop, Dither: GifDithering.Value, MaxBytes: (long)Math.Round(GifLimit.Megabytes * 1024 * 1024));

    /// <summary>A range is marked on the timeline: the export can be of it alone.</summary>
    public bool HasRange => _range is not null;

    public string RangeText => _range is { } range
        ? $"Only the marked range ({TimeText(TimeSpan.FromSeconds(range.From))} to {TimeText(TimeSpan.FromSeconds(range.To))})"
        : "";

    /// <summary>The project has captions: they can be burned in and saved as files.</summary>
    public bool HasCaptions => SubtitleFiles.Cues(_project).Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private bool _onlyRange;

    [ObservableProperty]
    private bool _burnCaptions;

    [ObservableProperty]
    private bool _saveSrt;

    [ObservableProperty]
    private bool _saveVtt;

    /// <summary>How long the export is: the marked range, or the whole timeline.</summary>
    private double Duration => OnlyRange && _range is { } range ? Math.Min(range.To, _project.Duration) - range.From : _project.Duration;

    public IReadOnlyList<ExportPreset> Presets => ExportPresets.All;

    public IReadOnlyList<QualityChoice> Qualities { get; } =
    [
        new(QualityPreset.Small, "Small file"),
        new(QualityPreset.Balanced, "Balanced"),
        new(QualityPreset.High, "High"),
        new(QualityPreset.Lossless, "Near lossless"),
    ];

    /// <summary>Picks the file (set by the window; tests set their own).</summary>
    public IExportPathPicker? PathPicker { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private ExportPreset _selectedPreset;

    [ObservableProperty]
    private QualityChoice _quality;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _path;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isRunning;

    [ObservableProperty]
    private double _progress;

    [ObservableProperty]
    private string _progressText = "";

    /// <summary>The finished file, once there is one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDone))]
    private string? _result;

    [ObservableProperty]
    private string? _error;

    public bool IsIdle => !IsRunning;

    public bool IsDone => Result is not null;

    public string SizeText
    {
        get
        {
            if (!SelectedPreset.HasVideo)
            {
                return $"Sound only, {TimeText(TimeSpan.FromSeconds(Duration))} long";
            }

            if (SelectedPreset.IsGif)
            {
                var (w, h) = GifOptions.Size(_project.Canvas.Width, _project.Canvas.Height);
                return $"{w} × {h}, {GifFps} frames a second, {TimeText(TimeSpan.FromSeconds(Duration))} long, no sound, {(GifLoop ? "plays over and over" : "plays once")}";
            }

            var (width, height) = SelectedPreset.Size(_project.Canvas.Width, _project.Canvas.Height);
            return $"{width} × {height}, {_project.Canvas.Fps} fps, {TimeText(TimeSpan.FromSeconds(Duration))} long";
        }
    }

    /// <summary>Raised when the window should close.</summary>
    public event EventHandler? CloseRequested;

    partial void OnSelectedPresetChanged(ExportPreset value)
    {
        Path = System.IO.Path.ChangeExtension(Path, value.Extension);
        OnPropertyChanged(nameof(IsGif));
        OnPropertyChanged(nameof(HasQuality));
    }

    [RelayCommand]
    private void Browse()
    {
        var filter = $"{SelectedPreset.Name}|*{SelectedPreset.Extension}";
        if (PathPicker?.Pick(filter, System.IO.Path.GetFileName(Path), System.IO.Path.GetDirectoryName(Path) ?? "") is { } picked)
        {
            Path = picked;
            _confirmed = picked; // the save dialog asked about replacing it
        }
    }

    private bool CanStart() => !IsRunning && !string.IsNullOrWhiteSpace(Path);

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        Error = null;
        Result = null;
        IsRunning = true;
        Progress = 0;
        ProgressText = "Starting…";
        _cancel = new CancellationTokenSource();
        var preset = SelectedPreset;
        var quality = Quality.Value;
        var (burn, srt, vtt) = (BurnCaptions, SaveSrt, SaveVtt);
        var gif = GifOptions;
        var gifLimit = GifLimit.Megabytes;
        var range = OnlyRange ? _range : null;
        var path = System.IO.Path.GetFullPath(Path);
        if (File.Exists(path) && !string.Equals(path, _confirmed, StringComparison.OrdinalIgnoreCase))
        {
            // A file there already, not chosen in the save dialog: the export gets a name of its own.
            path = Unique(path);
            Path = path;
        }

        try
        {
            await _state.UpdateAsync(s => s with
            {
                VideoEditor = (s.VideoEditor ?? new VideoEditorState()) with
                {
                    ExportPreset = preset.Id,
                    ExportQuality = quality,
                    ExportFolder = System.IO.Path.GetDirectoryName(path),
                    ExportBurnCaptions = burn,
                    ExportSrt = srt,
                    ExportVtt = vtt,
                    GifFps = preset.IsGif ? gif.Fps : s.VideoEditor?.GifFps ?? 15,
                    GifWidth = preset.IsGif ? gif.Width : s.VideoEditor?.GifWidth ?? 800,
                    GifLoop = preset.IsGif ? gif.Loop : s.VideoEditor?.GifLoop ?? true,
                    GifDither = preset.IsGif ? gif.Dither.ToString() : s.VideoEditor?.GifDither,
                    GifMaxMegabytes = preset.IsGif ? gifLimit : s.VideoEditor?.GifMaxMegabytes ?? 0,
                },
            });
            var progress = new Progress<ExportProgress>(p =>
            {
                Progress = p.Fraction;
                ProgressText = p.Remaining is { } left
                    ? string.Create(CultureInfo.CurrentCulture, $"{p.Fraction:P0} · about {TimeText(left)} left")
                    : string.Create(CultureInfo.CurrentCulture, $"{p.Fraction:P0}");
            });
            var files = (srt ? CaptionFiles.Srt : CaptionFiles.None) | (vtt ? CaptionFiles.Vtt : CaptionFiles.None);
            var result = await _exporter.ExportAsync(
                new ExportRequest(_project, preset, quality, path, _settings.Current.Recording.Encoder, range, burn, HasCaptions ? files : CaptionFiles.None, preset.IsGif ? gif : null),
                progress,
                _cancel.Token);
            Progress = 1;
            var captions = result.CaptionPaths is { Count: > 0 } written ? $", with {string.Join(" and ", written.Select(System.IO.Path.GetFileName))}" : "";
            if (result.Gif is { } made && made != gif)
            {
                var (w, _) = made.Size(_project.Canvas.Width, _project.Canvas.Height);
                captions += $" (made smaller to fit: {w} px wide, {made.Fps} frames a second, {made.Colors} colours)";
            }
            ProgressText = $"Done in {TimeText(result.Elapsed)}: {System.IO.Path.GetFileName(result.Path)}, {result.Bytes / 1024.0 / 1024.0:0.0} MB{(result.Encoder is { } encoder ? $" ({encoder})" : "")}{captions}";
            Result = result.Path;
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Cancelled: no file was made.";
            Progress = 0;
        }
        catch (Exception e) when (e is ExportException or IOException or UnauthorizedAccessException)
        {
            LogFailed(e, path);
            Error = e.Message;
            ProgressText = "";
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

    [RelayCommand]
    private void Open()
    {
        if (Result is { } file)
        {
            _shell.OpenFile(file);
        }
    }

    [RelayCommand]
    private void ShowInFolder()
    {
        if (Result is { } file)
        {
            _shell.ShowInFolder(file);
        }
    }

    /// <summary>Stops a running export (the window is closing).</summary>
    public void Stop() => _cancel?.Cancel();

    private static string TimeText(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{(int)time.TotalMinutes}:{time.Seconds:00}";

    /// <summary>The path, or "name (2).ext", "name (3).ext"… when it is taken.</summary>
    internal static string Unique(string path)
    {
        var folder = System.IO.Path.GetDirectoryName(path) ?? "";
        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        var extension = System.IO.Path.GetExtension(path);
        for (var n = 2; File.Exists(path); n++)
        {
            path = System.IO.Path.Combine(folder, $"{name} ({n}){extension}");
        }

        return path;
    }

    /// <summary>A file name made of <paramref name="name"/> without the characters Windows doesn't allow.</summary>
    private static string Safe(string name)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        var safe = new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]).Trim();
        return safe.Length == 0 ? "Export" : safe;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Exporting {Path} failed")]
    private partial void LogFailed(Exception e, string path);
}
