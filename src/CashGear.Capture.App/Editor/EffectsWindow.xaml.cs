using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CashGear.Capture.App.Editor;

/// <summary>The parameter templates shared by the image dialogs (a resource dictionary with its own class, so it loads anywhere).</summary>
public partial class EditParameterTemplates : ResourceDictionary
{
    public EditParameterTemplates() => InitializeComponent();
}

/// <summary>The Effects window; everything lives in <see cref="EffectsViewModel"/>. Apply waits for the preview to be current.</summary>
public partial class EffectsWindow : Window
{
    public EffectsWindow() => InitializeComponent();

    /// <summary>
    /// Enter presses the default button while a number box still has the focus: its value only reaches the setting
    /// when the box loses the focus, so it is passed on first.
    /// </summary>
    internal static void CommitTypedText() =>
        (Keyboard.FocusedElement as TextBox)?.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (DataContext is not PreviewDialogViewModel viewModel)
        {
            return;
        }

        CommitTypedText();
        ApplyButton.IsEnabled = false;
        try
        {
            if (await viewModel.ApplyAsync())
            {
                DialogResult = true;
            }
        }
        finally
        {
            ApplyButton.IsEnabled = true;
        }
    }
}

/// <summary>Canvas size, Rotate and other small image dialogs (<see cref="ParameterDialogViewModel"/>).</summary>
public partial class ImageEditWindow : Window
{
    public ImageEditWindow() => InitializeComponent();

    private async void OnApply(object sender, RoutedEventArgs e)
    {
        EffectsWindow.CommitTypedText();
        if (DataContext is PreviewDialogViewModel viewModel && await viewModel.ApplyAsync())
        {
            DialogResult = true;
        }
    }
}
