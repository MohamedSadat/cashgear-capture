namespace CashGear.Capture.Core.Settings;

/// <summary>Well-known folders. Injected so tests can point everything at a temp directory.</summary>
/// <param name="DataDirectoryOverride">App state that is not a setting (e.g. the file-name counter); defaults to the parent of <paramref name="LogDirectory"/>.</param>
public sealed record AppPaths(
    string SettingsDirectory,
    string LogDirectory,
    string DefaultCaptureDirectory,
    string? DataDirectoryOverride = null)
{
    public string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

    /// <summary>Saved effect stacks and watermarks, and the image tools' last choices (next to the settings, so they roam with them).</summary>
    public string PresetsFile => Path.Combine(SettingsDirectory, "presets.json");

    /// <summary>Per-machine app state (<c>%LocalAppData%\CashGear\Capture</c>).</summary>
    public string DataDirectory => DataDirectoryOverride ?? Path.GetDirectoryName(LogDirectory) ?? LogDirectory;

    /// <summary>Copies of recent captures and their thumbnails.</summary>
    public string HistoryDirectory => Path.Combine(DataDirectory, "history");

    /// <summary>
    /// The usual folders, or with <c>CASHGEAR_INSTANCE=name</c> folders of their own (<c>CashGear\Capture-name</c>):
    /// a second copy (tools\Bench) then runs beside the installed one without sharing its settings or data.
    /// </summary>
    public static AppPaths FromEnvironment()
    {
        var folder = InstanceName is { } name ? "Capture-" + name : "Capture";
        return new(
            SettingsDirectory: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CashGear", folder),
            LogDirectory: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CashGear", folder, "logs"),
            DefaultCaptureDirectory: Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CashGear Capture"));
    }

    /// <summary>
    /// <c>CASHGEAR_INSTANCE</c>: letters, digits and dashes (anything else is left out), or null when unset. A copy
    /// started with it has its own single-instance names and folders.
    /// </summary>
    public static string? InstanceName
    {
        get
        {
            var name = new string((Environment.GetEnvironmentVariable("CASHGEAR_INSTANCE") ?? string.Empty)
                .Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').Take(32).ToArray());
            return name.Length == 0 ? null : name;
        }
    }

    /// <summary>The folder captures are saved to under the given settings.</summary>
    public string ResolveCaptureDirectory(OutputSettings output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return output.Folder is { Length: > 0 } folder
            ? Environment.ExpandEnvironmentVariables(folder)
            : DefaultCaptureDirectory;
    }
}
