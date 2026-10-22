using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Media;

namespace CashGear.Capture.App.Tests.Platform;

/// <summary>Windows looked at without showing them: their content laid out, its elements and its list items' names.</summary>
internal static class TestLayout
{
    /// <summary>Lays out a window's content without showing it (its templates are applied, its items made); returns the content.</summary>
    public static FrameworkElement Layout(Window window, double width = 900, double height = 1000)
    {
        var root = (FrameworkElement)window.Content;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        return root;
    }

    /// <summary>The elements of type <typeparamref name="T"/> under <paramref name="root"/>, in visual-tree (and Tab) order.</summary>
    public static IEnumerable<T> FindAll<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var inner in FindAll<T>(child))
            {
                yield return inner;
            }
        }
    }

    /// <summary>What a screen reader says for each item of <paramref name="list"/>.</summary>
    public static List<string> ItemNames(ItemsControl list) =>
        [.. UIElementAutomationPeer.CreatePeerForElement(list).GetChildren().Select(peer => peer.GetName())];
}
