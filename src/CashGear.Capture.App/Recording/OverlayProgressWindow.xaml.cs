using System.Windows;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.App.Recording;

/// <summary>The window shown while overlays are added to a recording: progress and Cancel (closing it cancels too).</summary>
public partial class OverlayProgressWindow : Window, IThemedWindow, IOverlayProgress
{
    private readonly OverlayProgressViewModel _viewModel;
    private bool _done;

    public OverlayProgressWindow(OverlayProgressViewModel viewModel, AppTheme theme)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        ApplyTheme(theme);
        Closing += (_, _) =>
        {
            if (!_done)
            {
                viewModel.CancelCommand.Execute(null);
            }
        };
    }

    public static OverlayProgressWindow Open(string name, Action cancel, AppTheme theme)
    {
        var window = new OverlayProgressWindow(new OverlayProgressViewModel(name, cancel), theme);
        window.Show();
        return window;
    }

    public void Report(double progress) => _viewModel.Progress = progress;

    public void ApplyTheme(AppTheme theme) => Theming.Apply(this, theme);

    public void Dispose()
    {
        _done = true;
        Close();
    }
}
