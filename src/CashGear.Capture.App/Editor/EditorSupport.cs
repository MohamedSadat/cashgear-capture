using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;

namespace CashGear.Capture.App.Editor;

/// <summary>
/// A toolbar tool: a radio button drawn as a toggle button. The style is a dynamic reference to the theme's
/// toggle-button style, so it follows light/dark (a <c>BasedOn</c> static reference would freeze one theme's colours).
/// </summary>
public sealed class ToolButton : RadioButton
{
    public ToolButton()
    {
        SetResourceReference(StyleProperty, typeof(ToggleButton));
        Focusable = false; // keys belong to the canvas (tool shortcuts, Delete, arrows)
        MinWidth = 58;
        Padding = new Thickness(4);
        Margin = new Thickness(0, 0, 2, 0);
    }
}

/// <summary>Binds a tool radio button: checked when the bound tool equals the parameter; checking it selects that tool.</summary>
public sealed class ToolIsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Equals(value, parameter);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? parameter : Binding.DoNothing;
}

/// <summary>Visible when the value is not null.</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
