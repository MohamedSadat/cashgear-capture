namespace CashGear.Capture.Core.Tests;

public sealed class HarnessTests
{
    [Fact]
    public void Harness_runs() => (1 + 1).Should().Be(2);
}
