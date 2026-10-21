using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Tests;

namespace CashGear.Capture.App.Tests.Platform;

[Collection(DesktopCollection.Name)]
public sealed class LocalizationTests
{
    private static readonly CultureInfo Arabic = CultureInfo.GetCultureInfo("ar");

    [Theory]
    [InlineData(UiLanguage.Automatic, "ar-SA", "ar")]
    [InlineData(UiLanguage.Automatic, "ar-EG", "ar")]
    [InlineData(UiLanguage.Automatic, "en-GB", "en")]
    [InlineData(UiLanguage.Automatic, "fr-FR", "en")]
    [InlineData(UiLanguage.English, "ar-SA", "en")]
    [InlineData(UiLanguage.Arabic, "en-US", "ar")]
    public void The_language_follows_the_setting_or_else_Windows(UiLanguage setting, string windows, string expected)
    {
        AppLanguage.Resolve(setting, CultureInfo.GetCultureInfo(windows)).Name.Should().Be(expected);
    }

    [Theory]
    [InlineData("""{ "appearance": { "theme": "dark", "language": "arabic" } }""", UiLanguage.Arabic)]
    [InlineData("""{ "Appearance": { "Language": "English" } }""", UiLanguage.English)]
    [InlineData("""{ "appearance": { "theme": "dark" } }""", UiLanguage.Automatic)]
    [InlineData("""{ "appearance": { "language": "klingon" } }""", UiLanguage.Automatic)]
    [InlineData("""{ "appearance": { "language": 7 } }""", UiLanguage.Automatic)]
    [InlineData("not json at all", UiLanguage.Automatic)]
    public void The_language_is_read_from_the_settings_file_before_the_app_starts(string json, UiLanguage expected)
    {
        var file = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N") + ".json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, json);
        try
        {
            AppLanguage.ReadSetting(file).Should().Be(expected);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void A_missing_settings_file_means_automatic()
    {
        AppLanguage.ReadSetting(Path.Combine(Path.GetTempPath(), "cashgear-tests", "missing", "settings.json")).Should().Be(UiLanguage.Automatic);
    }

    [Fact]
    public void Arabic_text_comes_from_the_satellite_assembly_with_its_plural_forms()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = Arabic;
            CommonText.Ok.Should().Be("موافق");
            TrayText.CaptureRegion.Should().Be("التقاط منطقة (_R)");
            TrayText.DelaySeconds(2).Should().Be("ثانيتان (_2)");
            TrayText.DelaySeconds(3).Should().Be("3 ثوانٍ (_3)");
            TrayText.DelaySeconds(11).Should().Be("11 ثانية (_11)");
            TrayText.RestartToUpdate("1.1.0").Should().Be("إعادة التشغيل للتحديث (1.1.0) (_U)");

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            CommonText.Ok.Should().Be("OK");
            TrayText.DelaySeconds(1).Should().Be("_1 second");
            TrayText.DelaySeconds(10).Should().Be("_10 seconds");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public void Text_in_an_unknown_language_falls_back_to_English()
    {
        var previous = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
            CommonText.Cancel.Should().Be("Cancel");
        }
        finally
        {
            CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    [Trait("Category", "Interactive")]
    public async Task A_dialog_in_Arabic_is_right_to_left_with_Arabic_buttons_and_Esc_answers_no()
    {
        InteractiveDesktop.Require();
        await StaDispatcher.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = Arabic;
            var window = AppDialog.Create(null, "رسالة", null, DialogButtons.YesNo, DialogKind.Question, AppTheme.Light, out var answer);
            window.Left = -10000;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Show();
            await Task.Delay(100, TestContext.Current.CancellationToken);

            window.FlowDirection.Should().Be(FlowDirection.RightToLeft);
            window.Title.Should().Be("CashGear Capture");
            var buttons = FindAll<Button>(window).ToList();
            buttons.Select(b => b.Content).Should().Equal("نعم", "لا");
            buttons[0].IsDefault.Should().BeTrue();
            buttons[1].IsCancel.Should().BeTrue();

            window.Close();
            answer().Should().Be(DialogAnswer.No, "closing without a choice is the Esc answer");
        });
    }

    [Fact]
    [Trait("Category", "Interactive")]
    public async Task A_dialog_in_English_is_left_to_right_and_its_buttons_answer()
    {
        InteractiveDesktop.Require();
        await StaDispatcher.RunAsync(async () =>
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            var window = AppDialog.Create(null, "Save the changes?", "Editor", DialogButtons.YesNoCancel, DialogKind.Question, AppTheme.Dark, out var answer);
            window.Left = -10000;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Show();
            await Task.Delay(100, TestContext.Current.CancellationToken);

            window.FlowDirection.Should().Be(FlowDirection.LeftToRight);
            var buttons = FindAll<Button>(window).ToList();
            buttons.Select(b => b.Content).Should().Equal("Yes", "No", "Cancel");
            buttons[1].RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            answer().Should().Be(DialogAnswer.No);
            window.IsVisible.Should().BeFalse();
        });
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var nested in FindAll<T>(child))
            {
                yield return nested;
            }
        }
    }
}
