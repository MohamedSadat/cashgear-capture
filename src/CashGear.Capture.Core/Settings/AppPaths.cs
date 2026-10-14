namespace CashGear.Capture.Core.Settings;

/// <summary>Well-known folders. Injected so tests can point everything at a temp directory.</summary>
public sealed record AppPaths(string SettingsDirectory, string LogDirectory, string DefaultCaptureDirectory)
{
    public string SettingsFile => Path.Combine(SettingsDirectory, "settings.json");

    public static AppPaths FromEnvironment() => new(
        SettingsDirectory: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CashGear", "Capture"),
        LogDirectory: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CashGear", "Capture", "logs"),
        DefaultCaptureDirectory: Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "CashGear Capture"));

    /// <summary>The folder captures are saved to under the given settings.</summary>
    public string ResolveCaptureDirectory(OutputSettings output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return output.Folder is { Length: > 0 } folder
            ? Environment.ExpandEnvironmentVariables(folder)
            : DefaultCaptureDirectory;
    }
}
