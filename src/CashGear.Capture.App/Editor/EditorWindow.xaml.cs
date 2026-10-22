using System.ComponentModel;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Editor.Model;
using CashGear.Capture.Editor.Text;

namespace CashGear.Capture.App.Editor;

/// <summary>
/// The editor window. Logic lives in <see cref="EditorViewModel"/> and the editor session; this handles window
/// chrome, keyboard shortcuts, the in-place text box and the unsaved-changes prompt on close.
/// </summary>
public partial class EditorWindow : Window, IThemedWindow
{
    private const double PanelWidth = 236;
    private const double ChromeHeight = 150; // toolbar, status bar and title bar, roughly
    private const double ToolbarWidth = 1180; // narrower and the toolbar wraps onto a second row

    private readonly EditorViewModel _viewModel;
    private bool _closeConfirmed;
    private bool _committingText;

    public EditorWindow(EditorViewModel viewModel, AppTheme theme)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        ApplyTheme(theme);
        Surface.Attach(viewModel);
        Surface.TextEditRequested += (_, request) => ShowTextEditor(request);
        viewModel.FinishTextEditRequested += (_, _) => CommitText();
        TextEditor.LostKeyboardFocus += (_, _) => CommitText();
        TextEditor.PreviewKeyDown += OnTextEditorKeyDown;
        TextEditor.TextChanged += (_, _) => FollowTextDirection();
        viewModel.PropertyChanged += OnViewModelChanged;
        AddHandler(ButtonBase.ClickEvent, new RoutedEventHandler(OnButtonClick), handledEventsToo: true);
        SizeToImage(viewModel.Document.Width, viewModel.Document.Height);
        Loaded += (_, _) => Surface.Focus();
    }

    public void ApplyTheme(AppTheme theme)
    {
        Theming.Apply(this, theme);
        Surface.Colors = CanvasColors.For(Theming.IsDark(theme)); // the system's under high contrast
    }

    /// <summary>Closes the window, asking about unsaved changes first. False if the user chose to keep it open.</summary>
    public async Task<bool> RequestCloseAsync()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        if (!await _viewModel.ConfirmCloseAsync())
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
        CommitText();
        if (_closeConfirmed || !_viewModel.IsDirty)
        {
            return;
        }

        // Ask after the close attempt returns (a message box can't run inside Closing's synchronous handler).
        e.Cancel = true;
        Dispatcher.InvokeAsync(async () =>
        {
            if (await _viewModel.ConfirmCloseAsync())
            {
                _closeConfirmed = true;
                Close();
            }
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        Surface.Release();
        _viewModel.Dispose();
        base.OnClosed(e);
    }

    /// <summary>
    /// The editor's keys. Tool letters, Delete and Esc work wherever the focus is, except while typing (a text box,
    /// the font list, a menu); Tab, the arrows, Enter and Space are the canvas's while it has the focus (elsewhere
    /// they move between and press controls). Ctrl shortcuts are key bindings in the XAML.
    /// </summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var focus = Keyboard.FocusedElement;
        if (e.Handled || OwnsLetters(focus))
        {
            return;
        }

        var onCanvas = focus == Surface;
        var modifiers = Keyboard.Modifiers;
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            // Ctrl+Shift+arrows: resize (right and down make it bigger).
            e.Handled = onCanvas && Arrow(e.Key) is { } grow && _viewModel.Session.ResizeBy(grow.X, grow.Y);
            return;
        }

        if (modifiers is not (ModifierKeys.None or ModifierKeys.Shift))
        {
            return; // Ctrl+Tab and Alt keys keep their usual meaning: Ctrl+Tab leaves the canvas
        }

        var shift = modifiers == ModifierKeys.Shift;
        var session = _viewModel.Session;
        switch (e.Key)
        {
            case Key.Space when onCanvas || focus is not ButtonBase:
                Surface.SpaceHeld = true;
                break;
            case Key.Delete or Key.Back when session.Selected is not null:
                session.DeleteSelection();
                break;
            case Key.Escape:
                if (!session.Cancel() && !onCanvas)
                {
                    return;
                }

                break;
            case Key.Enter when onCanvas:
                _viewModel.Enter();
                break;
            case Key.Tab when onCanvas:
                if (!session.SelectNext(backwards: shift))
                {
                    return; // nothing to select: on to the next control
                }

                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when onCanvas:
                var step = Arrow(e.Key)!.Value;
                _viewModel.Nudge(shift ? step.X * 10 : step.X, shift ? step.Y * 10 : step.Y);
                break;
            default:
                if (ToolForKey(e.Key) is not { } tool || shift)
                {
                    return;
                }

                _viewModel.Tool = tool;
                break;
        }

        e.Handled = true;
    }

    /// <summary>Controls that need letters and the editing keys themselves: text boxes, the font list, menus.</summary>
    private static bool OwnsLetters(IInputElement? focus) =>
        focus is TextBoxBase or PasswordBox or ComboBox or ComboBoxItem or MenuItem or Menu;

    /// <summary>An arrow key's direction (one pixel), or null for other keys.</summary>
    private static (int X, int Y)? Arrow(Key key) => key switch
    {
        Key.Left => (-1, 0),
        Key.Right => (1, 0),
        Key.Up => (0, -1),
        Key.Down => (0, 1),
        _ => null,
    };

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (e.Key == Key.Space)
        {
            Surface.SpaceHeld = false;
        }
    }

    private static EditorTool? ToolForKey(Key key) => key switch
    {
        Key.V => EditorTool.Select,
        Key.A => EditorTool.Arrow,
        Key.L => EditorTool.Line,
        Key.R => EditorTool.Rectangle,
        Key.E => EditorTool.Ellipse,
        Key.D => EditorTool.Freehand,
        Key.H => EditorTool.Highlight,
        Key.T => EditorTool.Text,
        Key.N => EditorTool.StepNumber,
        Key.B => EditorTool.Blur,
        Key.X => EditorTool.Pixelate,
        Key.C => EditorTool.Crop,
        Key.M => EditorTool.Area,
        Key.O => EditorTool.Spotlight,
        Key.G => EditorTool.Magnify,
        _ => null,
    };

    /// <summary>Puts the text box over the image where the text goes, at the zoomed size, so typing looks in place.</summary>
    private void ShowTextEditor(TextEditRequest request)
    {
        var zoom = _viewModel.Viewport.Zoom;
        var scale = Surface.UiScale;
        var style = request.Style;
        var topLeft = Surface.ToDips(_viewModel.Viewport.ToView(request.Position));
        var foreground = EditorViewModel.ToBrush(style.Color);

        TextEditor.FontFamily = new FontFamily(style.FontFamily);
        TextEditor.FontSize = Math.Max(1, style.FontSize * zoom / scale);
        TextEditor.FontWeight = style.Bold ? FontWeights.Bold : FontWeights.Normal;
        TextEditor.Foreground = foreground;
        TextEditor.CaretBrush = foreground;
        TextEditor.Background = style.HasFill ? EditorViewModel.ToBrush(style.Fill) : Brushes.Transparent;
        TextEditor.Padding = new Thickness(Math.Max(0, (TextLayout.PaddingFor(style) * zoom / scale) - 1));
        Canvas.SetLeft(TextEditor, topLeft.X - 1);
        Canvas.SetTop(TextEditor, topLeft.Y - 1);
        TextEditor.Text = request.Text;
        FollowTextDirection();
        TextEditor.Visibility = Visibility.Visible;

        // After the mouse click that asked for it has finished, or the canvas would take focus straight back.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            TextEditor.Focus();
            TextEditor.CaretIndex = TextEditor.Text.Length;
        });
    }

    /// <summary>Arabic or Hebrew text is typed right to left, as the annotation will show it (right-aligned).</summary>
    private void FollowTextDirection() =>
        TextEditor.FlowDirection = Bidi.IsRightToLeft(TextEditor.Text) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    /// <summary>
    /// A button clicked with the mouse gives the focus back to the canvas, so the canvas's keys (arrows, Space+drag)
    /// go on working as before the click; buttons reached with Tab keep it.
    /// </summary>
    private void OnButtonClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is ButtonBase { IsKeyboardFocused: true }
            && InputManager.Current.MostRecentInputDevice is not KeyboardDevice
            && _viewModel.Session.TextEdit is null)
        {
            Surface.Focus();
        }
    }

    /// <summary>A status message ("Copied to the clipboard") is read out by screen readers when it appears.</summary>
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(EditorViewModel.Message) && _viewModel.Message.Length > 0
            && AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
        {
            // Once the binding has put the new text in the label.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                UIElementAutomationPeer.CreatePeerForElement(MessageText)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged));
        }
    }

    /// <summary>Presets saved since the menu was last opened show up at once.</summary>
    private void OnEffectsOpened(object sender, RoutedEventArgs e) => (DataContext as EditorViewModel)?.RefreshPresets();

    /// <summary>Destinations set up in Settings since the menu was last opened show up at once.</summary>
    private void OnSendToOpened(object sender, RoutedEventArgs e) => (DataContext as EditorViewModel)?.RefreshSendTargets();

    private void OnTextEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control))
        {
            CommitText();
            e.Handled = true;
        }
    }

    private void CommitText()
    {
        if (_committingText || _viewModel.Session.TextEdit is null)
        {
            return;
        }

        _committingText = true;
        try
        {
            var text = TextEditor.Text;
            TextEditor.Visibility = Visibility.Collapsed;
            _viewModel.Session.CommitTextEdit(text);
        }
        finally
        {
            _committingText = false;
        }

        Surface.Focus();
    }

    /// <summary>Opens about the size of the image (at 100%) and wide enough for the toolbar, within 90% of the screen.</summary>
    private void SizeToImage(int imageWidth, int imageHeight)
    {
        var work = SystemParameters.WorkArea;
        Width = Math.Clamp(Math.Max(imageWidth + PanelWidth + 90, ToolbarWidth), MinWidth, work.Width * 0.9);
        Height = Math.Clamp(imageHeight + ChromeHeight + 48, MinHeight, work.Height * 0.9);
    }
}
