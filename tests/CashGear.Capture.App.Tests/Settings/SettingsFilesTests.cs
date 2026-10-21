using System.IO;
using CashGear.Capture.App.Hotkeys;
using CashGear.Capture.App.Settings;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Effects;
using CashGear.Capture.Editor.Presets;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Settings;

public sealed class SettingsFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", "settings-files-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly AppPaths _paths;
    private readonly JsonSettingsService _settings;
    private readonly JsonPresetStore _presets;
    private readonly SettingsFiles _files;

    public SettingsFilesTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "data", "logs"), Path.Combine(_root, "pictures"));
        _settings = new JsonSettingsService(_paths, NullLogger<JsonSettingsService>.Instance, TimeProvider.System, watchForExternalChanges: false);
        _presets = new JsonPresetStore(_paths, NullLogger<JsonPresetStore>.Instance);
        _files = new SettingsFiles(_settings, _presets, _paths, TimeProvider.System, NullLogger<SettingsFiles>.Instance);
    }

    public void Dispose()
    {
        _settings.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Settings_moved_to_another_PC_come_back_with_their_presets_merged()
    {
        await _settings.UpdateAsync(s => s with
        {
            Appearance = new AppearanceSettings(AppTheme.Dark, UiLanguage.Arabic),
            Hotkeys = s.Hotkeys with { CaptureRegion = "Ctrl+Shift+R" },
        }, TestContext.Current.CancellationToken);
        await _presets.UpdateAsync(p => p.WithEffects(new EffectPreset("Mine", [new ShadowEffect()])), TestContext.Current.CancellationToken);
        var file = Path.Combine(_root, "exported.json");
        await _files.ExportAsync(file, TestContext.Current.CancellationToken);

        // The other PC: its own settings and a preset of its own.
        await _settings.UpdateAsync(_ => new AppSettings(), TestContext.Current.CancellationToken);
        await _presets.UpdateAsync(p => p.WithoutEffects("Mine").WithEffects(new EffectPreset("Theirs", [new GrayscaleEffect()])), TestContext.Current.CancellationToken);

        var imported = await _files.ImportAsync(file, TestContext.Current.CancellationToken);

        imported.AppVersion.Should().NotBeNullOrEmpty();
        _settings.Current.Appearance.Should().Be(new AppearanceSettings(AppTheme.Dark, UiLanguage.Arabic));
        _settings.Current.Hotkeys.CaptureRegion.Should().Be("Ctrl+Shift+R");
        _presets.Current.FindEffects("Mine").Should().NotBeNull();
        _presets.Current.FindEffects("Theirs").Should().NotBeNull("presets here are kept");
        Directory.GetFiles(_paths.SettingsDirectory, "settings.json.before-import-*").Should().ContainSingle();
        Directory.GetFiles(_paths.SettingsDirectory, "presets.json.before-import-*").Should().ContainSingle();
    }

    [Fact]
    public async Task A_file_that_is_not_settings_changes_nothing()
    {
        var file = Path.Combine(_root, "other.json");
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(file, """{ "theme": "dark" }""", TestContext.Current.CancellationToken);
        var before = _settings.Current;

        var import = () => _files.ImportAsync(file, TestContext.Current.CancellationToken);

        await import.Should().ThrowAsync<SettingsImportException>();
        _settings.Current.Should().Be(before);
        Directory.GetFiles(_paths.SettingsDirectory, "*.before-import-*").Should().BeEmpty();
    }

    [Fact]
    public async Task Reset_all_keeps_the_language_and_a_backup()
    {
        await _settings.UpdateAsync(s => s with
        {
            Appearance = new AppearanceSettings(AppTheme.Dark, UiLanguage.Arabic),
            History = new HistorySettings(77),
        }, TestContext.Current.CancellationToken);

        await _files.ResetAsync(TestContext.Current.CancellationToken);

        _settings.Current.Appearance.Should().Be(new AppearanceSettings(AppTheme.System, UiLanguage.Arabic));
        _settings.Current.History.MaxItems.Should().Be(HistorySettings.DefaultMaxItems);
        var backup = Directory.GetFiles(_paths.SettingsDirectory, "settings.json.before-reset-*").Should().ContainSingle().Subject;
        (await File.ReadAllTextAsync(backup, TestContext.Current.CancellationToken)).Should().Contain("77");
    }

    [Fact]
    public async Task The_settings_window_imports_after_asking_and_shows_the_imported_settings()
    {
        var file = Path.Combine(_root, "exported.json");
        await _settings.UpdateAsync(s => s with { Appearance = s.Appearance with { Theme = AppTheme.Light } }, TestContext.Current.CancellationToken);
        await _files.ExportAsync(file, TestContext.Current.CancellationToken);
        await _settings.UpdateAsync(s => s with { Appearance = s.Appearance with { Theme = AppTheme.Dark } }, TestContext.Current.CancellationToken);
        var dialogs = new FakeDialogs { Import = file, Answer = false };
        using var registrar = new HotkeyRegistrar(new FakeHotkeyService(), _settings, new FakeNotifier(), new InlineUiDispatcher(), NullLogger<HotkeyRegistrar>.Instance);
        using var vm = new SettingsViewModel(
            _settings, _paths, registrar, new FakeStartupRegistration(), new FakeFolderPicker(), NullLogger<SettingsViewModel>.Instance,
            files: _files, fileDialogs: dialogs);
        vm.Theme.Should().Be(AppTheme.Dark);

        await vm.ImportSettingsCommand.ExecuteAsync(null);
        vm.Theme.Should().Be(AppTheme.Dark, "the user said no");
        dialogs.Questions.Should().ContainSingle().Which.Should().Contain("exported.json");

        dialogs.Answer = true;
        await vm.ImportSettingsCommand.ExecuteAsync(null);
        vm.Theme.Should().Be(AppTheme.Light);
        vm.SettingsFileStatus.Should().StartWith("Settings imported from exported.json (exported by version ");

        dialogs.Import = Path.Combine(_root, "missing.json");
        await vm.ImportSettingsCommand.ExecuteAsync(null);
        vm.SettingsFileStatus.Should().StartWith("That didn't work: ");

        dialogs.Export = Path.Combine(_root, "again.json");
        await vm.ExportSettingsCommand.ExecuteAsync(null);
        File.Exists(dialogs.Export).Should().BeTrue();
        vm.SettingsFileStatus.Should().Be($"Settings exported to {dialogs.Export}.");
        vm.PortableText.Should().BeNull("an installed copy");
    }

    [Fact]
    public void A_portable_copy_says_where_it_keeps_its_settings()
    {
        var portable = AppPaths.Portable(Path.Combine(_root, "stick"));
        using var registrar = new HotkeyRegistrar(new FakeHotkeyService(), _settings, new FakeNotifier(), new InlineUiDispatcher(), NullLogger<HotkeyRegistrar>.Instance);
        using var vm = new SettingsViewModel(_settings, portable, registrar, new FakeStartupRegistration(), new FakeFolderPicker(), NullLogger<SettingsViewModel>.Instance);

        vm.PortableText.Should().Be($"This is a portable copy: its settings, logs and data are kept in {Path.Combine(_root, "stick", "Data")}.");
    }

    private sealed class FakeDialogs : ISettingsFileDialogs
    {
        public string? Import { get; set; }

        public string? Export { get; set; }

        public bool Answer { get; set; }

        public List<string> Questions { get; } = [];

        public string? PickExportFile(string suggestedName) => Export;

        public string? PickImportFile() => Import;

        public bool Confirm(string question)
        {
            Questions.Add(question);
            return Answer;
        }
    }
}
