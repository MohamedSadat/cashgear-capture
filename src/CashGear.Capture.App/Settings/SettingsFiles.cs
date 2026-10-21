using System.IO;
using System.Text;
using System.Windows;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Editor.Presets;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CashGear.Capture.App.Settings;

/// <summary>The questions Settings › General asks around importing, exporting and resetting the settings.</summary>
public interface ISettingsFileDialogs
{
    /// <summary>Where to export to, or null if the user cancelled.</summary>
    string? PickExportFile(string suggestedName);

    /// <summary>The file to import, or null if the user cancelled.</summary>
    string? PickImportFile();

    /// <summary>Whether the user goes ahead with what <paramref name="question"/> asks.</summary>
    bool Confirm(string question);
}

/// <summary>
/// Settings › General › Export, Import and Reset all settings. Before an import or a reset, settings.json (and
/// presets.json for an import) is copied next to itself as <c>settings.json.before-import-…</c> /
/// <c>.before-reset-…</c>, so nothing is lost for good.
/// </summary>
public sealed partial class SettingsFiles(ISettingsService settings, IPresetStore presets, AppPaths paths, TimeProvider time, ILogger<SettingsFiles> logger)
{
    /// <summary>Writes the settings and presets to <paramref name="path"/>.</summary>
    public async Task ExportAsync(string path, CancellationToken ct = default)
    {
        var text = SettingsTransfer.Write(settings.Current, presets.Current.ToJson(), BuildInfo.Version, time.GetLocalNow());
        await File.WriteAllTextAsync(path, text, new UTF8Encoding(false), ct).ConfigureAwait(false);
        LogExported(path);
    }

    /// <summary>Replaces the settings with those in <paramref name="path"/> and adds its presets.</summary>
    /// <exception cref="SettingsImportException">Not an exported settings file.</exception>
    public async Task<ImportedSettings> ImportAsync(string path, CancellationToken ct = default)
    {
        var imported = SettingsTransfer.Read(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false));
        var library = imported.PresetsJson is { } json ? PresetLibrary.FromJson(json) : null;

        Backup("before-import", paths.SettingsFile, paths.PresetsFile);
        await settings.UpdateAsync(current => SettingsTransfer.KeepLocal(imported.Settings, current, keepLanguage: false), ct).ConfigureAwait(false);
        if (library is not null)
        {
            await presets.UpdateAsync(current => current.MergedWith(library), ct).ConfigureAwait(false);
        }

        LogImported(path, imported.AppVersion);
        return imported;
    }

    /// <summary>Every setting back to its default, but the language and the crash-report choice.</summary>
    public async Task ResetAsync(CancellationToken ct = default)
    {
        Backup("before-reset", paths.SettingsFile);
        await settings.UpdateAsync(current => SettingsTransfer.KeepLocal(new AppSettings(), current, keepLanguage: true), ct).ConfigureAwait(false);
        LogReset();
    }

    private void Backup(string reason, params string[] files)
    {
        var stamp = time.GetLocalNow().ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        foreach (var file in files.Where(File.Exists))
        {
            var copy = $"{file}.{reason}-{stamp}";
            File.Copy(file, copy, overwrite: true);
            LogBackedUp(file, copy);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Settings exported to {Path}")]
    private partial void LogExported(string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Settings imported from {Path} (exported by {Version})")]
    private partial void LogImported(string path, string? version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Settings reset to the defaults")]
    private partial void LogReset();

    [LoggerMessage(Level = LogLevel.Information, Message = "Backed up {File} as {Copy}")]
    private partial void LogBackedUp(string file, string copy);
}

/// <summary>The real dialogs, over the settings window.</summary>
internal sealed class WpfSettingsFileDialogs(ISettingsService settings) : ISettingsFileDialogs
{
    public string? PickExportFile(string suggestedName)
    {
        var dialog = new SaveFileDialog
        {
            Title = SettingsText.ExportTitle,
            Filter = SettingsText.SettingsFileFilter,
            FileName = suggestedName,
            DefaultExt = SettingsTransfer.Extension,
            AddExtension = true,
        };
        return dialog.ShowDialog(Owner()) == true ? dialog.FileName : null;
    }

    public string? PickImportFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = SettingsText.ImportTitle,
            Filter = SettingsText.SettingsFileFilter,
            CheckFileExists = true,
        };
        return dialog.ShowDialog(Owner()) == true ? dialog.FileName : null;
    }

    public bool Confirm(string question) =>
        AppDialog.Show(Owner(), question, buttons: DialogButtons.YesNo, kind: DialogKind.Question, theme: settings.Current.Appearance.Theme) == DialogAnswer.Yes;

    private static Window? Owner() => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
}
