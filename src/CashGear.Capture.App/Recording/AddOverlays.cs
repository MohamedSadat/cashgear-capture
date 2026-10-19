using System.IO;
using System.Windows;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Recording;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CashGear.Capture.App.Recording;

/// <summary>Picks a saved recording (behind an interface for tests).</summary>
public interface IRecordingPicker
{
    /// <summary>The chosen MP4, or null if the user cancelled.</summary>
    string? Pick(string? folder);
}

internal sealed class WpfRecordingPicker : IRecordingPicker
{
    public string? Pick(string? folder)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add overlays to a recording",
            Filter = "Recordings (*.mp4)|*.mp4",
            InitialDirectory = folder is not null && Directory.Exists(folder) ? folder : null,
        };
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }
}

/// <summary>Shows how far adding overlays has got, and lets it be cancelled.</summary>
public interface IOverlayProgress : IDisposable
{
    void Report(double progress);
}

/// <summary>The progress window's state: what is being done and how far, and Cancel.</summary>
public sealed partial class OverlayProgressViewModel(string name, Action cancel) : ObservableObject
{
    public string Title => $"Adding overlays to {name}";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PercentText))]
    private double _progress;

    public string PercentText => $"{Progress:P0}";

    [RelayCommand]
    private void Cancel() => cancel();
}

/// <summary>
/// Tray › Add Overlays to a Recording…: pick a saved recording, then draw its overlays in from its metadata file
/// (<see cref="IOverlayRenderer"/>) with a progress window, and say where the new file is.
/// </summary>
public sealed partial class AddOverlaysController(
    IOverlayRenderer renderer,
    IRecordingPicker picker,
    ISettingsService settings,
    AppPaths paths,
    ITrayNotifier notifier,
    IShell shell,
    IUiDispatcher ui,
    ILogger<AddOverlaysController> logger,
    Func<string, Action, IOverlayProgress>? showProgress = null)
{
    private readonly Func<string, Action, IOverlayProgress> _showProgress = showProgress ?? ((name, cancel) => OverlayProgressWindow.Open(name, cancel, settings.Current.Appearance.Theme));
    private int _busy;

    /// <summary>Adding overlays to a recording now.</summary>
    public bool IsBusy => Volatile.Read(ref _busy) == 1;

    public async Task RunAsync()
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            return;
        }

        try
        {
            var folder = paths.ResolveCaptureDirectory(settings.Current.Output);
            var video = await ui.InvokeAsync(() => picker.Pick(folder)).ConfigureAwait(false);
            if (video is null)
            {
                return;
            }

            var name = Path.GetFileName(video);
            if (renderer.FindMetadata(video) is null)
            {
                notifier.Notify(
                    "Add overlays",
                    $"{name} has no metadata file, so its clicks and keys aren't known. Recordings get one when Settings › Overlays › Save a metadata file is on.",
                    NotificationKind.Warning);
                return;
            }

            using var cancel = new CancellationTokenSource();
            var window = await ui.InvokeAsync(() => _showProgress(name, cancel.Cancel)).ConfigureAwait(false);
            try
            {
                var progress = new Progress<double>(p => _ = ui.InvokeAsync(() => window.Report(p)));
                var output = await renderer.RenderAsync(video, progress, cancel.Token).ConfigureAwait(false);
                notifier.Notify("Overlays added", $"{Path.GetFileName(output)}\nin {Path.GetDirectoryName(output)}", NotificationKind.Info, onClick: () => shell.ShowInFolder(output));
            }
            catch (OperationCanceledException)
            {
                // the user cancelled: nothing was kept
            }
            catch (Exception e) when (e is CaptureException or IOException or UnauthorizedAccessException)
            {
                LogFailed(e, video);
                notifier.Notify("Overlays couldn't be added", e.Message, NotificationKind.Error);
            }
            finally
            {
                await ui.InvokeAsync(window.Dispose).ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Adding overlays to {Video} failed")]
    private partial void LogFailed(Exception e, string video);
}
