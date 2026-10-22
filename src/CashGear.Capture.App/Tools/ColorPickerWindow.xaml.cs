using System.Windows;
using System.Windows.Input;
using CashGear.Capture.App.Platform;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.App.Tools;

/// <summary>
/// The colour picker's window: on top, out of captures (so it is never in the frozen screen it picks from). Enter
/// picks a colour on the screen (its default button), Esc closes it.
/// </summary>
public partial class ColorPickerWindow : Window, IThemedWindow, IScreenToolWindow
{
    public ColorPickerWindow(ColorPickerViewModel viewModel, AppTheme theme)
    {
        InitializeComponent();
        DataContext = viewModel;
        ApplyTheme(theme);
        SourceInitialized += (_, _) => ToolWindowStyle.Apply(this, clickThrough: false);
    }

    public void ApplyTheme(AppTheme theme) => Theming.Apply(this, theme);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
    }
}
