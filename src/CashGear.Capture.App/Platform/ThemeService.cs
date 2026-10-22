using System.Windows;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;

namespace CashGear.Capture.App.Platform;

/// <summary>A window that follows the light/dark theme while it is open.</summary>
public interface IThemedWindow
{
    /// <summary>Applies the theme (see <see cref="Theming.Apply"/>) and anything window-specific, such as a canvas colour.</summary>
    void ApplyTheme(AppTheme theme);
}

/// <summary>
/// Re-themes the open windows when the theme changes: when the setting is changed, and — for the "System" theme —
/// when Windows switches between light and dark apps (Settings › Personalisation › Colours), without a restart.
/// </summary>
public sealed partial class ThemeService : IDisposable
{
    private readonly ISettingsService _settings;
    private readonly IUiDispatcher _ui;
    private readonly Func<bool> _windowsIsDark;
    private readonly ILogger<ThemeService> _logger;
    private (AppTheme Theme, bool Dark, bool HighContrast) _applied;
    private bool _started;

    public ThemeService(ISettingsService settings, IUiDispatcher ui, ILogger<ThemeService> logger)
        : this(settings, ui, logger, () => Theming.IsDark(AppTheme.System))
    {
    }

    internal ThemeService(ISettingsService settings, IUiDispatcher ui, ILogger<ThemeService> logger, Func<bool> windowsIsDark)
    {
        _settings = settings;
        _ui = ui;
        _logger = logger;
        _windowsIsDark = windowsIsDark;
        _applied = Current();
    }

    /// <summary>Raised on the UI thread after the effective theme changed, with the new theme.</summary>
    public event EventHandler<AppTheme>? ThemeChanged;

    /// <summary>Starts listening. UI thread.</summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }

        _started = true;
        _settings.Changed += OnChanged;
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        SystemParameters.StaticPropertyChanged += OnSystemParameterChanged;
    }

    /// <summary>Re-checks the theme (the setting and, for System, Windows) and re-themes the open windows if it changed.</summary>
    public void Refresh()
    {
        var current = Current();
        if (current == _applied)
        {
            return;
        }

        if (current.HighContrast != _applied.HighContrast)
        {
            ChromeBrushes.Current.Refresh();
        }

        _applied = current;
        LogChanged(current.Theme, current.Dark, current.HighContrast);
        if (Application.Current is { } app)
        {
            foreach (var window in app.Windows.OfType<IThemedWindow>())
            {
                window.ApplyTheme(current.Theme);
            }
        }

        ThemeChanged?.Invoke(this, current.Theme);
    }

    public void Dispose()
    {
        if (_started)
        {
            _settings.Changed -= OnChanged;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            SystemParameters.StaticPropertyChanged -= OnSystemParameterChanged;
        }
    }

    private (AppTheme Theme, bool Dark, bool HighContrast) Current()
    {
        var theme = _settings.Current.Appearance.Theme;
        return (theme, theme switch
        {
            AppTheme.Dark => true,
            AppTheme.Light => false,
            _ => _windowsIsDark(),
        }, HighContrast.IsOn);
    }

    private void OnChanged(object? sender, EventArgs e) => _ = _ui.InvokeAsync(Refresh);

    // Windows sends this (category General) when the app theme is switched.
    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => _ = _ui.InvokeAsync(Refresh);

    // WPF updates SystemParameters.HighContrast when high contrast is turned on or off.
    private void OnSystemParameterChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SystemParameters.HighContrast))
        {
            _ = _ui.InvokeAsync(Refresh);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Theme is now {Theme} (dark: {Dark}, high contrast: {HighContrast})")]
    private partial void LogChanged(AppTheme theme, bool dark, bool highContrast);
}
