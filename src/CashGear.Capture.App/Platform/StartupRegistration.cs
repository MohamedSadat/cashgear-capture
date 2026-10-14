using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CashGear.Capture.App.Platform;

/// <summary>Whether the app starts when the user signs in to Windows.</summary>
public interface IStartupRegistration
{
    bool IsEnabled { get; }

    /// <summary>Turns start-with-Windows on (pointing at this executable) or off. Throws if the registry refuses.</summary>
    void SetEnabled(bool enabled);
}

/// <summary>
/// Uses the per-user <c>HKCU\…\CurrentVersion\Run</c> key, so no elevation is needed. The registry is the source of
/// truth (not settings.json), so turning it off in Task Manager's Startup tab is reflected here.
/// </summary>
public sealed partial class RegistryStartupRegistration : IStartupRegistration
{
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultValueName = "CashGear Capture";

    private readonly string _keyPath;
    private readonly string _valueName;
    private readonly string _command;
    private readonly ILogger<RegistryStartupRegistration> _logger;

    public RegistryStartupRegistration(ILogger<RegistryStartupRegistration> logger)
        : this(logger, RunKeyPath, DefaultValueName, $"\"{Environment.ProcessPath}\"")
    {
    }

    /// <summary>For tests: a scratch key instead of the real Run key.</summary>
    internal RegistryStartupRegistration(ILogger<RegistryStartupRegistration> logger, string keyPath, string valueName, string command)
    {
        _logger = logger;
        _keyPath = keyPath;
        _valueName = valueName;
        _command = command;
    }

    public bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(_keyPath);
            return key?.GetValue(_valueName) is string { Length: > 0 };
        }
    }

    public void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(_keyPath, writable: true);
        if (enabled)
        {
            // Always rewrite: the app may have moved since it was last registered.
            key.SetValue(_valueName, _command, RegistryValueKind.String);
        }
        else
        {
            key.DeleteValue(_valueName, throwOnMissingValue: false);
        }

        LogChanged(enabled, _command);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Start with Windows: {Enabled} ({Command})")]
    private partial void LogChanged(bool enabled, string command);
}
