using System.Collections.ObjectModel;
using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Media;
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
    VideoDecoding Decoding = VideoDecoding.Hardware)
{
    /// <summary>Where embedded media is unpacked and thumbnails are kept.</summary>
    public string CacheDirectory => Path.Combine(Paths.DataDirectory, "VideoEditor");
}

/// <summary>A row header of the timeline: the track's name, its switches and (for sound) its volume.</summary>
public sealed partial class TrackHeaderViewModel(VideoEditorViewModel owner, Track track, double height) : ObservableObject
{
    public Track Track { get; private set; } = track;

    public string Name => Track.Name;

    public double Height { get; } = height;

    /// <summary>The track's sound level in percent (0 to 200); a drag of the slider is one undo step.</summary>
    public double Volume
    {
        get => Math.Round(Track.Volume * 100);
        set => owner.ChangeTrack(Track, t => t with { Volume = Math.Clamp(value, 0, 200) / 100 }, "track volume", $"volume {Track.Id}");
    }

    /// <summary>The same track after an edit: its switches show the new state (the header stays, so a slider drag goes on).</summary>
    internal void Update(Track track)
    {
        Track = track;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void Delete() => owner.DeleteTrack(Track);

    public bool HasPictures => Track.HasPictures;

    public bool HasSound => Track.HasSound;

    public string Kind => Track.Kind switch
    {
        TrackKind.Video => "Picture",
        TrackKind.Webcam => "Webcam",
        TrackKind.Audio => "Sound",
        TrackKind.Annotation => "Annotations",
        TrackKind.Caption => "Captions",
        _ => "Effects",
    };

    public bool IsMuted
    {
        get => Track.Muted;
        set => owner.ChangeTrack(Track, t => t with { Muted = value }, value ? "mute" : "unmute");
    }

    public bool IsSolo
    {
        get => Track.Solo;
        set => owner.ChangeTrack(Track, t => t with { Solo = value }, value ? "solo" : "unsolo");
    }

    public bool IsLocked
    {
        get => Track.Locked;
        set => owner.ChangeTrack(Track, t => t with { Locked = value }, value ? "lock track" : "unlock track");
    }

    public bool IsShown
    {
        get => !Track.Hidden;
        set => owner.ChangeTrack(Track, t => t with { Hidden = !value }, value ? "show track" : "hide track");
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
        Frames = new PreviewFrames(services.Loggers, services.Decoding);
        Compositor = new FrameCompositor(Frames);
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
    }

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
        : "Untitled";

    public string Title => $"{(History.IsDirty ? "• " : "")}{Name} — Video Editor";

    public bool IsDirty => History.IsDirty;

    public bool IsPlaying => Engine.IsPlaying;

    public double Position => Timeline.Playhead;

    public string TimeText => $"{TimelineRenderer.Format(Position, Project.Canvas.Fps, frames: true)} / {TimelineRenderer.Format(Project.Duration, Project.Canvas.Fps, frames: true)}";

    public string Status => $"{Project.Canvas.Width} × {Project.Canvas.Height}, {Project.Canvas.Fps} fps · {PreviewBackend}{(Frames.IsHardware ? " · decoding on the graphics card" : "")}";

    public string UndoText => History.UndoName is { } name ? $"Undo {name}" : "Undo";

    public string RedoText => History.RedoName is { } name ? $"Redo {name}" : "Redo";

    /// <summary>What is selected, in words (empty when nothing is).</summary>
    public string SelectionText
    {
        get
        {
            if (Timeline.Selected is not { } item)
            {
                return Timeline.Range is { } range
                    ? $"Range {Format(range.From)} – {Format(range.To)} ({range.To - range.From:0.00} s). Cut it out with Delete."
                    : "";
            }

            var text = $"{Format(item.Start)} – {Format(item.End)} ({item.Duration:0.00} s)";
            if (item is Clip clip)
            {
                var asset = Project.Asset(clip.AssetId);
                text = $"{asset?.Name ?? "Missing media"}: {text}, source {Format(clip.SourceIn)} – {Format(clip.SourceOut)}";
            }

            return text;
        }
    }

    public bool HasRange => Timeline.Range is not null;

    /// <summary>How far the track headers move up with the rows (Shift+wheel on the timeline).</summary>
    public double RowsOffset => -View.ScrollRows;

    /// <summary>The timeline's rows were scrolled: the headers follow.</summary>
    public void RowsScrolled() => OnPropertyChanged(nameof(RowsOffset));

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
        if (zoomShown is not null)
        {
            DrawSelection(canvas, ZoomFrame(zoomShown), scale, corner: true);
        }
        else if (!Engine.IsPlaying && Timeline.Selected is { } selected && selected.Covers(Timeline.Playhead))
        {
            if (selected is Clip clip && Project.Find(clip.Id)?.Track is { HasPictures: true })
            {
                DrawSelection(canvas, PlacementEdits.Area(Project, clip), scale, corner: true);
            }
            else if (selected is AnnotationItem annotation)
            {
                DrawSelection(canvas, annotation, Timeline.Playhead, scale);
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
        var area = item is Clip clip ? PlacementEdits.Area(Project, clip) : default;
        _drag = new PreviewDrag(Project, item, ToCanvas(x, y), area, hit.Grip, ++_drags);
        PreviewInvalidated?.Invoke(this, EventArgs.Empty);
    }

    public void PreviewPointerMove(double x, double y)
    {
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
            name = drag.Grip == PreviewGrip.None ? "move annotation" : "resize annotation";
        }
        else if (drag.Item is Clip clip)
        {
            var placement = clip.Placement ?? Placement.Fit;
            var resize = drag.Grip != PreviewGrip.None;
            item = clip with
            {
                Placement = resize
                    ? PlacementEdits.Resize(placement, drag.Area, point)
                    : PlacementEdits.Move(placement, Project.Canvas, point.X - drag.Start.X, point.Y - drag.Start.Y),
            };
            name = resize ? "resize picture" : "move picture";
        }
        else if (drag.Item is ZoomItem zoom)
        {
            item = PlacementEdits.DragZoom(zoom, Project.Canvas, drag.Area, drag.Grip, point.X - drag.Start.X, point.Y - drag.Start.Y);
            name = drag.Grip == PreviewGrip.None ? "move zoom" : "resize zoom";
        }
        else
        {
            return;
        }

        _applied = item;
        History.Apply(name, drag.Base.WithItem(item), $"preview {drag.Number}");
    }

    public void PreviewPointerUp() => _drag = null;

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
        History.Apply("delete track", TrackEdits.Remove(Project, track.Id));
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
        _services.Previews.Changed -= OnPreviewsChanged;
        Engine.Dispose();
        Frames.Dispose();
    }

    [RelayCommand]
    private void PlayPause()
    {
        if (Engine.IsPlaying)
        {
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
        History.Apply("split", TimelineEdits.Split(Project, Timeline.Playhead));
    }

    /// <summary>Delete: the marked range out of every track (closing the gap), else the selected item and its linked clips.</summary>
    [RelayCommand]
    private void Delete()
    {
        StopPlaying();
        if (Timeline.Range is { } range)
        {
            History.Apply("cut out", TimelineEdits.DeleteRange(Project, range.From, range.To));
            Timeline.SetRange(null);
            Seek(Math.Min(range.From, Project.Duration));
        }
        else if (Timeline.SelectedId is { } id)
        {
            History.Apply("delete", TimelineEdits.Delete(Project, [id]));
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
            History.Apply("ripple delete", TimelineEdits.RippleDelete(Project, id));
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
        Timeline.SetRange(null);
        Timeline.Select(null);
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
            _services.Loggers.CreateLogger<ExportViewModel>());
        _dialogs.ShowExport(viewModel);
    }

    /// <summary>Adds another recording (or any video) on new tracks at the end of the timeline.</summary>
    [RelayCommand]
    private async Task AddRecordingAsync()
    {
        var path = _dialogs.PickMedia("Add a recording", "Videos (*.mp4;*.mkv;*.mov;*.webm)|*.mp4;*.mkv;*.mov;*.webm|All files (*.*)|*.*", Folder());
        if (path is null)
        {
            return;
        }

        try
        {
            var asset = await Task.Run(() => ProjectFactory.ReadAsset(path));
            History.Apply("add recording", ProjectFactory.AddRecording(Project, asset, Project.Duration));
        }
        catch (Exception e) when (e is FfmpegException or IOException)
        {
            _dialogs.ShowError($"{Path.GetFileName(path)} can't be opened: {e.Message}");
        }
    }

    /// <summary>Adds a music (or any sound) file on a sound track of its own, from the start, under the voice.</summary>
    [RelayCommand]
    private async Task AddMusicAsync()
    {
        var path = _dialogs.PickMedia("Add music", "Sound (*.mp3;*.m4a;*.aac;*.wav;*.ogg;*.opus;*.flac;*.wma)|*.mp3;*.m4a;*.aac;*.wav;*.ogg;*.opus;*.flac;*.wma|All files (*.*)|*.*", Folder());
        if (path is null)
        {
            return;
        }

        try
        {
            var asset = await Task.Run(() => ProjectFactory.ReadAsset(path));
            if (!asset.Streams.Any(s => s.Kind == StreamKind.Audio))
            {
                _dialogs.ShowError($"{Path.GetFileName(path)} has no sound.");
                return;
            }

            History.Apply("add music", ProjectFactory.AddSound(Project, asset, 0, "Music", ProjectFactory.MusicVolume));
        }
        catch (Exception e) when (e is FfmpegException or IOException)
        {
            _dialogs.ShowError($"{Path.GetFileName(path)} can't be opened: {e.Message}");
        }
    }

    [RelayCommand]
    private void InsertText() => Insert(AnnotationShape.Text);

    [RelayCommand]
    private void InsertArrow() => Insert(AnnotationShape.Arrow);

    [RelayCommand]
    private void InsertLine() => Insert(AnnotationShape.Line);

    [RelayCommand]
    private void InsertRectangle() => Insert(AnnotationShape.Rectangle);

    [RelayCommand]
    private void InsertEllipse() => Insert(AnnotationShape.Ellipse);

    [RelayCommand]
    private void InsertHighlight() => Insert(AnnotationShape.Highlight);

    [RelayCommand]
    private void InsertBlur() => Insert(AnnotationShape.Blur);

    [RelayCommand]
    private void InsertPixelate() => Insert(AnnotationShape.Pixelate);

    /// <summary>
    /// A new annotation over the marked range (else from the playhead for 3 s), selected so the panel and the preview
    /// show it.
    /// </summary>
    public string Insert(AnnotationShape shape)
    {
        StopPlaying();
        var (from, length) = Timeline.Range is { } range ? (range.From, range.To - range.From) : (Timeline.Playhead, AnnotationItem.DefaultLength);
        var (project, id) = AnnotationEdits.Add(Project, shape, from, length);
        History.Apply("add " + ItemProperties.ShapeName(shape).ToLowerInvariant(), project);
        Timeline.SetRange(null);
        Timeline.Select(id);
        if (!Timeline.Selected!.Covers(Timeline.Playhead))
        {
            Seek(from);
        }

        return id;
    }

    [RelayCommand]
    private void InsertZoom() => AddZoom();

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
        History.Apply("add zoom", project);
        Timeline.SetRange(null);
        Timeline.Select(id);
        if (!Timeline.Selected!.Covers(Timeline.Playhead))
        {
            Seek(from);
        }

        return id;
    }

    [RelayCommand]
    private void MakeAutoZooms() => AutoZoomFromInput();

    /// <summary>
    /// Zooms around the clicks and typing in the recordings' metadata (replacing Auto-Zoom's earlier ones), as one
    /// undo step; says so when there is nothing to zoom to. Returns how many were made.
    /// </summary>
    public int AutoZoomFromInput()
    {
        StopPlaying();
        var (project, count) = CashGear.Capture.VideoEditor.Editing.AutoZoom.Apply(Project, Compositor.Overlays);
        if (count == 0 && !MadeByAutoZoom(Project))
        {
            _dialogs.ShowError(Project.Assets.Any(a => Compositor.Overlays.Metadata(a) is not null)
                ? "Auto-Zoom found no clicks or typing in the recordings' metadata to zoom to."
                : "Auto-Zoom needs a recording's metadata file (clicks, keys and the pointer). Recordings get one when Settings › Overlays › Save a metadata file is on.");
            return 0;
        }

        History.Apply("auto-zoom", project);
        Timeline.Select(null);
        return count;
    }

    [RelayCommand]
    private void InsertClicks() => InsertEffect(EffectKind.Clicks);

    [RelayCommand]
    private void InsertKeystrokes() => InsertEffect(EffectKind.Keystrokes);

    [RelayCommand]
    private void InsertPointer() => InsertEffect(EffectKind.Pointer);

    /// <summary>An effect from the recordings' metadata over the marked range (else the whole timeline), selected.</summary>
    public string InsertEffect(EffectKind kind)
    {
        StopPlaying();
        var (from, length) = Timeline.Range is { } range ? (range.From, range.To - range.From) : (0, Math.Max(Project.Duration, 1));
        var (project, id) = AnnotationEdits.AddEffect(Project, kind, from, length);
        History.Apply(kind switch { EffectKind.Clicks => "add click highlights", EffectKind.Keystrokes => "add keystrokes", _ => "add pointer effects" }, project);
        Timeline.SetRange(null);
        Timeline.Select(id);
        return id;
    }

    /// <summary>A keyframe at the playhead for the selected annotation (where it is now), to move it from there.</summary>
    public void AddKeyframe()
    {
        if (Timeline.Selected is AnnotationItem item && item.Covers(Timeline.Playhead))
        {
            History.Apply("add keyframe", Project.WithItem(item.WithKeyframeAt(Timeline.Playhead, Project.Canvas.Fps)));
        }
    }

    public void RemoveKeyframe()
    {
        if (Timeline.Selected is AnnotationItem item)
        {
            History.Apply("remove keyframe", Project.WithItem(item.WithoutKeyframeAt(Timeline.Playhead, Project.Canvas.Fps)));
        }
    }

    /// <summary>Holds the picture at the playhead for 2 s; what follows moves on.</summary>
    [RelayCommand]
    private void FreezeFrame()
    {
        StopPlaying();
        History.Apply("freeze frame", TransitionEdits.Freeze(Project, Times.FrameStart(Timeline.Playhead, Project.Canvas.Fps)));
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
        StopPlaying();
        var after = TransitionEdits.SetSpeed(Project, clipId, speed, endSpeed);
        _applied = after.Find(clipId)?.Item; // the panel stays while its own slider moves
        History.Apply("speed", after, mergeKey);
    }

    public void SetFreezeLength(string clipId, double length, string mergeKey)
    {
        StopPlaying();
        var after = TransitionEdits.SetFreezeLength(Project, clipId, length);
        _applied = after.Find(clipId)?.Item;
        History.Apply("freeze length", after, mergeKey);
    }

    public void CrossfadeFrom(string clipId)
    {
        StopPlaying();
        _shown = null; // the panel's button becomes "Remove the crossfade"
        History.Apply("crossfade", TransitionEdits.Crossfade(Project, clipId));
    }

    public void RemoveCrossfadeFrom(string clipId)
    {
        StopPlaying();
        _shown = null;
        History.Apply("remove crossfade", TransitionEdits.RemoveCrossfade(Project, clipId));
    }

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
            _dialogs.ShowError($"The project couldn't be saved: {e.Message}");
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
        if (Tracks.Count == rows.Count && Tracks.Zip(rows).All(p => p.First.Track.Id == p.Second.Track.Id))
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
            var actions = new ItemProperties.ClipActions(SetSpeed, SetFreezeLength, CrossfadeFrom, RemoveCrossfadeFrom);
            foreach (var parameter in ItemProperties.For(Project, item, () => Project, ApplyProperty, new ItemProperties.Keyframes(AddKeyframe, RemoveKeyframe), actions))
            {
                Properties.Add(parameter);
            }
        }

        PropertiesTitle = item is null ? "" : ItemProperties.Title(Project, item);
        OnPropertyChanged(nameof(HasProperties));
    }

    private void ApplyProperty(TimelineItem item, string name, string mergeKey)
    {
        _applied = item;
        History.Apply(name, Project.WithItem(item), mergeKey);
    }

    private SKPoint ToCanvas(double x, double y) => new((float)((x - _preview.X) / _preview.Scale), (float)((y - _preview.Y) / _preview.Scale));

    private static bool MadeByAutoZoom(VideoProject project) => project.Tracks.Any(t => t.Items.Any(i => i is ZoomItem { Auto: true }));

    /// <summary>The selected zoom when it is under the playhead and nothing plays: the preview then shows its frame over the whole picture.</summary>
    private ZoomItem? SelectedZoom() =>
        !Engine.IsPlaying && Timeline.Selected is ZoomItem zoom && zoom.Covers(Timeline.Playhead) ? zoom : null;

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
    private static void DrawSelection(SKCanvas canvas, SKRect area, float scale, bool corner)
    {
        using var outline = new SKPaint { Color = SelectionColor, Style = SKPaintStyle.Stroke, StrokeWidth = 2 / scale, IsAntialias = true };
        canvas.DrawRect(area, outline);
        if (corner)
        {
            var grip = 6 / scale;
            using var fill = new SKPaint { Color = SelectionColor, IsAntialias = true };
            canvas.DrawRect(SKRect.Create(area.Right - grip, area.Bottom - grip, grip * 2, grip * 2), fill);
        }
    }

    /// <summary>An annotation's outline (or, for a line, its two ends) and grips.</summary>
    private static void DrawSelection(SKCanvas canvas, AnnotationItem item, double time, float scale)
    {
        if (!item.IsLine)
        {
            DrawSelection(canvas, FrameCompositor.Bounds(item, time), scale, corner: item.Shape != AnnotationShape.Text);
            return;
        }

        var place = item.At(time);
        using var fill = new SKPaint { Color = SelectionColor, IsAntialias = true };
        canvas.DrawCircle((float)place.X, (float)place.Y, 6 / scale, fill);
        canvas.DrawCircle((float)(place.X + place.Width), (float)(place.Y + place.Height), 6 / scale, fill);
    }

    private static readonly SKColor SelectionColor = new(0xFF, 0xB0, 0x20);

    private sealed record PreviewDrag(VideoProject Base, TimelineItem Item, SKPoint Start, SKRect Area, PreviewGrip Grip, int Number);

    [LoggerMessage(Level = LogLevel.Error, Message = "Saving the video project {Path} failed")]
    private partial void LogSaveFailed(Exception e, string path);
}
