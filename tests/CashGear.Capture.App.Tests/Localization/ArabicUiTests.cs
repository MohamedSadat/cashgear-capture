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
using CashGear.Capture.Media;
using CashGear.Capture.Outputs;
using CashGear.Capture.Outputs.Credentials;
using CashGear.Capture.Outputs.Sharing;
using CashGear.Capture.VideoEditor.Model;
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
        vm.Destinations.SignIns = id => new NamedSignIn(id);
        vm.Destinations.Load(settings.Current.Destinations);
        var window = new SettingsWindow(vm, AppTheme.Light);
        var tabs = (TabControl)TestLayoutFind<TabControl>(window);

        var problems = await ArabicUi.CheckAsync(
            window,
            "settings",
            leftToRight: () => [(FrameworkElement)window.FindName("WebcamArea")],
            data: text => text.Contains(paths.SettingsDirectory, StringComparison.OrdinalIgnoreCase) || text.Contains("Capture_", StringComparison.Ordinal) || text.Contains("me@example.test", StringComparison.Ordinal),
            pages: () =>
            [
                .. tabs.Items.Cast<TabItem>().Select((tab, i) => ($"{i + 1:00}", (Action)(() => tabs.SelectedItem = tab))),
                ("destinations-sharing", () => ShowInTemplate(tabs, 1, DestinationsText.YouTubeTags)),
                ("recording-monitors", () => ShowSection(tabs, 3, SettingsText.SeveralMonitorsSection)),
                ("overlays-webcam", () => ShowSection(tabs, 4, OverlaysText.LiveBackgroundLabel)),
            ]);

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

        var settings = new MutableSettings();
        settings.Current = settings.Current.Normalize();
        var monitors = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance);
        var schedule = new ScheduleRecordingViewModel(null!, null!, monitors, new Win32WindowService(), settings, TimeProvider.System, pro: true);
        schedule.Problem = RecordingText.BadTime("x");
        problems.AddRange(await ArabicUi.CheckAsync(new ScheduleRecordingWindow(schedule, AppTheme.Light), "schedule-recording", escCloses: true, data: IsData));

        problems.AddRange(await ArabicUi.CheckAsync(new RecordingProfilesWindow(new RecordingProfilesViewModel(settings), AppTheme.Light), "recording-profiles", escCloses: true, data: IsData));

        problems.AddRange(await SharingWindows(settings));

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

    /// <summary>The uploads and exports window with something on it, and the batch window.</summary>
    private async Task<List<string>> SharingWindows(MutableSettings settings)
    {
        var problems = new List<string>();
        var paths = new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "data", "logs"), Path.Combine(_root, "captures"));
        var shell = new NoShell();
        var exporter = new CashGear.Capture.VideoEditor.Export.ProjectExporter(new NoEncoders(), NullLogger<CashGear.Capture.VideoEditor.Export.ProjectExporter>.Instance);
        var exports = new App.VideoEditor.ExportQueue(exporter, settings, new FakeNotifier(), shell, paths, NullLogger<App.VideoEditor.ExportQueue>.Instance);
        var gate = new TaskCompletionSource();
        var source = new SlowSource(gate);
        var registry = new DestinationRegistry([source], settings, new NoSecrets());
        using var wake = new UploadWake();
        using var uploads = new UploadQueue(registry, new NoSecrets(), paths, wake, NullLogger<UploadQueue>.Instance);
        var outputs = new NoOutputs();
        var workflow = new App.Output.OutputWorkflow(new WorkflowRunner(registry, NullLogger<WorkflowRunner>.Instance), registry, settings, outputs, paths, new NoCounter(), new FakeNotifier(), shell, NullLogger<App.Output.OutputWorkflow>.Instance, uploads);
        var work = new App.Output.BackgroundWork(uploads, exports, workflow);
        Directory.CreateDirectory(_root);
        var file = Path.Combine(_root, "تسجيل.mp4");
        File.WriteAllBytes(file, new byte[1000]);
        uploads.Enqueue(new UploadRequest(file, false, "تسجيل", null, DateTimeOffset.UtcNow, UploadKind.Recording, [new UploadStep("slow"), new UploadStep("bad")]));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (uploads.Jobs[0].State != UploadState.Sending && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        using var viewModel = new App.Output.BackgroundWorkViewModel(work, new InlineUiDispatcher(), outputs, shell);
        problems.AddRange(await ArabicUi.CheckAsync(new App.Output.BackgroundWorkWindow(viewModel, AppTheme.Light), "uploads-and-exports", escCloses: true, data: text => IsData(text) || text.Contains("تسجيل.mp4", StringComparison.Ordinal) || text.Contains("slow", StringComparison.Ordinal) || text.Contains("bad", StringComparison.Ordinal)));
        gate.SetResult();
        await uploads.WhenIdleAsync(CancellationToken.None);

        viewModel.Refresh();
        problems.AddRange(await ArabicUi.CheckAsync(new App.Output.BackgroundWorkWindow(viewModel, AppTheme.Light), "uploads-and-exports-ended", escCloses: true, data: text => IsData(text) || text.Contains("تسجيل.mp4", StringComparison.Ordinal) || text.Contains("slow", StringComparison.Ordinal) || text.Contains("bad", StringComparison.Ordinal) || text.Contains("https://", StringComparison.Ordinal) || text.Contains("No", StringComparison.Ordinal)));

        var batch = new App.VideoEditor.BatchExportViewModel(exports, null, [new App.VideoEditor.BatchSource("مشروع"), new App.VideoEditor.BatchSource("Another")], _root, null);
        batch.Presets[0].IsChosen = true;
        problems.AddRange(await ArabicUi.CheckAsync(new App.VideoEditor.BatchExportWindow(batch, AppTheme.Light), "batch-export", escCloses: true, data: text => IsData(text) || text.Contains(_root, StringComparison.OrdinalIgnoreCase) || text == "Another" || text == "مشروع"));
        return problems;
    }

    /// <summary>Signed in for YouTube and Drive, not for the others: both sets of buttons show.</summary>
    private sealed class NamedSignIn(string id) : ISignInDestination
    {
        public bool IsSignedIn => id is DestinationIds.YouTube or DestinationIds.GoogleDrive;

        public string? Account => IsSignedIn ? "me@example.test" : null;

        public string Id => id;

        public string DisplayName => id;

        public bool SupportsVideo => true;

        public Task SignInAsync(CancellationToken ct) => Task.CompletedTask;

        public void SignOut()
        {
        }

        public Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct) => throw new NotSupportedException();

        public Task<OutputResult> TestAsync(CancellationToken ct) => Task.FromResult(OutputResult.Done("ok"));
    }

    private sealed class SlowSource(TaskCompletionSource gate) : IDestinationSource
    {
        public IEnumerable<IOutputDestination> Create(AppSettings settings, ICredentialStore credentials) => [new Slow("slow", gate, false), new Slow("bad", gate, true)];
    }

    private sealed class Slow(string id, TaskCompletionSource gate, bool fails) : IOutputDestination
    {
        public string Id => id;

        public string DisplayName => id;

        public bool SupportsVideo => true;

        public bool IsUpload => true;

        public async Task<OutputResult> SendAsync(OutputItem item, CancellationToken ct)
        {
            await gate.Task.WaitAsync(ct);
            return fails ? OutputResult.Failed("No") : new OutputResult(true, "Sent") { Link = "https://example.test/video" };
        }

        public Task<OutputResult> TestAsync(CancellationToken ct) => Task.FromResult(OutputResult.Done("ok"));
    }

    private sealed class NoSecrets : ICredentialStore
    {
        public string? Get(string key) => null;

        public void Set(string key, string? value)
        {
        }

        public void RemoveAll(string prefix)
        {
        }
    }

    private sealed class NoShell : App.Platform.IShell
    {
        public void ShowInFolder(string path)
        {
        }

        public void OpenLink(string url)
        {
        }

        public void OpenFile(string path)
        {
        }
    }

    private sealed class NoCounter : Core.Output.ICaptureCounter
    {
        public int Next() => 1;
    }

    private sealed class NoEncoders : Core.Recording.IRecordingEncoders
    {
        public string? Problem => null;

        public string? FfmpegVersion => "test";

        public IReadOnlyList<Core.Recording.EncoderProbe>? Detected => [];

        public Task<IReadOnlyList<Core.Recording.EncoderProbe>> DetectAsync(bool force = false, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Core.Recording.EncoderProbe>>([]);
    }

    private sealed class NoOutputs : Core.Output.IOutputService
    {
        public Task CopyTextToClipboardAsync(string text, CancellationToken ct = default) => Task.CompletedTask;

        public Task<string?> SaveAsync(Core.Imaging.CapturedImage image, Core.Output.SaveOptions options, CancellationToken ct = default) => throw new NotSupportedException();

        public Task SaveToFileAsync(Core.Imaging.CapturedImage image, string path, ImageFileFormat format, int jpegQuality, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CopyToClipboardAsync(Core.Imaging.CapturedImage image, CancellationToken ct = default) => throw new NotSupportedException();

        public Task CopyFilesToClipboardAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => throw new NotSupportedException();
    }

    /// <summary>A tab scrolled to a text inside a list's template, which only the visual tree reaches.</summary>
    private static void ShowInTemplate(TabControl tabs, int tab, string text)
    {
        tabs.SelectedIndex = tab;
        tabs.UpdateLayout();
        static IEnumerable<DependencyObject> InTree(DependencyObject parent)
        {
            for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                yield return child;
                foreach (var below in InTree(child))
                {
                    yield return below;
                }
            }
        }

        InTree(tabs).OfType<TextBlock>().FirstOrDefault(b => b.Text == text)?.BringIntoView();
    }

    /// <summary>A tab scrolled to the part of it that holds this text, to be looked at.</summary>
    private static void ShowSection(TabControl tabs, int tab, string text)
    {
        tabs.SelectedIndex = tab;
        tabs.UpdateLayout();
        Descendants(((TabItem)tabs.Items[tab]).Content as DependencyObject ?? tabs).OfType<TextBlock>().FirstOrDefault(b => b.Text == text)?.BringIntoView();
    }

    /// <summary>What comes from the PC, not the app: monitor names, window titles, times typed, a frame rate.</summary>
    private static bool IsData(string text) => text.Contains("DISPLAY", StringComparison.Ordinal) || text.All(c => c < 128) || text.Contains(':', StringComparison.Ordinal) || text.Contains('«', StringComparison.Ordinal);

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

    [Fact]
    public Task The_transcript_panel_and_its_windows() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var arabic = ArabicUi.InArabic();
        var problems = new List<string>();
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(Recording()));
        var words = Enumerable.Range(0, 30).Select(i => new SpokenWord(i * 0.1, (i * 0.1) + 0.08, $"كلمة{i}")).ToList();
        var stream = project.Tracks.First(t => t.HasSound).Clips.First().Stream;
        project = project.WithAsset(project.Assets[0] with { Transcript = new MediaTranscript(stream, "ar", words) });
        var folder = Path.Combine(Root, "models");
        Directory.CreateDirectory(folder);
        var store = new CashGear.Capture.Transcription.WhisperModelStore(folder, new System.Net.Http.HttpClient(), NullLogger<CashGear.Capture.Transcription.WhisperModelStore>.Instance);
        using var vm = new VideoEditorViewModel(project, null, Services with { Transcriber = new FakeTranscriber(), Models = store }, Dialogs);
        static bool Data(string text) => text.Contains(".mp4", StringComparison.OrdinalIgnoreCase) || text.Contains("Rec", StringComparison.Ordinal);

        vm.Transcript.Select(2);
        await vm.Transcript.ShowFillersAsync();
        var window = new VideoEditorWindow(vm, AppTheme.Dark, tryGpu: false, NullLogger.Instance);
        problems.AddRange(await ArabicUi.CheckAsync(window, "video-editor-transcript", leftToRight: () => [window.TimelineSurface, window.TranscriptArea2.WordsControl], data: Data));

        vm.OpenTranscript();
        problems.AddRange(await ArabicUi.CheckAsync(new TranscribeWindow(Dialogs.Transcribe!, AppTheme.Dark), "transcript-from-speech", data: Data, escCloses: true));
        vm.OpenTranslate();
        problems.AddRange(await ArabicUi.CheckAsync(new TranslateWindow(Dialogs.Translate!, AppTheme.Dark), "translate", data: Data, escCloses: true));

        problems.Should().BeEmpty(ArabicUi.Report("Transcript", problems));
    });

    [Fact]
    public Task A_sound_clips_settings_and_the_exports_sound() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var arabic = ArabicUi.InArabic();
        var problems = new List<string>();
        using var vm = Open();
        static bool Data(string text) => text.Contains(".mp4", StringComparison.OrdinalIgnoreCase) || text.Contains("Rec", StringComparison.Ordinal);

        // Everything that can be on is on, so every setting is shown.
        var clip = vm.Project.Tracks.First(t => t.HasSound).Clips.First();
        vm.History.Apply("sound", vm.Project.WithItem(clip with { VolumeKeys = [new(0.5, 0.5), new(1, 1)], Sound = new ClipSound(GainDb: 3, NoiseRemoval: true, Gate: true, Compressor: true, Limiter: true, Eq: SoundEq.Of(EqPreset.Voice)) }));
        vm.Timeline.Select(clip.Id);
        vm.Seek(clip.Start + 0.5);
        var window = new VideoEditorWindow(vm, AppTheme.Dark, tryGpu: false, NullLogger.Instance);
        problems.AddRange(await ArabicUi.CheckAsync(window, "video-editor-sound", leftToRight: () => [window.TimelineSurface], data: Data));

        vm.ExportCommand.Execute(null);
        Dialogs.Export!.HasSoundChoices.Should().BeTrue();
        Dialogs.Export!.ChoosesTracks.Should().BeTrue();
        problems.AddRange(await ArabicUi.CheckAsync(new ExportWindow(Dialogs.Export!, AppTheme.Dark), "export-sound", data: Data, escCloses: true));

        problems.Should().BeEmpty(ArabicUi.Report("Sound", problems));
    });

    private sealed class YouTubeSender : IExportSender
    {
        public YouTubeSettings YouTubeDefaults { get; } = new(VideoPrivacy.Private, "", ["One", "two"]);

        public IReadOnlyList<(string Id, string Name)> Targets() => [(DestinationIds.YouTube, "YouTube"), (DestinationIds.Dropbox, "Dropbox")];

        public void Send(string path, string name, string destination, IReadOnlyList<ExportChapter> chapters, UploadDetails? details = null)
        {
        }

        public Task<IReadOnlyList<(string Id, string Title)>> PlaylistsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<(string, string)>>([]);
    }

    [Fact]
    public Task The_export_window_with_YouTube_and_the_thumbnail_window() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var arabic = ArabicUi.InArabic();
        var problems = new List<string>();
        var project = ProjectFactory.FromRecording(ProjectFactory.ReadAsset(Recording()));
        project = project with { Markers = [new TimelineMarker("a", 0, "مقدمة"), new TimelineMarker("b", 1, "Part 2")] };
        bool Shown(string text) => text.Contains("مقدمة", StringComparison.Ordinal) || text.Contains("stamped", StringComparison.Ordinal) || text.Contains(Root, StringComparison.OrdinalIgnoreCase) || text.Contains("0:0", StringComparison.Ordinal) || text == "One, two";

        var sender = new YouTubeSender();
        var export = new ExportViewModel(project, "stamped", Root, Services.Exporter, Services.Settings, Services.State, Services.Shell, NullLogger.Instance, null, null, null, sender);
        export.SendTo = export.SendTargets.First(t => t.Id == DestinationIds.YouTube);
        problems.AddRange(await ArabicUi.CheckAsync(new ExportWindow(export, AppTheme.Light), "export-youtube", data: Shown, escCloses: true));

        using var thumbnail = new ThumbnailViewModel(project, 1, new CashGear.Capture.VideoEditor.Export.ThumbnailMaker(), CashGear.Capture.VideoEditor.Editing.BuiltInTemplates.All, null, Dialogs, "stamped", Path.Combine(Root, "work"), forUpload: true);
        thumbnail.SelectedTitle = thumbnail.Titles[1];
        thumbnail.Fields[0].Text = "عنوان الفيديو";
        await thumbnail.WhenDrawnAsync();
        var window = new ThumbnailWindow(thumbnail, AppTheme.Light);
        problems.AddRange(await ArabicUi.CheckAsync(window, "thumbnail", data: text => Shown(text) || text.Contains("عنوان الفيديو", StringComparison.Ordinal), escCloses: true));

        problems.Should().BeEmpty(ArabicUi.Report("Sharing", problems));
    });

    [Fact]
    public Task The_media_panel_on_each_of_its_tabs() => StaDispatcher.RunAsync(async () =>
    {
        InteractiveDesktop.Require();
        using var arabic = ArabicUi.InArabic();
        var problems = new List<string>();
        using var vm = Open();

        // The brand's fonts are shown by their own names, as a font's list does everywhere.
        static bool Data(string text) => text.Contains(".mp4", StringComparison.OrdinalIgnoreCase) || text.Contains("Rec", StringComparison.Ordinal) || text == BrandKit.Default.HeadingFont;

        vm.Media.IsShown = true;
        vm.AddMarkerCommand.Execute(null);
        for (var tab = 0; tab < 5; tab++)
        {
            vm.Media.ShownTab = tab;
            var window = new VideoEditorWindow(vm, AppTheme.Dark, tryGpu: false, NullLogger.Instance);
            problems.AddRange(await ArabicUi.CheckAsync(window, $"video-editor-media-{tab}", leftToRight: () => [window.TimelineSurface], data: Data));
        }

        problems.Should().BeEmpty(ArabicUi.Report("Media panel", problems));
    });
}
