using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.Wpf;
using Microsoft.Extensions.Logging;
using SkiaSharp;
using SkiaSharp.Views.Desktop;
using SkiaSharp.Views.WPF;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>
/// The video editor's preview: the frame at the playhead drawn by the project's compositor, on the graphics card
/// (an OpenGL Skia surface, <see cref="SKGLElement"/>) when it can, else in memory (<see cref="SKElement"/>). The
/// choice is made once: a graphics card that fails (no OpenGL, Remote Desktop, a driver error while drawing) switches
/// the preview to memory for good. While the window is shown, WPF's render loop advances playback and draws a new
/// frame when one is due. It takes the focus (a frame round it shows it), and then the arrow keys move the selected
/// picture, annotation or zoom a pixel (Shift: 10 pixels).
/// </summary>
internal sealed partial class PreviewHost : Border
{
    private VideoEditorViewModel? _viewModel;
    private FrameworkElement? _surface;
    private ILogger? _logger;
    private bool _dirty = true;
    private bool _rendering;
    private SKColor _surround = new(0x10, 0x10, 0x10);
    private int _renderWidth;
    private long _drawnFrame = -1;

    public PreviewHost()
    {
        Focusable = true;
        FocusVisualStyle = null; // the frame is drawn with the picture
    }

    internal VideoEditorViewModel? ViewModel => _viewModel;

    /// <summary>Drawing on the graphics card.</summary>
    public bool UsesGpu { get; private set; }

    /// <summary>Frames drawn so far (for tests).</summary>
    public int Paints { get; private set; }

    public void Attach(VideoEditorViewModel viewModel, bool tryGpu, ILogger logger)
    {
        _viewModel = viewModel;
        _logger = logger;
        viewModel.PreviewInvalidated += (_, _) => Dispatcher.BeginInvoke(Invalidate);
        Use(tryGpu && !RemoteSession());
        IsVisibleChanged += (_, _) => Follow();
        Loaded += (_, _) => Follow();
        Unloaded += (_, _) => Stop();
    }

    public SKColor Surround
    {
        get => _surround;
        set
        {
            _surround = value;
            Invalidate();
        }
    }

    /// <summary>Draws again: on the render loop's next frame, or now when the loop isn't running.</summary>
    public void Invalidate()
    {
        _dirty = true;
        if (!_rendering)
        {
            _surface?.InvalidateVisual();
        }
    }

    public void Stop()
    {
        if (_rendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }
    }

    /// <summary>Puts the GPU surface in, or the memory one (also when the GPU one can't be made).</summary>
    private void Use(bool gpu)
    {
        if (_surface is SKGLElement old)
        {
            old.PaintSurface -= OnGpuPaint;
            old.Dispose();
        }

        _surface = null;
        if (gpu)
        {
            try
            {
                var element = new SKGLElement();
                element.PaintSurface += OnGpuPaint;
                _surface = element;
                UsesGpu = true;
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                LogNoGpu(e);
            }
        }

        if (_surface is null)
        {
            var element = new SKElement();
            element.PaintSurface += OnCpuPaint;
            _surface = element;
            UsesGpu = false;
        }

        Child = _surface;
        if (_viewModel is not null)
        {
            _viewModel.PreviewBackend = UsesGpu ? VideoEditorText.PreviewOnGpu : VideoEditorText.PreviewInMemory;
        }

        Invalidate();
    }

    private void Follow()
    {
        if (IsVisible && IsLoaded && !_rendering)
        {
            CompositionTarget.Rendering += OnRendering;
            _rendering = true;
        }
        else if (!IsVisible)
        {
            Stop();
        }
    }

    /// <summary>
    /// Every WPF frame: playback moves on, and the preview is drawn when the playhead reaches a new video frame or
    /// something changed (an edit, a picture that was still being decoded).
    /// </summary>
    private void OnRendering(object? sender, EventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.Tick();
        var frame = (long)Math.Floor((_viewModel.Position * _viewModel.Project.Canvas.Fps) + 1e-6);
        if (_dirty || (_viewModel.IsPlaying && frame != _drawnFrame))
        {
            _dirty = false;
            _drawnFrame = frame;
            _surface?.InvalidateVisual();
        }
    }

    private void OnCpuPaint(object? sender, SKPaintSurfaceEventArgs e) => Draw(e.Surface.Canvas, e.Info.Width, e.Info.Height);

    private void OnGpuPaint(object? sender, SKPaintGLSurfaceEventArgs e)
    {
        try
        {
            Draw(e.Surface.Canvas, e.BackendRenderTarget.Width, e.BackendRenderTarget.Height);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // The graphics card failed while drawing: from now on the preview is drawn in memory.
            LogNoGpu(ex);
            Dispatcher.BeginInvoke(() => Use(false));
        }
    }

    private void Draw(SKCanvas canvas, int width, int height)
    {
        Paints++;
        _renderWidth = width;
        if (_viewModel is null)
        {
            canvas.Clear(Surround);
            return;
        }

        if (_viewModel.RenderPreview(canvas, width, height, Surround) > 0)
        {
            _dirty = true; // a picture is still on its way: draw again when it comes
        }

        if (IsKeyboardFocused)
        {
            var scale = (float)DpiHelper.GetScale(this).ScaleX;
            using var frame = new SKPaint { Color = VideoEditorPalette.Focus, Style = SKPaintStyle.Stroke, StrokeWidth = 2 * scale };
            canvas.DrawRect(SKRect.Create(scale, scale, width - (2 * scale), height - (2 * scale)), frame);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PreviewAutomationPeer(this);

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        Invalidate();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Invalidate();
    }

    /// <summary>The arrow keys move the selected picture, annotation or zoom; with nothing to move, ← and → step through the frames.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        var modifiers = Keyboard.Modifiers;
        if (e.Handled || _viewModel is null || e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down) || modifiers is not (ModifierKeys.None or ModifierKeys.Shift))
        {
            return;
        }

        var step = modifiers == ModifierKeys.Shift ? 10 : 1;
        var dx = e.Key == Key.Left ? -step : e.Key == Key.Right ? step : 0;
        var dy = e.Key == Key.Up ? -step : e.Key == Key.Down ? step : 0;
        e.Handled = _viewModel.NudgeOnPreview(dx, dy) || dy != 0 || modifiers != ModifierKeys.None;
    }

    /// <summary>A position on the preview in the pixels it is drawn in (DIPs times the surface's own scale).</summary>
    private Point ToPixels(MouseEventArgs e)
    {
        var at = e.GetPosition(this);
        var scale = _renderWidth > 0 && ActualWidth > 0 ? _renderWidth / ActualWidth : 1;
        return new Point(at.X * scale, at.Y * scale);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (_viewModel is null || e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var at = ToPixels(e);
        Focus();
        CaptureMouse();
        _viewModel.PreviewPointerDown(at.X, at.Y);
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_viewModel is null)
        {
            return;
        }

        var at = ToPixels(e);
        if (IsMouseCaptured)
        {
            _viewModel.PreviewPointerMove(at.X, at.Y);
        }

        Cursor = _viewModel.PreviewHitAt(at.X, at.Y) switch
        {
            { Grip: CashGear.Capture.VideoEditor.Editing.PreviewGrip.Corner } => Cursors.SizeNWSE,
            { OnGrip: true } => Cursors.Cross,
            not null => Cursors.SizeAll,
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

        _viewModel.PreviewPointerUp();
        ReleaseMouseCapture();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _viewModel?.PreviewPointerUp();
    }

    /// <summary>Under Remote Desktop OpenGL goes through software: the memory surface is quicker and safer.</summary>
    private static bool RemoteSession() => Engine.Display.Win32DesktopState.Instance.IsRemoteSession;

    private void LogNoGpu(Exception e)
    {
        if (_logger is { } logger)
        {
            LogNoGpu(logger, e);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Video editor: the preview can't draw on the graphics card; drawing in memory")]
    private static partial void LogNoGpu(ILogger logger, Exception e);
}
