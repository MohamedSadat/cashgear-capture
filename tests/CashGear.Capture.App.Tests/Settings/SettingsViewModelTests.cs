using CashGear.Capture.Core.Audio;
using CashGear.Capture.App.Hotkeys;
using CashGear.Capture.App.Settings;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Hotkeys;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Settings;

public sealed class SettingsViewModelTests : IDisposable
{
    private readonly MutableSettings _settings = new();
    private readonly FakeHotkeyService _hotkeyService = new();
    private readonly FakeNotifier _notifier = new();
    private readonly FakeStartupRegistration _startup = new();
    private readonly FakeFolderPicker _picker = new();
    private readonly HotkeyRegistrar _registrar;
    private readonly AppPaths _paths = new(@"C:\cfg", @"C:\data\logs", @"C:\Users\me\Pictures\CashGear Capture");

    public SettingsViewModelTests()
    {
        _registrar = new HotkeyRegistrar(_hotkeyService, _settings, _notifier, new InlineUiDispatcher(), NullLogger<HotkeyRegistrar>.Instance);
        _registrar.Start();
    }

    public void Dispose() => _registrar.Dispose();

    [Fact]
    public async Task Delays_per_kind_of_capture_the_fixed_size_and_the_radius_are_edited_and_saved()
    {
        _settings.Current = new AppSettings
        {
            Capture = new CaptureSettings(
                DelaySeconds: 2,
                FixedRegionWidth: 700,
                FixedRegionHeight: 500,
                CornerRadius: 22,
                Delays: new ModeDelays(Region: 7)),
        };

        using var vm = Create();
        vm.ModeDelays.Select(r => r.Mode).Should().Equal(Enum.GetValues<CaptureMode>());
        vm.ModeDelays.Single(r => r.Mode == CaptureMode.Region).Choice!.Label.Should().Be("7 s", "a value set by hand is offered");
        vm.ModeDelays.Single(r => r.Mode == CaptureMode.Menu).Choice!.Seconds.Should().Be(3);
        vm.ModeDelays.Single(r => r.Mode == CaptureMode.Polygon).Choice!.Seconds.Should().BeNull();
        vm.FixedRegionSize.Should().Be("700 × 500");
        vm.CornerRadiusChoices.Should().Contain(22).And.BeInAscendingOrder();
        vm.CornerRadius.Should().Be(22);

        vm.ModeDelays.Single(r => r.Mode == CaptureMode.Menu).Choice = vm.ModeDelayChoices.Single(c => c.Seconds is null);
        vm.ModeDelays.Single(r => r.Mode == CaptureMode.Object).Choice = vm.ModeDelayChoices.Single(c => c.Seconds == 5);
        vm.FixedRegionSize = "abc";
        vm.HasErrors.Should().BeTrue();
        vm.Errors.Should().Contain("Fixed-size region");
        vm.FixedRegionSize = "1280x720";
        vm.CornerRadius = 8;
        vm.HasErrors.Should().BeFalse();
        await vm.ApplyCommand.ExecuteAsync(null);

        var capture = _settings.Current.Capture;
        capture.DelayFor(CaptureMode.Menu).Should().Be(2, "back to the general delay");
        capture.DelayFor(CaptureMode.Object).Should().Be(5);
        capture.DelayFor(CaptureMode.Region).Should().Be(7);
        capture.FixedRegionSize.Should().Be(new Core.Geometry.PixelSize(1280, 720));
        capture.CornerRadius.Should().Be(8);
    }

    [Fact]
    public void Loads_the_current_settings()
    {
        _settings.Current = new AppSettings
        {
            Output = new OutputSettings(Folder: @"D:\Shots", Format: ImageFileFormat.Jpeg, JpegQuality: 70, FileNameTemplate: "{counter}"),
            AfterCapture = new AfterCaptureSettings(Save: true, CopyToClipboard: false),
            Capture = new CaptureSettings(DelaySeconds: 3, IncludeCursor: true, Backend: CaptureBackend.Gdi),
            Appearance = new AppearanceSettings(AppTheme.Dark),
        };
        _startup.IsEnabled = true;

        using var vm = Create();

        vm.OutputFolder.Should().Be(@"D:\Shots");
        vm.Format.Should().Be(ImageFileFormat.Jpeg);
        vm.IsJpeg.Should().BeTrue();
        vm.JpegQuality.Should().Be(70);
        vm.CopyToClipboard.Should().BeFalse();
        vm.DelaySeconds.Should().Be(3);
        vm.IncludeCursor.Should().BeTrue();
        vm.Backend.Should().Be(CaptureBackend.Gdi);
        vm.Theme.Should().Be(AppTheme.Dark);
        vm.StartWithWindows.Should().BeTrue();
        vm.Hotkeys.Select(h => h.Text).Should().Equal("PrintScreen", "Alt+PrintScreen", "Ctrl+PrintScreen", "Ctrl+Shift+PrintScreen", "Shift+PrintScreen", "Ctrl+Alt+PrintScreen", "Ctrl+Shift+D", "Ctrl+Alt+Shift+PrintScreen", "", "", "", "", "");
        vm.Hotkeys.Single(h => h.Action == HotkeyAction.ToggleDrawing).Status.Should().Be("Works while recording");
        vm.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Preview_follows_the_template_and_format()
    {
        using var vm = Create();

        vm.FileNamePreview.Should().Be("Capture_2026-03-07_14-30-05_region.png");

        vm.Format = ImageFileFormat.Jpeg;
        vm.FileNameTemplate = "{window} #{counter}";
        vm.FileNamePreview.Should().Be("Untitled - Notepad #0001.jpg");
    }

    [Fact]
    public void Invalid_template_blocks_saving()
    {
        using var vm = Create();

        vm.FileNameTemplate = "shot_{date}";

        vm.HasErrors.Should().BeTrue();
        vm.Errors.Should().Contain("Unknown token {date}");
        vm.FileNamePreview.Should().Be("—");
        vm.SaveCommand.CanExecute(null).Should().BeFalse();
        vm.ApplyCommand.CanExecute(null).Should().BeFalse();
    }

    [Theory]
    [InlineData("", true)]
    [InlineData(@"D:\Shots", true)]
    [InlineData(@"%USERPROFILE%\Shots", true)]
    [InlineData(@"\\server\share\shots", true)]
    [InlineData(@"Shots", false)]
    [InlineData(@"..\Shots", false)]
    public void Folder_must_be_a_full_path_or_empty(string folder, bool valid)
    {
        using var vm = Create();

        vm.OutputFolder = folder;

        vm.HasErrors.Should().Be(!valid);
    }

    [Fact]
    public void Something_must_happen_after_capture()
    {
        using var vm = Create();

        vm.SaveToFile = false;
        vm.CopyToClipboard = false;
        vm.OpenEditor = false;

        vm.Errors.Should().Contain("After capture");
        vm.OpenEditor = true;
        vm.HasErrors.Should().BeFalse("opening the editor alone is enough");
    }

    [Fact]
    public void Duplicate_invalid_and_unsafe_hotkeys_are_flagged_per_row()
    {
        using var vm = Create();
        var region = vm.Hotkeys.Single(h => h.Action == HotkeyAction.CaptureRegion);
        var full = vm.Hotkeys.Single(h => h.Action == HotkeyAction.CaptureFullScreen);
        var window = vm.Hotkeys.Single(h => h.Action == HotkeyAction.CaptureActiveWindow);

        full.Text = "PrintScreen";
        window.Text = "A";

        full.HasProblem.Should().BeTrue();
        full.Status.Should().Be("Same as Capture Region");
        window.Status.Should().Be("Add Ctrl, Alt, Shift or Win");
        region.HasProblem.Should().BeFalse();
        vm.Errors.Should().Contain("already used for Capture Region").And.Contain("would block that key");

        full.ClearCommand.Execute(null);
        window.Text = "Ctrl+Alt+W";
        vm.HasErrors.Should().BeFalse();
        full.Status.Should().Be("Not set");
        window.Status.Should().Be("Applies when you save");
    }

    [Fact]
    public async Task Ok_saves_everything_applies_startup_and_closes()
    {
        using var vm = Create();
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        vm.OutputFolder = @"D:\Shots";
        vm.Format = ImageFileFormat.Bmp;
        vm.FileNameTemplate = "shot_{counter}";
        vm.SaveToFile = true;
        vm.CopyToClipboard = false;
        vm.DelaySeconds = 5;
        vm.HideDelayMs = 500;
        vm.IncludeWindowShadow = true;
        vm.FullScreenOutput = FullScreenOutput.AllMonitors;
        vm.Theme = AppTheme.Light;
        vm.StartWithWindows = true;
        vm.Hotkeys.Single(h => h.Action == HotkeyAction.CaptureScrolling).Text = string.Empty;
        vm.Hotkeys.Single(h => h.Action == HotkeyAction.CaptureRegion).Text = "Ctrl+Shift+F9";
        await vm.SaveCommand.ExecuteAsync(null);

        var s = _settings.Current;
        s.Output.Should().Be(new OutputSettings(@"D:\Shots", ImageFileFormat.Bmp, 90, "shot_{counter}"));
        s.AfterCapture.Should().Be(new AfterCaptureSettings(Save: true, CopyToClipboard: false, OpenEditor: true));
        s.Capture.DelaySeconds.Should().Be(5);
        s.Capture.HideDelayMs.Should().Be(500);
        s.Capture.IncludeWindowShadow.Should().BeTrue();
        s.Capture.FullScreenOutput.Should().Be(FullScreenOutput.AllMonitors);
        s.Appearance.Theme.Should().Be(AppTheme.Light);
        s.Hotkeys.CaptureScrolling.Should().BeNull();
        s.Hotkeys.CaptureRegion.Should().Be("Ctrl+Shift+F9");
        _startup.IsEnabled.Should().BeTrue();
        _hotkeyService.Registered[HotkeyAction.CaptureRegion].Should().Be(HotkeyGesture.Parse("Ctrl+Shift+F9"), "saving re-registers hotkeys");
        closed.Should().Be(1);
    }

    [Fact]
    public async Task Apply_saves_without_closing_and_startup_is_only_touched_when_changed()
    {
        using var vm = Create();
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        vm.JpegQuality = 55;
        await vm.ApplyCommand.ExecuteAsync(null);

        _settings.Current.Output.JpegQuality.Should().Be(55);
        _startup.Changes.Should().Be(0);
        closed.Should().Be(0);
    }

    [Fact]
    public async Task Status_shows_a_key_that_another_app_owns_after_saving()
    {
        _hotkeyService.Taken.Add(HotkeyGesture.Parse("Ctrl+Shift+F9"));
        using var vm = Create();
        var region = vm.Hotkeys.Single(h => h.Action == HotkeyAction.CaptureRegion);

        region.Text = "Ctrl+Shift+F9";
        await vm.ApplyCommand.ExecuteAsync(null);

        region.HasProblem.Should().BeTrue();
        region.Status.Should().Be("Ctrl+Shift+F9 is already used by another app or by Windows.");
        vm.Hotkeys.Single(h => h.Action == HotkeyAction.CaptureFullScreen).Status.Should().Be("Active");
    }

    [Fact]
    public void Cancel_closes_without_saving()
    {
        using var vm = Create();
        var closed = 0;
        vm.CloseRequested += (_, _) => closed++;

        vm.JpegQuality = 10;
        vm.CancelCommand.Execute(null);

        closed.Should().Be(1);
        _settings.Current.Output.JpegQuality.Should().Be(90);
    }

    [Fact]
    public void Reset_to_defaults_only_changes_the_window()
    {
        _settings.Current = new AppSettings { Output = new OutputSettings(Format: ImageFileFormat.Jpeg), Capture = new CaptureSettings(DelaySeconds: 10) };
        using var vm = Create();

        vm.ResetToDefaultsCommand.Execute(null);

        vm.Format.Should().Be(ImageFileFormat.Png);
        vm.DelaySeconds.Should().Be(0);
        vm.FileNameTemplate.Should().Be(FileNameTemplate.Default);
        _settings.Current.Output.Format.Should().Be(ImageFileFormat.Jpeg);
    }

    [Fact]
    public void Browse_sets_the_folder_and_choosing_the_default_clears_it()
    {
        using var vm = Create();

        _picker.Result = @"E:\Captures";
        vm.BrowseFolderCommand.Execute(null);
        vm.OutputFolder.Should().Be(@"E:\Captures");
        _picker.LastInitialFolder.Should().Be(_paths.DefaultCaptureDirectory);

        _picker.Result = _paths.DefaultCaptureDirectory.ToUpperInvariant();
        vm.BrowseFolderCommand.Execute(null);
        vm.OutputFolder.Should().BeEmpty();

        _picker.Result = null;
        vm.OutputFolder = @"F:\x";
        vm.BrowseFolderCommand.Execute(null);
        vm.OutputFolder.Should().Be(@"F:\x", "cancelling the picker keeps the current folder");
    }

    [Fact]
    public async Task Recording_settings_are_loaded_and_saved_keeping_the_toolbar_volumes()
    {
        _settings.Current = _settings.Current with
        {
            Recording = new RecordingSettings(15, QualityPreset.Small, false, "software", MicrophoneGain: 1.5f),
            Overlays = new OverlaySettings(Clicks: new ClickOverlaySettings(Enabled: false)),
        };
        using var vm = Create();
        vm.RecordingFps.Should().Be(15);
        vm.RecordingQuality.Should().Be(QualityPreset.Small);
        vm.RecordingEncoder!.Id.Should().Be("software");
        vm.RecordingTracks.Should().Be(AudioTrackLayout.Mixed);
        vm.IsFfmpegMissing.Should().BeTrue("this view model was given no FFmpeg");

        vm.RecordingFps = 60;
        vm.RecordingQuality = QualityPreset.High;
        vm.Overlays.ClicksEnabled = true;
        vm.RecordingTracks = AudioTrackLayout.Both;
        vm.RecordingEncoder = vm.Encoders.Single(e => e.Id == "h264_mf_hw");
        await vm.ApplyCommand.ExecuteAsync(null);

        _settings.Current.Recording.Should().Be(new RecordingSettings(
            60, QualityPreset.High, false, "h264_mf_hw", Tracks: AudioTrackLayout.Both, MicrophoneGain: 1.5f));
        _settings.Current.Overlays.ClickSettings.Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task Overlay_settings_are_loaded_and_saved_and_excluded_apps_edited()
    {
        _settings.Current = _settings.Current with
        {
            Overlays = new OverlaySettings(
                Clicks: new ClickOverlaySettings(LeftColor: "#123456", Sound: true),
                Keystrokes: new KeystrokeOverlaySettings(ExcludedApps: ["keepass.exe"])),
        };
        using var vm = Create();
        var overlays = vm.Overlays;
        overlays.BurnIn.Should().BeTrue();
        overlays.LeftColor!.Label.Should().Be("Custom (#123456)", "a colour set by hand is kept and listed");
        overlays.Colors.Should().Contain(overlays.LeftColor);
        overlays.ClickSound.Should().BeTrue();
        overlays.ExcludedApps.Should().Equal("keepass.exe");

        overlays.BurnIn = false;
        overlays.PointerEffect = PointerEffect.Spotlight;
        overlays.PointerScale = 1.5;
        overlays.KeystrokeMode = KeystrokeMode.AllKeys;
        overlays.KeystrokePosition = OverlayAnchor.TopRight;
        overlays.NewExcludedApp = "Bitwarden (Bitwarden.exe)";
        overlays.AddExcludedAppCommand.Execute(null);
        overlays.NewExcludedApp = "1Password";
        overlays.AddExcludedAppCommand.Execute(null);
        overlays.NewExcludedApp = "keepass.exe";
        overlays.AddExcludedAppCommand.CanExecute(null).Should().BeFalse("already there");
        overlays.RemoveExcludedAppCommand.Execute("keepass.exe");
        await vm.ApplyCommand.ExecuteAsync(null);

        var saved = _settings.Current.Overlays;
        saved.BurnIn.Should().BeFalse();
        saved.ClickSettings.LeftColor.Should().Be("#123456");
        saved.PointerSettings.Effect.Should().Be(PointerEffect.Spotlight);
        saved.PointerSettings.Scale.Should().Be(1.5);
        saved.KeystrokeSettings.Mode.Should().Be(KeystrokeMode.AllKeys);
        saved.KeystrokeSettings.Position.Should().Be(OverlayAnchor.TopRight);
        saved.KeystrokeSettings.ExcludedApps.Should().Equal("1password.exe", "bitwarden.exe");
    }

    [Fact]
    public async Task The_encoder_list_says_what_works_here_and_can_be_checked_again()
    {
        var encoders = new FakeEncoders(
        [
            new EncoderProbe("h264_nvenc", false, "Cannot load cuMemAllocAsync"),
            new EncoderProbe("h264_mf_hw", true, "NVIDIA H.264 Encoder MFT"),
            new EncoderProbe("h264_mf", true, "H264 Encoder MFT"),
            new EncoderProbe("libopenh264", true),
        ]);
        using var vm = Create(encoders: encoders);

        vm.Encoders.Select(e => e.Id).Should().StartWith(["auto", "software"]).And.Contain(EncoderPresets.All.Select(c => c.Id));
        vm.Encoders.Single(e => e.Id == "h264_nvenc").Label.Should().Be("NVIDIA NVENC (H.264) (not available here)");
        vm.Encoders.Single(e => e.Id == "h264_mf_hw").Label.Should().Be("Windows hardware encoder (H.264): NVIDIA H.264 Encoder MFT");
        vm.EncoderStatus.Should().Be("Works on this PC: Windows hardware encoder (H.264), Windows software encoder (H.264), OpenH264 (software).");
        vm.RecordingEncoder!.Id.Should().Be("auto");
        vm.FfmpegStatus.Should().Contain("FFmpeg n9.0.2");

        encoders.Next = [new EncoderProbe("h264_nvenc", true)];
        vm.RecordingEncoder = vm.Encoders.Single(e => e.Id == "libopenh264");
        await vm.RedetectEncodersCommand.ExecuteAsync(null);

        encoders.Forced.Should().BeTrue();
        vm.Encoders.Single(e => e.Id == "h264_nvenc").Label.Should().Be("NVIDIA NVENC (H.264)");
        vm.RecordingEncoder!.Id.Should().Be("libopenh264", "the choice is kept");
        vm.EncoderStatus.Should().Be("Works on this PC: NVIDIA NVENC (H.264).");
    }

    /// <summary>The window itself: its XAML loads and the Recording tab shows the new choices.</summary>
    [Fact(Timeout = 30_000)]
    [Trait("Category", CashGear.Capture.Engine.Tests.InteractiveDesktop.Category)]
    public Task The_settings_window_shows_the_recording_tab()
    {
        CashGear.Capture.Engine.Tests.InteractiveDesktop.Require();
        return StaDispatcher.RunAsync(async () =>
        {
            var monitor = new FakeMonitor();
            using var vm = Create(encoders: new FakeEncoders([new EncoderProbe("h264_mf", true, "H264 Encoder MFT")]), monitor: monitor);
            var window = new SettingsWindow(vm, AppTheme.Light);
            try
            {
                window.Show();
                var tabs = FindAll<System.Windows.Controls.TabControl>(window).Single();
                tabs.SelectedItem = tabs.Items.Cast<System.Windows.Controls.TabItem>().Single(t => (string)t.Header == "Recording");
                window.UpdateLayout();
                await Task.Delay(300, TestContext.Current.CancellationToken);
                await vm.MonitorWork;

                var boxes = FindAll<System.Windows.Controls.ComboBox>(window).Where(b => b.IsVisible).ToList();
                boxes.Should().Contain(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Quality" && b.SelectedItem is QualityPreset);
                boxes.Should().Contain(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Encoder" && b.SelectedItem is EncoderChoice);
                boxes.Should().Contain(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Sound tracks");
                monitor.Requests.Should().ContainSingle("the meters listen once the Recording page shows");
                FindAll<CashGear.Capture.App.Wpf.LevelMeter>(window).Count(m => m.IsVisible).Should().Be(2);

                tabs.SelectedIndex = 0;
                await Task.Delay(100, TestContext.Current.CancellationToken);
                await vm.MonitorWork;
                monitor.Listening.Should().BeFalse("another page stops them");
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Fact]
    public async Task Webcam_settings_are_loaded_and_saved_and_its_place_moved()
    {
        _settings.Current = _settings.Current with
        {
            Overlays = new OverlaySettings(Webcam: new WebcamSettings(Enabled: true, Device: "gone", Width: 640, Height: 480, Shape: WebcamShape.RoundedRectangle)),
        };
        using var vm = Create(webcams: new Recording.FakeWebcams(() => null));
        var overlays = vm.Overlays;
        overlays.WebcamEnabled.Should().BeTrue();
        overlays.Camera!.Label.Should().Be("The camera chosen before (not connected)", "a camera that isn't connected stays chosen");
        overlays.WebcamSize.Should().Be(new SizeChoice(640, 480));
        await overlays.RefreshCamerasAsync();
        overlays.Cameras.Select(c => c.Label).Should().Contain("test camera");
        overlays.Camera!.Id.Should().Be("gone");

        overlays.Camera = overlays.Cameras.Single(c => c.Id == "test");
        overlays.WebcamShape = WebcamShape.Circle;
        overlays.WebcamHeight = 40;
        overlays.MoveWebcam(0.25, 1.4);
        overlays.WebcamMirror = false;
        await vm.ApplyCommand.ExecuteAsync(null);

        var saved = _settings.Current.Overlays.WebcamSettings;
        (saved.Device, saved.Shape, saved.Size, saved.CenterX, saved.CenterY, saved.Mirror, saved.Width).Should().Be(("test", WebcamShape.Circle, 40, 25.0, 100.0, false, 640));
    }

    /// <summary>The webcam's placement preview shows the camera's picture while the page is open and the webcam on.</summary>
    [Fact(Timeout = 30_000)]
    [Trait("Category", CashGear.Capture.Engine.Tests.InteractiveDesktop.Category)]
    public Task The_overlays_tab_previews_the_webcam_and_lets_go_of_it()
    {
        CashGear.Capture.Engine.Tests.InteractiveDesktop.Require();
        _settings.Current = _settings.Current with { Overlays = new OverlaySettings(Webcam: new WebcamSettings(Enabled: true)) };
        Recording.FakeCamera? camera = null;
        return StaDispatcher.RunAsync(async () =>
        {
            using var vm = Create(webcams: new Recording.FakeWebcams(() => camera = new Recording.FakeCamera(System.Diagnostics.Stopwatch.GetTimestamp(), [])));
            var window = new SettingsWindow(vm, AppTheme.Light);
            try
            {
                window.Show();
                var tabs = FindAll<System.Windows.Controls.TabControl>(window).Single();
                tabs.SelectedItem = tabs.Items.Cast<System.Windows.Controls.TabItem>().Single(t => (string)t.Header == "Overlays");
                await Task.Delay(800, TestContext.Current.CancellationToken);

                var box = (System.Windows.Controls.Border)window.FindName("WebcamBox");
                box.Background.Should().BeOfType<System.Windows.Media.ImageBrush>("the camera's picture is shown");
                ((System.Windows.Media.ImageBrush)box.Background).ImageSource.Should().NotBeNull();
                box.Width.Should().BeApproximately(WebcamSettings.DefaultSize / 100.0 * 180, 0.01, "a circle as tall as its share of the area");

                vm.Overlays.WebcamEnabled = false;
                await Task.Delay(100, TestContext.Current.CancellationToken);
                camera!.Disposed.Should().BeTrue("turning the webcam off lets go of the camera");
            }
            finally
            {
                window.Close();
            }
        });
    }

    /// <summary>The Overlays tab's XAML loads and its choices are bound to the settings.</summary>
    [Fact(Timeout = 30_000)]
    [Trait("Category", CashGear.Capture.Engine.Tests.InteractiveDesktop.Category)]
    public Task The_settings_window_shows_the_overlays_tab()
    {
        CashGear.Capture.Engine.Tests.InteractiveDesktop.Require();
        _settings.Current = _settings.Current with
        {
            Overlays = new OverlaySettings(BurnIn: false, Keystrokes: new KeystrokeOverlaySettings(KeystrokeMode.Shortcuts)),
        };
        return StaDispatcher.RunAsync(async () =>
        {
            using var vm = Create();
            var window = new SettingsWindow(vm, AppTheme.Light);
            try
            {
                window.Show();
                var tabs = FindAll<System.Windows.Controls.TabControl>(window).Single();
                tabs.SelectedItem = tabs.Items.Cast<System.Windows.Controls.TabItem>().Single(t => (string)t.Header == "Overlays");
                window.UpdateLayout();
                await Task.Delay(300, TestContext.Current.CancellationToken);

                var boxes = FindAll<System.Windows.Controls.ComboBox>(window).Where(b => b.IsVisible).ToList();
                boxes.Should().Contain(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Keystrokes shown" && Equals(b.SelectedItem, KeystrokeMode.Shortcuts));
                boxes.Should().Contain(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Pointer effect" && Equals(b.SelectedItem, PointerEffect.None));
                boxes.Should().Contain(b => System.Windows.Automation.AutomationProperties.GetName(b) == "Left button colour" && b.SelectedItem is ColorChoice);
                var radios = FindAll<System.Windows.Controls.RadioButton>(window).ToList();
                radios.Should().ContainSingle(r => r.IsChecked == true && ((string)r.Content).StartsWith("Keep the video clean", StringComparison.Ordinal));
                FindAll<System.Windows.Controls.ListBox>(window).Single().Items.Count.Should().Be(KeystrokeOverlaySettings.DefaultExcludedApps.Count);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static IEnumerable<T> FindAll<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        for (var i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var inner in FindAll<T>(child))
            {
                yield return inner;
            }
        }
    }

    [Fact]
    public void An_encoder_set_by_hand_that_is_not_known_stays_chosen()
    {
        _settings.Current = _settings.Current with { Recording = new RecordingSettings(Encoder: "future_encoder") };

        using var vm = Create();

        vm.RecordingEncoder!.Id.Should().Be("future_encoder");
        vm.RecordingEncoder.Label.Should().Contain("unknown");
    }

    [Fact]
    public async Task Sound_settings_list_the_microphones_and_are_saved()
    {
        var audio = new FakeAudioDevices(new AudioDevice("{mic-1}", "Microphone (H5)"), new AudioDevice("{mic-2}", "Line In"));
        using var vm = Create(audio);

        vm.RecordSystemAudio.Should().BeTrue("system audio is on by default");
        vm.Microphones.Select(m => m.Label).Should().Equal("None", "Default microphone (Windows setting)", "Microphone (H5)", "Line In");
        vm.RecordingMicrophone!.Id.Should().BeNull("no microphone by default");
        vm.SpeakersStatus.Should().Contain("Speakers (Realtek)");
        vm.OutputDevices.Select(d => d.Label).Should().Equal("Default output device (follows Windows)", "Speakers (Realtek)", "Headphones (H5)");
        vm.RecordingOutput!.Id.Should().BeNull("Windows' default by default");

        vm.RecordSystemAudio = false;
        vm.RecordingMicrophone = vm.Microphones.Single(m => m.Label == "Microphone (H5)");
        vm.RecordingOutput = vm.OutputDevices.Single(d => d.Label == "Headphones (H5)");
        await vm.ApplyCommand.ExecuteAsync(null);

        _settings.Current.Recording.SystemAudio.Should().BeFalse();
        _settings.Current.Recording.Microphone.Should().Be("{mic-1}");
        _settings.Current.Recording.SystemAudioDevice.Should().Be("{out-2}");
    }

    [Fact]
    public async Task A_microphone_that_is_not_connected_stays_chosen()
    {
        _settings.Current = _settings.Current with { Recording = new RecordingSettings(Microphone: "{unplugged}", SystemAudioDevice: "{gone}") };
        using var vm = Create(new FakeAudioDevices(new AudioDevice("{mic-1}", "Microphone (H5)")) { HasSpeakers = false });

        vm.RecordingMicrophone!.Id.Should().Be("{unplugged}");
        vm.RecordingMicrophone.Label.Should().Contain("not connected");
        vm.Microphones.Should().Contain(vm.RecordingMicrophone);
        vm.SpeakersStatus.Should().StartWith("No sound output device");
        vm.RecordingOutput!.Id.Should().Be("{gone}");
        vm.RecordingOutput.Label.Should().Contain("not connected");

        vm.RecordingQuality = QualityPreset.Small;
        await vm.ApplyCommand.ExecuteAsync(null);
        _settings.Current.Recording.Microphone.Should().Be("{unplugged}", "only choosing another microphone replaces it");
        _settings.Current.Recording.SystemAudioDevice.Should().Be("{gone}");
    }

    [Fact]
    public async Task One_app_can_be_chosen_for_system_audio()
    {
        var apps = new FakeApps(new AudioApp("spotify.exe", "Spotify", PlayingSound: true), new AudioApp("chrome.exe", "Google Chrome"));
        using var vm = Create(new FakeAudioDevices(), apps: apps);

        vm.SystemAudioScopes.Should().Equal(SystemAudioScope.All, SystemAudioScope.OnlyApp, SystemAudioScope.AllExceptApp);
        vm.RecordingSystemAudioScope.Should().Be(SystemAudioScope.All);
        vm.IsOutputChoiceEnabled.Should().BeTrue();
        vm.IsAppChoiceEnabled.Should().BeFalse();
        vm.Apps.Select(a => a.Label).Should().Equal("Spotify (playing sound)", "Google Chrome");

        vm.RecordingSystemAudioScope = SystemAudioScope.OnlyApp;
        vm.IsAppChoiceEnabled.Should().BeTrue();
        vm.IsOutputChoiceEnabled.Should().BeFalse("only the app is recorded, on whatever device it plays to");
        vm.HasErrors.Should().BeTrue("an app has to be chosen");
        vm.Errors.Should().Contain("choose the app");

        vm.RecordingApp = vm.Apps[0];
        vm.HasErrors.Should().BeFalse();
        await vm.ApplyCommand.ExecuteAsync(null);

        _settings.Current.Recording.SystemAudioScope.Should().Be(SystemAudioScope.OnlyApp);
        _settings.Current.Recording.SystemAudioApp.Should().Be("spotify.exe");
        _settings.Current.Recording.SystemAudioAppName.Should().Be("Spotify");

        vm.RecordSystemAudio = false;
        vm.IsAppChoiceEnabled.Should().BeFalse();
        vm.HasErrors.Should().BeFalse();
    }

    [Fact]
    public void An_app_that_is_not_running_stays_chosen_and_is_found_after_a_refresh()
    {
        _settings.Current = _settings.Current with
        {
            Recording = new RecordingSettings(SystemAudioScope: SystemAudioScope.AllExceptApp, SystemAudioApp: "discord.exe", SystemAudioAppName: "Discord"),
        };
        var apps = new FakeApps(new AudioApp("chrome.exe", "Google Chrome"));
        using var vm = Create(apps: apps);

        vm.RecordingSystemAudioScope.Should().Be(SystemAudioScope.AllExceptApp);
        vm.RecordingApp!.Label.Should().Be("Discord (not running)");

        apps.Running = [new AudioApp("chrome.exe", "Google Chrome"), new AudioApp("discord.exe", "Discord", PlayingSound: true)];
        vm.RefreshAppsCommand.Execute(null);

        vm.Apps.Select(a => a.Label).Should().Equal("Google Chrome", "Discord (playing sound)");
        vm.RecordingApp!.Exe.Should().Be("discord.exe");
    }

    [Fact]
    public async Task Microphone_processing_is_loaded_and_saved_keeping_what_settings_does_not_show()
    {
        _settings.Current = _settings.Current with
        {
            Recording = new RecordingSettings(
                Microphone: "default",
                MicrophoneProcessing: new MicrophoneProcessing(NoiseSuppression: true, NoiseSuppressionStrength: 60, NoiseGate: true, GateThresholdDb: -50)),
        };
        using var vm = Create(new FakeAudioDevices());

        vm.HasMicrophone.Should().BeTrue();
        vm.MicrophoneGate.Should().BeTrue();
        vm.GateThresholdDb.Should().Be(-50);
        vm.GateThresholdText.Should().Be("-50 dB");
        vm.MicrophoneCompressor.Should().BeFalse();
        vm.MicrophoneLimiter.Should().BeTrue("on by default");
        vm.NormalizeLoudness.Should().BeFalse("off by default");

        vm.NormalizeLoudness = true;
        vm.GateThresholdDb = -38.4;
        vm.MicrophoneCompressor = true;
        vm.MicrophoneLimiter = false;
        await vm.ApplyCommand.ExecuteAsync(null);

        _settings.Current.Recording.MicrophoneProcessing.Should().Be(
            new MicrophoneProcessing(NoiseSuppression: true, NoiseSuppressionStrength: 60, NoiseGate: true, GateThresholdDb: -38, Compressor: true, Limiter: false));
        _settings.Current.Recording.NormalizeLoudness.Should().BeTrue();

        vm.RecordingMicrophone = vm.Microphones.Single(m => m.Id is null);
        vm.HasMicrophone.Should().BeFalse("processing only applies to a microphone");
    }

    [Fact]
    public async Task The_meters_listen_to_what_a_recording_would_record_and_follow_the_choices()
    {
        _settings.Current = _settings.Current with { Recording = new RecordingSettings(Microphone: "{mic-1}") };
        var monitor = new FakeMonitor();
        using var vm = Create(new FakeAudioDevices(new AudioDevice("{mic-1}", "Microphone (H5)")), monitor: monitor);
        vm.CanMonitor.Should().BeTrue();
        monitor.Requests.Should().BeEmpty("nothing is opened until the page is shown");

        vm.StartMonitoring();
        await vm.MonitorWork;
        monitor.Requests.Should().ContainSingle().Which.Should().Be(new MonitorRequest("{mic-1}", new MicrophoneProcessing(), SystemAudio: true));

        vm.RecordSystemAudio = false;
        vm.RecordingOutput = vm.OutputDevices.Single(d => d.Id == "{out-2}");
        await vm.MonitorWork;
        monitor.Requests.Should().HaveCount(3, "each change listens again");
        monitor.Requests[^1].SystemAudio.Should().BeFalse();
        monitor.Requests[^1].SystemAudioDevice.Should().Be("{out-2}");

        vm.MicrophoneCompressor = true;
        monitor.Processing.Should().ContainSingle().Which.Compressor.Should().BeTrue("processing changes apply at once, without reopening");

        monitor.Levels = new SoundLevels(new AudioLevel(0.5f, 0.1f, false), new AudioLevel(0.25f, 0.1f, true), default);
        vm.RefreshMeters(TimeSpan.FromSeconds(1));
        vm.SystemAudioLevel.Peak.Should().BeApproximately(0.9, 0.01);
        vm.MicrophoneLevel.Clipping.Should().BeTrue();

        monitor.Problem = "The microphone chosen in Settings isn't connected.";
        vm.RefreshMeters(TimeSpan.FromSeconds(2));
        vm.MonitorStatus.Should().Be(monitor.Problem);

        vm.StopMonitoring();
        await vm.MonitorWork;
        monitor.Listening.Should().BeFalse();
        vm.MicrophoneLevel.Peak.Should().Be(0, "the meters go back to silence");
    }

    [Fact]
    public async Task The_microphone_test_records_and_plays_back_as_recorded_and_processed()
    {
        _settings.Current = _settings.Current with { Recording = new RecordingSettings(Microphone: "default") };
        var monitor = new FakeMonitor();
        using var vm = Create(new FakeAudioDevices(), monitor: monitor);
        vm.PlayOriginalCommand.CanExecute(null).Should().BeFalse("nothing recorded yet");

        await vm.TestMicrophoneCommand.ExecuteAsync(null);
        vm.MicrophoneTestStatus.Should().StartWith("Recorded.");
        vm.PlayOriginalCommand.CanExecute(null).Should().BeTrue();

        await vm.PlayOriginalCommand.ExecuteAsync(null);
        await vm.PlayProcessedCommand.ExecuteAsync(null);
        monitor.Played.Should().HaveCount(2);
        monitor.Played[0].Should().Equal(monitor.Recording);
        monitor.Played[1].Should().Equal(1f, 1f, 0.5f, 0.5f);

        monitor.PlayGate = new TaskCompletionSource();
        var playing = vm.PlayOriginalCommand.ExecuteAsync(null);
        vm.IsPlayingTest.Should().BeTrue();
        vm.TestMicrophoneCommand.CanExecute(null).Should().BeFalse("not while playing");
        vm.StopPlaybackCommand.Execute(null);
        await playing;
        vm.MicrophoneTestStatus.Should().Be("Stopped.");
        vm.IsPlayingTest.Should().BeFalse();

        vm.RecordingMicrophone = vm.Microphones.Single(m => m.Id is null);
        vm.TestMicrophoneCommand.CanExecute(null).Should().BeFalse("no microphone chosen");
    }

    [Fact]
    public async Task Noise_suppression_is_offered_with_RNNoise_and_changes_what_the_meter_hears()
    {
        Create(monitor: new FakeMonitor()).CanSuppressNoise.Should().BeFalse("no RNNoise: not offered");

        var monitor = new FakeMonitor { CanSuppressNoise = true };
        _settings.Current = _settings.Current with { Recording = new RecordingSettings(Microphone: "default") };
        using var vm = Create(new FakeAudioDevices(), monitor: monitor);
        vm.CanSuppressNoise.Should().BeTrue();
        vm.MicrophoneNoiseSuppression.Should().BeFalse("off by default");
        vm.NoiseSuppressionStrength.Should().Be(100);
        vm.NoiseSuppressionStrengthText.Should().Be("100%");

        vm.StartMonitoring();
        await vm.MonitorWork;
        vm.MicrophoneNoiseSuppression = true;
        vm.NoiseSuppressionStrength = 80;
        monitor.Processing[^1].Should().Be(new MicrophoneProcessing(NoiseSuppression: true, NoiseSuppressionStrength: 80));

        await vm.ApplyCommand.ExecuteAsync(null);
        _settings.Current.Recording.MicrophoneProcessing!.NoiseSuppression.Should().BeTrue();
        _settings.Current.Recording.MicrophoneProcessing.NoiseSuppressionStrength.Should().Be(80);
    }

    [Fact]
    public async Task Push_to_talk_takes_any_key_but_not_one_of_the_hotkeys()
    {
        using var vm = Create();
        vm.PushToTalkText.Should().BeEmpty("off by default");
        vm.PushToTalkStatus.Should().StartWith("Not set");

        vm.PushToTalkText = "F13";
        vm.HasErrors.Should().BeFalse("a key on its own is fine: it isn't registered");
        vm.PushToTalkStatus.Should().Be("Hold it while recording");
        await vm.ApplyCommand.ExecuteAsync(null);
        _settings.Current.Recording.PushToTalk.Should().Be("F13");

        vm.PushToTalkText = "Shift+PrintScreen";
        vm.HasErrors.Should().BeTrue();
        vm.PushToTalkHasProblem.Should().BeTrue();
        vm.PushToTalkStatus.Should().StartWith("Same as Record Screen");

        vm.PushToTalkText = "Ctrl+";
        vm.PushToTalkStatus.Should().Be("Not a valid key");

        vm.ClearPushToTalkCommand.Execute(null);
        vm.HasErrors.Should().BeFalse();
        await vm.ApplyCommand.ExecuteAsync(null);
        _settings.Current.Recording.PushToTalk.Should().BeNull();
    }

    [Fact]
    public void Without_process_loopback_only_all_sound_is_offered()
    {
        _settings.Current = _settings.Current with
        {
            Recording = new RecordingSettings(SystemAudioScope: SystemAudioScope.OnlyApp, SystemAudioApp: "spotify.exe"),
        };
        using var vm = Create(apps: new FakeApps { IsSupported = false });

        vm.IsAppAudioSupported.Should().BeFalse();
        vm.SystemAudioScopes.Should().Equal(SystemAudioScope.All);
        vm.RecordingSystemAudioScope.Should().Be(SystemAudioScope.All);
    }

    [Fact]
    public void Recording_a_hotkey_pauses_global_hotkeys_until_done()
    {
        using var vm = Create();
        _hotkeyService.Registered.Should().NotBeEmpty();

        vm.BeginHotkeyRecording();
        vm.BeginHotkeyRecording(); // focus moving between boxes
        _hotkeyService.Registered.Should().BeEmpty();

        vm.EndHotkeyRecording();
        _hotkeyService.Registered.Should().HaveCount(7);
    }

    [Fact]
    public void Disposing_the_window_resumes_hotkeys()
    {
        var vm = Create();
        vm.BeginHotkeyRecording();

        vm.Dispose();

        _hotkeyService.Registered.Should().HaveCount(7);
    }

    [Fact]
    public async Task History_size_and_update_checks_are_loaded_and_saved()
    {
        _settings.Current = new AppSettings
        {
            History = new HistorySettings(MaxItems: 30), // set by hand: not one of the offered sizes
            Updates = new UpdateSettings(CheckAutomatically: false, FeedUrl: @"\\server\capture-releases"),
        };
        using var vm = Create();

        vm.HistorySize!.Value.Should().Be(30);
        vm.HistorySizes.Should().Contain(vm.HistorySize, "a hand-set size is offered so it isn't lost");
        vm.CheckForUpdates.Should().BeFalse();
        vm.CanCheckForUpdates.Should().BeFalse("no update service: not an installed copy");
        vm.VersionText.Should().StartWith("Version ");

        vm.HistorySize = vm.HistorySizes.Single(c => c.Value == 0);
        vm.CheckForUpdates = true;
        await vm.ApplyCommand.ExecuteAsync(null);

        _settings.Current.History.MaxItems.Should().Be(0);
        _settings.Current.Updates.CheckAutomatically.Should().BeTrue();
        _settings.Current.Updates.FeedUrl.Should().Be(@"\\server\capture-releases", "the address is only edited in the file and must survive");
    }

    private SettingsViewModel Create(
        IAudioDevices? audio = null, IRecordingEncoders? encoders = null, IAudioApps? apps = null, IAudioMonitor? monitor = null, CashGear.Capture.Overlays.Webcam.IWebcams? webcams = null) =>
        new(_settings, _paths, _registrar, _startup, _picker, NullLogger<SettingsViewModel>.Instance, encoders: encoders, audio: audio, apps: apps, monitor: monitor, webcams: webcams);

    /// <summary>Records what it is asked to listen to; levels and the test recording are set by the test.</summary>
    private sealed class FakeMonitor : IAudioMonitor
    {
        public List<MonitorRequest> Requests { get; } = [];

        public List<MicrophoneProcessing> Processing { get; } = [];

        public List<float[]> Played { get; } = [];

        public bool Listening { get; private set; }

        public SoundLevels Levels { get; set; }

        public float[] Recording { get; set; } = [0.5f, 0.5f, 0.25f, 0.25f];

        public TaskCompletionSource? PlayGate { get; set; }

        public bool CanSuppressNoise { get; init; }

        public string? Problem { get; set; }

        public void Start(MonitorRequest request)
        {
            Requests.Add(request);
            Listening = true;
        }

        public void SetProcessing(MicrophoneProcessing processing) => Processing.Add(processing);

        public void Stop() => Listening = false;

        public SoundLevels TakeLevels() => Levels;

        public Task<float[]> RecordMicrophoneAsync(string microphone, TimeSpan length, CancellationToken ct = default) => Task.FromResult(Recording);

        public float[] Process(float[] recorded, MicrophoneProcessing processing) => [.. recorded.Select(s => s * 2)];

        public async Task PlayAsync(float[] samples, CancellationToken ct = default)
        {
            Played.Add(samples);
            if (PlayGate is { } gate)
            {
                await gate.Task.WaitAsync(ct);
            }
        }

        public void Dispose() => Stop();
    }

    private sealed class FakeApps(params AudioApp[] running) : IAudioApps
    {
        public bool IsSupported { get; init; } = true;

        public IReadOnlyList<AudioApp> Running { get; set; } = running;

        public IReadOnlyList<AudioApp> List() => Running;
    }

    private sealed class FakeEncoders(IReadOnlyList<EncoderProbe> detected) : IRecordingEncoders
    {
        public IReadOnlyList<EncoderProbe>? Next { get; set; }

        public bool Forced { get; private set; }

        public string? Problem => null;

        public string? FfmpegVersion => "n9.0.2";

        public IReadOnlyList<EncoderProbe>? Detected { get; private set; } = detected;

        public Task<IReadOnlyList<EncoderProbe>> DetectAsync(bool force = false, CancellationToken ct = default)
        {
            Forced |= force;
            if (force && Next is not null)
            {
                Detected = Next;
            }

            return Task.FromResult(Detected!);
        }
    }

    private sealed class FakeAudioDevices(params AudioDevice[] microphones) : IAudioDevices
    {
        public bool HasSpeakers { get; init; } = true;

        public IReadOnlyList<AudioDevice> Microphones() => microphones;

        public IReadOnlyList<AudioDevice> Outputs() => HasSpeakers ? [new AudioDevice("{out}", "Speakers (Realtek)"), new AudioDevice("{out-2}", "Headphones (H5)")] : [];

        public AudioDevice? DefaultOutput() => HasSpeakers ? new AudioDevice("{out}", "Speakers (Realtek)") : null;
    }
}
