using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Overlays.Webcam;

namespace CashGear.Capture.App.Settings;

/// <summary>
/// The settings window. Logic lives in <see cref="SettingsViewModel"/>; this only wires window chrome and focus, and
/// runs the sound meters (twenty times a second) while the Recording page is shown.
/// </summary>
public partial class SettingsWindow : Window, IThemedWindow
{
    private const double PreviewWidth = 320;
    private const double PreviewHeight = 180;

    private readonly SettingsViewModel _viewModel;
    private readonly DispatcherTimer _meters;
    private readonly Brush _placeholder = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A));
    private IWebcamSource? _preview;
    private WriteableBitmap? _previewBitmap;
    private int _previewPending;
    private int _previewRequest;
    private Point? _dragOffset;

    public SettingsWindow(SettingsViewModel viewModel, AppTheme theme)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        ApplyTheme(theme);

        viewModel.CloseRequested += (_, _) => Close();
        var clock = Stopwatch.StartNew();
        _meters = new DispatcherTimer(TimeSpan.FromMilliseconds(50), DispatcherPriority.Background, (_, _) => viewModel.RefreshMeters(clock.Elapsed), Dispatcher);
        viewModel.Overlays.PropertyChanged += OnOverlaysChanged;
        Loaded += (_, _) => PlaceWebcam();
    }

    public void ApplyTheme(AppTheme theme) => Theming.Apply(this, theme);

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Overlays.PropertyChanged -= OnOverlaysChanged;
        StopPreview();
        _meters.Stop();
        _viewModel.Dispose(); // also resumes hotkeys if a box still had focus, and closes the sound devices
        base.OnClosed(e);
    }

    /// <summary>The meters listen only while the Recording page is shown (the microphone light is on meanwhile).</summary>
    private void OnTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, Tabs))
        {
            return; // a list on a page, not the page itself
        }

        if (ReferenceEquals(Tabs.SelectedItem, RecordingTab))
        {
            _viewModel.StartMonitoring();
            _meters.Start();
        }
        else
        {
            _meters.Stop();
            _viewModel.StopMonitoring();
        }

        if (ReferenceEquals(Tabs.SelectedItem, OverlaysTab))
        {
            _ = ShowOverlaysPageAsync();
        }
        else
        {
            StopPreview();
        }
    }

    /// <summary>The Overlays page: the cameras are listed, and the chosen one previewed when the webcam is on.</summary>
    private async Task ShowOverlaysPageAsync()
    {
        await _viewModel.Overlays.RefreshCamerasAsync();
        await RestartPreviewAsync();
    }

    private void OnOverlaysChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(OverlaySettingsViewModel.WebcamEnabled) or nameof(OverlaySettingsViewModel.Camera):
                _ = RestartPreviewAsync();
                PlaceWebcam();
                break;
            case nameof(OverlaySettingsViewModel.WebcamCenterX) or nameof(OverlaySettingsViewModel.WebcamCenterY) or nameof(OverlaySettingsViewModel.WebcamHeight)
                or nameof(OverlaySettingsViewModel.WebcamShape) or nameof(OverlaySettingsViewModel.WebcamBorderWidth)
                or nameof(OverlaySettingsViewModel.WebcamBorderColor) or nameof(OverlaySettingsViewModel.WebcamMirror):
                PlaceWebcam();
                break;
        }
    }

    /// <summary>Shows the chosen camera's picture in the preview while the page is shown and the webcam is on (the camera light is on meanwhile).</summary>
    private async Task RestartPreviewAsync()
    {
        StopPreview();
        var overlays = _viewModel.Overlays;
        if (!overlays.WebcamEnabled || overlays.Webcams is not { } webcams || !ReferenceEquals(Tabs.SelectedItem, OverlaysTab))
        {
            return;
        }

        var request = ++_previewRequest;
        var opened = await webcams.OpenAsync(overlays.Camera?.Id, 640, 360, 15);
        if (opened.Source is not { } source)
        {
            overlays.WebcamStatus = opened.Note ?? string.Empty;
            return;
        }

        if (request != _previewRequest || !IsVisible)
        {
            source.Dispose(); // the page or the choice changed meanwhile
            return;
        }

        _preview = source;
        try
        {
            source.Start(OnPreviewPicture);
        }
        catch (InvalidOperationException ex)
        {
            overlays.WebcamStatus = ex.Message;
            StopPreview();
        }
    }

    private void StopPreview()
    {
        _previewRequest++;
        var preview = _preview;
        _preview = null;
        preview?.Dispose();
        _previewBitmap = null;
        WebcamImage.ImageSource = null;
        PlaceWebcam();
    }

    /// <summary>A camera picture (camera thread): shown on the UI thread, skipping pictures while one is still on its way.</summary>
    private void OnPreviewPicture(ReadOnlySpan<byte> bgra, int width, int height, int stride, long timestamp)
    {
        if (Interlocked.Exchange(ref _previewPending, 1) == 1)
        {
            return;
        }

        var copy = bgra.ToArray();
        Dispatcher.BeginInvoke(() =>
        {
            _previewPending = 0;
            if (_preview is null)
            {
                return;
            }

            if (_previewBitmap is null || _previewBitmap.PixelWidth != width || _previewBitmap.PixelHeight != height)
            {
                _previewBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgr32, null);
                WebcamImage.ImageSource = _previewBitmap;
                PlaceWebcam();
            }

            _previewBitmap.WritePixels(new Int32Rect(0, 0, width, height), copy, stride, 0);
        });
    }

    /// <summary>Puts the webcam's box in the preview where and how the settings say.</summary>
    private void PlaceWebcam()
    {
        if (WebcamBox is null)
        {
            return;
        }

        var overlays = _viewModel.Overlays;
        var aspect = _previewBitmap is { } bitmap ? bitmap.PixelWidth / (double)bitmap.PixelHeight : 16.0 / 9;
        var height = overlays.WebcamHeight / 100 * PreviewHeight;
        var width = overlays.WebcamIsCircle ? height : height * aspect;
        WebcamBox.Width = width;
        WebcamBox.Height = height;
        Canvas.SetLeft(WebcamBox, Math.Clamp((overlays.WebcamCenterX / 100 * PreviewWidth) - (width / 2), 0, Math.Max(0, PreviewWidth - width)));
        Canvas.SetTop(WebcamBox, Math.Clamp((overlays.WebcamCenterY / 100 * PreviewHeight) - (height / 2), 0, Math.Max(0, PreviewHeight - height)));
        WebcamBox.CornerRadius = new CornerRadius(overlays.WebcamIsCircle ? width / 2 : height * 0.12);
        WebcamBox.BorderThickness = new Thickness(overlays.WebcamBorderWidth <= 0 ? 0 : Math.Max(1, overlays.WebcamBorderWidth / 3));
        WebcamBox.BorderBrush = overlays.WebcamBorderColor is { } color && OverlaySettings.TryParseColor(color.Value, out var argb)
            ? new SolidColorBrush(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb))
            : Brushes.White;
        WebcamImage.RelativeTransform = overlays.WebcamMirror ? new ScaleTransform(-1, 1, 0.5, 0.5) : Transform.Identity;
        WebcamBox.Background = WebcamImage.ImageSource is null ? _placeholder : WebcamImage;
    }

    private void OnWebcamDragStart(object sender, MouseButtonEventArgs e)
    {
        _dragOffset = e.GetPosition(WebcamBox);
        WebcamBox.CaptureMouse();
        e.Handled = true;
    }

    private void OnWebcamDrag(object sender, MouseEventArgs e)
    {
        if (_dragOffset is not { } offset)
        {
            return;
        }

        var at = e.GetPosition(WebcamCanvas);
        var centreX = at.X - offset.X + (WebcamBox.ActualWidth / 2);
        var centreY = at.Y - offset.Y + (WebcamBox.ActualHeight / 2);
        _viewModel.Overlays.MoveWebcam(centreX / PreviewWidth, centreY / PreviewHeight);
    }

    private void OnWebcamDragEnd(object sender, MouseButtonEventArgs e)
    {
        _dragOffset = null;
        WebcamBox.ReleaseMouseCapture();
    }

    /// <summary>The running apps are listed when the list opens (not when the window does: listing them takes a moment).</summary>
    private void OnExcludedAppsOpened(object? sender, EventArgs e) => _viewModel.Overlays.RefreshRunningApps();

    private void OnHotkeyBoxGotFocus(object sender, KeyboardFocusChangedEventArgs e) => _viewModel.BeginHotkeyRecording();

    private void OnHotkeyBoxLostFocus(object sender, KeyboardFocusChangedEventArgs e) => _viewModel.EndHotkeyRecording();
}
