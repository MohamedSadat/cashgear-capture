using System.ComponentModel;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.App.Platform;

/// <summary>The buttons of an <see cref="AppDialog"/>.</summary>
internal enum DialogButtons
{
    Ok,
    OkCancel,
    YesNo,
    YesNoCancel,
}

/// <summary>What the user answered; Cancel also when the dialog was closed with Esc or its close box.</summary>
internal enum DialogAnswer
{
    Ok,
    Cancel,
    Yes,
    No,
}

internal enum DialogKind
{
    Information,
    Question,
    Warning,
    Error,
}

/// <summary>
/// The app's message box, in place of <see cref="MessageBox"/>: its buttons are in the app's language (Windows' box
/// uses Windows' language), it is laid out right to left in Arabic and follows the app's light or dark theme. Enter
/// picks the first button; Esc answers Cancel, or No when there is no Cancel.
/// </summary>
internal static class AppDialog
{
    /// <summary>The icons' font.</summary>
    [Localizable(false)] // a font's name
    private static readonly FontFamily SymbolFont = new("Segoe MDL2 Assets");

    /// <summary>Shows the dialog and waits for an answer. UI (STA) thread.</summary>
    public static DialogAnswer Show(
        Window? owner,
        string message,
        string? title = null,
        DialogButtons buttons = DialogButtons.Ok,
        DialogKind kind = DialogKind.Information,
        AppTheme theme = AppTheme.System)
    {
        var window = Create(owner, message, title, buttons, kind, theme, out var answer);
        window.ShowDialog();
        return answer();
    }

    /// <summary>
    /// Shows the dialog without waiting (a question at start-up must not hold up an exit or a restart);
    /// <paramref name="answered"/> gets the answer when it is closed. <paramref name="yes"/> and <paramref name="no"/>
    /// label Yes and No.
    /// </summary>
    public static Window ShowModeless(
        string message, string title, DialogKind kind, AppTheme theme, string yes, string no, Action<DialogAnswer> answered)
    {
        var window = Create(null, message, title, DialogButtons.YesNo, kind, theme, out var answer, yes, no);
        window.Closed += (_, _) => answered(answer());
        window.Show();
        window.Activate();
        return window;
    }

    /// <summary>The dialog's window, not shown yet (tests); <paramref name="answer"/> reads the answer once it is closed.</summary>
    internal static Window Create(
        Window? owner,
        string message,
        string? title,
        DialogButtons buttons,
        DialogKind kind,
        AppTheme theme,
        out Func<DialogAnswer> answer,
        string? yes = null,
        string? no = null)
    {
        var choices = buttons switch
        {
            DialogButtons.OkCancel => new[] { (CommonText.Ok, DialogAnswer.Ok), (CommonText.Cancel, DialogAnswer.Cancel) },
            DialogButtons.YesNo => [(yes ?? CommonText.Yes, DialogAnswer.Yes), (no ?? CommonText.No, DialogAnswer.No)],
            DialogButtons.YesNoCancel => [(yes ?? CommonText.Yes, DialogAnswer.Yes), (no ?? CommonText.No, DialogAnswer.No), (CommonText.Cancel, DialogAnswer.Cancel)],
            _ => [(CommonText.Ok, DialogAnswer.Ok)],
        };
        var escape = buttons switch
        {
            DialogButtons.YesNo => DialogAnswer.No,
            DialogButtons.Ok => DialogAnswer.Ok,
            _ => DialogAnswer.Cancel,
        };

        var result = escape;
        var window = new Window
        {
            Title = title ?? CommonText.ProductName,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = owner is null,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = owner is { IsLoaded: true } ? owner : null,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        foreach (var (label, value) in choices)
        {
            var button = new Button
            {
                Content = label,
                MinWidth = 90,
                Margin = new Thickness(8, 0, 0, 0),
                IsDefault = row.Children.Count == 0,
                IsCancel = value == escape,
            };
            button.Click += (_, _) =>
            {
                result = value;
                window.Close();
            };
            row.Children.Add(button);
        }

        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 440, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Assertive);
        var glyph = new TextBlock
        {
            Text = kind switch
            {
                DialogKind.Warning => "",
                DialogKind.Error => "",
                DialogKind.Question => "",
                _ => "",
            },
            FontFamily = SymbolFont,
            FontSize = 28,
            Margin = new Thickness(0, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = kind switch
            {
                DialogKind.Warning => new SolidColorBrush(Color.FromRgb(0xC1, 0x9C, 0x00)),
                DialogKind.Error => new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C)),
                _ => SystemColors.HighlightBrush,
            },
        };
        AutomationProperties.SetName(glyph, kind switch
        {
            DialogKind.Warning => CommonText.Warning,
            DialogKind.Error => CommonText.Error,
            DialogKind.Question => CommonText.Question,
            _ => CommonText.Information,
        });

        var body = new DockPanel { Margin = new Thickness(20, 18, 20, 16) };
        DockPanel.SetDock(row, Dock.Bottom);
        body.Children.Add(row);
        DockPanel.SetDock(glyph, Dock.Left);
        body.Children.Add(glyph);
        body.Children.Add(text);
        window.Content = body;
        window.Loaded += (_, _) => (row.Children[0] as Button)?.Focus();
        Theming.Apply(window, theme);
        if (SystemParameters.HighContrast)
        {
            glyph.Foreground = SystemColors.WindowTextBrush;
        }

        answer = () => result;
        return window;
    }
}
