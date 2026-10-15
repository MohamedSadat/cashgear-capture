using CashGear.Capture.App.Tray;

namespace CashGear.Capture.App.Tests.Tray;

public sealed class TrayIconHostTests
{
    private const string Installed = @"C:\Users\me\AppData\Local\CashGear.Capture\current\CashGear.Capture.App.exe";
    private const string Development = @"D:\src\cashgear-capture\src\CashGear.Capture.App\bin\Debug\net10.0-windows10.0.22621.0\CashGear.Capture.App.exe";

    [Fact]
    public void Each_program_file_gets_its_own_icon_id_so_copies_never_lock_each_other_out()
    {
        // Windows refuses an icon id from a second program path while the first file exists; the installed copy then
        // started without a tray icon because a development build had used the same (name-based) id.
        TrayIconHost.IconId(Installed).Should().NotBe(TrayIconHost.IconId(Development));
    }

    [Fact]
    public void The_id_is_stable_for_a_path_whatever_its_case()
    {
        // Stable across starts and updates (Velopack always runs the installed copy from current\), so Windows keeps
        // the user's "always show this icon" choice.
        TrayIconHost.IconId(Installed).Should().Be(TrayIconHost.IconId(Installed.ToLowerInvariant()));
        TrayIconHost.IconId(Installed).Should().NotBe(Guid.Empty);
    }
}
