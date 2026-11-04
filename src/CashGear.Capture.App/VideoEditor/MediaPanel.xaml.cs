using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>The media panel beside the preview: its rows are dragged onto the timeline, or added with Enter.</summary>
public partial class MediaPanel : UserControl
{
    /// <summary>What a row's drag carries: the asset's id (a format name, not text).</summary>
    [Localizable(false)]
    public const string DragFormat = "CashGear.Capture.MediaAsset";

    /// <summary>What a transition's drag carries: its row (a format name, not text).</summary>
    [Localizable(false)]
    public const string TransitionFormat = "CashGear.Capture.Transition";

    private Point? _pressed;

    public MediaPanel() => InitializeComponent();

    /// <summary>The rows, for the keyboard's first stop.</summary>
    internal ListBox Rows => List;

    private void OnListPress(object sender, MouseButtonEventArgs e) =>
        _pressed = RowAt(e.OriginalSource) is not null ? e.GetPosition(List) : null;

    private void OnListMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressed is not { } from)
        {
            return;
        }

        var moved = e.GetPosition(List) - from;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _pressed = null;
        if (RowAt(e.OriginalSource)?.DataContext is MediaItemViewModel row)
        {
            DragDrop.DoDragDrop(List, new DataObject(DragFormat, row.Asset.Id), DragDropEffects.Copy);
        }
    }

    private void OnListKey(object sender, KeyEventArgs e)
    {
        if (DataContext is not MediaPanelViewModel panel)
        {
            return;
        }

        if (e.Key == Key.Enter && panel.AddToTimelineCommand.CanExecute(null))
        {
            panel.AddToTimelineCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete && panel.RemoveCommand.CanExecute(null))
        {
            panel.RemoveCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RowAt(e.OriginalSource) is not null && DataContext is MediaPanelViewModel panel && panel.AddToTimelineCommand.CanExecute(null))
        {
            panel.AddToTimelineCommand.Execute(null);
        }
    }

    private void OnTransitionPress(object sender, MouseButtonEventArgs e) =>
        _pressed = RowAt(e.OriginalSource) is not null ? e.GetPosition(TransitionList) : null;

    private void OnTransitionMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _pressed is not { } from)
        {
            return;
        }

        var moved = e.GetPosition(TransitionList) - from;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _pressed = null;
        if (RowAt(e.OriginalSource)?.DataContext is TransitionChoice row)
        {
            DragDrop.DoDragDrop(TransitionList, new DataObject(TransitionFormat, row), DragDropEffects.Copy);
        }
    }

    private void OnTransitionKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DataContext is MediaPanelViewModel panel && panel.ApplyTransitionCommand.CanExecute(null))
        {
            panel.ApplyTransitionCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>The row an event came from.</summary>
    private static ListBoxItem? RowAt(object source)
    {
        var element = source as DependencyObject;
        while (element is not null and not ListBoxItem)
        {
            element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element);
        }

        return element as ListBoxItem;
    }
}
