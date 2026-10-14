using System.Diagnostics;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.Core.Settings;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CashGear.Capture.App.Tray;

/// <summary>Commands behind the tray context menu.</summary>
public sealed partial class TrayViewModel : ObservableObject
{
    private readonly ITrayNotifier _notifier;
    private readonly IAppLifetime _lifetime;
    private readonly AppPaths _paths;

    public TrayViewModel(ITrayNotifier notifier, IAppLifetime lifetime, AppPaths paths)
    {
        _notifier = notifier;
        _lifetime = lifetime;
        _paths = paths;
    }

    [RelayCommand]
    private void CaptureRegion() =>
        _notifier.Notify("Capture Region", "Region capture is coming in Phase 2.");

    [RelayCommand]
    private void CaptureWindow() =>
        _notifier.Notify("Capture Window", "Window capture is coming in Phase 3.");

    [RelayCommand]
    private Task CaptureFullScreenAsync()
    {
        _notifier.Notify("Capture Full Screen", "Full-screen capture is coming in Phase 1.");
        return Task.CompletedTask;
    }

    /// <summary>Until the settings window exists (Phase 4), open the JSON file in Notepad.</summary>
    [RelayCommand]
    private void OpenSettings() =>
        Process.Start(new ProcessStartInfo("notepad.exe", $"\"{_paths.SettingsFile}\"") { UseShellExecute = true })?.Dispose();

    [RelayCommand]
    private void Exit() => _lifetime.Shutdown();
}
