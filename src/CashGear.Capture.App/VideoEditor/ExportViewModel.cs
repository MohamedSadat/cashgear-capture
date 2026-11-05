using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Presets;
using CashGear.Capture.Licensing;
using CashGear.Capture.Media;
using CashGear.Capture.Outputs.Sharing;
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

/// <summary>A quality as the export window lists it (its name is what screen readers read for it).</summary>
public sealed record QualityChoice(QualityPreset Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A GIF's dithering as the export window lists it.</summary>
public sealed record DitherChoice(GifDither Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A size a GIF must stay under (0: no limit).</summary>
public sealed record SizeLimitChoice(double Megabytes, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A caption track to burn in. Its name is what screen readers read for it.</summary>
public sealed record CaptionTrackChoice(string TrackId, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A loudness target as the export window lists it (0: as mixed).</summary>
public sealed record LoudnessChoice(double Lufs, string Name)
{
    public override string ToString() => Name;
}

/// <summary>How the sound tracks go out, as the export window lists it.</summary>
public sealed record TracksOutChoice(SoundTracksOut Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A destination an export can go to (none: it stays here).</summary>
public sealed record SendChoice(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A YouTube playlist (none: it joins no list).</summary>
public sealed record PlaylistChoice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>Who can see a video, as the export window lists it.</summary>
public sealed record PrivacyChoice(VideoPrivacy Value, string Name)
{
    public override string ToString() => Name;
}

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
    private readonly bool _soundPost;
    private readonly IFeatureGate _gate;
    private readonly IPresetStore? _presets;
    private readonly IExportSender? _sender;
    private readonly string _name;
    private bool _applying;
    private CancellationTokenSource? _cancel;
    private string? _confirmed;

    public ExportViewModel(VideoProject project, string name, string folder, ProjectExporter exporter, ISettingsService settings, IAppStateStore state, IShell shell, ILogger logger, TimeRange? range = null, IFeatureGate? features = null, IPresetStore? presets = null, IExportSender? sender = null)
    {
        var gate = features ?? FeatureGate.Open;
        _gate = gate;
        _presets = presets;
        _sender = sender;
        _name = name;
        var hdr = gate.IsEnabled(ProFeature.RecordingPro) && ProjectExporter.HasHdrMedia(project) ? ExportPresets.Hdr : [];
        Presets = [.. ExportPresets.All.Where(p => (!p.IsGif || gate.IsEnabled(ProFeature.GifExport)) && (!p.HasCanvas || gate.IsEnabled(ProFeature.StudioLook))), .. hdr];
        _project = project;
        _range = range;
        _exporter = exporter;
        _settings = settings;
        _state = state;
        _shell = shell;
        _logger = logger;
        var remembered = state.Current.VideoEditor ?? new VideoEditorState();
        _selectedPreset = Presets.FirstOrDefault(p => p == ExportPresets.Find(remembered.ExportPreset)) ?? ExportPresets.Mp4H264Hd;
        _quality = Qualities.FirstOrDefault(q => q.Value == remembered.ExportQuality) ?? Qualities[2];
        _path = System.IO.Path.Combine(folder, Safe(name) + _selectedPreset.Extension);
        _onlyRange = range is not null;
        _burnCaptions = remembered.ExportBurnCaptions;
        _burnTrack = CaptionTracks.FirstOrDefault(c => project.Track(c.TrackId)?.Hidden == false) ?? CaptionTracks.FirstOrDefault();
        _saveSrt = remembered.ExportSrt;
        _saveVtt = remembered.ExportVtt;
        var gif = GifChoices.From(remembered);
        GifWidths = [.. new[] { 320, 480, 640, 800, 960, 1280 }.Where(w => w < project.Canvas.Width).Append(project.Canvas.Width)];
        _gifFps = GifRates.Contains(gif.Fps) ? gif.Fps : 15;
        _gifWidth = GifWidths.Where(w => w <= gif.Width).DefaultIfEmpty(GifWidths[0]).Max();
        _gifLoop = gif.Loop;
        _gifDithering = GifDithers.FirstOrDefault(d => d.Value == gif.Dither) ?? GifDithers[0];
        _gifLimit = GifLimits.FirstOrDefault(l => Math.Abs(l.Megabytes - remembered.GifMaxMegabytes) < 0.01) ?? GifLimits[0];
        _soundPost = gate.IsEnabled(ProFeature.AudioPost);
        _loudness = Loudnesses.FirstOrDefault(l => Math.Abs(l.Lufs - remembered.ExportLoudness) < 0.01) ?? Loudnesses[1];
        _tracksOut = TracksOutChoices.FirstOrDefault(t => t.Value.ToString() == remembered.ExportTracksOut) ?? TracksOutChoices[0];
        _chaptersInFile = remembered.ExportChapters;
        _chaptersVtt = remembered.ExportChaptersVtt;
        NamedExports = [.. BuiltInExports.All.Concat(presets?.Current.SavedExports ?? [])];
        SendTargets = [new SendChoice(null, VideoEditorText.SendNowhere), .. (sender?.Targets() ?? []).Select(t => new SendChoice(t.Id, t.Name))];
        _sendTo = SendTargets.FirstOrDefault(t => t.Id == remembered.ExportSendTo) ?? SendTargets[0];
        var youtube = sender?.YouTubeDefaults ?? new YouTubeSettings();
        _youTubeTitle = name;
        _youTubeDescription = youtube.Description;
        _youTubeTags = string.Join(", ", youtube.Tags ?? []);
        _privacy = Privacies.First(p => p.Value == youtube.Privacy);
        Playlists = [new PlaylistChoice(string.Empty, VideoEditorText.NoPlaylist)];
        _playlist = Playlists[0];
        _defaultPlaylist = YouTubeDestination.PlaylistId(youtube.Playlist);
        if (_defaultPlaylist is { } typed)
        {
            Playlists.Add(new PlaylistChoice(typed, typed));
            _playlist = Playlists[1];
        }

        if (IsYouTube)
        {
            _ = LoadPlaylistsAsync();
        }
    }

    private readonly string? _defaultPlaylist;
    private bool _playlistsLoaded;

    /// <summary>Presets of this window's choices: those that come with the app, then the saved ones.</summary>
    public ObservableCollection<NamedExport> NamedExports { get; }

    /// <summary>The preset last chosen; changing a choice leaves it.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteNamedExportCommand))]
    private NamedExport? _selectedNamedExport;

    /// <summary>Asks for a preset's name (set by the owner; tests set their own).</summary>
    public Func<string, string, string?>? AskName { get; set; }

    /// <summary>Lets the user make a thumbnail and gives its file (set by the owner).</summary>
    public Func<string?>? PickThumbnail { get; set; }

    /// <summary>The project has markers, which can become chapters.</summary>
    public bool HasMarkers => _project.Markers is { Count: > 0 };

    /// <summary>This kind of file can hold chapters.</summary>
    public bool CanHoldChapters => SelectedPreset.CanHoldChapters;

    [ObservableProperty]
    private bool _chaptersInFile;

    [ObservableProperty]
    private bool _chaptersVtt;

    /// <summary>Where the file can go when done.</summary>
    public IReadOnlyList<SendChoice> SendTargets { get; }

    public bool CanSend => SendTargets.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsYouTube))]
    private SendChoice _sendTo;

    /// <summary>It goes to YouTube: what YouTube asks for.</summary>
    public bool IsYouTube => SendTo.Id is { } id && DestinationIds.Same(id, DestinationIds.YouTube);

    [ObservableProperty]
    private string _youTubeTitle;

    [ObservableProperty]
    private string _youTubeDescription;

    /// <summary>Separated by commas.</summary>
    [ObservableProperty]
    private string _youTubeTags;

    public IReadOnlyList<PrivacyChoice> Privacies { get; } =
    [
        new(VideoPrivacy.Private, VideoEditorText.PrivacyPrivate),
        new(VideoPrivacy.Unlisted, VideoEditorText.PrivacyUnlisted),
        new(VideoPrivacy.Public, VideoEditorText.PrivacyPublic),
    ];

    [ObservableProperty]
    private PrivacyChoice _privacy;

    public ObservableCollection<PlaylistChoice> Playlists { get; }

    [ObservableProperty]
    private PlaylistChoice _playlist;

    /// <summary>The chapters' times go at the end of the description.</summary>
    [ObservableProperty]
    private bool _chaptersInDescription = true;

    /// <summary>The picture sent with the video.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ThumbnailText))]
    private string? _thumbnailPath;

    public string ThumbnailText => ThumbnailPath is { } path ? VideoEditorText.ThumbnailChosen(System.IO.Path.GetFileName(path)) : VideoEditorText.ThumbnailNone;

    /// <summary>The chapters as the description will have them, for the project as it is.</summary>
    public string ChaptersPreview => HasMarkers ? ProjectChapters.Description(ProjectChapters.From(Chosen().Request.Prepared())) : string.Empty;

    /// <summary>Why YouTube wouldn't show them, if it wouldn't.</summary>
    public string ChaptersNote => HasMarkers ? ProjectChapters.YouTubeProblem(ProjectChapters.From(Chosen().Request.Prepared())) ?? string.Empty : string.Empty;

    /// <summary>The window's choices as a preset keeps them.</summary>
    public ExportChoices Choices => new(
        SelectedPreset.Id,
        Quality.Value,
        Loudness.Lufs,
        TracksOut.Value.ToString(),
        BurnCaptions,
        SaveSrt,
        SaveVtt,
        ChaptersInFile,
        ChaptersVtt,
        GifFps,
        GifWidth,
        GifLoop,
        GifDithering.Value.ToString(),
        GifLimit.Megabytes,
        SendTo.Id);

    /// <summary>Saves the choices under a name; a name that is taken among the saved ones is replaced.</summary>
    [RelayCommand]
    private async Task SaveNamedExportAsync()
    {
        if (AskName?.Invoke(VideoEditorText.SaveExportPresetTitle, SelectedNamedExport?.Name ?? string.Empty) is not { Length: > 0 } name || _presets is null)
        {
            return;
        }

        var saved = new NamedExport(Guid.NewGuid().ToString("N"), name.Trim(), Choices);
        await _presets.UpdateAsync(p => p.WithExport(saved));
        if (NamedExports.FirstOrDefault(e => string.Equals(e.Name, saved.Name, StringComparison.OrdinalIgnoreCase) && !BuiltInExports.All.Any(b => b.Id == e.Id)) is { } replaced)
        {
            NamedExports.Remove(replaced);
        }

        NamedExports.Add(saved);
        _applying = true;
        SelectedNamedExport = saved;
        _applying = false;
    }

    private bool CanDeleteNamedExport() => _presets is not null && SelectedNamedExport is { } export && !BuiltInExports.All.Any(b => b.Id == export.Id);

    [RelayCommand(CanExecute = nameof(CanDeleteNamedExport))]
    private async Task DeleteNamedExportAsync()
    {
        if (SelectedNamedExport is not { } export || _presets is null)
        {
            return;
        }

        await _presets.UpdateAsync(p => p.WithoutExport(export.Name));
        NamedExports.Remove(export);
        SelectedNamedExport = null;
    }

    partial void OnSelectedNamedExportChanged(NamedExport? value)
    {
        if (value is not null && !_applying)
        {
            Apply(value.Choices);
        }
    }

    /// <summary>Sets the window to a preset's choices. What this project or licence can't have stays as it was.</summary>
    public void Apply(ExportChoices choices)
    {
        ArgumentNullException.ThrowIfNull(choices);
        _applying = true;
        try
        {
            var (preset, note) = choices.PresetFor(_project, Allowances());
            Error = note;
            SelectedPreset = Presets.FirstOrDefault(p => p == preset) ?? SelectedPreset;
            Quality = Qualities.FirstOrDefault(q => q.Value == choices.Quality) ?? Quality;
            Loudness = Loudnesses.FirstOrDefault(l => Math.Abs(l.Lufs - choices.LoudnessLufs) < 0.01) ?? Loudness;
            TracksOut = TracksOutChoices.FirstOrDefault(t => t.Value.ToString() == choices.TracksOut) ?? TracksOutChoices[0];
            BurnCaptions = choices.BurnCaptions;
            SaveSrt = choices.SaveSrt;
            SaveVtt = choices.SaveVtt;
            ChaptersInFile = choices.Chapters;
            ChaptersVtt = choices.ChaptersVtt;
            GifFps = GifRates.Contains(choices.GifFps) ? choices.GifFps : GifFps;
            GifWidth = GifWidths.Where(w => w <= choices.GifWidth).DefaultIfEmpty(GifWidths[0]).Max();
            GifLoop = choices.GifLoop;
            GifDithering = GifDithers.FirstOrDefault(d => d.Value.ToString() == choices.GifDither) ?? GifDithers[0];
            GifLimit = GifLimits.FirstOrDefault(l => Math.Abs(l.Megabytes - choices.GifMaxMegabytes) < 0.01) ?? GifLimits[0];
            SendTo = SendTargets.FirstOrDefault(t => t.Id == choices.SendTo) ?? SendTargets[0];
        }
        finally
        {
            _applying = false;
        }
    }

    private ExportAllowances Allowances() => new(_gate.IsEnabled(ProFeature.RecordingPro), _gate.IsEnabled(ProFeature.GifExport), _gate.IsEnabled(ProFeature.StudioLook), _gate.IsEnabled(ProFeature.AudioPost));

    private ChosenExport Chosen(string path = "") =>
        Choices.ToRequest(_project, path, _settings.Current.Recording.Encoder, OnlyRange ? _range : null, ChoosesCaptionTrack ? BurnTrack?.TrackId : null, Allowances());

    partial void OnSendToChanged(SendChoice value)
    {
        if (IsYouTube)
        {
            _ = LoadPlaylistsAsync();
        }
    }

    [RelayCommand]
    private void ChooseThumbnail()
    {
        if (PickThumbnail?.Invoke() is { } path)
        {
            ThumbnailPath = path;
        }
    }

    /// <summary>The account's playlists, once: one that can't be read just isn't listed.</summary>
    private async Task LoadPlaylistsAsync()
    {
        if (_playlistsLoaded || _sender is null)
        {
            return;
        }

        _playlistsLoaded = true;
        try
        {
            var found = await _sender.PlaylistsAsync(CancellationToken.None);
            foreach (var (id, title) in found)
            {
                var named = new PlaylistChoice(id, title);
                if (Playlists.FirstOrDefault(p => p.Id == id) is { } typed)
                {
                    var wasChosen = Playlist == typed;
                    Playlists[Playlists.IndexOf(typed)] = named;
                    if (wasChosen)
                    {
                        Playlist = named;
                    }
                }
                else
                {
                    Playlists.Add(named);
                }
            }
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or SignInRequiredException or UploadRefusedException or System.Text.Json.JsonException or OperationCanceledException or KeyNotFoundException or InvalidOperationException)
        {
            LogPlaylistsFailed(e.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The playlists couldn't be read: {Reason}")]
    private partial void LogPlaylistsFailed(string reason);

    /// <summary>What an export can do with sound here (a licence that has it, a kind of file with sound).</summary>
    public bool HasSoundChoices => _soundPost && SelectedPreset.HasSound;

    /// <summary>There are sound tracks to keep apart.</summary>
    public bool ChoosesTracks => HasSoundChoices && ProjectExporter.HeardTracks(_project).Count > 0;

    public IReadOnlyList<LoudnessChoice> Loudnesses { get; } =
    [
        new(0, VideoEditorText.LoudnessAsMixed),
        new(ExportLoudness.Online, VideoEditorText.LoudnessOnline),
        new(ExportLoudness.YouTube, VideoEditorText.LoudnessYouTube),
        new(ExportLoudness.Broadcast, VideoEditorText.LoudnessBroadcast),
    ];

    [ObservableProperty]
    private LoudnessChoice _loudness;

    /// <summary>How the tracks can go out with this kind of file: a stream each only in a video's file.</summary>
    public IReadOnlyList<TracksOutChoice> TracksOutChoices => SelectedPreset.CanHoldTracks
        ?
        [
            new(SoundTracksOut.Mix, VideoEditorText.TracksOutMix),
            new(SoundTracksOut.Streams, VideoEditorText.TracksOutStreams),
            new(SoundTracksOut.WavFiles, VideoEditorText.TracksOutFiles),
        ]
        :
        [
            new(SoundTracksOut.Mix, VideoEditorText.TracksOutMix),
            new(SoundTracksOut.WavFiles, VideoEditorText.TracksOutFiles),
        ];

    [ObservableProperty]
    private TracksOutChoice _tracksOut;

    /// <summary>The kind of file is a GIF: its own choices instead of the quality.</summary>
    public bool IsGif => SelectedPreset.IsGif;

    public bool HasQuality => !SelectedPreset.IsGif;

    public IReadOnlyList<int> GifRates { get; } = [10, 12, 15, 20, 25, 30];

    /// <summary>Widths up to the project's own.</summary>
    public IReadOnlyList<int> GifWidths { get; }

    public IReadOnlyList<DitherChoice> GifDithers { get; } =
    [
        new(GifDither.Pattern, VideoEditorText.DitherPattern),
        new(GifDither.Diffusion, VideoEditorText.DitherDiffusion),
        new(GifDither.None, VideoEditorText.DitherNone),
    ];

    public IReadOnlyList<SizeLimitChoice> GifLimits { get; } =
    [
        new(0, VideoEditorText.NoLimit),
        new(1, VideoEditorText.SizeLimit(1)),
        new(2, VideoEditorText.SizeLimit(2)),
        new(5, VideoEditorText.SizeLimit(5)),
        new(8, VideoEditorText.SizeLimit(8)),
        new(10, VideoEditorText.SizeLimit(10)),
        new(25, VideoEditorText.SizeLimit(25)),
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
        ? VideoEditorText.OnlyMarkedRange(TimeText(TimeSpan.FromSeconds(range.From)), TimeText(TimeSpan.FromSeconds(range.To)))
        : "";

    /// <summary>The project has captions: they can be burned in and saved as files.</summary>
    public bool HasCaptions => SubtitleFiles.Cues(_project).Count > 0;

    /// <summary>The caption tracks with text: a track per language.</summary>
    public IReadOnlyList<CaptionTrackChoice> CaptionTracks => [.. _project.Tracks.Where(t => t.Kind == TrackKind.Caption && SubtitleFiles.Cues(_project, t.Id).Count > 0).Select(t => new CaptionTrackChoice(t.Id, TrackNames.Display(t.Name)))];

    /// <summary>Several tracks, so one is chosen to burn in.</summary>
    public bool ChoosesCaptionTrack => CaptionTracks.Count > 1;

    /// <summary>The track burned in, when there is a choice: the first shown.</summary>
    [ObservableProperty]
    private CaptionTrackChoice? _burnTrack;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    [NotifyPropertyChangedFor(nameof(ChaptersPreview))]
    [NotifyPropertyChangedFor(nameof(ChaptersNote))]
    private bool _onlyRange;

    [ObservableProperty]
    private bool _burnCaptions;

    [ObservableProperty]
    private bool _saveSrt;

    [ObservableProperty]
    private bool _saveVtt;

    /// <summary>How long the export is: the marked range, or the whole timeline.</summary>
    private double Duration => OnlyRange && _range is { } range ? Math.Min(range.To, _project.Duration) - range.From : _project.Duration;

    /// <summary>The kinds of file to export to (the GIF one only where GIFs may be made).</summary>
    public IReadOnlyList<ExportPreset> Presets { get; }

    public IReadOnlyList<QualityChoice> Qualities { get; } =
    [
        new(QualityPreset.Small, VideoEditorText.QualitySmall),
        new(QualityPreset.Balanced, VideoEditorText.QualityBalanced),
        new(QualityPreset.High, VideoEditorText.QualityHigh),
        new(QualityPreset.Lossless, VideoEditorText.QualityNearLossless),
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
                return VideoEditorText.SoundOnlySize(TimeText(TimeSpan.FromSeconds(Duration)));
            }

            if (SelectedPreset.IsGif)
            {
                var (w, h) = GifOptions.Size(_project.Canvas.Width, _project.Canvas.Height);
                var length = TimeText(TimeSpan.FromSeconds(Duration));
                return GifLoop ? VideoEditorText.GifSizeLooping(w, h, GifFps, length) : VideoEditorText.GifSizeOnce(w, h, GifFps, length);
            }

            var (width, height) = SelectedPreset.Size(_project.Canvas.Width, _project.Canvas.Height);
            return VideoEditorText.VideoSize(width, height, _project.Canvas.Fps, TimeText(TimeSpan.FromSeconds(Duration)));
        }
    }

    /// <summary>Raised when the window should close.</summary>
    public event EventHandler? CloseRequested;

    partial void OnSelectedPresetChanged(ExportPreset value)
    {
        Path = System.IO.Path.ChangeExtension(Path, value.Extension);
        OnPropertyChanged(nameof(IsGif));
        OnPropertyChanged(nameof(CanHoldChapters));
        OnPropertyChanged(nameof(HasQuality));
        OnPropertyChanged(nameof(HasSoundChoices));
        OnPropertyChanged(nameof(ChoosesTracks));
        var kept = TracksOutChoices.FirstOrDefault(t => t.Value == TracksOut.Value) ?? TracksOutChoices[0];
        OnPropertyChanged(nameof(TracksOutChoices));
        TracksOut = kept;
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
        ProgressText = VideoEditorText.Starting;
        _cancel = new CancellationTokenSource();
        var preset = SelectedPreset;
        var quality = Quality.Value;
        var (burn, srt, vtt) = (BurnCaptions, SaveSrt, SaveVtt);
        var (chaptersInFile, chaptersVtt, sendTo) = (ChaptersInFile, ChaptersVtt, SendTo.Id);
        var gif = GifOptions;
        var gifLimit = GifLimit.Megabytes;
        var (loudness, tracksOut) = (Loudness.Lufs, TracksOut.Value);
        double? target = HasSoundChoices && loudness != 0 ? loudness : null;
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
                    ExportLoudness = loudness,
                    ExportTracksOut = tracksOut.ToString(),
                    ExportChapters = chaptersInFile,
                    ExportChaptersVtt = chaptersVtt,
                    ExportSendTo = sendTo,
                },
            });
            var progress = new Progress<ExportProgress>(p =>
            {
                Progress = p.Fraction;
                ProgressText = p.Remaining is { } left
                    ? VideoEditorText.ProgressTimeLeft(p.Fraction, TimeText(left))
                    : string.Create(CultureInfo.CurrentCulture, $"{p.Fraction:P0}");
            });
            var chosen = Chosen(path);
            var result = await _exporter.ExportAsync(chosen.Request, progress, _cancel.Token);
            Progress = 1;
            var captions = result.CaptionPaths is { Count: > 0 } written
                ? VideoEditorText.WithCaptionFiles(written.Select(System.IO.Path.GetFileName).Aggregate((list, next) => VideoEditorText.AndList(list, next)))
                : "";
            if (result.Gif is { } made && made != gif)
            {
                var (w, _) = made.Size(_project.Canvas.Width, _project.Canvas.Height);
                captions += VideoEditorText.GifMadeSmaller(w, made.Fps, made.Colors);
            }
            if (result.PointerOutOfView is { Count: > 0 } missed)
            {
                captions += VideoEditorText.ExportPointerOutOfView(string.Join(", ", missed.Select(m => TimeText(TimeSpan.FromSeconds(m.From)))));
            }

            if (result.TrackPaths is { Count: > 0 } trackFiles)
            {
                captions += VideoEditorText.WithTrackFiles(trackFiles.Select(System.IO.Path.GetFileName).Aggregate((list, next) => VideoEditorText.AndList(list, next)));
            }

            if (result.MeasuredLufs is { } measured && target is { } met)
            {
                captions += VideoEditorText.ExportLoudnessDone(measured, met);
            }

            if (result.ChaptersPath is { } chaptersFile)
            {
                captions += VideoEditorText.WithChaptersFile(System.IO.Path.GetFileName(chaptersFile));
            }

            if (chosen.Note is { } note)
            {
                captions += " " + note;
            }

            if (sendTo is not null && _sender is not null)
            {
                _sender.Send(result.Path, _name, sendTo, IsYouTube && ChaptersInDescription ? ProjectChapters.From(chosen.Request.Prepared()) : [], IsYouTube ? Details() : null);
                captions += VideoEditorText.SentInBackground(SendTo.Name);
            }

            var encoder = result.Encoder is { } used ? $" ({used})" : "";
            ProgressText = VideoEditorText.ExportDone(TimeText(result.Elapsed), System.IO.Path.GetFileName(result.Path), CommonText.Megabytes(result.Bytes / 1024.0 / 1024.0), encoder, captions);
            Result = result.Path;
        }
        catch (OperationCanceledException)
        {
            ProgressText = VideoEditorText.ExportCancelled;
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

    /// <summary>What was typed for YouTube.</summary>
    private UploadDetails Details() => new(
        YouTubeTitle,
        YouTubeDescription,
        [.. YouTubeTags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
        Privacy.Value,
        Playlist.Id,
        ThumbnailPath);

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
        return safe.Length == 0 ? VideoEditorText.ExportFileName : safe;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Exporting {Path} failed")]
    private partial void LogFailed(Exception e, string path);
}
