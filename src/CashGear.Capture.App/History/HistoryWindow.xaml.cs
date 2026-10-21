using System.IO;
using System.Windows;
using System.Windows.Input;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.History;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace CashGear.Capture.App.History;

/// <summary>The History window. Logic lives in <see cref="HistoryViewModel"/>; this supplies the dialogs.</summary>
public partial class HistoryWindow : Window, IThemedWindow
{
    private readonly HistoryViewModel _viewModel;

    public HistoryWindow(HistoryViewModel viewModel, AppTheme theme, string captureFolder)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        ApplyTheme(theme);

        viewModel.AskSavePath = entry => new WpfEditorDialogs(this, theme).AskSavePath(
            captureFolder,
            HistoryActions.SavedFile(entry) is { } saved
                ? Path.GetFileNameWithoutExtension(saved)
                : HistoryText.DefaultFileName(entry.CapturedAt),
            ImageFileFormat.Png);
        viewModel.ConfirmClear = count => AppDialog.Show(
            this,
            HistoryText.ClearConfirm(count),
            HistoryText.WindowTitle,
            DialogButtons.YesNo,
            DialogKind.Question,
            theme) == DialogAnswer.Yes;
        Loaded += (_, _) =>
        {
            List.Focus();
            if (viewModel.Items.Count > 0)
            {
                viewModel.Selected ??= viewModel.Items[0];
            }
        };
    }

    public void ApplyTheme(AppTheme theme) => Theming.Apply(this, theme);

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is FrameworkElement { DataContext: HistoryItem } && _viewModel.OpenCommand.CanExecute(null))
        {
            _viewModel.OpenCommand.Execute(null);
        }
    }
}

/// <summary>Opens the History window, or brings the open one to the front. UI thread only.</summary>
public sealed class HistoryWindowService(IServiceProvider services, ISettingsService settings, AppPaths paths)
{
    private HistoryWindow? _window;

    public void Show()
    {
        if (_window is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return;
        }

        var viewModel = ActivatorUtilities.CreateInstance<HistoryViewModel>(services);
        var window = new HistoryWindow(viewModel, settings.Current.Appearance.Theme, paths.ResolveCaptureDirectory(settings.Current.Output));
        window.Closed += (_, _) => _window = null;
        _window = window;
        window.Show();
        window.Activate();
    }
}
