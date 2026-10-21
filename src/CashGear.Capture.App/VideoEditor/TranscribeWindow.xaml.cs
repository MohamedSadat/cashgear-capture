using System.ComponentModel;
using System.Windows;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>The Captions from Speech window; closing it cancels a running transcription.</summary>
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
}
