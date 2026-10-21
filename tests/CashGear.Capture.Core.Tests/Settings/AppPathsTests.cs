using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Tests.Settings;

public sealed class AppPathsTests
{
    [Fact]
    public void A_named_instance_gets_folders_of_its_own()
    {
        var before = Environment.GetEnvironmentVariable("CASHGEAR_INSTANCE");
        try
        {
            Environment.SetEnvironmentVariable("CASHGEAR_INSTANCE", "bench");
            var paths = AppPaths.FromEnvironment();

            AppPaths.InstanceName.Should().Be("bench");
            paths.SettingsDirectory.Should().EndWith(Path.Combine("CashGear", "Capture-bench"));
            paths.DataDirectory.Should().EndWith(Path.Combine("CashGear", "Capture-bench"));
            paths.LogDirectory.Should().EndWith(Path.Combine("CashGear", "Capture-bench", "logs"));

            Environment.SetEnvironmentVariable("CASHGEAR_INSTANCE", @"..\other\é b-1");
            AppPaths.InstanceName.Should().Be("otherb-1", "only letters, digits and dashes are kept");

            Environment.SetEnvironmentVariable("CASHGEAR_INSTANCE", null);
            AppPaths.InstanceName.Should().BeNull();
            AppPaths.FromEnvironment().SettingsDirectory.Should().EndWith(Path.Combine("CashGear", "Capture"));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CASHGEAR_INSTANCE", before);
        }
    }
}
