using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>Pick a frame and a title. Enter uses it for the upload when there is one, Esc closes.</summary>
public partial class ThumbnailWindow : Window, IThemedWindow
{
    public ThumbnailWindow(ThumbnailViewModel viewModel, AppTheme theme)
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

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        (DataContext as ThumbnailViewModel)?.Dispose();
    }
}
