using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.App.Settings;

/// <summary>
/// The settings window. Logic lives in <see cref="SettingsViewModel"/>; this only wires window chrome and focus, and
/// runs the sound meters (twenty times a second) while the Recording page is shown.
/// </summary>
public partial class SettingsWindow : Window, IThemedWindow
{
    private readonly SettingsViewModel _viewModel;
    private readonly DispatcherTimer _meters;

    public SettingsWindow(SettingsViewModel viewModel, AppTheme theme)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        ApplyTheme(theme);

        viewModel.CloseRequested += (_, _) => Close();
        var clock = Stopwatch.StartNew();
        _meters = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => viewModel.RefreshMeters(clock.Elapsed), Dispatcher);
    }

    public void ApplyTheme(AppTheme theme) => Theming.Apply(this, theme);

    protected override void OnClosed(EventArgs e)
    {
        _meters.Stop();
        _viewModel.Dispose(); // also resumes hotkeys if a box still had focus, and closes the sound devices
        base.OnClosed(e);
    }

    /// <summary>The meters listen only while the Recording page is shown (the microphone light is on meanwhile).</summary>
    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs))
        {
            return; // a list on a page, not the page itself
        }

        if (ReferenceEquals(Tabs.SelectedItem, RecordingTab))
        {
            _viewModel.StartMonitoring();
            _meters.Start();
        }
        else
        {
            _meters.Stop();
            _viewModel.StopMonitoring();
        }
    }

    /// <summary>The running apps are listed when the list opens (not when the window does: listing them takes a moment).</summary>
    private void OnExcludedAppsOpened(object? sender, EventArgs e) => _viewModel.Overlays.RefreshRunningApps();

    private void OnHotkeyBoxGotFocus(object sender, KeyboardFocusChangedEventArgs e) => _viewModel.BeginHotkeyRecording();

    private void OnHotkeyBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) => _viewModel.EndHotkeyRecording();
}
