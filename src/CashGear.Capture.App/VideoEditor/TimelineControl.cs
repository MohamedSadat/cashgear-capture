using System.Windows.Automation.Peers;
using System.Windows.Input;
using CashGear.Capture.App.Wpf;
using CashGear.Capture.VideoEditor.Timeline;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>
/// The timeline, drawn with Skia by <see cref="TimelineRenderer"/> at physical-pixel resolution; the mouse goes to
/// the <see cref="TimelineSession"/> in view pixels (DIPs). The wheel scrolls in time, Ctrl+wheel zooms at the mouse
/// and Shift+wheel scrolls the rows. With the focus (a frame round it shows it): Tab and Shift+Tab select the next
/// and previous item, ↑ and ↓ move the selected one to the track above or below; Ctrl+Tab moves on to the next
/// control. Screen readers see its tracks and items (<see cref="TimelineAutomationPeer"/>).
/// </summary>
internal sealed class TimelineControl : SKElement
{
    private readonly TimelineRenderer _renderer = new();
    private VideoEditorViewModel? _viewModel;

    public TimelineControl()
    {
        Focusable = true;
        FocusVisualStyle = null; // the frame is drawn with the timeline
        ClipToBounds = true;
    }

    internal VideoEditorViewModel? ViewModel => _viewModel;

    public TimelineColors Colors
    {
        get => _renderer.Colors;
        set
        {
            _renderer.Colors = value;
            InvalidateVisual();
        }
    }

    public void Attach(VideoEditorViewModel viewModel)
    {
        _viewModel = viewModel;
        _renderer.Previews = viewModel.Previews;
        viewModel.TimelineInvalidated += (_, _) => Dispatcher.BeginInvoke(InvalidateVisual);
        viewModel.View.Changed += (_, _) => InvalidateVisual();
        viewModel.Timeline.Changed += (_, _) =>
        {
            InvalidateVisual();
            (UIElementAutomationPeer.FromElement(this) as TimelineAutomationPeer)?.TimelineChanged();
        };
        viewModel.History.Changed += (_, _) => (UIElementAutomationPeer.FromElement(this) as TimelineAutomationPeer)?.ProjectChanged();
        SizeChanged += (_, _) =>
        {
            viewModel.View.Width = ActualWidth;
            viewModel.View.Height = ActualHeight;
        };
    }

    public void Release() => _renderer.Dispose();

    protected override AutomationPeer OnCreateAutomationPeer() => new TimelineAutomationPeer(this);

    protected override void OnPaintSurface(SKPaintSurfaceEventArgs e)
    {
        base.OnPaintSurface(e);
        if (_viewModel is null)
        {
            return;
        }

        var view = _viewModel.View;
        view.Width = ActualWidth;
        view.Height = ActualHeight;
        var scale = (float)DpiHelper.GetScale(this).ScaleX;
        _renderer.Render(e.Surface.Canvas, _viewModel.Timeline, scale);
        if (IsKeyboardFocused)
        {
            using var frame = new SKPaint { Color = Colors.Focus, Style = SKPaintStyle.Stroke, StrokeWidth = 2 * scale };
            e.Surface.Canvas.DrawRect(SKRect.Create(scale, scale, e.Info.Width - (2 * scale), e.Info.Height - (2 * scale)), frame);
        }
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        InvalidateVisual();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _viewModel is null)
        {
            return;
        }

        var modifiers = Keyboard.Modifiers;
        switch (e.Key)
        {
            case Key.Tab when modifiers is ModifierKeys.None or ModifierKeys.Shift:
                // An empty timeline lets Tab move on.
                e.Handled = _viewModel.SelectNextItem(backward: modifiers == ModifierKeys.Shift);
                break;
            case Key.Up or Key.Down when modifiers == ModifierKeys.None:
                _viewModel.MoveToTrack(e.Key == Key.Up ? -1 : 1);
                e.Handled = true; // the arrows never take the focus off the timeline
                break;
        }
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (_viewModel is null || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        Focus();
        var at = e.GetPosition(this);
        CaptureMouse();
        _viewModel.Timeline.PointerDown(at.X, at.Y, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_viewModel is null)
        {
            return;
        }

        var at = e.GetPosition(this);
        if (_viewModel.Timeline.IsDragging)
        {
            _viewModel.Timeline.PointerMove(at.X, at.Y, Keyboard.Modifiers.HasFlag(ModifierKeys.Alt));
        }

        Cursor = _viewModel.Timeline.CursorAt(at.X, at.Y) switch
        {
            TimelineCursor.Move => Cursors.SizeAll,
            TimelineCursor.Trim => Cursors.SizeWE,
            TimelineCursor.Scrub => Cursors.IBeam,
            _ => Cursors.Arrow,
        };
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_viewModel is null || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var at = e.GetPosition(this);
        _viewModel.Timeline.PointerUp(at.X, at.Y);
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_viewModel?.Timeline.IsDragging == true)
        {
            _viewModel.Timeline.PointerUp(0, 0);
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_viewModel is null)
        {
            return;
        }

        var view = _viewModel.View;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            view.ZoomAt(e.GetPosition(this).X, e.Delta > 0 ? 1.25 : 0.8);
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            var rows = TimelineView.RowsHeight(_viewModel.Project);
            view.ScrollRows = Math.Clamp(view.ScrollRows - (e.Delta / 3.0), 0, Math.Max(0, rows - (ActualHeight - TimelineView.RulerHeight)));
            InvalidateVisual();
            _viewModel.RowsScrolled();
        }
        else
        {
            view.Scroll -= e.Delta / 2.0 / view.PixelsPerSecond;
        }

        e.Handled = true;
    }
}
