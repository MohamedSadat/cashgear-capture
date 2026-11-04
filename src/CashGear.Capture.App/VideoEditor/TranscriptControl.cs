using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.VideoEditor.Editing;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>
/// The transcript's words, drawn by this control, in lines that wrap and in paragraphs, only those in view, so a long
/// transcript stays quick and the word being said can change every moment. A paragraph runs in its own language's
/// direction, whatever the window's. A click selects a word and goes there in the video; Shift selects as far as the
/// word, as Shift with the arrows does. The word being said is marked, cut words are struck through, corrected ones have
/// a dotted line under them. Colours come from the system under high contrast. Screen readers see paragraphs of words
/// (<see cref="TranscriptAutomationPeer"/>).
/// </summary>
internal sealed class TranscriptControl : FrameworkElement
{
    /// <summary>The space between words, in device-independent pixels.</summary>
    private const double WordGap = 5;

    private const double Inset = 10;

    private const double ParagraphGap = 12;

    private const double TimeHeight = 16;

    private const double WheelStep = 48;

    private static readonly Brush SelectionText = Frozen(Colors.White);
    private static readonly Brush SelectionBack = Frozen(Color.FromRgb(0x00, 0x78, 0xD4));
    private static readonly Brush CurrentBack = Frozen(Color.FromArgb(0x70, 0xFF, 0xB0, 0x20));

    private readonly List<Placed> _placed = [];
    private readonly Dictionary<string, double> _widths = [];
    private readonly List<(double Y, double Height, (string Text, bool RightToLeft) Label)> _times = [];
    private TranscriptPanelViewModel? _panel;
    private ScrollBar? _scrollBar;
    private double _offset;
    private double _contentHeight;
    private double _laidOutWidth = -1;
    private int _dragAnchor = -1;

    public TranscriptControl()
    {
        Focusable = true;
        FocusVisualStyle = null; // the frame is drawn with the words
        ClipToBounds = true;
        KeyboardNavigation.SetIsTabStop(this, true);
    }

    /// <summary>The user wants to correct a word: its place, with the text to start from.</summary>
    public event EventHandler<(int Position, string Text)>? EditRequested;

    internal TranscriptPanelViewModel? Panel => _panel;

    private double FontSizeOfWords => 14;

    private double LineHeight => FontSizeOfWords * 1.6;

    private Typeface Face => new(TextElement.GetFontFamily(this), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private Brush TextBrush => TextElement.GetForeground(this);

    public void Attach(TranscriptPanelViewModel panel, ScrollBar scrollBar)
    {
        _panel = panel;
        _scrollBar = scrollBar;
        scrollBar.ValueChanged += (_, e) =>
        {
            if (Math.Abs(e.NewValue - _offset) > 0.5)
            {
                _offset = e.NewValue;
                InvalidateVisual();
            }
        };
        panel.WordsChanged += (_, _) =>
        {
            _laidOutWidth = -1;
            LayOut();
            InvalidateVisual();
            (UIElementAutomationPeer.FromElement(this) as TranscriptAutomationPeer)?.WordsChanged();
        };
        panel.MarksChanged += (_, _) =>
        {
            InvalidateVisual();
            if (panel.Current >= 0 && IsFollowing)
            {
                Reveal(panel.Current);
            }
        };
        panel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TranscriptPanelViewModel.CaretText))
            {
                (UIElementAutomationPeer.FromElement(this) as TranscriptAutomationPeer)?.CaretMoved();
            }
        };
        SizeChanged += (_, _) =>
        {
            if (Math.Abs(ActualWidth - _laidOutWidth) > 0.5)
            {
                LayOut();
            }

            UpdateScrollBar();
            InvalidateVisual();
        };
        IsKeyboardFocusedChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>The panel follows the word being said while nothing else is going on.</summary>
    internal bool IsFollowing { get; set; } = true;

    /// <summary>Where a word is in the control, or empty when it isn't laid out.</summary>
    public Rect BoxOf(int position)
    {
        var index = _placed.FindIndex(p => p.Position == position);
        if (index < 0)
        {
            return Rect.Empty;
        }

        var box = _placed[index].Box;
        box.Y -= _offset;
        return box;
    }

    /// <summary>Scrolls so the word is in view.</summary>
    public void Reveal(int position)
    {
        var box = BoxOf(position);
        if (box.IsEmpty)
        {
            return;
        }

        if (box.Top < 0)
        {
            ScrollTo(_offset + box.Top - LineHeight);
        }
        else if (box.Bottom > ActualHeight)
        {
            ScrollTo(_offset + box.Bottom - ActualHeight + LineHeight);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TranscriptAutomationPeer(this);

    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // the whole control takes the mouse
        if (_panel is null)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var culture = CultureInfo.CurrentUICulture;
        var text = TextBrush;
        var dimmed = HighContrast.IsOn ? SystemColors.GrayTextBrush : Dimmed(text);
        foreach (var (y, height, label) in _times)
        {
            if (y - _offset + height >= 0 && y - _offset <= ActualHeight)
            {
                var time = new FormattedText(label.Text, culture, FlowDirection.LeftToRight, Face, 11, dimmed, dpi);
                drawing.DrawText(time, new Point(label.RightToLeft ? ActualWidth - Inset - time.Width : Inset, y - _offset));
            }
        }

        var chosen = _panel.Selected.ToHashSet();
        var first = FirstInView();
        for (var i = first; i < _placed.Count; i++)
        {
            var word = _placed[i];
            if (word.Box.Top - _offset > ActualHeight)
            {
                break;
            }

            var box = new Rect(word.Box.X, word.Box.Y - _offset, word.Box.Width, word.Box.Height);
            var shown = _panel.View.Words[word.Position];
            var isChosen = chosen.Contains(word.Position);
            if (isChosen)
            {
                drawing.DrawRectangle(HighContrast.IsOn ? SystemColors.HighlightBrush : SelectionBack, null, box);
            }
            else if (word.Position == _panel.Current)
            {
                if (HighContrast.IsOn)
                {
                    drawing.DrawRectangle(null, new Pen(SystemColors.HighlightBrush, 2), box);
                }
                else
                {
                    drawing.DrawRectangle(CurrentBack, null, box);
                }
            }

            var foreground = isChosen ? (HighContrast.IsOn ? SystemColors.HighlightTextBrush : SelectionText) : shown.IsCut ? dimmed : text;
            var formatted = new FormattedText(shown.Word.Text, culture, word.RightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, Face, FontSizeOfWords, foreground, dpi);
            if (shown.IsCut)
            {
                formatted.SetTextDecorations(TextDecorations.Strikethrough);
            }

            // Right to left, the text's origin is its right edge.
            drawing.DrawText(formatted, new Point(word.RightToLeft ? box.Right - 2 : box.X + 2, box.Y + ((box.Height - formatted.Height) / 2)));
            if (shown.Word.Heard is not null)
            {
                var dotted = new Pen(foreground, 1) { DashStyle = DashStyles.Dot };
                drawing.DrawLine(dotted, new Point(box.X + 1, box.Bottom - 1), new Point(box.Right - 1, box.Bottom - 1));
            }

            if (word.Position == _panel.Caret && IsKeyboardFocused)
            {
                drawing.DrawRectangle(null, new Pen(HighContrast.IsOn ? SystemColors.HotTrackBrush : text, 1), Rect.Inflate(box, 1, 1));
            }
        }

        if (IsKeyboardFocused)
        {
            drawing.DrawRectangle(null, new Pen(HighContrast.IsOn ? SystemColors.HighlightBrush : SelectionBack, 2), new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)));
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollTo(_offset - (Math.Sign(e.Delta) * WheelStep));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        if (_panel is null || HitTest(e.GetPosition(this)) is not { } position)
        {
            _panel?.ClearSelection();
            return;
        }

        if (e.ClickCount == 2)
        {
            EditRequested?.Invoke(this, (position, _panel.View.Words[position].Word.Text));
            e.Handled = true;
            return;
        }

        var extend = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        _panel.Select(position, extend);
        _dragAnchor = _panel.Anchor;
        if (!extend)
        {
            _panel.SeekTo(position);
        }

        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panel is not null && IsMouseCaptured && _dragAnchor >= 0 && HitTest(e.GetPosition(this), nearest: true) is { } position && position != _panel.Caret)
        {
            _panel.Select(position, extend: true);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragAnchor = -1;
        ReleaseMouseCapture();
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (_panel is null || e.Text.Length == 0 || char.IsControl(e.Text[0]) || e.Text == " " || _panel.Caret < 0 || _panel.View.Words[_panel.Caret].IsCut)
        {
            return;
        }

        // Typing corrects: the word's text starts again from what was typed.
        EditRequested?.Invoke(this, (_panel.Caret, e.Text));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_panel is null)
        {
            return;
        }

        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Left:
            case Key.Right:
                {
                    // Left and right as they look: in a line running right to left, Left is the next word.
                    var rightToLeft = _panel.Caret >= 0 && PlacedOf(_panel.Caret) is { RightToLeft: true };
                    var forward = (e.Key == Key.Right) != rightToLeft;
                    MoveTo(_panel.Step(forward ? 1 : -1), shift);
                    e.Handled = true;
                    break;
                }

            case Key.Up:
            case Key.Down:
                MoveTo(LineStep(e.Key == Key.Up ? -1 : 1), shift);
                e.Handled = true;
                break;
            case Key.Home:
                MoveTo(control ? _placed.FirstOrDefault()?.Position ?? -1 : LineEnd(first: true), shift);
                e.Handled = true;
                break;
            case Key.End:
                MoveTo(control ? _placed.LastOrDefault()?.Position ?? -1 : LineEnd(first: false), shift);
                e.Handled = true;
                break;
            case Key.PageUp:
            case Key.PageDown:
                MoveTo(LineStep((e.Key == Key.PageUp ? -1 : 1) * Math.Max(1, (int)(ActualHeight / LineHeight) - 1)), shift);
                e.Handled = true;
                break;
            case Key.A when control:
                _panel.SelectAll();
                e.Handled = true;
                break;
            case Key.R when control:
                _panel.RestoreSelected();
                e.Handled = true;
                break;
            case Key.Delete:
            case Key.Back:
                _panel.CutSelected();
                if (_panel.Caret >= 0)
                {
                    Reveal(_panel.Caret);
                }

                e.Handled = true;
                break;
            case Key.Enter when _panel.Caret >= 0:
                _panel.SeekTo(_panel.Caret);
                e.Handled = true;
                break;
            case Key.F2 when _panel.Caret >= 0 && !_panel.View.Words[_panel.Caret].IsCut:
                EditRequested?.Invoke(this, (_panel.Caret, _panel.View.Words[_panel.Caret].Word.Text));
                e.Handled = true;
                break;
            case Key.Escape when _panel.Caret >= 0:
                _panel.ClearSelection();
                e.Handled = true;
                break;
        }
    }

    /// <summary>The word under a point, or with <paramref name="nearest"/> the nearest on that line.</summary>
    internal int? HitTest(Point point, bool nearest = false)
    {
        var y = point.Y + _offset;
        Placed? best = null;
        var distance = double.MaxValue;
        foreach (var word in _placed)
        {
            if (word.Box.Bottom < y - (nearest ? LineHeight : 0) || word.Box.Top > y)
            {
                continue;
            }

            if (word.Box.Contains(new Point(point.X, y)))
            {
                return word.Position;
            }

            if (nearest)
            {
                var along = point.X < word.Box.Left ? word.Box.Left - point.X : point.X > word.Box.Right ? point.X - word.Box.Right : 0;
                if (along < distance)
                {
                    distance = along;
                    best = word;
                }
            }
        }

        return best?.Position;
    }

    private void MoveTo(int position, bool extend)
    {
        if (_panel is null || position < 0)
        {
            return;
        }

        _panel.Select(position, extend);
        Reveal(position);
    }

    private Placed? PlacedOf(int position)
    {
        var index = _placed.FindIndex(p => p.Position == position);
        return index < 0 ? null : _placed[index];
    }

    /// <summary>The word <paramref name="lines"/> lines up or down from the caret, nearest it across the line.</summary>
    private int LineStep(int lines)
    {
        if (_panel is null || _placed.Count == 0)
        {
            return -1;
        }

        if (PlacedOf(_panel.Caret) is not { } caret)
        {
            return _placed[0].Position;
        }

        var tops = _placed.Select(p => p.Box.Top).Distinct().Order().ToList();
        var at = tops.BinarySearch(caret.Box.Top);
        var target = tops[Math.Clamp(at + lines, 0, tops.Count - 1)];
        var middle = (caret.Box.Left + caret.Box.Right) / 2;
        return _placed.Where(p => p.Box.Top == target).MinBy(p => Math.Abs(((p.Box.Left + p.Box.Right) / 2) - middle))!.Position;
    }

    /// <summary>The first or last word of the caret's line, in reading order.</summary>
    private int LineEnd(bool first)
    {
        if (_panel is null || PlacedOf(_panel.Caret) is not { } caret)
        {
            return -1;
        }

        var line = _placed.Where(p => p.Box.Top == caret.Box.Top).ToList();
        return first ? line[0].Position : line[^1].Position;
    }

    private int FirstInView()
    {
        var low = 0;
        var high = _placed.Count;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (_placed[middle].Box.Bottom < _offset)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>Every word's place, by its paragraph and its width.</summary>
    private void LayOut()
    {
        _placed.Clear();
        _times.Clear();
        _laidOutWidth = ActualWidth;
        if (_panel is null || ActualWidth <= 0)
        {
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var culture = CultureInfo.CurrentUICulture;
        var usable = Math.Max(60, ActualWidth - (2 * Inset));
        var y = Inset;
        var words = _panel.View.Words;
        var paragraph = -1;
        var rightToLeft = false;
        double x = 0;
        var started = false;
        for (var position = 0; position < words.Count; position++)
        {
            if (!_panel.Shows(position))
            {
                continue;
            }

            var word = words[position];
            if (word.Paragraph != paragraph)
            {
                if (started)
                {
                    y += LineHeight + ParagraphGap;
                }

                paragraph = word.Paragraph;
                rightToLeft = _panel.IsRightToLeft(word.AssetId);
                _times.Add((y, TimeHeight, (_panel.TimeOf(position), rightToLeft)));
                y += TimeHeight;
                x = 0;
                started = true;
            }

            var width = WidthOf(word.Word.Text, rightToLeft, culture, dpi) + 4;
            if (x > 0 && x + width > usable)
            {
                y += LineHeight;
                x = 0;
            }

            var left = rightToLeft ? ActualWidth - Inset - x - width : Inset + x;
            _placed.Add(new Placed(position, new Rect(left, y, Math.Min(width, usable), LineHeight - 2), rightToLeft));
            x += width + WordGap;
        }

        _contentHeight = started ? y + LineHeight + Inset : 0;
        UpdateScrollBar();
    }

    private double WidthOf(string text, bool rightToLeft, CultureInfo culture, double dpi)
    {
        if (!_widths.TryGetValue(text, out var width))
        {
            width = new FormattedText(text, culture, rightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight, Face, FontSizeOfWords, Brushes.Black, dpi).WidthIncludingTrailingWhitespace;
            _widths[text] = width;
        }

        return width;
    }

    private void UpdateScrollBar()
    {
        if (_scrollBar is null)
        {
            return;
        }

        var range = Math.Max(0, _contentHeight - ActualHeight);
        _scrollBar.Maximum = range;
        _scrollBar.ViewportSize = ActualHeight;
        _scrollBar.LargeChange = Math.Max(LineHeight, ActualHeight - LineHeight);
        _scrollBar.SmallChange = LineHeight;
        _scrollBar.Visibility = range > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_offset > range)
        {
            ScrollTo(range);
        }
    }

    private void ScrollTo(double offset)
    {
        var range = Math.Max(0, _contentHeight - ActualHeight);
        _offset = Math.Clamp(offset, 0, range);
        if (_scrollBar is not null)
        {
            _scrollBar.Value = _offset;
        }

        InvalidateVisual();
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>The text's own colour, fainter.</summary>
    private static Brush Dimmed(Brush text)
    {
        var color = text is SolidColorBrush solid ? solid.Color : Colors.Gray;
        var brush = new SolidColorBrush(Color.FromArgb(0x90, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }

    private sealed record Placed(int Position, Rect Box, bool RightToLeft);
}

/// <summary>
/// The transcript for screen readers: a list of paragraphs, each named by its time and its text, each holding its words,
/// named with their time. A word can be selected and invoked (the video goes there), and the word the keys move to takes the
/// screen reader's focus.
/// </summary>
internal sealed class TranscriptAutomationPeer(TranscriptControl owner) : FrameworkElementAutomationPeer(owner)
{
    private readonly Dictionary<int, TranscriptWordPeer> _words = [];
    private readonly Dictionary<int, TranscriptParagraphPeer> _paragraphs = [];

    internal TranscriptControl Words => (TranscriptControl)Owner;

    internal TranscriptWordPeer WordPeer(int position)
    {
        if (!_words.TryGetValue(position, out var peer))
        {
            peer = new TranscriptWordPeer(this, position);
            _words[position] = peer;
        }

        return peer;
    }

    internal void WordsChanged()
    {
        _words.Clear();
        _paragraphs.Clear();
        InvalidatePeer();
    }

    internal void CaretMoved()
    {
        if (Words.Panel is { Caret: >= 0 } panel && Words.IsKeyboardFocused && ListenerExists(AutomationEvents.AutomationFocusChanged))
        {
            var paragraph = ParagraphPeer(panel.View.Words[panel.Caret].Paragraph);
            _ = paragraph.GetChildren();
            WordPeer(panel.Caret).RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged);
        }
    }

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

    protected override string GetClassNameCore() => nameof(TranscriptControl);

    protected override string GetNameCore() => VideoEditorText.TranscriptWordsName;

    protected override string GetHelpTextCore() => VideoEditorText.TranscriptWordsHelp;

    protected override List<AutomationPeer>? GetChildrenCore()
    {
        if (Words.Panel is not { } panel)
        {
            return null;
        }

        return [.. panel.View.Words.Select((w, i) => (w, i)).Where(p => panel.Shows(p.i)).Select(p => p.w.Paragraph).Distinct().Select(ParagraphPeer)];
    }

    private TranscriptParagraphPeer ParagraphPeer(int paragraph)
    {
        if (!_paragraphs.TryGetValue(paragraph, out var peer))
        {
            peer = new TranscriptParagraphPeer(this, paragraph);
            _paragraphs[paragraph] = peer;
        }

        return peer;
    }
}

/// <summary>A paragraph of the transcript, for screen readers.</summary>
internal sealed class TranscriptParagraphPeer(TranscriptAutomationPeer list, int paragraph) : AutomationPeer
{
    private IEnumerable<int> Positions()
    {
        if (list.Words.Panel is { } panel)
        {
            for (var i = 0; i < panel.View.Words.Count; i++)
            {
                if (panel.View.Words[i].Paragraph == paragraph && panel.Shows(i))
                {
                    yield return i;
                }
            }
        }
    }

    public override object? GetPattern(PatternInterface patternInterface) => null;

    protected override string GetAcceleratorKeyCore() => "";

    protected override string GetAccessKeyCore() => "";

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

    protected override string GetAutomationIdCore() => FormattableString.Invariant($"paragraph-{paragraph}");

    protected override Rect GetBoundingRectangleCore() => Rect.Empty;

    protected override List<AutomationPeer> GetChildrenCore() => [.. Positions().Select(list.WordPeer)];

    protected override string GetClassNameCore() => nameof(TranscriptParagraphPeer);

    protected override Point GetClickablePointCore() => new(double.NaN, double.NaN);

    protected override string GetHelpTextCore() => "";

    protected override string GetItemStatusCore() => "";

    protected override string GetItemTypeCore() => "";

    protected override AutomationPeer? GetLabeledByCore() => null;

    protected override string GetLocalizedControlTypeCore() => "";

    protected override string GetNameCore()
    {
        if (list.Words.Panel is not { } panel)
        {
            return "";
        }

        var positions = Positions().ToList();
        return positions.Count == 0 ? "" : VideoEditorText.ParagraphAt(panel.TimeOf(positions[0]), string.Join(' ', positions.Select(p => panel.View.Words[p].Word.Text)));
    }

    protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;

    protected override bool HasKeyboardFocusCore() => false;

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    protected override bool IsEnabledCore() => true;

    protected override bool IsKeyboardFocusableCore() => false;

    protected override bool IsOffscreenCore() => false;

    protected override bool IsPasswordCore() => false;

    protected override bool IsRequiredForFormCore() => false;

    protected override void SetFocusCore()
    {
    }
}

/// <summary>A word of the transcript, for screen readers: selected as in the panel, invoked to go there in the video.</summary>
internal sealed class TranscriptWordPeer(TranscriptAutomationPeer list, int position) : AutomationPeer, ISelectionItemProvider, IInvokeProvider, IScrollItemProvider
{
    private TranscriptControl Control => list.Words;

    bool ISelectionItemProvider.IsSelected => Control.Panel?.Selected.Contains(position) == true;

    IRawElementProviderSimple? ISelectionItemProvider.SelectionContainer => ProviderFromPeer(list);

    void ISelectionItemProvider.AddToSelection() => Control.Panel?.Select(position, extend: true);

    void ISelectionItemProvider.RemoveFromSelection() => Control.Panel?.ClearSelection();

    void ISelectionItemProvider.Select() => Control.Panel?.Select(position);

    void IInvokeProvider.Invoke() => Control.Panel?.SeekTo(position);

    void IScrollItemProvider.ScrollIntoView() => Control.Reveal(position);

    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface is PatternInterface.SelectionItem or PatternInterface.Invoke or PatternInterface.ScrollItem ? this : null;

    protected override string GetAcceleratorKeyCore() => "";

    protected override string GetAccessKeyCore() => "";

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

    protected override string GetAutomationIdCore() => FormattableString.Invariant($"word-{position}");

    protected override Rect GetBoundingRectangleCore()
    {
        var box = Control.BoxOf(position);
        return box.IsEmpty || PresentationSource.FromVisual(Control) is null ? Rect.Empty : new Rect(Control.PointToScreen(box.TopLeft), Control.PointToScreen(box.BottomRight));
    }

    protected override List<AutomationPeer>? GetChildrenCore() => null;

    protected override string GetClassNameCore() => nameof(TranscriptWordPeer);

    protected override Point GetClickablePointCore()
    {
        var box = GetBoundingRectangleCore();
        return box.IsEmpty ? new Point(double.NaN, double.NaN) : new Point((box.Left + box.Right) / 2, (box.Top + box.Bottom) / 2);
    }

    protected override string GetHelpTextCore() => "";

    protected override string GetItemStatusCore() => "";

    protected override string GetItemTypeCore() => "";

    protected override AutomationPeer? GetLabeledByCore() => null;

    protected override string GetLocalizedControlTypeCore() => "";

    protected override string GetNameCore() => Control.Panel is { } panel && position < panel.View.Words.Count ? panel.NameOf(position) : "";

    protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;

    protected override bool HasKeyboardFocusCore() => Control.IsKeyboardFocused && Control.Panel?.Caret == position;

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    protected override bool IsEnabledCore() => true;

    protected override bool IsKeyboardFocusableCore() => true;

    protected override bool IsOffscreenCore()
    {
        var box = Control.BoxOf(position);
        return box.IsEmpty || box.Bottom < 0 || box.Top > Control.ActualHeight;
    }

    protected override bool IsPasswordCore() => false;

    protected override bool IsRequiredForFormCore() => false;

    protected override void SetFocusCore()
    {
        Control.Focus();
        Control.Panel?.Select(position);
    }
}
