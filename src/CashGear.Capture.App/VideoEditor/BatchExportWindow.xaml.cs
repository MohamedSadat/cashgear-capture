using System.IO;
using System.Windows;
using System.Windows.Input;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Presets;
using Microsoft.Win32;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>Chooses what a batch makes; it then runs in the background. Enter starts it, Esc closes.</summary>
public partial class BatchExportWindow : Window, IThemedWindow
{
    public BatchExportWindow(BatchExportViewModel viewModel, AppTheme theme)
    {
        InitializeComponent();
        DataContext = viewModel;
        viewModel.CloseRequested += (_, _) => Close();
        ApplyTheme(theme);
    }

    public void ApplyTheme(AppTheme theme) => Theming.Apply(this, theme);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}

/// <summary>Windows' own dialogs for the batch window.</summary>
internal sealed class WpfBatchPickers : IBatchPickers
{
    public IReadOnlyList<string> PickProjects()
    {
        var dialog = new OpenFileDialog { Title = VideoEditorText.BatchAddProjectsTitle, Filter = VideoEditorWindows.MediaFilter, Multiselect = true, CheckFileExists = true };
        return dialog.ShowDialog() == true ? dialog.FileNames : [];
    }

    public string? PickFolder(string current)
    {
        var dialog = new OpenFolderDialog { Title = VideoEditorText.BatchFolderTitle, InitialDirectory = Directory.Exists(current) ? current : null };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

/// <summary>Opens the batch window, or brings the open one to the front. UI thread only.</summary>
public sealed class BatchExportWindowService(ExportQueue queue, IPresetStore presets, ISettingsService settings, AppPaths paths, Output.BackgroundWorkWindowService background)
{
    private BatchExportWindow? _window;

    public bool IsAllowed => queue.IsAllowed;

    /// <param name="current">A project to start with (the editor's).</param>
    public void Show(BatchSource? current = null)
    {
        if (!queue.IsAllowed)
        {
            return;
        }

        if (_window is { } open)
        {
            if (open.DataContext is BatchExportViewModel model && current is not null && !model.Projects.Contains(current))
            {
                model.Projects.Add(current);
            }

            open.Activate();
            return;
        }

        var folder = paths.ResolveCaptureDirectory(settings.Current.Output);
        var viewModel = new BatchExportViewModel(queue, presets, current is null ? [] : [current], folder, new WpfBatchPickers());
        viewModel.Started += (_, _) => background.Show();
        var window = new BatchExportWindow(viewModel, settings.Current.Appearance.Theme);
        window.Closed += (_, _) => _window = null;
        _window = window;
        window.Show();
        window.Activate();
    }
}
