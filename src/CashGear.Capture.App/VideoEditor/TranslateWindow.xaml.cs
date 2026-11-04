using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>The Translate window; closing it cancels a running translation. Enter starts, Esc cancels or closes, and screen readers hear the progress and errors.</summary>
public partial class TranslateWindow : Window, IThemedWindow
{
    public TranslateWindow(TranslateViewModel viewModel, AppTheme theme)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TranslateViewModel.IsRunning))
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
        (DataContext as TranslateViewModel)?.Stop();
    }

    private void UpdateCancel() => CancelButton.Content = DataContext is TranslateViewModel { IsRunning: true } ? CommonText.Cancel : CommonText.Close;

    private void OnLiveTextUpdated(object? sender, DataTransferEventArgs e)
    {
        if (sender is TextBlock text)
        {
            LiveText.Announce(text);
        }
    }
}
