using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Settings;
using Microsoft.Win32;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>
/// The export window: settings, then the export's progress; closing it cancels a running export. Enter exports, Esc
/// cancels the export or closes the window, and screen readers hear the progress and errors.
/// </summary>
public partial class ExportWindow : Window, IThemedWindow
{
    public ExportWindow(ExportViewModel viewModel, AppTheme theme)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.PathPicker = new SaveDialogPicker(this);
        viewModel.CloseRequested += (_, _) => Close();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ExportViewModel.IsRunning))
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
        (DataContext as ExportViewModel)?.Stop();
    }

    private void UpdateCancel() => CancelButton.Content = DataContext is ExportViewModel { IsRunning: true } ? CommonText.Cancel : CommonText.Close;

    /// <summary>The progress line or the error changed: screen readers read it.</summary>
    private void OnLiveTextUpdated(object? sender, DataTransferEventArgs e)
    {
        if (sender is TextBlock text)
        {
            LiveText.Announce(text);
        }
    }

    private sealed class SaveDialogPicker(Window owner) : IExportPathPicker
    {
        public string? Pick(string filter, string fileName, string folder)
        {
            var dialog = new SaveFileDialog
            {
                Title = VideoEditorText.Export,
                Filter = filter,
                FileName = fileName,
                InitialDirectory = folder,
                AddExtension = true,
                OverwritePrompt = true,
            };
            return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
        }
    }
}
