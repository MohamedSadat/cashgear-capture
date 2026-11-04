using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>
/// A video editor window. Logic lives in <see cref="VideoEditorViewModel"/>; this wires the preview and the
/// timeline to it, handles the keys that aren't Ctrl shortcuts (never while typing in a text box or a list), and asks
/// about unsaved changes on close.
/// </summary>
public partial class VideoEditorWindow : Window, IThemedWindow
{
    private bool _closeConfirmed;
    private bool _focusFromKeyboard;
    private double _transcriptWidth = 330;
    private double _mediaWidth = 240;

    public VideoEditorWindow(VideoEditorViewModel viewModel, AppTheme theme, bool tryGpu, ILogger logger)
    {
        InitializeComponent();
        DataContext = viewModel;
        Preview.Attach(viewModel, tryGpu, logger);
        TimelineSurface.Attach(viewModel);
        TranscriptPanelView.Attach(viewModel.Transcript);
        ShowTranscript(viewModel.Transcript.IsShown);
        viewModel.Transcript.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TranscriptPanelViewModel.IsShown))
            {
                ShowTranscript(viewModel.Transcript.IsShown);
            }
        };
        ShowMedia(viewModel.Media.IsShown);
        viewModel.Media.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MediaPanelViewModel.IsShown))
            {
                ShowMedia(viewModel.Media.IsShown);
            }
        };
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

        // Whether the focus came with Tab or by a click: Space presses a button tabbed to, and plays after a click.
        PreviewGotKeyboardFocus += (_, _) => _focusFromKeyboard = InputManager.Current.MostRecentInputDevice is KeyboardDevice;
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

    /// <summary>The transcript panel beside the preview.</summary>
    internal TranscriptPanel TranscriptArea2 => TranscriptPanelView;

    /// <summary>The media panel beside the preview.</summary>
    internal MediaPanel MediaArea2 => MediaPanelView;

    /// <summary>Whether the preview draws on the graphics card.</summary>
    public bool PreviewUsesGpu => Preview.UsesGpu;

    /// <summary>Frames the preview has drawn (for tests).</summary>
    public int PreviewPaints => Preview.Paints;

    /// <summary>The timeline's colours, the preview's surround and its selection outline follow the theme and high contrast.</summary>
    public void ApplyTheme(AppTheme theme)
    {
        Theming.Apply(this, theme);
        var dark = Theming.IsDark(theme);
        TimelineSurface.Colors = VideoEditorPalette.Timeline(dark);
        if (DataContext is VideoEditorViewModel viewModel)
        {
            viewModel.SelectionColor = VideoEditorPalette.PreviewSelection;
        }

        Preview.Surround = VideoEditorPalette.PreviewSurround(dark);
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

    /// <summary>Whether <paramref name="element"/> takes typed keys itself: a text box, a password box, a list to pick from, or the transcript's words.</summary>
    internal static bool IsTextInput(object? element) => element is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem or TranscriptControl;

    /// <summary>
    /// The keys without Ctrl: S splits, I and O mark, Delete (Shift: ripple) deletes, Esc clears the selection, ← and →
    /// step a frame, Home and End go to the ends, Alt+← and Alt+→ move the selected item a frame (Shift: a second).
    /// False for any other key.
    /// </summary>
    internal static bool HandleKey(VideoEditorViewModel viewModel, Key key, ModifierKeys modifiers)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ICommand? command = (key, modifiers) switch
        {
            (Key.S, ModifierKeys.None) => viewModel.SplitCommand,
            (Key.M, ModifierKeys.None) => viewModel.AddMarkerCommand,
            (Key.Left, ModifierKeys.Control) => viewModel.PreviousMarkerCommand,
            (Key.Right, ModifierKeys.Control) => viewModel.NextMarkerCommand,
            (Key.I, ModifierKeys.None) => viewModel.MarkInCommand,
            (Key.O, ModifierKeys.None) => viewModel.MarkOutCommand,
            (Key.Delete, ModifierKeys.None) => viewModel.DeleteCommand,
            (Key.Delete, ModifierKeys.Shift) => viewModel.RippleDeleteCommand,
            (Key.Escape, ModifierKeys.None) => viewModel.ClearSelectionCommand,
            (Key.Left, ModifierKeys.None) => viewModel.StepBackCommand,
            (Key.Right, ModifierKeys.None) => viewModel.StepForwardCommand,
            (Key.Home, ModifierKeys.None) => viewModel.GoToStartCommand,
            (Key.End, ModifierKeys.None) => viewModel.GoToEndCommand,
            _ => null,
        };
        if (command is not null)
        {
            if (command.CanExecute(null))
            {
                command.Execute(null);
            }

            return true;
        }

        if (key is Key.Left or Key.Right && (modifiers == ModifierKeys.Alt || modifiers == (ModifierKeys.Alt | ModifierKeys.Shift)))
        {
            var frames = modifiers.HasFlag(ModifierKeys.Shift) ? viewModel.Project.Canvas.Fps : 1;
            viewModel.MoveInTime(key == Key.Left ? -frames : frames);
            return true;
        }

        return false;
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

    /// <summary>After the control with the focus had its chance (a slider takes the arrows, a text box Home and End).</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not VideoEditorViewModel viewModel || IsTextInput(Keyboard.FocusedElement))
        {
            return;
        }

        // With Alt held the key comes as a system key.
        e.Handled = HandleKey(viewModel, e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private Point? _headerPressed;

    /// <summary>What a track header's drag carries: its track's id (a format name, not text).</summary>
    [System.ComponentModel.Localizable(false)]
    private const string TrackFormat = "CashGear.Capture.Track";

    private void OnHeaderPress(object sender, MouseButtonEventArgs e) =>
        _headerPressed = e.OriginalSource is DependencyObject source && FindAncestor<ButtonBase>(source) is null && FindAncestor<Slider>(source) is null ? e.GetPosition(Headers) : null;

    private void OnHeaderMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _headerPressed is not { } from || sender is not FrameworkElement { DataContext: TrackHeaderViewModel header })
        {
            return;
        }

        var moved = e.GetPosition(Headers) - from;
        if (Math.Abs(moved.Y) >= SystemParameters.MinimumVerticalDragDistance)
        {
            _headerPressed = null;
            DragDrop.DoDragDrop((DependencyObject)sender, new DataObject(TrackFormat, header.Track.Id), DragDropEffects.Move);
        }
    }

    private void OnHeaderDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(TrackFormat) is string track && sender is FrameworkElement { DataContext: TrackHeaderViewModel target } && DataContext is VideoEditorViewModel viewModel)
        {
            viewModel.MoveTrackTo(track, target.Track.Id);
            e.Handled = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject element)
        where T : DependencyObject
    {
        for (var at = element; at is not null; at = at is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
        {
            if (at is T found)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>The media panel's column takes its width back when it is shown, and all of it when it is hidden.</summary>
    private void ShowMedia(bool shown)
    {
        if (shown)
        {
            MediaColumn.Width = new GridLength(Math.Max(_mediaWidth, 180));
            MediaSplitterColumn.Width = new GridLength(5);
        }
        else
        {
            if (MediaColumn.ActualWidth > 0)
            {
                _mediaWidth = MediaColumn.ActualWidth;
            }

            MediaColumn.Width = new GridLength(0);
            MediaSplitterColumn.Width = new GridLength(0);
        }

        MediaArea.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        MediaSplitter.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The transcript's column takes its width back when it is shown, and all of it when it is hidden.</summary>
    private void ShowTranscript(bool shown)
    {
        if (shown)
        {
            TranscriptColumn.Width = new GridLength(Math.Max(_transcriptWidth, 240));
            TranscriptSplitterColumn.Width = new GridLength(5);
            TranscriptArea.Visibility = Visibility.Visible;
            TranscriptSplitter.Visibility = Visibility.Visible;
        }
        else
        {
            if (TranscriptColumn.ActualWidth > 0)
            {
                _transcriptWidth = TranscriptColumn.ActualWidth;
            }

            TranscriptColumn.Width = new GridLength(0);
            TranscriptSplitterColumn.Width = new GridLength(0);
            TranscriptArea.Visibility = Visibility.Collapsed;
            TranscriptSplitter.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>
    /// Space plays or pauses wherever the focus is, except in a text box or a list, and on a button or switch the
    /// keyboard moved to (Space presses it, as everywhere).
    /// </summary>
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var focused = Keyboard.FocusedElement;
        if (e.Key != Key.Space || Keyboard.Modifiers != ModifierKeys.None || (IsTextInput(focused) && focused is not TranscriptControl) || focused is MenuItem
            || (focused is ButtonBase && _focusFromKeyboard))
        {
            return;
        }

        (DataContext as VideoEditorViewModel)?.PlayPauseCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>A live status line changed: screen readers read it (the time only when a key moved it, not while playing).</summary>
    private void OnLiveTextUpdated(object? sender, DataTransferEventArgs e)
    {
        if (sender is TextBlock text && !(text == TimeLabel && DataContext is VideoEditorViewModel { IsPlaying: true }))
        {
            LiveText.Announce(text);
        }
    }

    private void UpdatePlayButton()
    {
        var playing = DataContext is VideoEditorViewModel { IsPlaying: true };
        PlayButton.Content = playing ? "⏸" : "⏵";
        AutomationProperties.SetName(PlayButton, playing ? VideoEditorText.PauseButtonName : VideoEditorText.PlayButtonName);
    }
}
