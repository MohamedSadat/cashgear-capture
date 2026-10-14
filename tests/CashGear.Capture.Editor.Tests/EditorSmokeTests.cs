using CashGear.Capture.Editor;

namespace CashGear.Capture.Editor.Tests;

public sealed class EditorSmokeTests
{
    [Fact]
    public void Editor_assembly_loads() =>
        EditorAssemblyMarker.Name.Should().Be("CashGear.Capture.Editor");
}
