using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CashGear.Capture.App.Capture;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Region;
using CashGear.Capture.App.Scrolling;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.History;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Interop.Native;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Tests.Capture;

/// <summary>
/// The real capture path (coordinator, overlay, screen capture) with fakes where a capture leaves the app: the
/// clipboard, the editor, files, the history, the countdown. Captures go to <see cref="Output"/>. Build it on the
/// test's STA thread (its dispatcher runs the overlay).
/// </summary>
internal sealed class CaptureHarness : IDisposable
{
    private readonly ServiceProvider _services;

    public CaptureHarness(string root, Func<AppSettings, AppSettings>? configure = null)
    {
        Directory.CreateDirectory(root);
        var paths = new AppPaths(Path.Combine(root, "settings"), Path.Combine(root, "logs"), Path.Combine(root, "captures"));
        Settings.Current = (configure ?? (s => s))(new AppSettings
        {
            AfterCapture = new AfterCaptureSettings(Save: false, CopyToClipboard: true, OpenEditor: false),
            Capture = new CaptureSettings(HideDelayMs: 0),
        });

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(Recording.TestLog.Create());
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddCaptureCore(paths);
        services.AddCaptureEngine();
        services.AddSingleton<ISettingsService>(Settings);
        services.AddSingleton<IWindowService>(Windows);
        services.AddSingleton<IUiDispatcher>(new DispatcherUi(Dispatcher.CurrentDispatcher));
        services.AddSingleton<ITrayNotifier>(Notifier);
        services.AddSingleton<IOwnWindows, NoOwnWindows>();
        services.AddSingleton<IOutputService>(Output);
        services.AddSingleton<IEditorLauncher>(Output);
        services.AddSingleton<ICaptureCountdown>(Countdown);
        services.AddSingleton<ICaptureHistory, NoHistory>();
        services.AddSingleton<IShell, NoShell>();
        services.AddSingleton<RegionSelector>();
        services.AddSingleton<IScrollPlatform, Win32ScrollPlatform>();
        services.AddSingleton<ScrollTargetFinder>();
        services.AddSingleton<ScrollingCaptureService>();
        services.AddSingleton<CaptureCoordinator>();
        _services = services.BuildServiceProvider();
    }

    public MutableSettings Settings { get; } = new();

    public TestWindowService Windows { get; } = new();

    public FakeNotifier Notifier { get; } = new();

    public CapturedOutput Output { get; } = new();

    public RecordingCountdown Countdown { get; } = new();

    public CaptureCoordinator Coordinator => _services.GetRequiredService<CaptureCoordinator>();

    public RegionSelector Selector => _services.GetRequiredService<RegionSelector>();

    public T Get<T>()
        where T : notnull => _services.GetRequiredService<T>();

    /// <summary>Waits (pumping the dispatcher) until the overlay is up and has drawn.</summary>
    public async Task WaitForOverlayAsync()
    {
        for (var i = 0; i < 100 && !Selector.IsOpen; i++)
        {
            await Task.Delay(50);
        }

        Selector.IsOpen.Should().BeTrue("the selection overlay should have opened");
        await Task.Delay(400); // let it present and take the mouse
    }

    public void Dispose()
    {
        Output.Dispose();
        _services.Dispose();
    }
}

/// <summary>A top-most window showing a fixed, position-dependent pattern (so a capture of it can be checked against a screen grab).</summary>
internal static class PatternWindow
{
    /// <summary>Shows it with its top-left <paramref name="offset"/> into <paramref name="monitor"/>'s work area, <paramref name="size"/> in physical pixels.</summary>
    public static async Task<(Window Window, PixelRect Bounds)> ShowAsync(MonitorInfo monitor, PixelSize size, PixelPoint offset)
    {
        var bitmap = new WriteableBitmap(size.Width, size.Height, 96, 96, PixelFormats.Bgra32, null);
        var pixels = new byte[size.Width * size.Height * 4];
        for (var y = 0; y < size.Height; y++)
        {
            for (var x = 0; x < size.Width; x++)
            {
                var i = ((y * size.Width) + x) * 4;
                pixels[i] = (byte)(x * 7);
                pixels[i + 1] = (byte)(y * 5);
                pixels[i + 2] = (byte)((x + y) * 3);
                pixels[i + 3] = 255;
            }
        }

        bitmap.WritePixels(new Int32Rect(0, 0, size.Width, size.Height), pixels, size.Width * 4, 0);
        var image = new Image { Source = bitmap, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        var window = new Window
        {
            Title = "CashGear capture-mode test",
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            Content = image,
            Left = monitor.WorkArea.X / monitor.Scale.ScaleX,
            Top = monitor.WorkArea.Y / monitor.Scale.ScaleY,
            Width = 100,
            Height = 100,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        window.Show();
        var hwnd = new WindowInteropHelper(window).Handle;
        var bounds = new PixelRect(monitor.WorkArea.X + offset.X, monitor.WorkArea.Y + offset.Y, size.Width, size.Height);
        User32.SetWindowPos(hwnd, User32.HwndTopmost, bounds.X, bounds.Y, bounds.Width, bounds.Height, User32.SwpNoActivate);
        await Task.Delay(300);
        User32.SetWindowPos(hwnd, User32.HwndTopmost, bounds.X, bounds.Y, bounds.Width, bounds.Height, User32.SwpNoActivate); // after a DPI change on the way
        await Task.Delay(400);
        return (window, bounds);
    }
}

internal sealed class DispatcherUi(Dispatcher dispatcher) : IUiDispatcher
{
    public bool CheckAccess() => dispatcher.CheckAccess();

    public Task InvokeAsync(Action action) => dispatcher.InvokeAsync(action).Task;

    public Task<T> InvokeAsync<T>(Func<T> func) => dispatcher.InvokeAsync(func).Task;

    public Task InvokeAsync(Func<Task> func) => dispatcher.InvokeAsync(func).Task.Unwrap();

    public Task<T> InvokeAsync<T>(Func<Task<T>> func) => dispatcher.InvokeAsync(func).Task.Unwrap();
}

/// <summary>The real windows, plus test windows of this process (which the real service leaves out) on top.</summary>
internal sealed class TestWindowService : IWindowService
{
    private readonly Win32WindowService _real = new();

    public List<nint> Extra { get; } = [];

    public IReadOnlyList<WindowInfo> GetVisibleWindows()
    {
        var own = Extra.Select(h => _real.GetWindow(h)).OfType<WindowInfo>().Select((w, i) => w with { ZOrder = i }).ToList();
        return [.. own, .. _real.GetVisibleWindows().Select(w => w with { ZOrder = w.ZOrder + own.Count })];
    }

    public nint GetForegroundWindow() => _real.GetForegroundWindow();

    public WindowInfo? WindowFromPoint(PixelPoint point) => _real.WindowFromPoint(point);

    public WindowInfo? GetWindow(nint hwnd) => _real.GetWindow(hwnd);

    public bool IsOccluded(nint hwnd) => _real.IsOccluded(hwnd);

    /// <summary>When set, menus of this process count, as if this window were in front.</summary>
    public nint MenusOf { get; set; }

    public IReadOnlyList<WindowInfo> GetOpenMenus() =>
        MenusOf == 0 ? _real.GetOpenMenus() : _real.GetOpenMenus(includeOwnProcess: true, MenusOf);
}

/// <summary>Keeps a copy of every image copied, saved or opened in the editor, and every text copied.</summary>
internal sealed class CapturedOutput : IOutputService, IEditorLauncher, IDisposable
{
    private readonly List<CapturedImage> _images = [];

    public IReadOnlyList<CapturedImage> Images => _images;

    public List<string> Texts { get; } = [];

    public List<SaveOptions> Saves { get; } = [];

    public CapturedImage Last => _images[^1];

    public Task<string?> SaveAsync(CapturedImage image, SaveOptions options, CancellationToken ct = default)
    {
        Saves.Add(options);
        Keep(image);
        return Task.FromResult<string?>(Path.Combine(options.Directory, options.FileNameWithoutExtension + options.Format.FileExtension()));
    }

    public Task SaveToFileAsync(CapturedImage image, string path, ImageFileFormat format, int jpegQuality, CancellationToken ct = default) => Task.CompletedTask;

    public Task CopyToClipboardAsync(CapturedImage image, CancellationToken ct = default)
    {
        Keep(image);
        return Task.CompletedTask;
    }

    public Task CopyTextToClipboardAsync(string text, CancellationToken ct = default)
    {
        Texts.Add(text);
        return Task.CompletedTask;
    }

    public Task OpenAsync(CapturedImage image, string? savedPath, CancellationToken ct = default)
    {
        Keep(image);
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        foreach (var image in _images)
        {
            image.Dispose();
        }
    }

    private void Keep(CapturedImage image)
    {
        var copy = CapturedImage.Allocate(image.Width, image.Height, image.Metadata, clear: false);
        image.Pixels.CopyTo(copy.Pixels);
        _images.Add(copy);
    }
}

internal sealed class RecordingCountdown : ICaptureCountdown
{
    public List<(int Seconds, string What)> Requested { get; } = [];

    public Task RunAsync(int seconds, string what, CancellationToken ct)
    {
        Requested.Add((seconds, what));
        return Task.CompletedTask;
    }
}

internal sealed class NoOwnWindows : IOwnWindows
{
    public Task<IDisposable> HideAsync() => Task.FromResult<IDisposable>(new Nothing());

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }
}

internal sealed class NoHistory : ICaptureHistory
{
    public event EventHandler? Changed
    {
        add { }
        remove { }
    }

    public IReadOnlyList<CaptureHistoryEntry> Entries => [];

    public string ImagePath(CaptureHistoryEntry entry) => string.Empty;

    public string ThumbnailPath(CaptureHistoryEntry entry) => string.Empty;

    public Task<CaptureHistoryEntry?> AddAsync(CapturedImage image, string? savedPath, string? savedPngPath, CancellationToken ct = default) =>
        Task.FromResult<CaptureHistoryEntry?>(null);

    public Task RemoveAsync(string id) => Task.CompletedTask;

    public Task ClearAsync() => Task.CompletedTask;
}

internal sealed class NoShell : IShell
{
    public void ShowInFolder(string path)
    {
    }
}
