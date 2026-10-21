using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.App.Platform;

/// <summary>
/// Settings › What's new, and the notification after an update: a version's release notes in a window. The notes are
/// Markdown in English, optionally followed by a line <c>&lt;!-- lang:ar --&gt;</c> and the same in Arabic; the
/// window shows the part in the app's language.
/// </summary>
public sealed partial class ReleaseNotes(UpdateService updates, ISettingsService settings)
{
    private Window? _open;

    /// <summary>Shows the notes of <paramref name="version"/> (the downloaded update's, or this version's). UI thread.</summary>
    public void Show(string version)
    {
        _open?.Close();
        var notes = updates.NotesFor(version);
        var document = notes is null
            ? Markdown.Render(UpdatesText.NoReleaseNotes(version))
            : Markdown.Render(ForLanguage(notes, CultureInfo.CurrentUICulture));

        var close = new Button { Content = CommonText.Close, MinWidth = 90, IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var viewer = new FlowDocumentScrollViewer { Document = document, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, IsToolBarVisible = false };
        var panel = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(close);
        panel.Children.Add(viewer);

        var window = new Window
        {
            Title = UpdatesText.ReleaseNotesTitle(version),
            Content = panel,
            Width = 640,
            Height = 560,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };
        close.Click += (_, _) => window.Close();
        window.Closed += (_, _) => _open = null;
        Theming.Apply(window, settings.Current.Appearance.Theme);
        _open = window;
        window.Show();
        window.Activate();
        viewer.Focus();
    }

    /// <summary>The part of <paramref name="markdown"/> in <paramref name="culture"/>'s language, else the first (English) part.</summary>
    public static string ForLanguage(string markdown, CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var parts = LanguageMarker().Split(markdown);
        // parts: [english, lang1, text1, lang2, text2, ...]
        for (var i = 1; i + 1 < parts.Length; i += 2)
        {
            if (string.Equals(parts[i], culture.TwoLetterISOLanguageName, StringComparison.OrdinalIgnoreCase))
            {
                return parts[i + 1].Trim();
            }
        }

        return parts[0].Trim();
    }

    [GeneratedRegex(@"^\s*<!--\s*lang:([A-Za-z-]+)\s*-->\s*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex LanguageMarker();
}

/// <summary>
/// The Markdown release notes use: <c>#</c> headings, <c>-</c>/<c>*</c> and <c>1.</c> lists (nested by indenting),
/// paragraphs, <c>**bold**</c>, <c>*italic*</c>, <c>`code`</c> and <c>[links](https://…)</c>.
/// </summary>
public static partial class Markdown
{
    public static FlowDocument Render(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        var document = new FlowDocument { PagePadding = new Thickness(4), FontFamily = SystemFonts.MessageFontFamily, FontSize = 14 };
        var paragraph = new List<string>();
        var lists = new Stack<(List List, int Indent)>();

        void FlushParagraph()
        {
            if (paragraph.Count > 0)
            {
                document.Blocks.Add(Inline(new Paragraph { Margin = new Thickness(0, 0, 0, 8) }, string.Join(" ", paragraph)));
                paragraph.Clear();
            }
        }

        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            var heading = Heading().Match(line);
            if (heading.Success)
            {
                FlushParagraph();
                lists.Clear();
                var level = heading.Groups[1].Length;
                document.Blocks.Add(Inline(
                    new Paragraph { FontSize = level == 1 ? 22 : level == 2 ? 18 : 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, level == 1 ? 0 : 10, 0, 6) },
                    heading.Groups[2].Value));
                continue;
            }

            var item = ListItem().Match(line);
            if (item.Success)
            {
                FlushParagraph();
                var indent = item.Groups[1].Length;
                var numbered = char.IsDigit(item.Groups[2].Value[0]);
                while (lists.Count > 0 && lists.Peek().Indent > indent)
                {
                    lists.Pop();
                }

                if (lists.Count == 0 || lists.Peek().Indent < indent)
                {
                    var list = new List { MarkerStyle = numbered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 6), Padding = new Thickness(20, 0, 0, 0) };
                    if (lists.Count == 0)
                    {
                        document.Blocks.Add(list);
                    }
                    else
                    {
                        lists.Peek().List.ListItems.LastListItem?.Blocks.Add(list);
                    }

                    lists.Push((list, indent));
                }

                lists.Peek().List.ListItems.Add(new ListItem(Inline(new Paragraph { Margin = new Thickness(0, 0, 0, 2) }, item.Groups[3].Value)));
                continue;
            }

            if (lists.Count > 0 && raw.StartsWith(' ') && lists.Peek().List.ListItems.LastListItem?.Blocks.LastBlock is Paragraph continued)
            {
                AddInlines(continued, " " + line.Trim());
                continue;
            }

            lists.Clear();
            paragraph.Add(line.Trim());
        }

        FlushParagraph();
        return document;
    }

    private static Paragraph Inline(Paragraph paragraph, string text)
    {
        AddInlines(paragraph, text);
        return paragraph;
    }

    private static void AddInlines(Paragraph paragraph, string text)
    {
        var at = 0;
        foreach (Match match in Span().Matches(text))
        {
            if (match.Index > at)
            {
                paragraph.Inlines.Add(new Run(text[at..match.Index]));
            }

            if (match.Groups["bold"].Success)
            {
                paragraph.Inlines.Add(new Bold(new Run(match.Groups["bold"].Value)));
            }
            else if (match.Groups["italic"].Success)
            {
                paragraph.Inlines.Add(new Italic(new Run(match.Groups["italic"].Value)));
            }
            else if (match.Groups["code"].Success)
            {
                paragraph.Inlines.Add(new Run(match.Groups["code"].Value) { FontFamily = new FontFamily("Consolas") });
            }
            else if (Uri.TryCreate(match.Groups["url"].Value, UriKind.Absolute, out var url) && url.Scheme is "https" or "http")
            {
                var link = new Hyperlink(new Run(match.Groups["link"].Value)) { NavigateUri = url };
                link.RequestNavigate += (_, e) => Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
                paragraph.Inlines.Add(link);
            }
            else
            {
                paragraph.Inlines.Add(new Run(match.Groups["link"].Value));
            }

            at = match.Index + match.Length;
        }

        if (at < text.Length)
        {
            paragraph.Inlines.Add(new Run(text[at..]));
        }
    }

    [GeneratedRegex(@"^(#{1,6})\s+(.*)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)([-*+]|\d+[.)])\s+(.*)$")]
    private static partial Regex ListItem();

    [GeneratedRegex(@"\*\*(?<bold>.+?)\*\*|(?<![\w*])\*(?<italic>[^*\s][^*]*?)\*(?!\w)|`(?<code>[^`]+)`|\[(?<link>[^\]]+)\]\((?<url>[^)\s]+)\)")]
    private static partial Regex Span();
}
