using System.Collections.ObjectModel;
using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Presets;
using CashGear.Capture.Licensing;
using CashGear.Capture.Media;
using CashGear.Capture.Transcription;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using EffectKind = CashGear.Capture.VideoEditor.Model.EffectKind;
using CashGear.Capture.VideoEditor.Playback;
using CashGear.Capture.VideoEditor.Rendering;
using CashGear.Capture.VideoEditor.Timeline;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>What the video editor asks the user (behind an interface for tests).</summary>
public interface IVideoEditorDialogs
{
    /// <summary>Where to save the project (a .cgcap), or null.</summary>
    string? AskProjectPath(string suggestedName, string? folder);

    /// <summary>A media file to add, or null.</summary>
    string? PickMedia(string title, string filter, string? folder);

    SaveChangesChoice AskSaveChanges(string name);

    void ShowError(string message);

    /// <summary>Shows the export window (it runs the export, and closes when the user is done).</summary>
    void ShowExport(ExportViewModel viewModel);

    /// <summary>Shows the Captions from Speech window (it runs the transcription, and closes when it is done).</summary>
    void ShowTranscribe(TranscribeViewModel viewModel);

    /// <summary>Shows the Translate window (it runs the translation, and closes when it is done).</summary>
    void ShowTranslate(TranslateViewModel viewModel);

    /// <summary>Asks where to save a file (null: cancelled).</summary>
    string? AskSavePath(string title, string filter, string fileName, string? folder);

    /// <summary>Asks for a name (null: cancelled).</summary>
    string? AskName(string title, string suggested);
}

/// <summary>Where copied clips and annotations wait to be pasted: JSON, from any editor window to another.</summary>
public interface IVideoClipboard
{
    byte[]? Read();

    void Write(byte[] copied);
}

/// <summary>A clipboard of its own, for tests and whatever has no Windows clipboard.</summary>
internal sealed class MemoryVideoClipboard : IVideoClipboard
{
    private byte[]? _copied;

    public static MemoryVideoClipboard Shared { get; } = new();

    public byte[]? Read() => _copied;

    public void Write(byte[] copied) => _copied = copied;
}

/// <summary>The Windows clipboard, in a format of the app's own, so a text or a picture copied elsewhere is left alone until this is used.</summary>
internal sealed class WpfVideoClipboard : IVideoClipboard
{
    /// <summary>The clipboard format's name.</summary>
    [System.ComponentModel.Localizable(false)]
    private const string Format = "CashGear.Capture.VideoEditorItems";

    public byte[]? Read()
    {
        try
        {
            return System.Windows.Clipboard.GetDataObject()?.GetData(Format) is string text ? System.Text.Encoding.UTF8.GetBytes(text) : null;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return null;
        }
    }

    public void Write(byte[] copied)
    {
        try
        {
            var data = new System.Windows.DataObject();
            data.SetData(Format, System.Text.Encoding.UTF8.GetString(copied));
            System.Windows.Clipboard.SetDataObject(data, copy: true);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // Another program has the clipboard just now: nothing was copied, as if the key hadn't been pressed.
        }
    }
}

/// <summary>What every video editor window shares.</summary>
public sealed record VideoEditorServices(
    ProjectExporter Exporter,
    MediaPreviews Previews,
    ISettingsService Settings,
    IAppStateStore State,
    AppPaths Paths,
    IShell Shell,
    ILoggerFactory Loggers,
    Func<IAudioOutput?> AudioOutput,
    VideoDecoding Decoding = VideoDecoding.Hardware,
    ITranscriber? Transcriber = null,
    WhisperModelStore? Models = null,
    IFeatureGate? Features = null,
    IPresetStore? Presets = null,
    WebcamMasks? Masks = null,
    SoundLevelStore? Levels = null,
    CashGear.Capture.Inference.ITranslators? Translators = null,
    TemplateLibrary? TemplateStore = null,
    ProxyStore? Proxies = null,
    IVideoClipboard? Clipboard = null)
{
    /// <summary>Saved templates and media on this PC, in <c>%AppData%\CashGear\Capture\Library</c>.</summary>
    public TemplateLibrary Library => TemplateStore ?? new TemplateLibrary(Path.Combine(Paths.SettingsDirectory, "Library"));

    /// <summary>Which Pro features may be used (all of them in this version).</summary>
    public IFeatureGate Gate => Features ?? FeatureGate.Open;

    /// <summary>Where embedded media is unpacked and thumbnails are kept.</summary>
    public string CacheDirectory => Path.Combine(Paths.DataDirectory, "VideoEditor");
}

/// <summary>A row header of the timeline: the track's name, its switches and (for sound) its volume.</summary>
public sealed partial class TrackHeaderViewModel(VideoEditorViewModel owner, Track track, double height) : ObservableObject
{
    public Track Track { get; private set; } = track;

    public string Name => TrackNames.Display(Track.Name);

    public double Height { get; } = height;

    /// <summary>The track's sound level in percent (0 to 200); a drag of the slider is one undo step.</summary>
    public double Volume
    {
        get => Math.Round(Track.Volume * 100);
        set => owner.ChangeTrack(Track, t => t with { Volume = Math.Clamp(value, 0, 200) / 100 }, VideoEditorText.UndoTrackVolume, $"volume {Track.Id}");
    }

    /// <summary>The same track after an edit: its switches show the new state (the header stays, so a slider drag goes on).</summary>
    internal void Update(Track track)
    {
        Track = track;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void Delete() => owner.DeleteTrack(Track);

    [RelayCommand]
    private void MoveUp() => owner.MoveTrack(Track.Id, -1);

    [RelayCommand]
    private void MoveDown() => owner.MoveTrack(Track.Id, 1);

    public bool HasPictures => Track.HasPictures;

    public bool HasSound => Track.HasSound;

    public string Kind => Track.Kind switch
    {
        TrackKind.Video => VideoEditorText.KindPicture,
        TrackKind.Webcam => VideoEditorText.KindWebcam,
        TrackKind.Media => VideoEditorText.KindMedia,
        TrackKind.Audio => VideoEditorText.KindSound,
        TrackKind.Annotation => VideoEditorText.KindAnnotations,
        TrackKind.Caption => VideoEditorText.KindCaptions,
        TrackKind.Layout => VideoEditorText.KindLayout,
        _ => VideoEditorText.KindEffects,
    };

    public bool IsMuted
    {
        get => Track.Muted;
        set => owner.ChangeTrack(Track, t => t with { Muted = value }, value ? VideoEditorText.UndoMute : VideoEditorText.UndoUnmute);
    }

    public bool IsSolo
    {
        get => Track.Solo;
        set => owner.ChangeTrack(Track, t => t with { Solo = value }, value ? VideoEditorText.UndoSolo : VideoEditorText.UndoUnsolo);
    }

    public bool IsLocked
    {
        get => Track.Locked;
        set => owner.ChangeTrack(Track, t => t with { Locked = value }, value ? VideoEditorText.UndoLockTrack : VideoEditorText.UndoUnlockTrack);
    }

    public bool IsShown
    {
        get => !Track.Hidden;
        set => owner.ChangeTrack(Track, t => t with { Hidden = !value }, value ? VideoEditorText.UndoShowTrack : VideoEditorText.UndoHideTrack);
    }
}

/// <summary>The look a recording starts with when it opens in the editor.</summary>
internal static class NewRecordingLook
{
    /// <summary>The look's id or name: what was chosen, else the usual one.</summary>
    public static string IdOf(VideoEditorServices services) => services.State.Current.VideoEditor?.DefaultLook ?? Looks.DefaultId;

    public static VideoProject Apply(VideoProject project, VideoEditorServices services)
    {
        var look = Looks.Find(IdOf(services), services.Presets?.Current.SavedLooks);
        if (look is null || look.Id == Looks.NoneId)
        {
            return project;
        }

        // A saved look is part of Pro; the built-in ones aren't.
        return services.Gate.IsEnabled(ProFeature.StudioLook) || Looks.BuiltIn.Any(l => l.Id == look.Id) ? Looks.Apply(project, look) : project;
    }
}

/// <summary>
/// One video editor window's state: the project with its undo history, the timeline, playback and the preview.
/// The preview's render loop calls <see cref="Tick"/> and <see cref="RenderPreview"/>; edits go through
/// <see cref="History"/> so every one can be undone.
/// </summary>
public sealed partial class VideoEditorViewModel : ObservableObject, IDisposable
{
    private readonly VideoEditorServices _services;
    private readonly IVideoEditorDialogs _dialogs;
    private readonly ILogger _logger;
    private bool _disposed;
    private TimelineItem? _shown;
    private TimelineItem? _applied;
    private (float X, float Y, float Scale) _preview = (0, 0, 1);
    private volatile bool _waitingForPictures;
    private PreviewDrag? _drag;
    private int _drags;
    private (ProjectCanvas Canvas, Placement? Screen)? _lookShown;
    private readonly CancellationTokenSource _closing = new();
    private readonly HashSet<string> _masksAsked = [];
    private double? _stopAt;
    private List<SKPoint>? _freehand;
    private bool _awaitingFreehand;

    /// <summary>Motion blur's samples in a preview on the graphics card, and in one drawn in memory, where they cost far more.</summary>
    public const int GpuMotionSamples = 16;

    public const int MemoryMotionSamples = 4;

    public VideoEditorViewModel(VideoProject project, string? projectPath, VideoEditorServices services, IVideoEditorDialogs dialogs)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(services);
        _services = services;
        _dialogs = dialogs;
        _logger = services.Loggers.CreateLogger<VideoEditorViewModel>();
        ProjectPath = projectPath;
        History = new ProjectHistory(project);
        View = new TimelineView();
        Timeline = new TimelineSession(History, View);
        Frames = new PreviewFrames(services.Loggers, services.Decoding) { Proxies = services.Proxies };
        Compositor = new FrameCompositor(Frames) { Masks = services.Masks };
        Engine = new PlaybackEngine(() => History.Project, services.AudioOutput, _logger);
        History.Changed += (_, _) => OnProjectChanged();
        Timeline.Changed += (_, _) => OnTimelineChanged();
        Timeline.SeekRequested += (_, time) => Seek(time);
        Frames.FrameReady += (_, _) =>
        {
            // Pictures decoded ahead don't need a new draw; one the last draw went without does.
            if (_waitingForPictures)
            {
                PreviewInvalidated?.Invoke(this, EventArgs.Empty);
            }
        };
        services.Previews.Changed += OnPreviewsChanged;
        Engine.Ended += (_, _) => OnPropertyChanged(nameof(IsPlaying));
        RebuildTracks();
        RefreshLook();
        MakeMasks();
        Transcript = new TranscriptPanelViewModel(this, services);
        Media = new MediaPanelViewModel(this);
        var state = services.State.Current.VideoEditor;
        Timeline.Magnetic = state?.Magnetic == true;
        _proxyHeight = services.Proxies is null ? 0 : state?.ProxyHeight ?? 0;
        if (services.Proxies is { } proxies)
        {
            proxies.Height = _proxyHeight;
        }

        MakeProxies();
    }

    /// <summary>The height of the preview's smaller copies of large media (0: none).</summary>
    private int _proxyHeight;

    private readonly HashSet<string> _proxiesAsked = [];

    public bool ProxiesOff
    {
        get => _proxyHeight == 0;
        set => SetProxyHeight(value, ProxyStore.Off);
    }

    public bool Proxies1080
    {
        get => _proxyHeight == ProxyStore.Height1080;
        set => SetProxyHeight(value, ProxyStore.Height1080);
    }

    public bool Proxies720
    {
        get => _proxyHeight == ProxyStore.Height720;
        set => SetProxyHeight(value, ProxyStore.Height720);
    }

    private void SetProxyHeight(bool chosen, int height)
    {
        if (!chosen || height == _proxyHeight || _services.Proxies is not { } proxies)
        {
            return;
        }

        _proxyHeight = height;
        proxies.Height = height;
        _ = _services.State.UpdateAsync(s => s with { VideoEditor = (s.VideoEditor ?? new VideoEditorState()) with { ProxyHeight = height } });
        OnPropertyChanged(nameof(ProxiesOff));
        OnPropertyChanged(nameof(Proxies1080));
        OnPropertyChanged(nameof(Proxies720));
        MakeProxies();
        Frames.Update(Project, Timeline.Playhead, Engine.IsPlaying);
        PreviewInvalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Starts making the copies of the project's large media that aren't there yet.</summary>
    private void MakeProxies()
    {
        if (_services.Proxies is not { } proxies || _proxyHeight <= 0)
        {
            return;
        }

        foreach (var clip in Project.Tracks.Where(t => t.HasPictures).SelectMany(t => t.Clips))
        {
            if (Project.Asset(clip.AssetId) is { IsPicture: false } asset && File.Exists(asset.Path) && ProxyStore.Wants(asset.Stream(clip.Stream), _proxyHeight)
                && !proxies.IsReady(asset, clip.Stream, _proxyHeight) && _proxiesAsked.Add($"{asset.Path}|{clip.Stream}|{_proxyHeight}"))
            {
                _ = MakeProxyAsync(proxies, asset, clip.Stream, _proxyHeight);
            }
        }
    }

    private async Task MakeProxyAsync(ProxyStore proxies, MediaAsset asset, int stream, int height)
    {
        var progress = new Progress<double>(done => WorkText = VideoEditorText.MakingProxy(asset.Name, done * 100));
        WorkText = VideoEditorText.MakingProxy(asset.Name, 0);
        try
        {
            await proxies.EnsureAsync(asset, stream, height, progress, _closing.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e) when (e is FfmpegException or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            _proxiesAsked.Remove($"{asset.Path}|{stream}|{height}");
            _dialogs.ShowError(VideoEditorText.ProxyFailed(e.Message));
        }
        finally
        {
            if (!_disposed)
            {
                WorkText = "";
            }
        }

        if (!_disposed)
        {
            Frames.Update(Project, Timeline.Playhead, Engine.IsPlaying);
            PreviewInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>The timeline's ripple mode, from the View menu and its button; remembered.</summary>
    public bool IsMagnetic
    {
        get => Timeline.Magnetic;
        set
        {
            if (Timeline.Magnetic != value)
            {
                Timeline.Magnetic = value;
                _ = _services.State.UpdateAsync(s => s with { VideoEditor = (s.VideoEditor ?? new VideoEditorState()) with { Magnetic = value } });
                OnPropertyChanged();
            }
        }
    }

    private IVideoClipboard Clipboard => _services.Clipboard ?? MemoryVideoClipboard.Shared;

    /// <summary>Ctrl+C: the selected items, with what is linked and grouped with them.</summary>
    [RelayCommand]
    private void Copy()
    {
        var ids = Selection();
        if (ids.Count > 0)
        {
            Clipboard.Write(ClipboardEdits.Copy(Project, ids));
        }
    }

    /// <summary>Ctrl+X: copied, and gone (in the ripple mode, with their time).</summary>
    [RelayCommand]
    private void CutItems()
    {
        var ids = Selection();
        if (ids.Count > 0)
        {
            Clipboard.Write(ClipboardEdits.Copy(Project, ids));
            StopPlaying();
            History.Apply(VideoEditorText.UndoCut, RemoveItems(ids));
            Timeline.Select(null);
        }
    }

    /// <summary>Ctrl+V: what was copied, at the playhead, selected.</summary>
    [RelayCommand]
    private void Paste()
    {
        if (Clipboard.Read() is not { } copied)
        {
            return;
        }

        StopPlaying();
        var (project, ids) = ClipboardEdits.Paste(Project, copied, Timeline.Playhead);
        if (ids.Count > 0)
        {
            History.Apply(VideoEditorText.UndoPaste, project);
            Timeline.Select(ids[0]);
            foreach (var id in ids.Skip(1))
            {
                Timeline.ToggleSelected(id);
            }
        }
    }

    /// <summary>Ctrl+G: the items chosen, as one.</summary>
    [RelayCommand]
    private void GroupItems()
    {
        var (project, group) = GroupEdits.Group(Project, Timeline.SelectedIds);
        if (group is not null)
        {
            History.Apply(VideoEditorText.UndoGroup, project);
            OnPropertyChanged(nameof(SelectionText));
            TimelineInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Ctrl+Shift+G: each on its own again.</summary>
    [RelayCommand]
    private void UngroupItems()
    {
        var after = GroupEdits.Ungroup(Project, Timeline.SelectedIds);
        if (!ReferenceEquals(after, Project))
        {
            History.Apply(VideoEditorText.UndoUngroup, after);
            TimelineInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>What Copy and Group act on: what is selected, with its linked clips.</summary>
    private HashSet<string> Selection()
    {
        var ids = new HashSet<string>();
        foreach (var id in Timeline.SelectedIds)
        {
            ids.UnionWith(TimelineEdits.Linked(Project, id));
        }

        return ids;
    }

    /// <summary>Items gone, with their time in the ripple mode.</summary>
    private VideoProject RemoveItems(IReadOnlyCollection<string> ids)
    {
        if (!Timeline.Magnetic)
        {
            return TimelineEdits.Delete(Project, ids);
        }

        var result = Project;
        foreach (var span in ids.Select(id => Project.Find(id)?.Item).OfType<TimelineItem>().Select(i => (i.Start, i.End)).Distinct().OrderByDescending(s => s.Start))
        {
            result = TimelineEdits.DeleteRange(result, span.Start, span.End);
        }

        return result;
    }

    /// <summary>M: a marker at the playhead, named in turn.</summary>
    [RelayCommand]
    private void AddMarker()
    {
        var (project, id) = MarkerEdits.Add(Project, Times.FrameStart(Timeline.Playhead, Project.Canvas.Fps), VideoEditorText.MarkerName((Project.Markers?.Count ?? 0) + 1));
        History.Apply(VideoEditorText.UndoAddMarker, project);
        Media.SelectMarker(id);
    }

    [RelayCommand]
    private void NextMarker()
    {
        if (MarkerEdits.Next(Project, Timeline.Playhead, backward: false) is { } time)
        {
            StopPlaying();
            Seek(time);
        }
    }

    [RelayCommand]
    private void PreviousMarker()
    {
        if (MarkerEdits.Next(Project, Timeline.Playhead, backward: true) is { } time)
        {
            StopPlaying();
            Seek(time);
        }
    }

    internal void RenameMarker(TimelineMarker marker)
    {
        if (_dialogs.AskName(VideoEditorText.RenameMarkerTitle, marker.Name) is { Length: > 0 } name)
        {
            History.Apply(VideoEditorText.UndoMarker, MarkerEdits.Rename(Project, marker.Id, name));
        }
    }

    internal void MoveMarkerHere(TimelineMarker marker) =>
        History.Apply(VideoEditorText.UndoMarker, MarkerEdits.MoveTo(Project, marker.Id, Times.FrameStart(Timeline.Playhead, Project.Canvas.Fps)));

    internal void RemoveMarker(TimelineMarker marker) => History.Apply(VideoEditorText.UndoMarker, MarkerEdits.Remove(Project, marker.Id));

    /// <summary>Alt+↑/↓ on the timeline, and a header's menu: the selected item's track a row up or down.</summary>
    public bool MoveTrack(string trackId, int direction)
    {
        StopPlaying();
        var after = TrackEdits.Reorder(Project, trackId, direction);
        if (ReferenceEquals(after, Project))
        {
            return false;
        }

        History.Apply(VideoEditorText.UndoMoveTrack, after);
        return true;
    }

    /// <summary>A track header dragged onto another's row.</summary>
    public void MoveTrackTo(string trackId, string toTrackId)
    {
        var after = TrackEdits.MoveTo(Project, trackId, toTrackId);
        if (!ReferenceEquals(after, Project))
        {
            StopPlaying();
            History.Apply(VideoEditorText.UndoMoveTrack, after);
        }
    }

    /// <summary>Alt+↑/↓: the track of what is selected.</summary>
    public bool MoveSelectedTrack(int direction) =>
        Timeline.SelectedId is { } id && Project.Find(id)?.Track is { } track && MoveTrack(track.Id, direction);

    /// <summary>The media panel beside the preview.</summary>
    public MediaPanelViewModel Media { get; }

    /// <summary>The transcript panel beside the preview.</summary>
    public TranscriptPanelViewModel Transcript { get; }

    /// <summary>The preview should be drawn again (raised on any thread).</summary>
    public event EventHandler? PreviewInvalidated;

    /// <summary>The timeline should be drawn again (raised on any thread).</summary>
    public event EventHandler? TimelineInvalidated;

    public ProjectHistory History { get; }

    public TimelineSession Timeline { get; }

    public TimelineView View { get; }

    public PlaybackEngine Engine { get; }

    public PreviewFrames Frames { get; }

    public FrameCompositor Compositor { get; }

    public MediaPreviews Previews => _services.Previews;

    public VideoProject Project => History.Project;

    public ObservableCollection<TrackHeaderViewModel> Tracks { get; } = [];

    /// <summary>The selected item's settings (the properties panel).</summary>
    public ObservableCollection<EditParameter> Properties { get; } = [];

    /// <summary>The project's look and canvas: the properties panel when nothing is selected.</summary>
    public ObservableCollection<EditParameter> LookProperties { get; } = [];

    /// <summary>What is being worked out behind the scenes, in words (empty when nothing is).</summary>
    [ObservableProperty]
    private string _workText = "";

    [ObservableProperty]
    private string _propertiesTitle = "";

    public bool HasProperties => Properties.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    private string? _projectPath;

    /// <summary>Where the preview draws: the graphics card or the processor (set by the window).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Status))]
    private string _previewBackend = "";

    public string Name => ProjectPath is { } path ? Path.GetFileNameWithoutExtension(path)
        : Project.Assets.FirstOrDefault() is { } asset ? Path.GetFileNameWithoutExtension(asset.Path)
        : VideoEditorText.Untitled;

    public string Title => History.IsDirty ? VideoEditorText.WindowTitleUnsaved(Name) : VideoEditorText.WindowTitle(Name);

    public bool IsDirty => History.IsDirty;

    public bool IsPlaying => Engine.IsPlaying;

    public double Position => Timeline.Playhead;

    public string TimeText => $"{TimelineRenderer.Format(Position, Project.Canvas.Fps, frames: true)} / {TimelineRenderer.Format(Project.Duration, Project.Canvas.Fps, frames: true)}";

    public string Status => Frames.IsHardware
        ? VideoEditorText.StatusLineDecodingOnGpu(Project.Canvas.Width, Project.Canvas.Height, Project.Canvas.Fps, PreviewBackend)
        : VideoEditorText.StatusLine(Project.Canvas.Width, Project.Canvas.Height, Project.Canvas.Fps, PreviewBackend);

    public string UndoText => History.UndoName is { } name ? VideoEditorText.UndoNamed(name) : CommonText.Undo;

    public string RedoText => History.RedoName is { } name ? VideoEditorText.RedoNamed(name) : CommonText.Redo;

    /// <summary>What is selected, in words (empty when nothing is).</summary>
    public string SelectionText
    {
        get
        {
            if (Timeline.Selected is not { } item)
            {
                return Timeline.Range is { } range
                    ? VideoEditorText.SelectionRange(Format(range.From), Format(range.To), range.To - range.From)
                    : "";
            }

            var text = VideoEditorText.SelectionItem(Format(item.Start), Format(item.End), item.Duration);
            if (item is Clip clip)
            {
                var asset = Project.Asset(clip.AssetId);
                text = VideoEditorText.SelectionClip(asset?.Name ?? VideoEditorText.MissingMedia, text, Format(clip.SourceIn), Format(clip.SourceOut));
            }

            return text;
        }
    }

    public bool HasRange => Timeline.Range is not null;

    /// <summary>How far the track headers move up with the rows (Shift+wheel on the timeline).</summary>
    public double RowsOffset => -View.ScrollRows;

    /// <summary>The timeline's rows were scrolled: the headers follow.</summary>
    public void RowsScrolled() => OnPropertyChanged(nameof(RowsOffset));

    /// <summary>The selection's outline on the preview (the window sets the high-contrast one).</summary>
    public SKColor SelectionColor { get; set; } = new(0xFF, 0xB0, 0x20);

    /// <summary>Selects item <paramref name="id"/> and scrolls the timeline to it (a screen reader selecting it).</summary>
    public void SelectItem(string id)
    {
        Timeline.Reveal(id);
        Timeline.Select(id);
        RowsScrolled();
        TimelineInvalidated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Tab on the timeline: the next item (<paramref name="backward"/>: the previous), scrolled into view. False when there is none.</summary>
    public bool SelectNextItem(bool backward)
    {
        if (!Timeline.SelectNext(backward))
        {
            return false;
        }

        RowsScrolled();
        TimelineInvalidated?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>↑/↓ on the timeline: the selected item to the track above (<paramref name="direction"/> -1) or below (+1).</summary>
    public bool MoveToTrack(int direction)
    {
        StopPlaying();
        if (!Timeline.MoveSelectedToTrack(direction))
        {
            return false;
        }

        RowsScrolled();
        return true;
    }

    /// <summary>Alt+←/→: the selected item <paramref name="frames"/> frames later (earlier when negative).</summary>
    public bool MoveInTime(int frames)
    {
        StopPlaying();
        return Timeline.MoveSelectedByFrames(frames);
    }

    /// <summary>
    /// The arrow keys on the preview: moves the selected picture, annotation or zoom shown at the playhead by
    /// (<paramref name="dx"/>, <paramref name="dy"/>) output pixels, like a drag; moves one after another are one undo
    /// step. False when nothing selected is shown there to move (or its track is locked).
    /// </summary>
    public bool NudgeOnPreview(double dx, double dy)
    {
        StopPlaying();
        if (Timeline.Selected is not { } item || !item.Covers(Timeline.Playhead) || Project.Find(item.Id)?.Track is not { Locked: false, Hidden: false } track)
        {
            return false;
        }

        TimelineItem moved;
        string name;
        switch (item)
        {
            case ZoomItem zoom:
                moved = PlacementEdits.DragZoom(zoom, Project.Canvas, ZoomFrame(zoom), PreviewGrip.None, dx, dy);
                name = VideoEditorText.UndoMoveZoom;
                break;
            case AnnotationItem annotation:
                moved = AnnotationEdits.Drag(annotation, Timeline.Playhead, PreviewGrip.None, SKPoint.Empty, new SKPoint((float)dx, (float)dy), Project.Canvas.Fps);
                name = VideoEditorText.UndoMoveAnnotation;
                break;
            case TemplateItem template:
                moved = template with { MoveX = Math.Round(template.MoveX + (dx / Project.Canvas.Width), 5), MoveY = Math.Round(template.MoveY + (dy / Project.Canvas.Height), 5) };
                name = VideoEditorText.UndoMoveTemplate;
                break;
            case Clip clip when track.HasPictures:
                moved = clip with { Placement = PlacementEdits.Move(clip.Placement ?? Placement.Fit, Project.Canvas, dx, dy) };
                name = VideoEditorText.UndoMovePicture;
                break;
            default:
                return false;
        }

        _applied = moved;
        History.Apply(name, Project.WithItem(moved), $"nudge-preview {item.Id}");
        return true;
    }

    /// <summary>The media the project uses that can't be found.</summary>
    public IReadOnlyList<MediaAsset> MissingMedia => [.. Project.Assets.Where(a => !File.Exists(a.Path))];

    /// <summary>Moves the playhead (and playback, if playing) to <paramref name="time"/>.</summary>
    public void Seek(double time)
    {
        time = Math.Clamp(time, 0, Project.Duration);
        Timeline.Playhead = time;
        Engine.Seek(time);
        Frames.Update(Project, time, Engine.IsPlaying);
        PreviewInvalidated?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(Position));
        OnPropertyChanged(nameof(TimeText));
        Transcript.PlayheadMoved(time);
        RefreshMotionPanel();
    }

    /// <summary>The panel shows an item's motion at the playhead: when the item moves, it is made again as the playhead does.</summary>
    private void RefreshMotionPanel()
    {
        if (!Engine.IsPlaying && Timeline.Selected is { } item && (MotionEdits.Of(item)?.KeyTimes().Count > 0 || item is AnnotationItem { Keyframes.Count: > 1 }))
        {
            _shown = null;
            _applied = null;
            RefreshProperties();
        }
    }

    /// <summary>Plays from <paramref name="from"/> and stops at <paramref name="to"/> (a row of a review list).</summary>
    public void PlayRange(double from, double to)
    {
        StopPlaying();
        Seek(from);
        _stopAt = to;
        PlayPause();
    }

    /// <summary>The render loop's step (UI thread): the playhead follows playback, and the decoders follow the playhead.</summary>
    public void Tick()
    {
        if (_disposed)
        {
            return;
        }

        if (Engine.IsPlaying)
        {
            var position = Math.Min(Engine.Position, Project.Duration);
            var ended = Engine.CheckEnd();
            Timeline.Playhead = ended ? Project.Duration : position;
            View.Reveal(Timeline.Playhead);
            OnPropertyChanged(nameof(Position));
            OnPropertyChanged(nameof(TimeText));
            if (ended)
            {
                OnPropertyChanged(nameof(IsPlaying));
            }

            Transcript.PlayheadMoved(Timeline.Playhead);
            if (_stopAt is { } stop && Timeline.Playhead >= stop)
            {
                _stopAt = null;
                PlayPause();
            }
        }

        Frames.Update(Project, Timeline.Playhead, Engine.IsPlaying);
    }

    /// <summary>
    /// Draws the frame at the playhead into a <paramref name="width"/> × <paramref name="height"/> pixel preview,
    /// letterboxed; returns how many pictures weren't decoded yet.
    /// </summary>
    public int RenderPreview(SKCanvas canvas, int width, int height, SKColor surround)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        canvas.Clear(surround);
        var size = Project.Canvas;
        var scale = Math.Min((float)width / size.Width, (float)height / size.Height);
        if (scale <= 0)
        {
            return 0;
        }

        canvas.Save();
        _preview = ((width - (size.Width * scale)) / 2, (height - (size.Height * scale)) / 2, scale);
        canvas.Translate(_preview.X, _preview.Y);
        canvas.Scale(scale);
        canvas.ClipRect(SKRect.Create(size.Width, size.Height));
        var zoomShown = SelectedZoom();
        var result = Compositor.Render(canvas, zoomShown is null ? Project : WithoutZooms(Project), Timeline.Playhead);
        if (_freehand is { Count: > 1 } stroke)
        {
            using var pen = new SKPaint { Color = SelectionColor, Style = SKPaintStyle.Stroke, StrokeWidth = 3 / scale, IsAntialias = true };
            for (var i = 1; i < stroke.Count; i++)
            {
                canvas.DrawLine(stroke[i - 1], stroke[i], pen);
            }
        }

        if (zoomShown is not null)
        {
            DrawSelection(canvas, ZoomFrame(zoomShown), scale, corner: true, SelectionColor);
        }
        else if (!Engine.IsPlaying && Timeline.Selected is { } selected && selected.Covers(Timeline.Playhead))
        {
            if (selected is Clip clip && Project.Find(clip.Id)?.Track is { HasPictures: true })
            {
                DrawSelection(canvas, PlacementEdits.Area(Project, clip, Timeline.Playhead), scale, corner: true, SelectionColor);
            }
            else if (selected is TemplateItem template)
            {
                DrawSelection(canvas, TemplateEdits.Bounds(template, Project, Timeline.Playhead), scale, corner: false, SelectionColor);
            }
            else if (selected is AnnotationItem annotation)
            {
                DrawSelection(canvas, annotation, Timeline.Playhead, scale, SelectionColor, size);
            }
        }

        canvas.Restore();
        _waitingForPictures = result.Missing > 0;
        return result.Missing;
    }

    /// <summary>What a press on the preview at (<paramref name="x"/>, <paramref name="y"/>) would take hold of (pixels of the last frame drawn).</summary>
    public PreviewHit? PreviewHitAt(double x, double y) =>
        PlacementEdits.HitTest(Project, Timeline.Playhead, ToCanvas(x, y), PlacementEdits.GripSize / _preview.Scale);

    /// <summary>A press on the preview: selects the picture under it, to move it (or resize it by its grip).</summary>
    public void PreviewPointerDown(double x, double y)
    {
        StopPlaying();
        if (_awaitingFreehand)
        {
            _freehand = [ToCanvas(x, y)];
            return;
        }

        if (SelectedZoom() is { } zoom && ZoomFrame(zoom) is var frame && ZoomGrip(frame, ToCanvas(x, y)) is { } zoomGrip)
        {
            _drag = new PreviewDrag(Project, zoom, ToCanvas(x, y), frame, zoomGrip, ++_drags);
            return;
        }

        if (PreviewHitAt(x, y) is not { } hit || Project.Find(hit.ItemId)?.Item is not { } item)
        {
            Timeline.Select(null);
            return;
        }

        Timeline.Select(item.Id);
        var area = item is Clip clip ? PlacementEdits.Area(Project, clip, Timeline.Playhead) : default;
        _drag = new PreviewDrag(Project, item, ToCanvas(x, y), area, hit.Grip, ++_drags);
        PreviewInvalidated?.Invoke(this, EventArgs.Empty);
    }

    public void PreviewPointerMove(double x, double y)
    {
        if (_freehand is { } stroke)
        {
            var at = ToCanvas(x, y);
            if (SKPoint.Distance(stroke[^1], at) >= 2)
            {
                stroke.Add(at);
                PreviewInvalidated?.Invoke(this, EventArgs.Empty);
            }

            return;
        }

        if (_drag is not { } drag)
        {
            return;
        }

        var point = ToCanvas(x, y);
        TimelineItem item;
        string name;
        if (drag.Item is AnnotationItem annotation)
        {
            item = AnnotationEdits.Drag(annotation, Timeline.Playhead, drag.Grip, drag.Start, point, Project.Canvas.Fps);
            name = drag.Grip == PreviewGrip.None ? VideoEditorText.UndoMoveAnnotation : VideoEditorText.UndoResizeAnnotation;
        }
        else if (drag.Item is Clip clip)
        {
            var placement = clip.Placement ?? Placement.Fit;
            var resize = drag.Grip != PreviewGrip.None;
            name = resize ? VideoEditorText.UndoResizePicture : VideoEditorText.UndoMovePicture;
            if (clip.Motion is { Animated: true } motion)
            {
                // While it animates, a drag makes a keyframe at the playhead, as the panel's values do.
                var state = motion.At(Timeline.Playhead - clip.Start);
                var size = Project.Canvas;
                item = clip with
                {
                    Motion = resize
                        ? MotionEdits.SetValue(motion, MotionLane.Scale, Timeline.Playhead - clip.Start, Math.Clamp(state.Scale * Math.Max(1e-3, Math.Max((point.X - drag.Area.MidX) / Math.Max(1, drag.Area.Width / 2), (point.Y - drag.Area.MidY) / Math.Max(1, drag.Area.Height / 2))), 0.01, 8), size.Fps)
                        : MotionEdits.SetValue(MotionEdits.SetValue(motion, MotionLane.X, Timeline.Playhead - clip.Start, state.X + ((point.X - drag.Start.X) / size.Width), size.Fps), MotionLane.Y, Timeline.Playhead - clip.Start, state.Y + ((point.Y - drag.Start.Y) / size.Height), size.Fps),
                };
            }
            else
            {
                item = clip with
                {
                    Placement = resize
                        ? PlacementEdits.Resize(placement, drag.Area, point)
                        : PlacementEdits.Move(placement, Project.Canvas, point.X - drag.Start.X, point.Y - drag.Start.Y),
                };
            }

        }
        else if (drag.Item is TemplateItem template)
        {
            item = template with
            {
                MoveX = Math.Round(template.MoveX + ((point.X - drag.Start.X) / Project.Canvas.Width), 5),
                MoveY = Math.Round(template.MoveY + ((point.Y - drag.Start.Y) / Project.Canvas.Height), 5),
            };
            name = VideoEditorText.UndoMoveTemplate;
        }
        else if (drag.Item is ZoomItem zoom)
        {
            item = PlacementEdits.DragZoom(zoom, Project.Canvas, drag.Area, drag.Grip, point.X - drag.Start.X, point.Y - drag.Start.Y);
            name = drag.Grip == PreviewGrip.None ? VideoEditorText.UndoMoveZoom : VideoEditorText.UndoResizeZoom;
        }
        else
        {
            return;
        }

        _applied = item;
        History.Apply(name, drag.Base.WithItem(item), $"preview {drag.Number}");
    }

    public void PreviewPointerUp()
    {
        _drag = null;
        if (_freehand is { } stroke)
        {
            _freehand = null;
            CancelFreehand();
            if (stroke.Count >= 2)
            {
                var (project, id) = AnnotationEdits.AddFreehand(Project, stroke, Timeline.Playhead);
                History.Apply(VideoEditorText.UndoAddCallout, project);
                Timeline.Select(id);
            }

            PreviewInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Esc while waiting to draw a stroke: not any more.</summary>
    private void CancelFreehand()
    {
        if (_awaitingFreehand)
        {
            _awaitingFreehand = false;
            _freehand = null;
            WorkText = "";
            PreviewInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Changes a track (mute, solo, lock, show, volume) as one undo step (merged with others of the same key).</summary>
    public void ChangeTrack(Track track, Func<Track, Track> change, string name, string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(change);
        if (Project.Track(track.Id) is { } current)
        {
            History.Apply(name, Project.WithTrack(change(current)), mergeKey);
        }
    }

    /// <summary>Removes a track and everything on it.</summary>
    public void DeleteTrack(Track track)
    {
        ArgumentNullException.ThrowIfNull(track);
        StopPlaying();
        History.Apply(VideoEditorText.UndoDeleteTrack, TrackEdits.Remove(Project, track.Id));
    }

    /// <summary>Asks about unsaved changes before the window closes. False keeps it open.</summary>
    public async Task<bool> ConfirmCloseAsync()
    {
        if (!IsDirty)
        {
            return true;
        }

        return _dialogs.AskSaveChanges(Name) switch
        {
            SaveChangesChoice.Save => await SaveProjectAsync(),
            SaveChangesChoice.Discard => true,
            _ => false,
        };
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _closing.Cancel();
        _closing.Dispose();
        _services.Previews.Changed -= OnPreviewsChanged;
        Media.Release();
        Engine.Dispose();
        Frames.Dispose();
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (Engine.IsPlaying)
        {
            _stopAt = null;
            Engine.Pause();
            Timeline.Playhead = Times.FrameStart(Engine.Position, Project.Canvas.Fps);
        }
        else
        {
            var from = Timeline.Playhead >= Project.Duration - 0.01 ? 0 : Timeline.Playhead;
            Frames.Update(Project, from, playing: true);
            Engine.Play(from);
        }

        OnPropertyChanged(nameof(IsPlaying));
        OnPropertyChanged(nameof(Status));
        PreviewInvalidated?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void StepForward() => Step(1);

    [RelayCommand]
    private void StepBack() => Step(-1);

    [RelayCommand]
    private void GoToStart() => Seek(0);

    [RelayCommand]
    private void GoToEnd() => Seek(Project.Duration);

    /// <summary>Cuts every clip under the playhead (S).</summary>
    [RelayCommand]
    private void Split()
    {
        StopPlaying();
        History.Apply(VideoEditorText.UndoSplit, TimelineEdits.Split(Project, Timeline.Playhead));
    }

    /// <summary>Delete: the marked range out of every track (closing the gap), else the selected items and what is linked to them (in the ripple mode, with their time).</summary>
    [RelayCommand]
    private void Delete()
    {
        StopPlaying();
        if (Timeline.Range is { } range)
        {
            History.Apply(VideoEditorText.UndoCutOut, TimelineEdits.DeleteRange(Project, range.From, range.To));
            Timeline.SetRange(null);
            Seek(Math.Min(range.From, Project.Duration));
        }
        else if (Timeline.SelectedId is not null)
        {
            History.Apply(VideoEditorText.UndoDelete, RemoveItems(Timeline.SelectedIds));
            Timeline.Select(null);
        }
    }

    /// <summary>Shift+Delete: the selected item and its time, from every track.</summary>
    [RelayCommand]
    private void RippleDelete()
    {
        StopPlaying();
        if (Timeline.SelectedId is { } id)
        {
            History.Apply(VideoEditorText.UndoRippleDelete, TimelineEdits.RippleDelete(Project, id));
            Timeline.Select(null);
        }
        else
        {
            Delete();
        }
    }

    [RelayCommand]
    private void MarkIn() => Timeline.SetRange((Timeline.Playhead, Math.Max(Timeline.Range?.To ?? Project.Duration, Timeline.Playhead)));

    [RelayCommand]
    private void MarkOut() => Timeline.SetRange((Math.Min(Timeline.Range?.From ?? 0, Timeline.Playhead), Timeline.Playhead));

    [RelayCommand]
    private void ClearSelection()
    {
        CancelFreehand();
        Timeline.SetRange(null);
        Timeline.Select(null);
    }

    /// <summary>Ctrl+[: the selected item's start to the playhead.</summary>
    [RelayCommand]
    private void TrimStartToPlayhead()
    {
        StopPlaying();
        Timeline.TrimSelectedToPlayhead(ItemEdge.Start);
    }

    /// <summary>Ctrl+]: the selected item's end to the playhead.</summary>
    [RelayCommand]
    private void TrimEndToPlayhead()
    {
        StopPlaying();
        Timeline.TrimSelectedToPlayhead(ItemEdge.End);
    }

    [RelayCommand]
    private void Undo()
    {
        StopPlaying();
        History.Undo();
    }

    [RelayCommand]
    private void Redo()
    {
        StopPlaying();
        History.Redo();
    }

    [RelayCommand]
    private void ZoomIn() => View.ZoomAt(View.X(Timeline.Playhead), 1.5);

    [RelayCommand]
    private void ZoomOut() => View.ZoomAt(View.X(Timeline.Playhead), 1 / 1.5);

    [RelayCommand]
    private void ZoomFit() => View.Fit(Project.Duration);

    [RelayCommand]
    private Task<bool> SaveProjectAsync() => SaveAsync(ProjectPath, embed: false);

    [RelayCommand]
    private Task<bool> SaveProjectAsAsync() => SaveAsync(null, embed: false);

    /// <summary>Saves a copy of the project with its media inside, to take it to another PC.</summary>
    [RelayCommand]
    private Task<bool> SaveWithMediaAsync() => SaveAsync(null, embed: true);

    [RelayCommand]
    private void Export()
    {
        StopPlaying();
        var state = _services.State.Current.VideoEditor ?? new VideoEditorState();
        var folder = state.ExportFolder is { } remembered && Directory.Exists(remembered)
            ? remembered
            : _services.Paths.ResolveCaptureDirectory(_services.Settings.Current.Output);
        var viewModel = new ExportViewModel(
            Project,
            Name,
            folder,
            _services.Exporter,
            _services.Settings,
            _services.State,
            _services.Shell,
            _services.Loggers.CreateLogger<ExportViewModel>(),
            MarkedRange(),
            _services.Gate);
        _dialogs.ShowExport(viewModel);
    }

    /// <summary>View › Media: shows or hides the panel beside the preview.</summary>
    [RelayCommand]
    private void ToggleMedia() => Media.IsShown = !Media.IsShown;

    /// <summary>Insert › Picture, Video or Sound: a file added to the project and put on the timeline at the playhead.</summary>
    [RelayCommand]
    private async Task InsertMediaAsync()
    {
        if (PickMediaFile(VideoEditorText.ImportMediaTitle, VideoEditorText.AnyMediaFilter) is { } path)
        {
            await ImportFilesAsync([path], Position, null);
        }
    }

    [RelayCommand]
    private void InsertColour() => AddSolidAtPlayhead();

    /// <summary>A media file chosen by the user, or null.</summary>
    internal string? PickMediaFile(string title, string filter) => _dialogs.PickMedia(title, filter, Folder());

    /// <summary>
    /// Reads files into the project, each as one undo step, and (when <paramref name="at"/> is given) puts them on the
    /// timeline one after another from there, on <paramref name="trackId"/> when it has room. Says which it couldn't read.
    /// </summary>
    public async Task ImportFilesAsync(IReadOnlyList<string> paths, double? at, string? trackId)
    {
        ArgumentNullException.ThrowIfNull(paths);
        StopPlaying();
        foreach (var path in paths)
        {
            MediaAsset asset;
            try
            {
                var known = Project.Assets.FirstOrDefault(a => string.Equals(a.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) && a.Streams.Count > 0);
                asset = known ?? await Task.Run(() => ProjectFactory.ReadMedia(path));
            }
            catch (Exception e) when (e is FfmpegException or IOException or InvalidDataException or UnauthorizedAccessException)
            {
                _dialogs.ShowError(VideoEditorText.CantBeOpened(Path.GetFileName(path), e.Message));
                continue;
            }

            if (at is { } time)
            {
                var id = AddMediaAsset(asset, time, trackId);
                if (id is not null && Project.Find(id)?.Item is { } added)
                {
                    at = added.End;
                }
            }
            else if (Project.Asset(asset.Id) is null)
            {
                History.Apply(VideoEditorText.UndoImportMedia, Project.WithAsset(asset));
            }
        }
    }

    /// <summary>Media of the project on the timeline at <paramref name="at"/> (a row of the panel dragged there, or Enter on it).</summary>
    public string? AddMediaAt(string assetId, double at, string? trackId) =>
        Project.Asset(assetId) is { } asset ? AddMediaAsset(asset, at, trackId) : null;

    private string? AddMediaAsset(MediaAsset asset, double at, string? trackId)
    {
        StopPlaying();
        var (project, id) = MediaEdits.Add(Project, asset, at, trackId);
        if (id is null)
        {
            _dialogs.ShowError(VideoEditorText.CantBeOpened(asset.Name, VideoEditorText.HasNoSound(asset.Name)));
            return null;
        }

        History.Apply(VideoEditorText.UndoAddMedia, project);
        Timeline.Select(id);
        if (!Timeline.Selected!.Covers(Timeline.Playhead))
        {
            Seek(Timeline.Selected.Start);
        }

        return id;
    }

    /// <summary>A file out of the project, unless something on the timeline uses it.</summary>
    public void RemoveMedia(string assetId)
    {
        if (Project.Asset(assetId) is not { } asset)
        {
            return;
        }

        if (MediaEdits.IsUsed(Project, assetId))
        {
            _dialogs.ShowError(VideoEditorText.MediaStillUsed(asset.Name));
            return;
        }

        History.Apply(VideoEditorText.UndoRemoveMedia, MediaEdits.RemoveAsset(Project, assetId));
    }

    /// <summary>A template from the panel at <paramref name="at"/> (a row dragged there, or Enter at the playhead), selected so its texts can be typed.</summary>
    public string? InsertTemplate(TitleTemplate template, double at)
    {
        ArgumentNullException.ThrowIfNull(template);
        if (!Allowed(ProFeature.Templates))
        {
            return null;
        }

        StopPlaying();
        var (project, id) = TemplateEdits.Insert(Project, template, at, _services.Library.AssetsFolder);
        History.Apply(VideoEditorText.UndoAddTemplate, project);
        Timeline.Select(id);
        if (!Timeline.Selected!.Covers(Timeline.Playhead))
        {
            Seek(Timeline.Selected.Start);
        }

        return id;
    }

    /// <summary>The library the panel shows.</summary>
    internal TemplateLibrary Library => _services.Library;

    /// <summary>Whether <paramref name="feature"/> may be used, without saying anything.</summary>
    internal bool Enabled(ProFeature feature) => _services.Gate.IsEnabled(feature);

    /// <summary>The selected annotation, the annotations grouped with it, or the selected template, saved in the library under a name; says why not when there is nothing to save.</summary>
    public bool SaveSelectionAsTemplate()
    {
        if (!Allowed(ProFeature.Templates))
        {
            return false;
        }

        var chosen = SelectedForTemplate();
        if (chosen.Count == 0 && Timeline.Selected is not TemplateItem)
        {
            _dialogs.ShowError(VideoEditorText.SaveTemplateNeedsSelection);
            return false;
        }

        if (_dialogs.AskName(VideoEditorText.SaveTemplateTitle, VideoEditorText.MyTemplate) is not { Length: > 0 } name)
        {
            return false;
        }

        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        TitleTemplate template;
        if (Timeline.Selected is TemplateItem placed)
        {
            // As it is now: what its fields say becomes what they say at first.
            var fields = placed.Template.Fields?.Select(f => f with { Default = placed.Text(f) }).ToList();
            template = placed.Template with { Id = Ids.New(), Name = name, Kind = TemplateKind.Saved, Length = placed.FullLength, Fields = fields };
            foreach (var (file, assetId) in placed.AssetIds ?? new Dictionary<string, string>())
            {
                if (Project.Asset(assetId) is { } asset)
                {
                    files[file] = asset.Path;
                }
            }
        }
        else
        {
            template = TemplateEdits.FromItems(Project, chosen, name);
            foreach (var item in chosen)
            {
                if (item.PictureAssetId is { } id && Project.Asset(id) is { } asset)
                {
                    files[asset.Name] = asset.Path;
                }
            }
        }

        try
        {
            _services.Library.Save(template, files);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(VideoEditorText.PackFailed(e.Message));
            return false;
        }

        Media.RefreshTemplates();
        return true;
    }

    private List<AnnotationItem> SelectedForTemplate()
    {
        if (Timeline.Selected is not AnnotationItem first)
        {
            return [];
        }

        return first.Group is { } group
            ? [.. Project.Tracks.SelectMany(t => t.Items.OfType<AnnotationItem>()).Where(a => a.Group == group).OrderBy(a => a.Start)]
            : [first];
    }

    /// <summary>A template out of the library.</summary>
    internal void DeleteSavedTemplate(TitleTemplate template)
    {
        _services.Library.Delete(template.Id);
        Media.RefreshTemplates();
    }

    /// <summary>The library's templates and brand kit as one file, where the user says.</summary>
    internal void ExportPack()
    {
        if (!Allowed(ProFeature.Templates) || _dialogs.AskSavePath(VideoEditorText.ExportPackTitle, VideoEditorText.PackFilter, VideoEditorText.PackName + TemplateLibrary.PackExtension, Folder()) is not { } path)
        {
            return;
        }

        try
        {
            _services.Library.ExportPack(path, _services.Library.Templates(), _services.Library.Brand());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(VideoEditorText.PackFailed(e.Message));
        }
    }

    /// <summary>A pack's templates and files into the library.</summary>
    internal void ImportPack()
    {
        if (!Allowed(ProFeature.Templates) || _dialogs.PickMedia(VideoEditorText.ImportPackTitle, VideoEditorText.PackFilter, Folder()) is not { } path)
        {
            return;
        }

        try
        {
            _services.Library.ImportPack(path);
            Media.RefreshTemplates();
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(VideoEditorText.PackFailed(e.Message));
        }
    }

    /// <summary>A change to the project's brand kit: one undo step, merged by its key. Every template follows.</summary>
    internal void ChangeBrand(string key, Func<BrandKit, BrandKit> change)
    {
        if (Allowed(ProFeature.Templates))
        {
            History.Apply(VideoEditorText.UndoBrand, TemplateEdits.WithBrand(Project, change), $"brand {key}");
        }
    }

    /// <summary>A file chosen for the brand joins the project's media; (its id, or null when none was chosen).</summary>
    internal void PickBrandFile(string? path, string key, Func<BrandKit, string, BrandKit> set)
    {
        if (path is null || !Allowed(ProFeature.Templates))
        {
            return;
        }

        try
        {
            var asset = Project.Assets.FirstOrDefault(a => string.Equals(a.Path, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) && a.Streams.Count > 0) ?? ProjectFactory.ReadMedia(path);
            History.Apply(VideoEditorText.UndoBrand, TemplateEdits.WithBrand(Project.WithAsset(asset), b => set(b, asset.Id)), $"brand {key} {asset.Id}");
        }
        catch (Exception e) when (e is FfmpegException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(VideoEditorText.CantBeOpened(Path.GetFileName(path), e.Message));
        }
    }

    /// <summary>A file for the brand, chosen by the user.</summary>
    internal string? PickBrandPath() => _dialogs.PickMedia(VideoEditorText.BrandPickFile, VideoEditorText.AnyMediaFilter, Folder());

    /// <summary>The logo on the timeline at the playhead, small in a corner, for what is left of the timeline.</summary>
    internal void InsertBrandLogo()
    {
        if (Project.Brand?.LogoAssetId is not { } id || Project.Asset(id) is not { } asset)
        {
            _dialogs.ShowError(VideoEditorText.BrandNoFile);
            return;
        }

        StopPlaying();
        var (project, clipId) = MediaEdits.Add(Project, asset, Position);
        if (clipId is not null && project.Find(clipId)?.Item is Clip { IsFreeze: true } clip)
        {
            project = project.WithItem(clip with { Placement = new Placement(0.9, 0.12, 0.15), FreezeLength = Project.Duration - Position >= TimelineEdits.ShortestItem ? Times.Round(Project.Duration - Position) : clip.FreezeLength });
        }

        History.Apply(VideoEditorText.UndoBrandClip, project);
        Timeline.Select(clipId);
    }

    /// <summary>The intro at the start (what is there moves on), or the outro after the end.</summary>
    internal void InsertBrandClip(bool intro)
    {
        var id = intro ? Project.Brand?.IntroAssetId : Project.Brand?.OutroAssetId;
        if (id is null || Project.Asset(id) is not { } asset)
        {
            _dialogs.ShowError(VideoEditorText.BrandNoFile);
            return;
        }

        StopPlaying();
        var (project, clipId) = intro ? MediaEdits.InsertPushing(Project, asset, 0) : MediaEdits.Add(Project, asset, Project.Duration, Project.Tracks.FirstOrDefault(t => t.Kind == TrackKind.Video)?.Id);
        History.Apply(VideoEditorText.UndoBrandClip, project);
        Timeline.Select(clipId);
    }

    /// <summary>The brand's look on the project.</summary>
    internal void UseBrandLook()
    {
        if (Project.Brand?.Look is { } name && AvailableLooks().FirstOrDefault(l => l.Id == name || l.Name == name) is { } look)
        {
            ApplyLook(look.Id);
        }
    }

    /// <summary>The looks a brand can have.</summary>
    internal IReadOnlyList<StudioLook> LooksToChoose() => AvailableLooks();

    /// <summary>The project's kit kept in the library, its files with it, for other projects on this PC.</summary>
    internal void SaveBrandToLibrary()
    {
        if (!Allowed(ProFeature.Templates))
        {
            return;
        }

        var kit = Project.Brand ?? BrandKit.Default;
        string? Kept(string? assetId) => assetId is not null && Project.Asset(assetId) is { } asset && File.Exists(asset.Path) ? _services.Library.AddAsset(asset.Path) : null;
        try
        {
            _services.Library.SaveBrand(new LibraryBrand(kit with { LogoAssetId = null, IntroAssetId = null, OutroAssetId = null }, Kept(kit.LogoAssetId), Kept(kit.IntroAssetId), Kept(kit.OutroAssetId)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(VideoEditorText.PackFailed(e.Message));
        }
    }

    /// <summary>The kit kept in the library on this project.</summary>
    internal void UseSavedBrand()
    {
        if (!Allowed(ProFeature.Templates))
        {
            return;
        }

        if (_services.Library.Brand() is not { } brand)
        {
            _dialogs.ShowError(VideoEditorText.BrandNoSaved);
            return;
        }

        try
        {
            History.Apply(VideoEditorText.UndoBrand, _services.Library.ApplyBrand(Project, brand));
        }
        catch (Exception e) when (e is FfmpegException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(VideoEditorText.PackFailed(e.Message));
        }
    }

    [RelayCommand]
    private void InsertBubble() => InsertCallout(AnnotationShape.SpeechBubble);

    [RelayCommand]
    private void InsertStep() => InsertCallout(AnnotationShape.StepNumber);

    [RelayCommand]
    private void InsertSpotlight() => InsertCallout(AnnotationShape.Spotlight);

    [RelayCommand]
    private void InsertMagnify() => InsertCallout(AnnotationShape.Magnify);

    /// <summary>Insert › Title or Lower Third: the panel on its templates.</summary>
    [RelayCommand]
    private void ShowTemplates()
    {
        Media.IsShown = true;
        Media.ShownTab = MediaPanelViewModel.TemplatesTabIndex;
    }

    /// <summary>Insert › Freehand: the next drag on the preview draws a stroke. Esc cancels.</summary>
    [RelayCommand]
    private void InsertFreehand()
    {
        if (Allowed(ProFeature.VideoEditorAnnotations))
        {
            StopPlaying();
            _awaitingFreehand = true;
            WorkText = VideoEditorText.DrawFreehandNow;
        }
    }

    private void InsertCallout(AnnotationShape shape)
    {
        if (Allowed(ProFeature.VideoEditorAnnotations))
        {
            Insert(shape);
        }
    }

    /// <summary>A plain colour over the marked range (else from the playhead for 5 s), selected so the panel shows it.</summary>
    public string AddSolidAtPlayhead()
    {
        StopPlaying();
        var (from, length) = Timeline.Range is { } range ? (range.From, range.To - range.From) : (Timeline.Playhead, MediaEdits.DefaultLength);
        var (project, id) = MediaEdits.AddSolid(Project, MediaEdits.DefaultSolid, from, length);
        History.Apply(VideoEditorText.UndoAddColour, project);
        Timeline.SetRange(null);
        Timeline.Select(id);
        return id;
    }

    /// <summary>View › Transcript: shows or hides the panel beside the preview.</summary>
    [RelayCommand]
    private void ToggleTranscript() => Transcript.IsShown = !Transcript.IsShown;

    /// <summary>Insert › Translate: the window that makes a caption track in another language.</summary>
    [RelayCommand]
    private void Translate()
    {
        if (Allowed(ProFeature.Translation))
        {
            OpenTranslate();
        }
    }

    /// <summary>The window that translates; what it makes comes back as a caption track, one undo step. Null when there is nothing to translate.</summary>
    public TranslateViewModel? OpenTranslate()
    {
        StopPlaying();
        var viewModel = new TranslateViewModel(Project, _services.Translators, _services.Transcriber, _services.Models, _services.State, _services.Loggers.CreateLogger<TranslateViewModel>(), AddTranslation);
        if (viewModel.Choices.Count == 0)
        {
            _dialogs.ShowError(VideoEditorText.NothingToTranslate);
            return null;
        }

        _dialogs.ShowTranslate(viewModel);
        return viewModel;
    }

    private void AddTranslation(IReadOnlyList<CaptionItem> captions, string language)
    {
        var (project, _) = CaptionEdits.AddTrack(Project, captions, VideoEditorText.CaptionsInLanguage(TranscribeViewModel.LanguageName(language)));
        History.Apply(VideoEditorText.UndoTranslate, project);
    }

    /// <summary>A transcript of the media on a sound track: the window that makes it; it comes back as one undo step. Null when there is nothing to listen to.</summary>
    public TranscribeViewModel? OpenTranscript()
    {
        StopPlaying();
        if (_services.Transcriber is null || _services.Models is null)
        {
            _dialogs.ShowError(VideoEditorText.SpeechUnavailable);
            return null;
        }

        if (!Project.Tracks.Any(t => t.HasSound && t.Items.Count > 0))
        {
            _dialogs.ShowError(VideoEditorText.NoSoundForCaptions);
            return null;
        }

        var viewModel = new TranscribeViewModel(Project, null, _services.Transcriber, _services.Models, _services.State, _services.Loggers.CreateLogger<TranscribeViewModel>(), (_, _) => { }, AddTranscripts, _services.Levels);
        _dialogs.ShowTranscribe(viewModel);
        return viewModel;
    }

    private void AddTranscripts(IReadOnlyList<MadeTranscript> made)
    {
        var project = Project;
        foreach (var (assetId, transcript) in made)
        {
            project = TranscriptEdits.WithTranscript(project, assetId, transcript);
        }

        History.Apply(VideoEditorText.UndoMakeTranscript, project);
        Transcript.IsShown = true;
    }

    /// <summary>Whether <paramref name="feature"/> may be used; says it is part of Pro when it can't.</summary>
    internal bool AllowedFeature(ProFeature feature) => Allowed(feature);

    /// <summary>A time as the timeline writes it.</summary>
    internal string FormatTime(double seconds) => Format(seconds);

    /// <summary>Stops playing before an edit.</summary>
    internal void StopPlayingNow() => StopPlaying();

    /// <summary>Says what went wrong.</summary>
    internal void ShowProblem(string message) => _dialogs.ShowError(message);

    /// <summary>Cancelled when the window closes.</summary>
    internal CancellationToken ClosingToken => _closing.Token;

    /// <summary>Whether <paramref name="feature"/> may be used; says it is part of Pro when it can't.</summary>
    private bool Allowed(ProFeature feature)
    {
        if (_services.Gate.IsEnabled(feature))
        {
            return true;
        }

        _dialogs.ShowError(CommonText.ProOnly);
        return false;
    }

    [RelayCommand]
    private void InsertCaption()
    {
        if (Allowed(ProFeature.Captions))
        {
            AddCaption();
        }
    }

    /// <summary>A caption over the marked range (else from the playhead for 3 s), selected so its text can be typed.</summary>
    public string AddCaption()
    {
        StopPlaying();
        var (from, length) = Timeline.Range is { } range ? (range.From, range.To - range.From) : (Timeline.Playhead, CaptionItem.DefaultLength);
        var (project, id) = CaptionEdits.Add(Project, from, length, VideoEditorText.DefaultCaptionText);
        History.Apply(VideoEditorText.UndoAddCaption, project);
        Timeline.SetRange(null);
        Timeline.Select(id);
        if (!Timeline.Selected!.Covers(Timeline.Playhead))
        {
            Seek(from);
        }

        return id;
    }

    [RelayCommand]
    private void Transcribe()
    {
        if (Allowed(ProFeature.Captions))
        {
            OpenTranscribe();
        }
    }

    /// <summary>
    /// Captions from Speech: the window that makes them (over the marked range, if there is one); the captions it
    /// hears come back as a new caption track, one undo step. Null when there is nothing to listen to.
    /// </summary>
    public TranscribeViewModel? OpenTranscribe()
    {
        StopPlaying();
        if (_services.Transcriber is null || _services.Models is null)
        {
            _dialogs.ShowError(VideoEditorText.SpeechUnavailable);
            return null;
        }

        if (!Project.Tracks.Any(t => t.HasSound && t.Items.Count > 0))
        {
            _dialogs.ShowError(VideoEditorText.NoSoundForCaptions);
            return null;
        }

        var viewModel = new TranscribeViewModel(Project, MarkedRange(), _services.Transcriber, _services.Models, _services.State, _services.Loggers.CreateLogger<TranscribeViewModel>(), AddCaptionsFromSpeech);
        _dialogs.ShowTranscribe(viewModel);
        return viewModel;
    }

    /// <summary>File › Import Captions: an SRT or WebVTT file as a caption track of its own.</summary>
    [RelayCommand]
    private void ImportCaptions()
    {
        if (!Allowed(ProFeature.Captions))
        {
            return;
        }

        var path = _dialogs.PickMedia(VideoEditorText.ImportCaptionsTitle, VideoEditorText.SubtitlesFilter, Folder());
        if (path is null)
        {
            return;
        }

        try
        {
            var captions = CaptionEdits.FromCues(SubtitleFiles.Load(path));
            var (project, _) = CaptionEdits.AddTrack(Project, captions, Path.GetFileNameWithoutExtension(path));
            History.Apply(VideoEditorText.UndoImportCaptions, project);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(VideoEditorText.CantBeRead(Path.GetFileName(path), e.Message));
        }
    }

    /// <summary>File › Save Captions: every caption track's captions as an SRT or WebVTT file.</summary>
    [RelayCommand]
    private void SaveCaptions()
    {
        var cues = SubtitleFiles.Cues(Project);
        if (cues.Count == 0)
        {
            _dialogs.ShowError(VideoEditorText.NoCaptionsToSave);
            return;
        }

        var path = _dialogs.AskSavePath(VideoEditorText.SaveCaptionsTitle, VideoEditorText.SaveCaptionsFilter, Name + ".srt", Folder());
        if (path is null)
        {
            return;
        }

        try
        {
            SubtitleFiles.Save(path, cues, string.Equals(Path.GetExtension(path), ".vtt", StringComparison.OrdinalIgnoreCase) ? SubtitleFormat.Vtt : SubtitleFormat.Srt);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _dialogs.ShowError(VideoEditorText.CaptionsNotSaved(e.Message));
        }
    }

    private TimeRange? MarkedRange() => Timeline.Range is { } range && range.To - range.From >= TimelineEdits.ShortestItem ? new TimeRange(range.From, range.To) : null;

    private void AddCaptionsFromSpeech(IReadOnlyList<CaptionItem> captions, string language)
    {
        var name = TranscribeViewModel.LanguageName(language) is { Length: > 0 } heard ? VideoEditorText.CaptionsInLanguage(heard) : null;
        var (project, _) = CaptionEdits.AddTrack(Project, captions, name);
        History.Apply(VideoEditorText.UndoCaptionsFromSpeech, project);
    }

    private void SplitCaption(string id)
    {
        StopPlaying();
        var (project, second) = CaptionEdits.Split(Project, id, Timeline.Playhead);
        if (second is not null)
        {
            History.Apply(VideoEditorText.UndoSplitCaption, project);
        }
    }

    private void MergeCaption(string id)
    {
        StopPlaying();
        var project = CaptionEdits.MergeWithNext(Project, id);
        if (!ReferenceEquals(project, Project))
        {
            History.Apply(VideoEditorText.UndoMergeCaptions, project);
        }
    }

    /// <summary>Changes a caption track's look: one undo step called <paramref name="name"/>, merged by <paramref name="key"/> (not translated).</summary>
    private void ChangeCaptionStyle(string trackId, Func<CaptionStyle, CaptionStyle> change, string key, string name)
    {
        if (Project.Track(trackId) is { } track)
        {
            History.Apply(name, Project.WithTrack(track with { Captions = change(track.Captions ?? new CaptionStyle()) }), $"caption style {trackId} {key}");
        }
    }

    /// <summary>Adds another recording (or any video) on new tracks at the end of the timeline.</summary>
    [RelayCommand]
    private async Task AddRecordingAsync()
    {
        var path = _dialogs.PickMedia(VideoEditorText.AddRecordingTitle, VideoEditorText.VideosFilter, Folder());
        if (path is null)
        {
            return;
        }

        try
        {
            var asset = await Task.Run(() => ProjectFactory.ReadAsset(path));
            History.Apply(VideoEditorText.UndoAddRecording, ProjectFactory.AddRecording(Project, asset, Project.Duration));
        }
        catch (Exception e) when (e is FfmpegException or IOException)
        {
            _dialogs.ShowError(VideoEditorText.CantBeOpened(Path.GetFileName(path), e.Message));
        }
    }

    /// <summary>Adds a music (or any sound) file on a sound track of its own, from the start, under the voice.</summary>
    [RelayCommand]
    private async Task AddMusicAsync()
    {
        var path = _dialogs.PickMedia(VideoEditorText.AddMusicTitle, VideoEditorText.SoundFilter, Folder());
        if (path is null)
        {
            return;
        }

        try
        {
            var asset = await Task.Run(() => ProjectFactory.ReadAsset(path));
            if (!asset.Streams.Any(s => s.Kind == StreamKind.Audio))
            {
                _dialogs.ShowError(VideoEditorText.HasNoSound(Path.GetFileName(path)));
                return;
            }

            History.Apply(VideoEditorText.UndoAddMusic, ProjectFactory.AddSound(Project, asset, 0, VideoEditorText.MusicTrack, ProjectFactory.MusicVolume));
        }
        catch (Exception e) when (e is FfmpegException or IOException)
        {
            _dialogs.ShowError(VideoEditorText.CantBeOpened(Path.GetFileName(path), e.Message));
        }
    }

    [RelayCommand]
    private void InsertText() => InsertAnnotation(AnnotationShape.Text);

    [RelayCommand]
    private void InsertArrow() => InsertAnnotation(AnnotationShape.Arrow);

    [RelayCommand]
    private void InsertLine() => InsertAnnotation(AnnotationShape.Line);

    [RelayCommand]
    private void InsertRectangle() => InsertAnnotation(AnnotationShape.Rectangle);

    [RelayCommand]
    private void InsertEllipse() => InsertAnnotation(AnnotationShape.Ellipse);

    [RelayCommand]
    private void InsertHighlight() => InsertAnnotation(AnnotationShape.Highlight);

    [RelayCommand]
    private void InsertBlur() => InsertAnnotation(AnnotationShape.Blur);

    [RelayCommand]
    private void InsertPixelate() => InsertAnnotation(AnnotationShape.Pixelate);

    private void InsertAnnotation(AnnotationShape shape)
    {
        if (Allowed(ProFeature.VideoEditorAnnotations))
        {
            Insert(shape);
        }
    }

    /// <summary>
    /// A new annotation over the marked range (else from the playhead for 3 s), selected so the panel and the preview
    /// show it.
    /// </summary>
    public string Insert(AnnotationShape shape)
    {
        StopPlaying();
        var (from, length) = Timeline.Range is { } range ? (range.From, range.To - range.From) : (Timeline.Playhead, AnnotationItem.DefaultLength);
        var (project, id) = AnnotationEdits.Add(Project, shape, from, length);
        History.Apply(AddStep(shape), project);
        Timeline.SetRange(null);
        Timeline.Select(id);
        if (!Timeline.Selected!.Covers(Timeline.Playhead))
        {
            Seek(from);
        }

        return id;
    }

    [RelayCommand]
    private void InsertZoom()
    {
        if (Allowed(ProFeature.AutoZoom))
        {
            AddZoom();
        }
    }

    /// <summary>
    /// A zoom over the marked range (else from the playhead for 3 s), following the pointer when a recording has
    /// metadata, selected so the preview shows its frame.
    /// </summary>
    public string AddZoom()
    {
        StopPlaying();
        var (from, length) = Timeline.Range is { } range ? (range.From, range.To - range.From) : (Timeline.Playhead, ZoomItem.DefaultLength);
        var follow = Project.Assets.Any(a => Compositor.Overlays.Metadata(a) is not null);
        var (project, id) = AnnotationEdits.AddZoom(Project, from, length, follow);
        if (_services.Gate.IsEnabled(ProFeature.StudioLook))
        {
            project = project.WithItem((ZoomItem)project.Find(id)!.Value.Item with { MotionBlur = ZoomItem.DefaultMotionBlur });
        }

        History.Apply(VideoEditorText.UndoAddZoom, project);
        Timeline.SetRange(null);
        Timeline.Select(id);
        if (!Timeline.Selected!.Covers(Timeline.Playhead))
        {
            Seek(from);
        }

        return id;
    }

    [RelayCommand]
    private void MakeAutoZooms()
    {
        if (Allowed(ProFeature.AutoZoom))
        {
            AutoZoomFromInput();
        }
    }

    /// <summary>
    /// Zooms around the clicks and typing in the recordings' metadata (replacing Auto-Zoom's earlier ones), as one
    /// undo step; says so when there is nothing to zoom to. Returns how many were made.
    /// </summary>
    public int AutoZoomFromInput()
    {
        StopPlaying();
        var options = new AutoZoomOptions(MotionBlur: _services.Gate.IsEnabled(ProFeature.StudioLook) ? ZoomItem.DefaultMotionBlur : 0);
        var (project, count) = CashGear.Capture.VideoEditor.Editing.AutoZoom.Apply(Project, Compositor.Overlays, options);
        if (count == 0 && !MadeByAutoZoom(Project))
        {
            _dialogs.ShowError(Project.Assets.Any(a => Compositor.Overlays.Metadata(a) is not null)
                ? VideoEditorText.AutoZoomFoundNothing
                : VideoEditorText.AutoZoomNeedsMetadata);
            return 0;
        }

        History.Apply(VideoEditorText.UndoAutoZoom, project);
        Timeline.Select(null);
        return count;
    }

    [RelayCommand]
    private void InsertClicks() => InsertMetadataEffect(EffectKind.Clicks);

    [RelayCommand]
    private void InsertKeystrokes() => InsertMetadataEffect(EffectKind.Keystrokes);

    [RelayCommand]
    private void InsertPointer() => InsertMetadataEffect(EffectKind.Pointer);

    private void InsertMetadataEffect(EffectKind kind)
    {
        if (Allowed(ProFeature.VideoEditorEffects))
        {
            InsertEffect(kind);
        }
    }

    /// <summary>An effect from the recordings' metadata over the marked range (else the whole timeline), selected.</summary>
    public string InsertEffect(EffectKind kind)
    {
        StopPlaying();
        var (from, length) = Timeline.Range is { } range ? (range.From, range.To - range.From) : (0, Math.Max(Project.Duration, 1));
        var (project, id) = AnnotationEdits.AddEffect(Project, kind, from, length);
        History.Apply(kind switch { EffectKind.Clicks => VideoEditorText.UndoAddClickHighlights, EffectKind.Keystrokes => VideoEditorText.UndoAddKeystrokes, _ => VideoEditorText.UndoAddPointerEffects }, project);
        Timeline.SetRange(null);
        Timeline.Select(id);
        return id;
    }

    /// <summary>A keyframe at the playhead for the selected annotation (where it is now), to move it from there.</summary>
    public void AddKeyframe()
    {
        if (Timeline.Selected is AnnotationItem item && item.Covers(Timeline.Playhead))
        {
            History.Apply(VideoEditorText.UndoAddKeyframe, Project.WithItem(item.WithKeyframeAt(Timeline.Playhead, Project.Canvas.Fps)));
        }
    }

    public void RemoveKeyframe()
    {
        if (Timeline.Selected is AnnotationItem item)
        {
            History.Apply(VideoEditorText.UndoRemoveKeyframe, Project.WithItem(item.WithoutKeyframeAt(Timeline.Playhead, Project.Canvas.Fps)));
        }
    }

    [RelayCommand]
    private void InsertBubbleLayout() => AddLayout(LayoutKind.CornerBubble);

    [RelayCommand]
    private void InsertSideBySideLayout() => AddLayout(LayoutKind.SideBySide);

    [RelayCommand]
    private void InsertCameraLayout() => AddLayout(LayoutKind.CameraOnly);

    [RelayCommand]
    private void InsertScreenLayout() => AddLayout(LayoutKind.ScreenOnly);

    /// <summary>A layout over the marked range (else from the playhead for 5 s), selected so the panel shows it; null when there is no webcam.</summary>
    public string? AddLayout(LayoutKind kind)
    {
        StopPlaying();
        if (!Project.Tracks.Any(t => t.Kind == TrackKind.Webcam && t.Clips.Any()))
        {
            _dialogs.ShowError(VideoEditorText.LayoutNeedsWebcam);
            return null;
        }

        var (from, length) = Timeline.Range is { } range ? (range.From, range.To - range.From) : (Timeline.Playhead, LayoutItem.DefaultLength);
        var (project, id) = CanvasEdits.AddLayout(Project, kind, from, length);
        History.Apply(VideoEditorText.UndoAddLayout, project);
        Timeline.SetRange(null);
        Timeline.Select(id);
        return id;
    }

    /// <summary>View › Look and Canvas: nothing selected, so the panel shows them.</summary>
    [RelayCommand]
    private void ShowLook()
    {
        StopPlaying();
        Timeline.Select(null);
    }

    /// <summary>View › Use This Look for New Recordings: remembers the project's look, by name when it has one.</summary>
    [RelayCommand]
    private async Task UseLookForRecordingsAsync()
    {
        var look = Looks.Current(Project, AvailableLooks());
        if (look is null)
        {
            look = await SaveLookAsync();
            if (look is null)
            {
                return;
            }
        }

        await _services.State.UpdateAsync(s => s with { VideoEditor = (s.VideoEditor ?? new VideoEditorState()) with { DefaultLook = look.Id } });
        _lookShown = null;
        RefreshLook();
    }

    /// <summary>The built-in looks, and the saved ones when they may be used.</summary>
    private List<StudioLook> AvailableLooks()
    {
        var looks = Looks.BuiltIn.ToList();
        if (_services.Presets is { } presets && _services.Gate.IsEnabled(ProFeature.StudioLook))
        {
            looks.AddRange(presets.Current.SavedLooks.Select(Looks.FromSaved));
        }

        return looks;
    }

    private void ApplyLook(string id)
    {
        if (AvailableLooks().FirstOrDefault(l => l.Id == id) is not { } look)
        {
            return;
        }

        StopPlaying();
        _lookShown = null;
        History.Apply(VideoEditorText.UndoLook, Looks.Apply(Project, look));
    }

    /// <summary>Saves the project's look under a name the user gives; null when they don't, or when it isn't allowed.</summary>
    private async Task<StudioLook?> SaveLookAsync()
    {
        if (_services.Presets is not { } presets || !Allowed(ProFeature.StudioLook) || _dialogs.AskName(VideoEditorText.SaveLookTitle, VideoEditorText.MyLook) is not { Length: > 0 } name)
        {
            return null;
        }

        var saved = Looks.Capture(Project, name);
        await presets.UpdateAsync(library => library.WithLook(saved));
        _lookShown = null;
        RefreshLook();
        return Looks.FromSaved(saved);
    }

    private async Task DeleteLookAsync(string name)
    {
        if (_services.Presets is { } presets)
        {
            await presets.UpdateAsync(library => library.WithoutLook(name));
            _lookShown = null;
            RefreshLook();
        }
    }

    /// <summary>A change from the look panel: one undo step, merged by its key; the panel stays while its own slider moves.</summary>
    private void ChangeLook(string name, string key, Func<VideoProject, VideoProject> change)
    {
        var changed = change(Project);
        _lookShown = (changed.Canvas, Looks.ScreenPlacement(changed));
        History.Apply(name, changed, $"look {key}");
    }

    /// <summary>A change that shows or hides settings of the panel: it is made again afterwards.</summary>
    private void ChangeLookAndPanel(string name, string key, Func<VideoProject, VideoProject> change)
    {
        _lookShown = null;
        History.Apply(name, change(Project), $"look {key}");
    }

    /// <summary>A picture file chosen for the project, among its assets; null when none was chosen.</summary>
    private string? PickPicture() => _dialogs.PickMedia(VideoEditorText.PickPictureTitle, VideoEditorText.PicturesFilter, Folder());

    /// <summary>The look panel, made again when the look was changed by something other than the panel itself (undo).</summary>
    private void RefreshLook()
    {
        var now = (Project.Canvas, Looks.ScreenPlacement(Project));
        if (_lookShown == now)
        {
            return;
        }

        _lookShown = now;
        LookProperties.Clear();
        var actions = new ItemProperties.LookActions(
            AvailableLooks(),
            _services.Presets?.Current.SavedLooks.Select(l => l.Name).ToList() ?? [],
            ApplyLook,
            () => SaveLookAsync(),
            DeleteLookAsync,
            UseLookForRecordingsCommand,
            Looks.Find(NewRecordingLook.IdOf(_services), _services.Presets?.Current.SavedLooks)?.Name ?? "",
            ChangeLook,
            ChangeLookAndPanel,
            PickPicture,
            ReframeNote);
        foreach (var parameter in ItemProperties.ForProject(Project, () => Project, actions, _services.Gate))
        {
            LookProperties.Add(parameter);
        }
    }

    /// <summary>What auto-reframe says about the pointer: when it can't follow it, or that it has nothing to follow.</summary>
    private string ReframeNote()
    {
        if (!Project.Assets.Any(a => Compositor.Overlays.Metadata(a) is not null))
        {
            return VideoEditorText.ReframeNoMetadata;
        }

        var missed = Compositor.Camera.PointerOutOfView(Project);
        return missed.Count == 0 ? VideoEditorText.ReframeAllInView : VideoEditorText.ReframeOutOfView(string.Join(", ", missed.Take(6).Select(m => Format(m.From))));
    }

    /// <summary>Starts working out the masks that webcam clips in the project need and don't have yet.</summary>
    private void MakeMasks()
    {
        if (_services.Masks is not { } masks)
        {
            return;
        }

        foreach (var clip in Project.Tracks.Where(t => t.HasPictures && !t.Hidden).SelectMany(t => t.Clips).Where(c => c.Background is { Effect: not BackgroundEffect.None }))
        {
            if (Project.Asset(clip.AssetId) is { } asset && !masks.IsReady(asset, clip.Stream) && _masksAsked.Add($"{asset.Path}|{clip.Stream}"))
            {
                _ = MakeMasksAsync(masks, asset, clip.Stream);
            }
        }
    }

    private async Task MakeMasksAsync(WebcamMasks masks, MediaAsset asset, int stream)
    {
        var progress = new Progress<double>(done => WorkText = VideoEditorText.FindingPerson(done));
        WorkText = VideoEditorText.FindingPerson(0);
        try
        {
            await masks.EnsureAsync(asset, stream, progress, _closing.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e) when (e is System.Net.Http.HttpRequestException or InvalidDataException or InvalidOperationException or FfmpegException or IOException or UnauthorizedAccessException)
        {
            LogMasksFailed(e, asset.Name);
            _masksAsked.Remove($"{asset.Path}|{stream}");
            _dialogs.ShowError(VideoEditorText.MasksFailed(e.Message));
        }
        finally
        {
            if (!_disposed)
            {
                WorkText = "";
            }
        }

        if (!_disposed)
        {
            PreviewInvalidated?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Holds the picture at the playhead for 2 s; what follows moves on.</summary>
    [RelayCommand]
    private void FreezeFrame()
    {
        if (!Allowed(ProFeature.VideoEditorSpeed))
        {
            return;
        }

        StopPlaying();
        History.Apply(VideoEditorText.UndoFreezeFrame, TransitionEdits.Freeze(Project, Times.FrameStart(Timeline.Playhead, Project.Canvas.Fps)));
    }

    /// <summary>The selected clip crossfades into the one after it (1 s).</summary>
    [RelayCommand]
    private void Crossfade()
    {
        if (Timeline.SelectedId is { } id)
        {
            CrossfadeFrom(id);
        }
    }

    /// <summary>Changes a clip's speed (and its linked clips'); what follows moves with its new length.</summary>
    public void SetSpeed(string clipId, double speed, double? endSpeed, string mergeKey)
    {
        if (!Allowed(ProFeature.VideoEditorSpeed))
        {
            return;
        }

        StopPlaying();
        var after = TransitionEdits.SetSpeed(Project, clipId, speed, endSpeed);
        _applied = after.Find(clipId)?.Item; // the panel stays while its own slider moves
        History.Apply(VideoEditorText.UndoSpeed, after, mergeKey);
    }

    public void SetFreezeLength(string clipId, double length, string mergeKey)
    {
        StopPlaying();
        var after = TransitionEdits.SetFreezeLength(Project, clipId, length);
        _applied = after.Find(clipId)?.Item;
        History.Apply(VideoEditorText.UndoFreezeLength, after, mergeKey);
    }

    public void CrossfadeFrom(string clipId)
    {
        if (!Allowed(ProFeature.VideoEditorSpeed))
        {
            return;
        }

        StopPlaying();
        _shown = null; // the panel's button becomes "Remove the crossfade"
        History.Apply(VideoEditorText.UndoCrossfade, TransitionEdits.Crossfade(Project, clipId));
    }

    public void RemoveCrossfadeFrom(string clipId)
    {
        StopPlaying();
        _shown = null;
        History.Apply(VideoEditorText.UndoRemoveCrossfade, TransitionEdits.RemoveCrossfade(Project, clipId));
    }

    /// <summary>A way in or out of a clip, from its panel; what follows moves with an overlap it makes.</summary>
    public void SetClipTransition(string clipId, ItemEdge edge, ClipTransition? transition, string mergeKey)
    {
        if (transition is { } made && !TransitionAllowed(made.Kind))
        {
            return;
        }

        StopPlaying();
        var after = TransitionEdits.SetTransition(Project, clipId, edge, transition);
        _applied = after.Find(clipId)?.Item; // the panel stays while its own slider moves
        History.Apply(VideoEditorText.UndoTransition, after, mergeKey);
    }

    /// <summary>A transition from the library on the clip's edge: Enter on its row with a clip chosen, nearest the playhead, or at the cut it was dropped on.</summary>
    public bool ApplyTransition(ClipTransition transition, string? trackId, double? at)
    {
        ArgumentNullException.ThrowIfNull(transition);
        (string ClipId, ItemEdge Edge)? edge = null;
        if (trackId is not null && at is { } time && Project.Track(trackId) is { } track)
        {
            edge = TransitionEdits.EdgeNear(track, time);
        }
        else if (Timeline.Selected is Clip selected && Project.Find(selected.Id)?.Track is { HasPictures: true })
        {
            edge = (selected.Id, Math.Abs(selected.Start - Position) <= Math.Abs(selected.End - Position) ? ItemEdge.Start : ItemEdge.End);
        }

        if (edge is not { } target)
        {
            _dialogs.ShowError(VideoEditorText.TransitionNeedsClip);
            return false;
        }

        if (!TransitionAllowed(transition.Kind))
        {
            return false;
        }

        StopPlaying();
        _shown = null;
        History.Apply(VideoEditorText.UndoTransition, TransitionEdits.SetTransition(Project, target.ClipId, target.Edge, transition));
        Timeline.Select(target.ClipId);
        return true;
    }

    /// <summary>Dissolve and dip are free; the other transitions are part of Pro.</summary>
    private bool TransitionAllowed(TransitionKind kind) => kind is TransitionKind.Dissolve or TransitionKind.DipToColor || Allowed(ProFeature.MotionEffects);

    private void Step(int frames)
    {
        StopPlaying();
        var fps = Project.Canvas.Fps;
        Seek(Times.FrameStart(Timeline.Playhead, fps) + ((double)frames / fps));
    }

    private void StopPlaying()
    {
        if (Engine.IsPlaying)
        {
            PlayPause();
        }
    }

    private string? Folder() => ProjectPath is { } path ? Path.GetDirectoryName(path) : Project.Assets.FirstOrDefault() is { } asset ? Path.GetDirectoryName(asset.Path) : null;

    private async Task<bool> SaveAsync(string? path, bool embed)
    {
        path ??= _dialogs.AskProjectPath(Name, Folder());
        if (path is null)
        {
            return false;
        }

        try
        {
            var project = Project;
            var saved = await Task.Run(() => ProjectFile.Save(project, path, embed));
            if (!embed || ProjectPath is null)
            {
                // A copy with media inside doesn't become the file being edited unless there was none.
                History.MarkSaved(ReferenceEquals(project, Project) ? saved : null);
                ProjectPath = path;
            }

            await _services.State.UpdateAsync(s => s with { VideoEditor = (s.VideoEditor ?? new VideoEditorState()).WithRecent(path) });
            OnPropertyChanged(nameof(Title));
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            LogSaveFailed(e, path);
            _dialogs.ShowError(VideoEditorText.ProjectNotSaved(e.Message));
            return false;
        }
    }

    private void OnProjectChanged()
    {
        if (Timeline.SelectedId is { } id && Project.Find(id) is null)
        {
            Timeline.Select(null);
        }

        if (Timeline.Playhead > Project.Duration)
        {
            Timeline.Playhead = Project.Duration;
        }

        RebuildTracks();
        RefreshProperties();
        RefreshLook();
        MakeMasks();
        Transcript.Refresh();
        Transcript.PlayheadMoved(Timeline.Playhead);
        Media?.Refresh();
        MakeProxies();
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(TimeText));
        OnPropertyChanged(nameof(UndoText));
        OnPropertyChanged(nameof(RedoText));
        OnPropertyChanged(nameof(SelectionText));
        Frames.Update(Project, Timeline.Playhead, Engine.IsPlaying);
        PreviewInvalidated?.Invoke(this, EventArgs.Empty);
        TimelineInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void OnTimelineChanged()
    {
        RefreshProperties();
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(HasRange));
        TimelineInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void OnPreviewsChanged(object? sender, EventArgs e) => TimelineInvalidated?.Invoke(this, EventArgs.Empty);

    /// <summary>The track headers, in the timeline's row order; the same headers, updated, while the tracks are the same.</summary>
    private void RebuildTracks()
    {
        var rows = TimelineView.Rows(Project);
        if (Tracks.Count == rows.Count && Tracks.Zip(rows).All(p => p.First.Track.Id == p.Second.Track.Id && p.First.Height == p.Second.Height))
        {
            foreach (var (header, row) in Tracks.Zip(rows))
            {
                if (!ReferenceEquals(header.Track, row.Track))
                {
                    header.Update(row.Track);
                }
            }

            return;
        }

        Tracks.Clear();
        foreach (var row in rows)
        {
            Tracks.Add(new TrackHeaderViewModel(this, row.Track, row.Height));
        }
    }

    private string Format(double seconds) => TimelineRenderer.Format(seconds, Project.Canvas.Fps, frames: true);

    /// <summary>
    /// The properties panel for the selection: made again when the selection changes or its item was changed by
    /// something else (undo); kept while its own settings change the item, so a slider being dragged stays.
    /// </summary>
    private void RefreshProperties()
    {
        var item = Timeline.Selected;
        if (ReferenceEquals(item, _shown))
        {
            return;
        }

        var own = item is not null && _shown is not null && item.Id == _shown.Id && ReferenceEquals(item, _applied);
        _shown = item;
        if (own)
        {
            return;
        }

        Properties.Clear();
        if (item is not null)
        {
            var actions = new ItemProperties.ClipActions(SetSpeed, SetFreezeLength, CrossfadeFrom, RemoveCrossfadeFrom)
            {
                SetTransition = SetClipTransition,
                PickLut = () => _dialogs.PickMedia(VideoEditorText.LutTitle, VideoEditorText.LutFilter, Folder()),
            };
            var captions = new ItemProperties.CaptionActions(SplitCaption, MergeCaption, ChangeCaptionStyle);
            var motion = new ItemProperties.MotionActions(() => Timeline.Playhead, Seek, SetMovesWithZoom, Project.Canvas.Fps);
            foreach (var parameter in ItemProperties.For(Project, item, () => Project, ApplyProperty, new ItemProperties.Keyframes(AddKeyframe, RemoveKeyframe), actions, captions, _services.Gate, PickPicture, (asset, name, key) => History.Apply(name, Project.WithAsset(asset), key), motion))
            {
                Properties.Add(parameter);
            }
        }

        PropertiesTitle = item is null ? "" : ItemProperties.Title(Project, item);
        OnPropertyChanged(nameof(HasProperties));
    }

    private void SetMovesWithZoom(string trackId, bool moves)
    {
        if (Project.Track(trackId) is { } track)
        {
            _applied = Timeline.Selected;
            History.Apply(VideoEditorText.UndoMovesWithZoom, Project.WithTrack(track with { MovesWithZoom = moves }));
        }
    }

    private void ApplyProperty(TimelineItem item, string name, string mergeKey)
    {
        _applied = item;
        History.Apply(name, Project.WithItem(item), mergeKey);
    }

    private SKPoint ToCanvas(double x, double y) => new((float)((x - _preview.X) / _preview.Scale), (float)((y - _preview.Y) / _preview.Scale));

    /// <summary>The undo step of adding an annotation of <paramref name="shape"/>.</summary>
    private static string AddStep(AnnotationShape shape) => shape switch
    {
        AnnotationShape.Arrow => VideoEditorText.UndoAddArrow,
        AnnotationShape.Line => VideoEditorText.UndoAddLine,
        AnnotationShape.Rectangle => VideoEditorText.UndoAddRectangle,
        AnnotationShape.Ellipse => VideoEditorText.UndoAddEllipse,
        AnnotationShape.Highlight => VideoEditorText.UndoAddHighlight,
        AnnotationShape.Blur => VideoEditorText.UndoAddBlurredArea,
        AnnotationShape.Pixelate => VideoEditorText.UndoAddPixelatedArea,
        AnnotationShape.SpeechBubble or AnnotationShape.StepNumber or AnnotationShape.Spotlight or AnnotationShape.Magnify or AnnotationShape.Freehand => VideoEditorText.UndoAddCallout,
        _ => VideoEditorText.UndoAddText,
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "The webcam masks of {Name} couldn't be worked out")]
    private partial void LogMasksFailed(Exception e, string name);

    private static bool MadeByAutoZoom(VideoProject project) => project.Tracks.Any(t => t.Items.Any(i => i is ZoomItem { Auto: true }));

    /// <summary>
    /// The selected zoom when it is under the playhead and nothing plays: the preview then shows its frame over the
    /// whole picture. Not where the screen covers a canvas of another shape: there is no whole picture to show it on.
    /// </summary>
    private ZoomItem? SelectedZoom() =>
        !Engine.IsPlaying && Project.Canvas.Framing == ScreenFraming.Fit && Timeline.Selected is ZoomItem zoom && zoom.Covers(Timeline.Playhead) ? zoom : null;

    /// <summary>The canvas area a zoom shows at the playhead, fully zoomed in.</summary>
    private SKRect ZoomFrame(ZoomItem zoom)
    {
        var view = Compositor.Camera.Target(Project, zoom, Timeline.Playhead);
        return SKRect.Create((float)view.X, (float)view.Y, (float)view.Width, (float)view.Height);
    }

    /// <summary>The grip of a zoom's frame at <paramref name="point"/>: its corner (zoom), inside it (move), or null.</summary>
    private PreviewGrip? ZoomGrip(SKRect frame, SKPoint point)
    {
        var reach = PlacementEdits.GripSize / _preview.Scale;
        if (Math.Abs(point.X - frame.Right) <= reach && Math.Abs(point.Y - frame.Bottom) <= reach)
        {
            return PreviewGrip.Corner;
        }

        return frame.Contains(point) ? PreviewGrip.None : null;
    }

    /// <summary>The project as the preview shows it while a zoom is selected: the whole picture.</summary>
    private static VideoProject WithoutZooms(VideoProject project) =>
        project with { Tracks = [.. project.Tracks.Select(t => t.Kind == TrackKind.Effect && t.Items.Any(i => i is ZoomItem) ? t.WithItems(t.Items.Where(i => i is not ZoomItem)) : t)] };

    /// <summary>The selection's outline and its resize grip, over the preview (never exported).</summary>
    private static void DrawSelection(SKCanvas canvas, SKRect area, float scale, bool corner, SKColor color)
    {
        using var outline = new SKPaint { Color = color, Style = SKPaintStyle.Stroke, StrokeWidth = 2 / scale, IsAntialias = true };
        canvas.DrawRect(area, outline);
        if (corner)
        {
            var grip = 6 / scale;
            using var fill = new SKPaint { Color = color, IsAntialias = true };
            canvas.DrawRect(SKRect.Create(area.Right - grip, area.Bottom - grip, grip * 2, grip * 2), fill);
        }
    }

    /// <summary>An annotation's outline (or, for a line, its two ends) and grips.</summary>
    private static void DrawSelection(SKCanvas canvas, AnnotationItem item, double time, float scale, SKColor color, ProjectCanvas? canvasSize = null)
    {
        if (!item.IsLine)
        {
            DrawSelection(canvas, canvasSize is null ? FrameCompositor.Bounds(item, time) : FrameCompositor.Bounds(item, time, canvasSize), scale, corner: item.Shape != AnnotationShape.Text, color);
            return;
        }

        var place = item.At(time);
        using var fill = new SKPaint { Color = color, IsAntialias = true };
        canvas.DrawCircle((float)place.X, (float)place.Y, 6 / scale, fill);
        canvas.DrawCircle((float)(place.X + place.Width), (float)(place.Y + place.Height), 6 / scale, fill);
    }

    private sealed record PreviewDrag(VideoProject Base, TimelineItem Item, SKPoint Start, SKRect Area, PreviewGrip Grip, int Number);

    [LoggerMessage(Level = LogLevel.Error, Message = "Saving the video project {Path} failed")]
    private partial void LogSaveFailed(Exception e, string path);
}
