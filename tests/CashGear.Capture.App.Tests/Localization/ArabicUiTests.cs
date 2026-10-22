using System.IO;
using System.Windows;
using System.Windows.Controls;
using CashGear.Capture.App.Capture;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.History;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Recording;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.Scrolling;
using CashGear.Capture.App.Settings;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Tests.Editor;
using CashGear.Capture.App.Tests.History;
using CashGear.Capture.App.Tests.ImageTools;
using CashGear.Capture.App.Tests.Recording;
using CashGear.Capture.App.Tests.VideoEditor;
using CashGear.Capture.App.Toolbar;
using CashGear.Capture.App.Tools;
using CashGear.Capture.App.Tray;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Model;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Engine.Tests;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace CashGear.Capture.App.Tests.Localization;

/// <summary>
/// The acceptance check "the whole UI is usable in Arabic RTL": every window, made in Arabic as the app makes it,
/// is laid out right to left with its pixel surfaces left to right, says everything in Arabic, names every control
/// for a screen reader, lets Tab reach every control and closes with Esc where it is a dialog. Each window is saved
/// as a PNG in <c>artifacts\arabic-ui</c> to be looked at.
/// </summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class ArabicUiTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("cashgear-arabic-ui-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // a thumbnail may still be open
        }
    }

    private static void Pass(string window, List<string> problems) =>
        problems.Should().BeEmpty(ArabicUi.Report(window, problems));

    [Fact]
    public Task Settings_on_every_tab() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var arabic = ArabicUi.InArabic();
        var settings = new MutableSettings();
        var paths = new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "data", "logs"), Path.Combine(_root, "captures"));
        using var registrar = new App.Hotkeys.HotkeyRegistrar(new FakeHotkeyService(), settings, new FakeNotifier(), new InlineUiDispatcher(), NullLogger<App.Hotkeys.HotkeyRegistrar>.Instance);
        registrar.Start();
        using var vm = new SettingsViewModel(settings, paths, registrar, new FakeStartupRegistration(), new FakeFolderPicker(), NullLogger<SettingsViewModel>.Instance);
        var window = new SettingsWindow(vm, AppTheme.Light);
        var tabs = (TabControl)TestLayoutFind<TabControl>(window);

        var problems = await ArabicUi.CheckAsync(
            window,
            "settings",
            leftToRight: () => [(FrameworkElement)window.FindName("WebcamArea")],
            data: text => text.Contains(paths.SettingsDirectory, StringComparison.OrdinalIgnoreCase) || text.Contains("Capture_", StringComparison.Ordinal),
            pages: () => tabs.Items.Cast<TabItem>().Select((tab, i) => ($"{i + 1:00}", (Action)(() => tabs.SelectedItem = tab))).ToList());

        Pass("Settings", problems);
    });

    [Fact]
    public Task The_image_editor_with_annotations() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var arabic = ArabicUi.InArabic();
        using var editors = new EditorAccessibilityTests();
        var vm = editors.Create(640, 400, disposeLater: false);
        vm.Document.Execute(new AddAnnotationCommand(new ArrowAnnotation(vm.Document.NewId(), new AnnotationStyle(), new SKPoint(40, 40), new SKPoint(200, 120))));
        vm.Document.Execute(new AddAnnotationCommand(new TextAnnotation(vm.Document.NewId(), new AnnotationStyle(), new SKPoint(260, 200), "مرحبًا بالعالم")));
        var window = new EditorWindow(vm, AppTheme.Light);

        var problems = await ArabicUi.CheckAsync(window, "image-editor", leftToRight: () => [window.Surface]);

        await EditorAccessibilityTests.DisposeOnThisThreadAsync(vm, window);
        Pass("Image editor", problems);
    });

    [Fact]
    public Task History_with_captures() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var arabic = ArabicUi.InArabic();
        var history = new HistoryTests.FakeHistory(_root);
        await history.AddPngAsync(HistoryTests.Checkerboard(16, 16), Path.Combine(_root, "لقطة.png"));
        await history.AddPngAsync(HistoryTests.Checkerboard(20, 12), null);
        var actions = new HistoryActions(history, new HistoryTests.FakeEditor(), new FakeOutputService(), new HistoryTests.FakeShell(), new FakeNotifier(), NullLogger<HistoryActions>.Instance);
        var window = new HistoryWindow(new HistoryViewModel(history, actions, new InlineUiDispatcher(), TimeProvider.System), AppTheme.Light, _root);

        Pass("History", await ArabicUi.CheckAsync(window, "history", escCloses: true, data: text => text.Contains("لقطة", StringComparison.Ordinal)));
    });

    [Fact]
    public Task The_colour_picker() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var arabic = ArabicUi.InArabic();
        var state = new JsonAppStateStore(new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "data", "logs"), _root), NullLogger<JsonAppStateStore>.Instance);
        var vm = new ColorPickerViewModel(new FakeOutputService(), new MutableSettings(), state, new InlineUiDispatcher(), () => Task.CompletedTask);
        var window = new ColorPickerWindow(vm, AppTheme.Dark);

        Pass("Colour picker", await ArabicUi.CheckAsync(window, "colour-picker", escCloses: true));
    });

    [Fact]
    public Task A_question_and_the_release_notes() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var arabic = ArabicUi.InArabic();
        var dialog = AppDialog.Create(null, SettingsText.ConfirmResetAll, null, DialogButtons.YesNoCancel, DialogKind.Question, AppTheme.Light, out _);
        Pass("Dialog", await ArabicUi.CheckAsync(dialog, "dialog", escCloses: true));

        var settings = new MutableSettings();
        var notes = Path.Combine(_root, "notes");
        Directory.CreateDirectory(notes);
        var updates = new UpdateService(new NoUpdates(), settings, new FakeNotifier(), new UiReadySignal(), TimeProvider.System, NullLogger<UpdateService>.Instance, null, notes);
        var releaseNotes = new ReleaseNotes(updates, settings);
        releaseNotes.Show("1.1.0");
        var window = (Window)typeof(ReleaseNotes).GetField("_open", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(releaseNotes)!;
        window.Hide();
        Pass("Release notes", await ArabicUi.CheckAsync(window, "release-notes", escCloses: true, data: text => text.Contains("1.1.0", StringComparison.Ordinal)));
    });

    [Fact]
    public Task The_small_windows() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var arabic = ArabicUi.InArabic();
        var problems = new List<string>();

        var countdown = new CountdownWindow(AppTheme.Light, workArea: null, cancel: () => { });
        countdown.Show(3);
        countdown.Hide();
        problems.AddRange(await ArabicUi.CheckAsync(countdown, "countdown"));

        var scroll = new ScrollProgressWindow(new ScrollProgressViewModel(new InlineUiDispatcher(), () => { }), AppTheme.Light, new PixelRect(0, 0, 1600, 900), new PixelPoint(0, 0));
        problems.AddRange(await ArabicUi.CheckAsync(scroll, "scroll-progress"));

        // As the app does, cancelling ends the work, which then closes the window (once, after the key press).
        OverlayProgressWindow? overlays = null;
        var cancelled = false;
        overlays = new OverlayProgressWindow(
            new OverlayProgressViewModel("تسجيل.mp4", () =>
            {
                if (!cancelled)
                {
                    cancelled = true;
                    overlays?.Dispatcher.BeginInvoke(() => overlays.Dispose());
                }
            }),
            AppTheme.Light);


        problems.AddRange(await ArabicUi.CheckAsync(overlays, "adding-overlays", escCloses: true, data: text => text.Contains("تسجيل", StringComparison.Ordinal)));

        var refused = new TrayIconRefusedWindow(_root, AppTheme.Light);
        problems.AddRange(await ArabicUi.CheckAsync(refused, "tray-icon-refused", escCloses: true, data: text => text.Contains(_root, StringComparison.OrdinalIgnoreCase)));

        Pass("Small windows", problems);
    });

    [Fact]
    public Task The_toolbars() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var arabic = ArabicUi.InArabic();
        var problems = new List<string>();
        var monitors = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance);
        var state = new JsonAppStateStore(new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "data", "logs"), _root), NullLogger<JsonAppStateStore>.Instance);

        var capture = new CaptureToolbarWindow(new object(), AppTheme.Light, state, monitors);
        problems.AddRange(await ArabicUi.CheckAsync(capture, "capture-toolbar"));

        var recorder = new FakeRecorder(RecordingSound.System | RecordingSound.Microphone);
        var toolbar = new RecordingToolbarViewModel(recorder, () => { }, () => { });
        var work = monitors.GetMonitors().First(m => m.IsPrimary).WorkArea;
        var recording = new RecordingToolbarWindow(toolbar, AppTheme.Light, work);
        problems.AddRange(await ArabicUi.CheckAsync(recording, "recording-toolbar"));

        Pass("Toolbars", problems);
    });

    private static DependencyObject TestLayoutFind<T>(Window window)
        where T : DependencyObject =>
        LogicalTreeHelper.GetChildren(window).OfType<DependencyObject>().SelectMany(Descendants).OfType<T>().First();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root) =>
        new[] { root }.Concat(LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>().SelectMany(Descendants));

    private sealed class NoUpdates : IUpdateBackend
    {
        public bool IsInstalled => false;

        public string? InstalledChannel => null;

        public string? PendingVersion => null;

        public Task<UpdateCheck> DownloadNewerAsync(string feed, string? channel, CancellationToken ct) => throw new NotSupportedException();

        public void InstallAfterExit()
        {
        }
    }
}

/// <summary>The image tools in Arabic (see <see cref="ArabicUiTests"/>).</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class ArabicImageToolUiTests : ImageToolTestBase
{
    [Fact]
    public Task Batch_combine_and_pdf() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var arabic = ArabicUi.InArabic();
        var problems = new List<string>();
        static bool Data(string text) => text.Contains(".png", StringComparison.OrdinalIgnoreCase) || text.Contains("صورة", StringComparison.Ordinal);

        var batch = new App.ImageTools.BatchViewModel(Presets, Picker, Shell, Settings, Paths);
        batch.List.Add([Picture("صورة1.png", 20, 20, SKColors.Red)]);
        problems.AddRange(await ArabicUi.CheckAsync(new App.ImageTools.BatchWindow(batch, AppTheme.Light), "batch", data: Data, escCloses: true));

        var combine = new App.ImageTools.CombineViewModel(Presets, Picker, new NoEditor(), new FakeOutputService(), Settings, Paths) { PreviewDelay = TimeSpan.Zero };
        combine.List.Add([Picture("صورة1.png", 20, 20, SKColors.Red), Picture("صورة2.png", 20, 20, SKColors.Blue)]);
        await combine.RefreshPreviewAsync();
        var combineWindow = new App.ImageTools.CombineWindow(combine, AppTheme.Light);
        problems.AddRange(await ArabicUi.CheckAsync(combineWindow, "combine", data: Data, escCloses: true));

        var pdf = new App.ImageTools.PdfToolViewModel(Presets, Picker, Shell, Settings, Paths);
        pdf.List.Add([Picture("صورة1.png", 20, 20, SKColors.Red)]);
        problems.AddRange(await ArabicUi.CheckAsync(new App.ImageTools.PdfWindow(pdf, AppTheme.Light), "images-to-pdf", data: Data, escCloses: true));

        problems.Should().BeEmpty(ArabicUi.Report("Image tools", problems));
    });

    private sealed class NoEditor : App.Editor.IEditorLauncher
    {
        public Task OpenAsync(CapturedImage image, string? savedPath, IReadOnlyList<WorkflowStep>? pending = null, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}


/// <summary>The video editor and its dialogs in Arabic (see <see cref="ArabicUiTests"/>).</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class ArabicVideoEditorUiTests : VideoEditorTestBase
{
    [Fact]
    public Task The_video_editor_export_and_captions() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var arabic = ArabicUi.InArabic();
        var problems = new List<string>();
        using var vm = Open();
        static bool Data(string text) => text.Contains(".mp4", StringComparison.OrdinalIgnoreCase) || text.Contains("Rec", StringComparison.Ordinal);

        var window = new VideoEditorWindow(vm, AppTheme.Dark, tryGpu: false, NullLogger.Instance);
        problems.AddRange(await ArabicUi.CheckAsync(window, "video-editor", leftToRight: () => [window.TimelineSurface], data: Data));

        vm.ExportCommand.Execute(null);
        problems.AddRange(await ArabicUi.CheckAsync(new ExportWindow(Dialogs.Export!, AppTheme.Dark), "export", data: Data, escCloses: true));

        problems.Should().BeEmpty(ArabicUi.Report("Video editor", problems));
    });
}
