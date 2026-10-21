using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.Core.Tests.Settings;

public sealed class PortableModeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", "portable-" + Guid.NewGuid().ToString("N")[..8]);

    public PortableModeTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void An_installed_copy_is_not_portable()
    {
        Directory.CreateDirectory(Path.Combine(_root, "current"));

        PortableMode.FindRoot(Path.Combine(_root, "current")).Should().BeNull();
        PortableMode.FindRoot(_root).Should().BeNull();
    }

    [Fact]
    public void The_portable_zip_marks_its_root_next_to_current()
    {
        var current = Path.Combine(_root, "current");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(_root, PortableMode.Marker), string.Empty);

        PortableMode.FindRoot(current + Path.DirectorySeparatorChar).Should().Be(_root);
        PortableMode.FindRoot(_root).Should().Be(_root, "a folder of its own with the marker is portable too");
    }

    [Fact]
    public void A_marker_above_a_folder_not_called_current_does_not_count()
    {
        var other = Path.Combine(_root, "bin");
        Directory.CreateDirectory(other);
        File.WriteAllText(Path.Combine(_root, PortableMode.Marker), string.Empty);

        PortableMode.FindRoot(other).Should().BeNull();
    }

    [Fact]
    public void A_portable_copy_keeps_everything_in_Data_outside_current()
    {
        var paths = AppPaths.Portable(_root);

        paths.PortableRoot.Should().Be(_root);
        paths.SettingsFile.Should().Be(Path.Combine(_root, "Data", "settings.json"));
        paths.PresetsFile.Should().Be(Path.Combine(_root, "Data", "presets.json"));
        paths.DataDirectory.Should().Be(Path.Combine(_root, "Data"));
        paths.LogDirectory.Should().Be(Path.Combine(_root, "Data", "logs"));
        paths.HistoryDirectory.Should().Be(Path.Combine(_root, "Data", "history"));
        paths.DefaultCaptureDirectory.Should().EndWith("CashGear Capture", "captures still go to Pictures");
        AppPaths.ForUser().PortableRoot.Should().BeNull();
    }

    [Fact]
    public void The_first_start_takes_this_users_settings_and_later_ones_keep_their_own()
    {
        var user = new AppPaths(Path.Combine(_root, "roaming"), Path.Combine(_root, "local", "logs"), _root);
        Directory.CreateDirectory(user.SettingsDirectory);
        File.WriteAllText(user.SettingsFile, """{ "appearance": { "theme": "dark" } }""");
        File.WriteAllText(user.PresetsFile, "{}");
        var portable = AppPaths.Portable(Path.Combine(_root, "stick"));

        PortableMode.SeedFrom(portable, user).Should().Equal(portable.SettingsFile, portable.PresetsFile);
        File.ReadAllText(portable.SettingsFile).Should().Contain("dark");

        File.WriteAllText(portable.SettingsFile, """{ "appearance": { "theme": "light" } }""");
        PortableMode.SeedFrom(portable, user).Should().BeEmpty();
        File.ReadAllText(portable.SettingsFile).Should().Contain("light");
    }

    [Fact]
    public void Nothing_to_take_leaves_the_defaults()
    {
        var user = new AppPaths(Path.Combine(_root, "none"), Path.Combine(_root, "none", "logs"), _root);
        var portable = AppPaths.Portable(Path.Combine(_root, "stick"));

        PortableMode.SeedFrom(portable, user).Should().BeEmpty();
        File.Exists(portable.SettingsFile).Should().BeFalse();
    }

    [Fact]
    public void Each_portable_folder_is_an_instance_of_its_own()
    {
        var a = PortableMode.InstanceName(Path.Combine(_root, "a"));

        a.Should().MatchRegex("^Portable-[0-9a-f]{8}$");
        PortableMode.InstanceName(Path.Combine(_root, "A") + Path.DirectorySeparatorChar).Should().Be(a, "the same folder, written differently");
        PortableMode.InstanceName(Path.Combine(_root, "b")).Should().NotBe(a);
    }
}
