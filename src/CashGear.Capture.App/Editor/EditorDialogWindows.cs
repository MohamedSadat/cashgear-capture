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

/// <summary>The name prompt; the value lives in <see cref="NameViewModel"/>.</summary>
public partial class NameWindow : Window
{
    public NameWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if ((DataContext as NameViewModel)?.IsValid == true)
        {
            DialogResult = true;
        }
    }
}
