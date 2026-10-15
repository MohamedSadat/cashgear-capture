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
        vm.Hotkeys.Select(h => h.Text).Should().Equal("PrintScreen", "Alt+PrintScreen", "Ctrl+PrintScreen", "Ctrl+Shift+PrintScreen", "Shift+PrintScreen", "Ctrl+Alt+PrintScreen");
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
    public async Task Recording_settings_are_loaded_and_saved()
    {
        _settings.Current = _settings.Current with { Recording = new RecordingSettings(15, 28, false, false, RecordingEncoder.Software) };
        using var vm = Create();
        vm.RecordingFps.Should().Be(15);
        vm.RecordingQuality.Should().Be(28);
        vm.RecordingEncoder.Should().Be(RecordingEncoder.Software);
        vm.IsFfmpegMissing.Should().BeTrue("this view model was given no ffmpeg");

        vm.RecordingFps = 60;
        vm.RecordingQuality = 20;
        vm.RecordingShowClicks = true;
        await vm.ApplyCommand.ExecuteAsync(null);

        _settings.Current.Recording.Should().Be(new RecordingSettings(60, 20, false, true, RecordingEncoder.Software));
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
        _hotkeyService.Registered.Should().HaveCount(6);
    }

    [Fact]
    public void Disposing_the_window_resumes_hotkeys()
    {
        var vm = Create();
        vm.BeginHotkeyRecording();

        vm.Dispose();

        _hotkeyService.Registered.Should().HaveCount(6);
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

    private SettingsViewModel Create() =>
        new(_settings, _paths, _registrar, _startup, _picker, NullLogger<SettingsViewModel>.Instance);
}
