using System.Windows;
using System.Windows.Navigation;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.App.About;

/// <summary>The app's version and website, in the current language and theme.</summary>
public partial class AboutWindow : Window, IThemedWindow
{
    private readonly IShell _shell;

    public AboutWindow(IShell shell, AppTheme theme)
    {
        InitializeComponent();
        _shell = shell;
        ApplyTheme(theme);
        Loaded += (_, _) => CloseButton.Focus();
    }

    public void ApplyTheme(AppTheme theme) => Theming.Apply(this, theme);

    private void OnWebsiteNavigate(object sender, RequestNavigateEventArgs e)
    {
        _shell.OpenLink(e.Uri.AbsoluteUri);
        e.Handled = true;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
