using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Interaction;
using CashGear.Capture.Editor.Model;
using SkiaSharp;

namespace CashGear.Capture.App.Editor;

/// <summary>
/// The editor window. Logic lives in <see cref="EditorViewModel"/> and the editor session; this handles window
/// chrome, keyboard shortcuts, the in-place text box and the unsaved-changes prompt on close.
/// </summary>
public partial class EditorWindow : Window
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
        Theming.Apply(this, theme);
        Surface.CanvasColor = Theming.IsDark(theme) ? new SKColor(0x1E, 0x1E, 0x1E) : new SKColor(0xE3, 0xE3, 0xE3);
        Surface.Attach(viewModel);
        Surface.TextEditRequested += (_, request) => ShowTextEditor(request);
        viewModel.FinishTextEditRequested += (_, _) => CommitText();
        TextEditor.LostKeyboardFocus += (_, _) => CommitText();
        TextEditor.PreviewKeyDown += OnTextEditorKeyDown;
        SizeToImage(viewModel.Document.Width, viewModel.Document.Height);
        Loaded += (_, _) => Surface.Focus();
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

    /// <summary>Single-key tools and editing keys. Ctrl shortcuts are key bindings in the XAML.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        var focus = Keyboard.FocusedElement;
        if (e.Handled || (focus is not null && focus != Surface && focus != this))
        {
            return; // typing in a text box, a slider or the font list
        }

        var shift = Keyboard.Modifiers == ModifierKeys.Shift;
        if (Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Shift))
        {
            return;
        }

        var session = _viewModel.Session;
        switch (e.Key)
        {
            case Key.Space:
                Surface.SpaceHeld = true;
                break;
            case Key.Delete or Key.Back:
                session.DeleteSelection();
                break;
            case Key.Escape:
                session.Cancel();
                break;
            case Key.Enter when _viewModel.IsCropping:
                _viewModel.ApplyCropCommand.Execute(null);
                break;
            case Key.Left or Key.Right or Key.Up or Key.Down when session.Selected is not null:
                var step = shift ? 10 : 1;
                session.Nudge(
                    e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0,
                    e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0);
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
        TextEditor.Visibility = Visibility.Visible;

        // After the mouse click that asked for it has finished, or the canvas would take focus straight back.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            TextEditor.Focus();
            TextEditor.CaretIndex = TextEditor.Text.Length;
        });
    }

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
