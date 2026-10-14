using System.Windows;
using System.Windows.Input;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.App.Settings;

/// <summary>The settings window. Logic lives in <see cref="SettingsViewModel"/>; this only wires window chrome and focus.</summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsViewModel _viewModel;

    public SettingsWindow(SettingsViewModel viewModel, AppTheme theme)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        Theming.Apply(this, theme);

        viewModel.CloseRequested += (_, _) => Close();
        viewModel.Saved += (_, _) => Theming.Apply(this, viewModel.Theme);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose(); // also resumes hotkeys if a box still had focus
        base.OnClosed(e);
    }

    private void OnHotkeyBoxGotFocus(object sender, KeyboardFocusChangedEventArgs e) => _viewModel.BeginHotkeyRecording();

    private void OnHotkeyBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) => _viewModel.EndHotkeyRecording();
}
