using System.IO;
using System.Windows;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Media;
using CashGear.Capture.Outputs;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Playback;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>Opens recordings and projects in the video editor.</summary>
public interface IVideoEditorLauncher
{
    /// <summary>Opens a recording (any video) or a <c>.cgcap</c> project in a new editor window; says why when it can't.</summary>
    Task OpenAsync(string path, CancellationToken ct = default);
}

/// <summary>
/// The video editor's windows (one per recording or project): Tray › Video Editor, the recording workflow's step,
/// the <c>--video-editor</c> switch. Media is read with FFmpeg off the UI thread; missing media in a project is
/// offered to be located. On exit every window asks about its unsaved changes.
/// </summary>
public sealed partial class VideoEditorWindows : IVideoEditorLauncher
{
    public const string MediaFilter = "Recordings and projects (*.mp4;*.mkv;*.mov;*.webm;*.cgcap)|*.mp4;*.mkv;*.mov;*.webm;*.cgcap|Projects (*.cgcap)|*.cgcap|All files (*.*)|*.*";

    private readonly List<VideoEditorWindow> _windows = [];
    private readonly IUiDispatcher _ui;
    private readonly ISettingsService _settings;
    private readonly IAppStateStore _state;
    private readonly AppPaths _paths;
    private readonly FfmpegRuntime _runtime;
    private readonly ITrayNotifier _notifier;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger _logger;
    private readonly VideoEditorServices _services;

    public VideoEditorWindows(
        IUiDispatcher ui,
        ISettingsService settings,
        IAppStateStore state,
        AppPaths paths,
        FfmpegRuntime runtime,
        ProjectExporter exporter,
        MediaPreviews previews,
        IShell shell,
        ITrayNotifier notifier,
        ILoggerFactory loggers)
    {
        _ui = ui;
        _settings = settings;
        _state = state;
        _paths = paths;
        _runtime = runtime;
        _notifier = notifier;
        _loggers = loggers;
        _logger = loggers.CreateLogger<VideoEditorWindows>();
        _services = new VideoEditorServices(exporter, previews, settings, state, paths, shell, loggers, () => new WasapiAudioOutput());
    }

    public int OpenCount => _windows.Count;

    /// <summary>The last recording saved, when it is still there.</summary>
    public string? LastRecording => _state.Current.VideoEditor?.LastRecording is { } path && File.Exists(path) ? path : null;

    public async Task OpenAsync(string path, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (_runtime.Problem is { } problem)
        {
            _notifier.Notify("The video editor can't start", problem, NotificationKind.Error);
            return;
        }

        try
        {
            if (string.Equals(Path.GetExtension(path), ProjectFile.Extension, StringComparison.OrdinalIgnoreCase))
            {
                var opened = await Task.Run(() => ProjectFile.Open(path, Path.Combine(_services.CacheDirectory, "projects")), ct).ConfigureAwait(false);
                var project = opened.Project;
                foreach (var missing in opened.Missing)
                {
                    var located = await _ui.InvokeAsync(() => Locate(missing)).ConfigureAwait(false);
                    if (located is not null)
                    {
                        project = ProjectFile.Relocate(project, missing.Id, located);
                    }
                }

                await _state.UpdateAsync(s => s with { VideoEditor = (s.VideoEditor ?? new VideoEditorState()).WithRecent(path) }, ct).ConfigureAwait(false);
                await _ui.InvokeAsync(() => Show(project, path, relocated: !ReferenceEquals(project, opened.Project))).ConfigureAwait(false);
            }
            else
            {
                var asset = await Task.Run(() => ProjectFactory.ReadAsset(path), ct).ConfigureAwait(false);
                var project = ProjectFactory.FromRecording(asset);
                if (project.Duration <= 0)
                {
                    _notifier.Notify("The video editor", $"{Path.GetFileName(path)} has nothing to edit.", NotificationKind.Warning);
                    return;
                }

                await _ui.InvokeAsync(() => Show(project, null, relocated: false)).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is FfmpegException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            LogOpenFailed(e, path);
            _notifier.Notify("The video editor can't open it", $"{Path.GetFileName(path)}: {e.Message}", NotificationKind.Error);
        }
    }

    /// <summary>Tray › Video Editor › Open: a recording or a project picked in a dialog.</summary>
    public async Task PickAndOpenAsync()
    {
        var folder = _paths.ResolveCaptureDirectory(_settings.Current.Output);
        var path = await _ui.InvokeAsync(() =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Open in the video editor",
                Filter = MediaFilter,
                InitialDirectory = Directory.Exists(folder) ? folder : null,
                CheckFileExists = true,
            };
            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }).ConfigureAwait(false);
        if (path is not null)
        {
            await OpenAsync(path).ConfigureAwait(false);
        }
    }

    /// <summary>Remembers a saved recording for Edit the Last Recording.</summary>
    public Task RememberRecordingAsync(string path) =>
        _state.UpdateAsync(s => s with { VideoEditor = (s.VideoEditor ?? new VideoEditorState()) with { LastRecording = path } });

    /// <summary>Closes every window. False if the user kept one open (so the app should not exit).</summary>
    public async Task<bool> CloseAllAsync()
    {
        foreach (var window in _windows.ToList())
        {
            if (!await window.RequestCloseAsync())
            {
                return false;
            }
        }

        return true;
    }

    private void Show(VideoProject project, string? projectPath, bool relocated)
    {
        var theme = _settings.Current.Appearance.Theme;
        VideoEditorWindow? window = null;
        var dialogs = new WindowDialogs(() => window!, theme);
        var viewModel = new VideoEditorViewModel(project, projectPath, _services, dialogs);
        if (relocated)
        {
            viewModel.History.Replace(project); // the new places count as a change to save
        }

        var gpu = (_state.Current.VideoEditor ?? new VideoEditorState()).GpuPreview
            && Environment.GetEnvironmentVariable("CASHGEAR_CPU_PREVIEW") is null;
        window = new VideoEditorWindow(viewModel, theme, gpu, _loggers.CreateLogger<VideoEditorWindow>());
        window.Closed += (_, _) => _windows.Remove(window);
        _windows.Add(window);
        window.Show();
        window.Activate();
        LogOpened(projectPath ?? project.Assets.FirstOrDefault()?.Path ?? "", window.PreviewUsesGpu);
    }

    private static string? Locate(MediaAsset missing)
    {
        var dialog = new OpenFileDialog
        {
            Title = $"Where is {missing.Name}?",
            Filter = $"{missing.Name}|{missing.Name}|Videos and sound (*.mp4;*.mkv;*.mov;*.webm;*.mp3;*.m4a;*.wav;*.ogg)|*.mp4;*.mkv;*.mov;*.webm;*.mp3;*.m4a;*.wav;*.ogg|All files (*.*)|*.*",
            CheckFileExists = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Opened {Path} in the video editor (preview on the graphics card: {Gpu})")]
    private partial void LogOpened(string path, bool gpu);

    [LoggerMessage(Level = LogLevel.Error, Message = "The video editor couldn't open {Path}")]
    private partial void LogOpenFailed(Exception e, string path);

    /// <summary>The window's dialogs (made after the window, which needs the view model that needs them).</summary>
    private sealed class WindowDialogs(Func<Window> owner, AppTheme theme) : IVideoEditorDialogs
    {
        private const string Caption = "CashGear Capture — Video Editor";

        public string? AskProjectPath(string suggestedName, string? folder)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save the project",
                Filter = "Video projects (*.cgcap)|*.cgcap",
                FileName = suggestedName + ProjectFile.Extension,
                InitialDirectory = folder is not null && Directory.Exists(folder) ? folder : null,
                AddExtension = true,
                OverwritePrompt = true,
            };
            return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
        }

        public string? PickMedia(string title, string filter, string? folder)
        {
            var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = filter,
                InitialDirectory = folder is not null && Directory.Exists(folder) ? folder : null,
                CheckFileExists = true,
            };
            return dialog.ShowDialog(owner()) == true ? dialog.FileName : null;
        }

        public SaveChangesChoice AskSaveChanges(string name) =>
            MessageBox.Show(owner(), $"Save the changes to {name}?", Caption, MessageBoxButton.YesNoCancel, MessageBoxImage.Question) switch
            {
                MessageBoxResult.Yes => SaveChangesChoice.Save,
                MessageBoxResult.No => SaveChangesChoice.Discard,
                _ => SaveChangesChoice.Cancel,
            };

        public void ShowError(string message) =>
            MessageBox.Show(owner(), message, Caption, MessageBoxButton.OK, MessageBoxImage.Warning);

        public void ShowExport(ExportViewModel viewModel) =>
            new ExportWindow(viewModel, theme) { Owner = owner() }.ShowDialog();
    }
}

/// <summary>A recording-workflow step: the saved recording opens in the video editor.</summary>
internal sealed class VideoEditorDestination(IVideoEditorLauncher launcher) : IOutputDestination
{
    public string Id => DestinationIds.VideoEditor;

    public string DisplayName => "Open in the video editor";

    public bool SupportsVideo => true;

    public async Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Kind != OutputKind.Video || item.SavedPath is not { } path)
        {
            return OutputResult.Skip("The video editor takes recordings only.");
        }

        await launcher.OpenAsync(path, ct).ConfigureAwait(false);
        return OutputResult.Done("Opened in the video editor");
    }

    public Task<OutputResult> TestAsync(CancellationToken ct) => Task.FromResult(OutputResult.Done("Nothing to set up."));
}
