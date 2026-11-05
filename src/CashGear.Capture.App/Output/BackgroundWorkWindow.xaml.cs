using System.Windows;
using System.Windows.Input;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.App.Output;

/// <summary>What is going on in the background. Closing it stops nothing. Esc closes it.</summary>
public partial class BackgroundWorkWindow : Window, IThemedWindow
{
    public BackgroundWorkWindow(BackgroundWorkViewModel viewModel, AppTheme theme)
    {
        InitializeComponent();
        DataContext = viewModel;
        ApplyTheme(theme);
    }

    public void ApplyTheme(AppTheme theme) => Theming.Apply(this, theme);

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
