using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Tests.Overlays;

public sealed class KeystrokeFormatterTests
{
    private const uint Shift = 0xA0;
    private const uint Ctrl = 0xA2;
    private const uint RightAlt = 0xA5;
    private const uint Win = 0x5B;
    private static readonly TimeSpan Hold = TimeSpan.FromSeconds(1.5);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    /// <summary>A US-like layout where AltGr+Q types '@' (as on a German keyboard) and OEM_2 is '/' or '?'.</summary>
    private static string? Layout(uint key, bool shift, bool altGr) => key switch
    {
        0x51 when altGr => "@",
        _ when altGr => null,
        >= 0x41 and <= 0x5A => shift ? ((char)key).ToString() : ((char)(key + 32)).ToString(),
        >= 0x30 and <= 0x39 => ((char)key).ToString(),
        0x20 => " ",
        0xBF => shift ? "?" : "/",
        0x0D => "\r",
        0x08 => "\b",
        0x09 => "\t",
        _ => null,
    };

    private static List<KeyCaption> Press(KeystrokeFormatter formatter, double at, params uint[] keys)
    {
        var captions = new List<KeyCaption>();
        foreach (var key in keys)
        {
            if (formatter.Key(S(at), key, down: true) is { } caption)
            {
                captions.Add(caption);
            }
        }

        foreach (var key in keys.Reverse())
        {
            formatter.Key(S(at), key, down: false).Should().BeNull("releasing a key never changes a caption");
        }

        return captions;
    }

    [Fact]
    public void A_shortcut_reads_with_its_modifiers_in_order()
    {
        var formatter = new KeystrokeFormatter(KeystrokeMode.Shortcuts, Hold, Layout);

        Press(formatter, 1, Ctrl, Shift, 0x53).Should().ContainSingle().Which.Text.Should().Be("Ctrl + Shift + S");
        Press(formatter, 5, Win, Shift, 0x53).Single().Text.Should().Be("Win + Shift + S");
        Press(formatter, 9, Ctrl, 0xBF).Single().Text.Should().Be("Ctrl + /", "a punctuation key is named by what it types");
        Press(formatter, 12, Ctrl, 0x25).Single().Text.Should().Be("Ctrl + ←");
    }

    [Fact]
    public void The_same_shortcut_again_soon_counts_and_later_starts_a_new_caption()
    {
        var formatter = new KeystrokeFormatter(KeystrokeMode.Shortcuts, Hold, Layout);

        var first = Press(formatter, 1, Ctrl, 0x5A).Single();
        var second = Press(formatter, 1.5, Ctrl, 0x5A).Single();
        var third = Press(formatter, 2, Ctrl, 0x5A).Single();
        var later = Press(formatter, 6, Ctrl, 0x5A).Single();

        (second.Id, second.Text).Should().Be((first.Id, "Ctrl + Z ×2"));
        third.Text.Should().Be("Ctrl + Z ×3");
        later.Id.Should().NotBe(first.Id);
        later.Text.Should().Be("Ctrl + Z");
    }

    [Fact]
    public void Shortcuts_mode_shows_no_typing_but_does_show_function_keys_and_escape()
    {
        var formatter = new KeystrokeFormatter(KeystrokeMode.Shortcuts, Hold, Layout);

        Press(formatter, 1, 0x48, 0x49).Should().BeEmpty("typing isn't a shortcut");
        Press(formatter, 2, Shift, 0x48).Should().BeEmpty("nor is Shift with a letter");
        Press(formatter, 3, 0x0D).Should().BeEmpty();
        Press(formatter, 4, Ctrl).Should().BeEmpty("a modifier alone shows nothing");
        Press(formatter, 5, 0x74).Single().Text.Should().Be("F5");
        Press(formatter, 7, Shift, 0x74).Single().Text.Should().Be("Shift + F5");
        Press(formatter, 9, 0x1B).Single().Text.Should().Be("Esc");
    }

    [Fact]
    public void All_keys_gathers_typing_into_one_caption_and_names_other_keys()
    {
        var formatter = new KeystrokeFormatter(KeystrokeMode.AllKeys, Hold, Layout);

        var h = Press(formatter, 1, Shift, 0x48).Single();
        var i = Press(formatter, 1.1, 0x49).Single();
        var space = Press(formatter, 1.2, 0x20).Single();
        var slash = Press(formatter, 1.3, 0xBF).Single();
        var enter = Press(formatter, 1.4, 0x0D).Single();
        var again = Press(formatter, 1.5, 0x41).Single();
        var backspace = Press(formatter, 1.6, 0x08).Single();
        var twice = Press(formatter, 1.7, 0x08).Single();

        h.Text.Should().Be("H");
        (i.Id, i.Text).Should().Be((h.Id, "Hi"));
        slash.Text.Should().Be("Hi /");
        space.Id.Should().Be(h.Id);
        (enter.Text, enter.Id == h.Id).Should().Be(("Enter", false));
        (again.Text, again.Id == h.Id).Should().Be(("a", false), "typing after another key starts a new caption");
        backspace.Text.Should().Be("Backspace");
        (twice.Id, twice.Text).Should().Be((backspace.Id, "Backspace ×2"));
    }

    [Fact]
    public void Long_typing_shows_only_its_end_and_a_pause_starts_a_new_caption()
    {
        var formatter = new KeystrokeFormatter(KeystrokeMode.AllKeys, Hold, Layout);
        KeyCaption last = default;
        for (var n = 0; n < 60; n++)
        {
            last = Press(formatter, 1 + (n * 0.05), (uint)(0x41 + (n % 26))).Single();
        }

        last.Text.Should().HaveLength(KeystrokeFormatter.MaxTypedLength).And.StartWith("…");
        Press(formatter, 10, 0x41).Single().Text.Should().Be("a");
    }

    [Fact]
    public void Alt_gr_typing_a_character_is_typing_not_a_shortcut()
    {
        var allKeys = new KeystrokeFormatter(KeystrokeMode.AllKeys, Hold, Layout);
        var shortcuts = new KeystrokeFormatter(KeystrokeMode.Shortcuts, Hold, Layout);

        // Windows sends AltGr as left Ctrl plus right Alt.
        Press(allKeys, 1, Ctrl, RightAlt, 0x51).Single().Text.Should().Be("@");
        Press(shortcuts, 1, Ctrl, RightAlt, 0x51).Should().BeEmpty();
        Press(shortcuts, 3, Ctrl, RightAlt, 0x58).Single().Text.Should().Be("Ctrl + Alt + X", "AltGr+X types nothing here");
    }

    [Fact]
    public void Off_shows_nothing_and_a_reset_forgets_held_modifiers()
    {
        var off = new KeystrokeFormatter(KeystrokeMode.Off, Hold, Layout);
        Press(off, 1, Ctrl, 0x53).Should().BeEmpty();

        var formatter = new KeystrokeFormatter(KeystrokeMode.Shortcuts, Hold, Layout);
        formatter.Key(S(1), Ctrl, down: true);
        formatter.Reset();
        formatter.Key(S(2), 0x53, down: true).Should().BeNull("Ctrl is no longer held");
    }
}
