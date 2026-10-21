using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.VideoEditor.Timeline;
using Microsoft.Extensions.Logging;
using SkiaSharp;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>
/// A video editor window. Logic lives in <see cref="VideoEditorViewModel"/>; this wires the preview and the
/// timeline to it, handles Space (play/pause wherever the focus is, except in a text box) and asks about unsaved
/// changes on close.
/// </summary>
public partial class VideoEditorWindow : Window, IThemedWindow
{
    private bool _closeConfirmed;

    public VideoEditorWindow(VideoEditorViewModel viewModel, AppTheme theme, bool tryGpu, ILogger logger)
    {
        InitializeComponent();
        DataContext = viewModel;
        Preview.Attach(viewModel, tryGpu, logger);
        TimelineSurface.Attach(viewModel);
        ApplyTheme(theme);
        UpdatePlayButton();
        viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VideoEditorViewModel.IsPlaying))
            {
                UpdatePlayButton();
            }
        };
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) =>
        {
            viewModel.View.Width = TimelineSurface.ActualWidth;
            viewModel.ZoomFitCommand.Execute(null);
            TimelineSurface.Focus();
        };
        Closed += (_, _) =>
        {
            Preview.Stop();
            TimelineSurface.Release();
            (DataContext as VideoEditorViewModel)?.Dispose();
        };
    }

    /// <summary>Whether the preview draws on the graphics card.</summary>
    public bool PreviewUsesGpu => Preview.UsesGpu;

    /// <summary>Frames the preview has drawn (for tests).</summary>
    public int PreviewPaints => Preview.Paints;

    public void ApplyTheme(AppTheme theme)
    {
        Theming.Apply(this, theme);
        var dark = Theming.IsDark(theme);
        TimelineSurface.Colors = dark ? TimelineColors.Dark : TimelineColors.Light;
        Preview.Surround = dark ? new SKColor(0x12, 0x12, 0x12) : new SKColor(0x30, 0x30, 0x30);
    }

    /// <summary>Closes the window, asking about unsaved changes first. False if the user chose to keep it open.</summary>
    public async Task<bool> RequestCloseAsync()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        if (DataContext is VideoEditorViewModel viewModel && !await viewModel.ConfirmCloseAsync())
        {
            return false;
        }

        _closeConfirmed = true;
        Close();
        return true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || DataContext is not VideoEditorViewModel { IsDirty: true })
        {
            return;
        }

        // Ask after the close attempt returns (a message box can't run inside Closing's synchronous handler).
        e.Cancel = true;
        Dispatcher.BeginInvoke(async () => await RequestCloseAsync());
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && Keyboard.FocusedElement is not TextBox && Keyboard.Modifiers == ModifierKeys.None)
        {
            (DataContext as VideoEditorViewModel)?.PlayPauseCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void UpdatePlayButton() =>
        PlayButton.Content = DataContext is VideoEditorViewModel { IsPlaying: true } ? "⏸" : "⏵";
}
