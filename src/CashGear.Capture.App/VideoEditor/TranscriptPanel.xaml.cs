using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>
/// The panel beside the preview: the transcript's words (<see cref="TranscriptControl"/>), what can be done with them, the
/// review lists, and the box a word is corrected in, which sits on the word and says that the video doesn't change.
/// </summary>
public partial class TranscriptPanel : UserControl
{
    private int _editing = -1;

    public TranscriptPanel()
    {
        InitializeComponent();
        Words.EditRequested += (_, e) => StartEditing(e.Position, e.Text);
    }

    internal TranscriptControl WordsControl => Words;

    /// <summary>The box a word is corrected in.</summary>
    internal TextBox Editor => WordEditor;

    internal TextBlock EditNoteLine => EditNote;

    public void Attach(TranscriptPanelViewModel viewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        DataContext = viewModel;
        Words.Attach(viewModel, WordsScroll);
    }

    /// <summary>Opens the box on a word, with <paramref name="text"/> in it to go on from.</summary>
    internal void StartEditing(int position, string text)
    {
        if (DataContext is not TranscriptPanelViewModel panel || position < 0 || position >= panel.View.Words.Count)
        {
            return;
        }

        Words.Reveal(position);
        var box = Words.BoxOf(position);
        if (box.IsEmpty)
        {
            return;
        }

        _editing = position;
        WordEditor.FlowDirection = panel.IsRightToLeft(panel.View.Words[position].AssetId) ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        WordEditor.Text = text;
        WordEditor.MinWidth = Math.Max(box.Width + 12, 60);
        Canvas.SetLeft(WordEditor, Math.Clamp(box.X - 6, 0, Math.Max(0, Words.ActualWidth - WordEditor.MinWidth)));
        Canvas.SetTop(WordEditor, Math.Max(0, box.Y - 4));
        EditorLayer.Visibility = Visibility.Visible;
        EditNote.Visibility = Visibility.Visible;
        WordEditor.Focus();
        WordEditor.CaretIndex = WordEditor.Text.Length;
    }

    /// <summary>Closes the box, keeping what was typed when asked; the focus goes back to the words.</summary>
    internal void FinishEditing(bool keep)
    {
        if (_editing < 0)
        {
            return;
        }

        var position = _editing;
        _editing = -1;
        var text = WordEditor.Text;
        EditorLayer.Visibility = Visibility.Collapsed;
        EditNote.Visibility = Visibility.Collapsed;
        if (keep && DataContext is TranscriptPanelViewModel panel)
        {
            panel.Correct(position, text);
        }

        Words.Focus();
    }

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Escape)
        {
            FinishEditing(keep: e.Key == Key.Enter);
            e.Handled = true;
        }
    }

    private void OnEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_editing >= 0 && !ReferenceEquals(e.NewFocus, WordEditor))
        {
            FinishEditing(keep: true);
        }
    }

    private void OnLiveTextUpdated(object? sender, DataTransferEventArgs e)
    {
        if (sender is TextBlock text)
        {
            LiveText.Announce(text);
        }
    }
}
