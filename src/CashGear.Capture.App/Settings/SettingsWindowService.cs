using System.Windows;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace CashGear.Capture.App.Settings;

/// <summary>Opens the settings window, or brings the open one to the front. UI thread only.</summary>
public sealed class SettingsWindowService(IServiceProvider services, ISettingsService settings)
{
    private SettingsWindow? _window;
    private UiLanguage? _offered;

    public void Show()
    {
        if (_window is { } open)
        {
            if (open.WindowState == WindowState.Minimized)
            {
                open.WindowState = WindowState.Normal;
            }

            open.Activate();
            return;
        }

        // Created per window and disposed by it; ActivatorUtilities keeps the container from holding on to it.
        var viewModel = ActivatorUtilities.CreateInstance<SettingsViewModel>(services, InstalledFonts());
        var window = new SettingsWindow(viewModel, settings.Current.Appearance.Theme);
        viewModel.Saved += (_, _) => OfferRestart(window);
        window.Closed += (_, _) => _window = null;
        _window = window;
        window.Show();
        window.Activate();
    }

    /// <summary>A new language shows once the app starts again: offers to restart now (asked once per language).</summary>
    private void OfferRestart(Window owner)
    {
        var language = settings.Current.Appearance.Language;
        if (!AppLanguage.NeedsRestart(language) || _offered == language)
        {
            return;
        }

        _offered = language;
        var answer = AppDialog.Show(
            owner, SettingsText.RestartForLanguage, buttons: DialogButtons.YesNo, kind: DialogKind.Question, theme: settings.Current.Appearance.Theme);
        if (answer == DialogAnswer.Yes)
        {
            owner.Close();
            _ = services.GetRequiredService<TrayViewModel>().RestartCommand.ExecuteAsync(null);
        }
    }

    /// <summary>The font families installed here, by name (for the keystroke overlay).</summary>
    private static IReadOnlyList<string> InstalledFonts() =>
        [.. System.Windows.Media.Fonts.SystemFontFamilies.Select(f => f.Source).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.CurrentCultureIgnoreCase)];
}
