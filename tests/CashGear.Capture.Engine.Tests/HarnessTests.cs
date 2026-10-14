namespace CashGear.Capture.Engine.Tests;

public sealed class HarnessTests
{
    [Fact]
    public void Harness_runs() => Environment.Is64BitProcess.Should().BeTrue();
}
