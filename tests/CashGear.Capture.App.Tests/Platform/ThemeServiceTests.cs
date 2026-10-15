using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Platform;

public sealed class ThemeServiceTests
{
    private readonly MutableSettings _settings = new();
    private bool _windowsDark;

    private ThemeService Create() =>
        new(_settings, new InlineUiDispatcher(), NullLogger<ThemeService>.Instance, () => _windowsDark);

    [Fact]
    public void Following_windows_picks_up_a_switch_to_dark_and_back()
    {
        using var themes = Create();
        var changes = new List<AppTheme>();
        themes.ThemeChanged += (_, theme) => changes.Add(theme);

        themes.Refresh();
        changes.Should().BeEmpty("nothing changed");

        _windowsDark = true;
        themes.Refresh();
        _windowsDark = false;
        themes.Refresh();

        changes.Should().Equal(AppTheme.System, AppTheme.System);
    }

    [Fact]
    public void A_fixed_theme_ignores_windows_but_follows_the_setting()
    {
        _settings.Current = new AppSettings { Appearance = new AppearanceSettings(AppTheme.Light) };
        using var themes = Create();
        themes.Start();
        var changes = new List<AppTheme>();
        themes.ThemeChanged += (_, theme) => changes.Add(theme);

        _windowsDark = true;
        themes.Refresh();
        changes.Should().BeEmpty();

        _settings.Update(s => s with { Appearance = new AppearanceSettings(AppTheme.Dark) });
        changes.Should().Equal(AppTheme.Dark);

        // Still dark, but windows must switch to "System" so they follow Windows from now on.
        _settings.Update(s => s with { Appearance = new AppearanceSettings(AppTheme.System) });
        changes.Should().Equal(AppTheme.Dark, AppTheme.System);
    }
}
