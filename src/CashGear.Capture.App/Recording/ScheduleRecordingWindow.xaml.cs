using System.Windows;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Licensing;

namespace CashGear.Capture.App.Recording;

/// <summary>Sets a recording for a time, or for a length. Esc closes it, Enter goes.</summary>
public partial class ScheduleRecordingWindow : Window, IThemedWindow
{
    public ScheduleRecordingWindow(ScheduleRecordingViewModel viewModel, AppTheme theme)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Done = Close;
        ApplyTheme(theme);
    }

    public void ApplyTheme(AppTheme theme) => Theming.Apply(this, theme);

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Opens the window that sets a recording, or brings the open one to the front. UI thread only.</summary>
public sealed class ScheduleWindowService(
    RecordingController recording,
    RecordingScheduler scheduler,
    IMonitorService monitors,
    IWindowService windows,
    ISettingsService settings,
    TimeProvider time,
    IFeatureGate features)
{
    private ScheduleRecordingWindow? _window;

    public void Show()
    {
        if (_window is { } open)
        {
            open.Activate();
            return;
        }

        var viewModel = new ScheduleRecordingViewModel(recording, scheduler, monitors, windows, settings, time, features.IsEnabled(ProFeature.RecordingPro));
        var window = new ScheduleRecordingWindow(viewModel, settings.Current.Appearance.Theme);
        window.Closed += (_, _) => _window = null;
        _window = window;
        window.Show();
        window.Activate();
    }
}
