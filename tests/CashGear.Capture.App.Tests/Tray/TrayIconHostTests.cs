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

    [Fact]
    public void The_usual_id_is_the_one_existing_installs_already_have()
    {
        // Pinned: changing it would give every install a new icon and drop the user's "always show this icon" choice.
        TrayIconHost.IconId(Installed, slot: 0).Should().Be(TrayIconHost.IconId(Installed));
        TrayIconHost.IconId(Installed).Should().Be(Guid.Parse("bec2e136-3638-24d3-a2d1-04b3aa277fcc"));
    }

    [Fact]
    public void Fallback_ids_differ_from_the_usual_one_and_from_each_other()
    {
        // A copy installed into a packaged app's virtualized AppData reports the same path as the real install, so the
        // path alone can't keep them apart; whichever starts second needs another id that Windows hasn't tied to a file.
        var ids = Enumerable.Range(0, TrayIconHost.IdSlots).Select(slot => TrayIconHost.IconId(Installed, slot)).ToList();

        ids.Should().HaveCount(TrayIconHost.IdSlots).And.OnlyHaveUniqueItems().And.NotContain(Guid.Empty);
        ids.Should().NotIntersectWith(
            Enumerable.Range(0, TrayIconHost.IdSlots).Select(slot => TrayIconHost.IconId(Development, slot)));
    }

    [Fact]
    public void Fallback_ids_are_stable_for_a_path_whatever_its_case()
    {
        // The same fallback every start, so Windows remembers the user's choice for it too.
        TrayIconHost.IconId(Installed, slot: 1).Should().Be(TrayIconHost.IconId(Installed.ToLowerInvariant(), slot: 1));
    }
}
