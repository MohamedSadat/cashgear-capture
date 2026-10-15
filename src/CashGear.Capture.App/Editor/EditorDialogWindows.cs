using System.Windows;

namespace CashGear.Capture.App.Editor;

/// <summary>Resize image dialog; the values live in <see cref="ResizeImageViewModel"/>.</summary>
public partial class ResizeImageWindow : Window
{
    public ResizeImageWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            WidthBox.Focus();
            WidthBox.SelectAll();
        };
    }

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}

/// <summary>Add border dialog; the values live in <see cref="BorderViewModel"/>.</summary>
public partial class BorderWindow : Window
{
    public BorderWindow() => InitializeComponent();

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;
}
