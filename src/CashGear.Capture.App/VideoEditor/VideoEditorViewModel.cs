using System.Collections.ObjectModel;
using System.IO;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Media;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
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

/// <summary>A row header of the timeline: the track's name and its switches.</summary>
public sealed partial class TrackHeaderViewModel(VideoEditorViewModel owner, Track track, double height) : ObservableObject
{
    public Track Track { get; } = track;

    public string Name => Track.Name;

    public double Height { get; } = height;

    public bool HasPictures => Track.HasPictures;

    public bool HasSound => Track.HasSound;

    public string Kind => Track.Kind switch
    {
        TrackKind.Video => "Picture",
        TrackKind.Webcam => "Webcam",
        TrackKind.Audio => "Sound",
        TrackKind.Annotation => "Annotations",
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
        Frames.FrameReady += (_, _) => PreviewInvalidated?.Invoke(this, EventArgs.Empty);
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
        canvas.Translate((width - (size.Width * scale)) / 2, (height - (size.Height * scale)) / 2);
        canvas.Scale(scale);
        canvas.ClipRect(SKRect.Create(size.Width, size.Height));
        var result = Compositor.Render(canvas, Project, Timeline.Playhead);
        canvas.Restore();
        return result.Missing;
    }

    /// <summary>Changes a track (mute, solo, lock, show) as one undo step.</summary>
    public void ChangeTrack(Track track, Func<Track, Track> change, string name)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(change);
        if (Project.Track(track.Id) is { } current)
        {
            History.Apply(name, Project.WithTrack(change(current)));
        }
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
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(HasRange));
        TimelineInvalidated?.Invoke(this, EventArgs.Empty);
    }

    private void OnPreviewsChanged(object? sender, EventArgs e) => TimelineInvalidated?.Invoke(this, EventArgs.Empty);

    /// <summary>The track headers, in the timeline's row order; kept when the tracks didn't change.</summary>
    private void RebuildTracks()
    {
        var rows = TimelineView.Rows(Project);
        if (Tracks.Count == rows.Count && Tracks.Zip(rows).All(p => ReferenceEquals(p.First.Track, p.Second.Track)))
        {
            return;
        }

        Tracks.Clear();
        foreach (var row in rows)
        {
            Tracks.Add(new TrackHeaderViewModel(this, row.Track, row.Height));
        }
    }

    private string Format(double seconds) => TimelineRenderer.Format(seconds, Project.Canvas.Fps, frames: true);

    [LoggerMessage(Level = LogLevel.Error, Message = "Saving the video project {Path} failed")]
    private partial void LogSaveFailed(Exception e, string path);
}
