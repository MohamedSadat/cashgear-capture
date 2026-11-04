using System.Windows;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.App.Recording;

/// <summary>Named recording profiles: the list and the selected one's settings. Esc closes it without saving.</summary>
public partial class RecordingProfilesWindow : Window, IThemedWindow
{
    public RecordingProfilesWindow(RecordingProfilesViewModel viewModel, AppTheme theme)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Done = Close;
        ApplyTheme(theme);
    }

    public void ApplyTheme(AppTheme theme) => Theming.Apply(this, theme);

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Opens the profiles window, or brings the open one to the front. UI thread only.</summary>
public sealed class ProfilesWindowService(ISettingsService settings)
{
    private RecordingProfilesWindow? _window;

    public void Show()
    {
        if (_window is { } open)
        {
            open.Activate();
            return;
        }

        var window = new RecordingProfilesWindow(new RecordingProfilesViewModel(settings), settings.Current.Appearance.Theme);
        window.Closed += (_, _) => _window = null;
        _window = window;
        window.Show();
        window.Activate();
    }
}
