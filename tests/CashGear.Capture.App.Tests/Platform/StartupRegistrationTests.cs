using CashGear.Capture.App.Platform;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;

namespace CashGear.Capture.App.Tests.Platform;

/// <summary>Uses a scratch key under HKCU\Software\CashGear\Tests — never the real Run key.</summary>
public sealed class StartupRegistrationTests : IDisposable
{
    private readonly string _keyPath = $@"Software\CashGear\Tests\Run-{Guid.NewGuid():N}";

    public void Dispose() => Registry.CurrentUser.DeleteSubKeyTree(_keyPath, throwOnMissingSubKey: false);

    [Fact]
    public void Enable_writes_the_quoted_command_and_disable_removes_it()
    {
        var startup = new RegistryStartupRegistration(
            NullLogger<RegistryStartupRegistration>.Instance, _keyPath, "CashGear Capture", "\"C:\\Apps\\CashGear.Capture.App.exe\"");

        startup.IsEnabled.Should().BeFalse();

        startup.SetEnabled(true);
        startup.IsEnabled.Should().BeTrue();
        using (var key = Registry.CurrentUser.OpenSubKey(_keyPath))
        {
            key!.GetValue("CashGear Capture").Should().Be("\"C:\\Apps\\CashGear.Capture.App.exe\"");
        }

        startup.SetEnabled(false);
        startup.IsEnabled.Should().BeFalse();
        startup.SetEnabled(false); // already off: no error
    }

    [Fact]
    public void Default_registration_points_at_the_real_run_key()
    {
        RegistryStartupRegistration.RunKeyPath.Should().Be(@"Software\Microsoft\Windows\CurrentVersion\Run");
        RegistryStartupRegistration.DefaultValueName.Should().Be("CashGear Capture");
    }
}
