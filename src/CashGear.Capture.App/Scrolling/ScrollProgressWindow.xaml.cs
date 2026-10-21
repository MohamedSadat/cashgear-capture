using System.Windows;
using System.Windows.Interop;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.Wpf;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Interop.Native;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CashGear.Capture.App.Scrolling;

/// <summary>Progress of a scrolling capture, and the user's Finish now / Cancel.</summary>
public sealed partial class ScrollProgressViewModel(IUiDispatcher ui, Action cancel) : ObservableObject, IScrollProgress
{
    private volatile bool _stopRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private int _frames;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary))]
    private int _height;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MethodText))]
    private string _method = string.Empty;

    /// <summary>"12 parts · 4800 px".</summary>
    public string Summary => CapturingText.ScrollProgress(Frames, Height);

    /// <summary>"Scrolling with the mouse wheel".</summary>
    public string MethodText => CapturingText.ScrollingWith(Method);

    public bool StopRequested => _stopRequested;

    public void Report(int frames, int height, string method) => _ = ui.InvokeAsync(() =>
    {
        Frames = frames;
        Height = height;
        Method = method;
    });

    [RelayCommand]
    private void Finish() => _stopRequested = true;

    [RelayCommand]
    private void Cancel() => cancel();
}

/// <summary>
/// The small window shown while a scrolling capture runs. It never takes the focus, stays on top, is left out of
/// screen captures, and sits in a corner of the monitor away from where the mouse wheel is turned.
/// </summary>
public partial class ScrollProgressWindow : Window, IThemedWindow
{
    private const int EdgeMargin = 16;

    private readonly PixelRect _workArea;
    private readonly PixelPoint _avoid;

    public ScrollProgressWindow(ScrollProgressViewModel viewModel, AppTheme theme, PixelRect workArea, PixelPoint avoid)
    {
        InitializeComponent();
        DataContext = viewModel;
        _workArea = workArea;
        _avoid = avoid;
        ApplyTheme(theme);
        SourceInitialized += (_, _) =>
            User32.SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, User32.WdaExcludeFromCapture);
        Loaded += (_, _) => Place();
    }

    /// <summary>Top-right of the work area, or bottom-right if that would sit where the wheel is turned.</summary>
    private void Place()
    {
        var scale = DpiHelper.GetScale(this);
        var width = (int)Math.Ceiling(ActualWidth * scale.ScaleX);
        var height = (int)Math.Ceiling(ActualHeight * scale.ScaleY);
        var x = _workArea.Right - width - EdgeMargin;
        var y = _workArea.Y + EdgeMargin;
        if (new PixelRect(x, y, width, height).Contains(_avoid))
        {
            y = _workArea.Bottom - height - EdgeMargin;
        }

        User32.SetWindowPos(
            new WindowInteropHelper(this).Handle, 0, x, y, 0, 0, User32.SwpNoActivate | 0x0001 /* NOSIZE */ | 0x0004 /* NOZORDER */);
    }

    public void ApplyTheme(AppTheme theme) => Theming.Apply(this, theme);
}
