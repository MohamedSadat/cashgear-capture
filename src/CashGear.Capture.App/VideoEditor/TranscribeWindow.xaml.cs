using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>
/// The Captions from Speech window; closing it cancels a running transcription. Enter starts, Esc cancels or closes,
/// and screen readers hear the progress and errors.
/// </summary>
public partial class TranscribeWindow : Window, IThemedWindow
{
    public TranscribeWindow(TranscribeViewModel viewModel, AppTheme theme)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TranscribeViewModel.IsRunning))
            {
                UpdateCancel();
            }
        };
        ApplyTheme(theme);
        UpdateCancel();
    }

    public void ApplyTheme(AppTheme theme) => Theming.Apply(this, theme);

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        (DataContext as TranscribeViewModel)?.Stop();
    }

    private void UpdateCancel() => CancelButton.Content = DataContext is TranscribeViewModel { IsRunning: true } ? CommonText.Cancel : CommonText.Close;

    /// <summary>The status line or the error changed: screen readers read it.</summary>
    private void OnLiveTextUpdated(object? sender, DataTransferEventArgs e)
    {
        if (sender is TextBlock text)
        {
            LiveText.Announce(text);
        }
    }
}
