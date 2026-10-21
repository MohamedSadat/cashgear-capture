namespace CashGear.Capture.Analyzers.Tests;

public sealed class XamlTextTests
{
    [Fact]
    public void Literal_text_in_XAML_is_reported_with_its_line()
    {
        var xaml = new InMemoryFile(
            Compiler.ProjectDirectory + @"\Settings\SettingsWindow.xaml",
            """
            <Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                    xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                    xmlns:r="clr-namespace:Demo.Resources"
                    Title="Settings">
              <StackPanel>
                <TextBlock Text="{x:Static r:DemoText.Hello}" />
                <TextBlock Text="{Binding Name, StringFormat={}{0} s}" />
                <Button Content="OK" AutomationProperties.Name="Save the settings" />
                <TextBlock>Hello there</TextBlock>
                <TextBlock Text="&#xE8EE;" />
                <TextBlock Text="−" ToolTip="{}{0} px" />
                <MenuItem Header="_File" InputGestureText="Ctrl+S" />
                <TextBlock x:Name="Status" Tag="not text" />
              </StackPanel>
            </Window>
            """);

        var reported = Compiler.Analyze("public static class Empty { }", xaml).Where(d => d.Id == "CG0002").ToArray();

        reported.Select(d => (d.Location.GetLineSpan().StartLinePosition.Line + 1, d.GetMessage())).Should().Equal(
            (4, "Title=\"Settings\" is literal text: use {x:Static} with a string from a .resx file"),
            (8, "Content=\"OK\" is literal text: use {x:Static} with a string from a .resx file"),
            (8, "AutomationProperties.Name=\"Save the settings\" is literal text: use {x:Static} with a string from a .resx file"),
            (9, "TextBlock=\"Hello there\" is literal text: use {x:Static} with a string from a .resx file"),
            (11, "ToolTip=\"{0} px\" is literal text: use {x:Static} with a string from a .resx file"),
            (12, "Header=\"_File\" is literal text: use {x:Static} with a string from a .resx file"));
        reported[0].Location.GetLineSpan().Path.Should().EndWith("SettingsWindow.xaml");
    }
}
