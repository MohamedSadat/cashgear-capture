using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Apps;

namespace CashGear.Capture.Outputs.Tests.Apps;

public sealed class OpenWithDestinationTests : IDisposable
{
    private readonly TempFolder _temp = new();
    private readonly FakeLauncher _launcher = new();

    public void Dispose() => _temp.Dispose();

    private string Program()
    {
        var program = _temp.File("paintdotnet.exe");
        File.WriteAllBytes(program, [0x4D, 0x5A]);
        return program;
    }

    [Theory]
    [InlineData("\"{file}\"", "\"C:\\Shots\\a b.png\"")]
    [InlineData("/open {file} /in {folder}", "/open C:\\Shots\\a b.png /in C:\\Shots")]
    [InlineData("--new-window", "--new-window \"C:\\Shots\\a b.png\"")]
    [InlineData("", "\"C:\\Shots\\a b.png\"")]
    [InlineData("\"{FILE}\" \"{file}\"", "\"C:\\Shots\\a b.png\" \"C:\\Shots\\a b.png\"")]
    public void The_command_line_gets_the_file(string template, string expected) =>
        OpenWithDestination.Arguments(template, @"C:\Shots\a b.png").Should().Be(expected);

    [Fact]
    public void Programs_are_found_by_path_or_on_the_path()
    {
        OpenWithDestination.Resolve(Program()).Should().Be(Program());
        OpenWithDestination.Resolve(_temp.File("missing.exe")).Should().BeNull();
        OpenWithDestination.Resolve("cmd").Should().EndWithEquivalentOf(@"\cmd.exe");
        OpenWithDestination.Resolve("%SystemRoot%\\System32\\cmd.exe").Should().EndWithEquivalentOf(@"\System32\cmd.exe");
        OpenWithDestination.Resolve(" ").Should().BeNull();
    }

    [Fact]
    public async Task An_unsaved_capture_is_kept_for_the_app_after_the_workflow_ends()
    {
        var keep = _temp.File("opened");
        var destination = new OpenWithDestination(new ExternalApp("pdn", "Paint.NET", Program()), _launcher, keep);
        using var image = TestCaptures.Image(8, 8);
        string temporary;
        OutputResult result;
        using (var item = TestCaptures.Item(image, _temp.File("outgoing")))
        {
            temporary = await item.GetFileAsync(TestContext.Current.CancellationToken);
            result = await destination.SendAsync(item, TestContext.Current.CancellationToken);
        }

        result.Succeeded.Should().BeTrue(result.Message);
        result.Message.Should().Be("Opened with paintdotnet");
        var (program, arguments, folder) = _launcher.Started.Single();
        program.Should().Be(Program());
        var kept = arguments.Trim('"');
        kept.Should().StartWith(keep).And.EndWith(Path.GetFileName(temporary));
        File.Exists(kept).Should().BeTrue("the app may read it later");
        File.Exists(temporary).Should().BeFalse("the item's own temporary file is gone");
        folder.Should().Be(Path.GetDirectoryName(kept));
        destination.DisplayName.Should().Be("Open with Paint.NET");
    }

    [Fact]
    public async Task A_saved_capture_is_opened_where_it_is()
    {
        var saved = _temp.File("Saved.png");
        File.WriteAllBytes(saved, Png.Solid(2, 2));
        using var image = TestCaptures.Image(2, 2);
        using var item = TestCaptures.Item(image, _temp.File("outgoing"));
        item.SavedPath = saved;

        await new OpenWithDestination(new ExternalApp("x", Path: Program(), Arguments: "-o {file}"), _launcher, _temp.File("opened")).SendAsync(item, TestContext.Current.CancellationToken);

        _launcher.Started.Single().Arguments.Should().Be("-o " + saved);
        Directory.Exists(_temp.File("opened")).Should().BeFalse();
    }

    [Fact]
    public async Task A_missing_program_is_reported_and_nothing_starts()
    {
        var destination = new OpenWithDestination(new ExternalApp("x", "Gone", _temp.File("gone.exe")), _launcher, _temp.File("opened"));
        using var image = TestCaptures.Image(2, 2);
        using var item = TestCaptures.Item(image, _temp.File("outgoing"));

        var result = await destination.SendAsync(item, TestContext.Current.CancellationToken);
        var test = await destination.TestAsync(TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        test.Succeeded.Should().BeFalse();
        test.Message.Should().Contain("isn't there");
        _launcher.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task Test_finds_the_program_and_starts_nothing()
    {
        var result = await new OpenWithDestination(new ExternalApp("x", Path: Program()), _launcher, _temp.File("opened")).TestAsync(TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.Message.Should().Contain(Program()).And.Contain("paintdotnet.exe \"C:\\…\\Capture.png\"");
        _launcher.Started.Should().BeEmpty();
    }

    private sealed class FakeLauncher : IProcessLauncher
    {
        public List<(string Program, string Arguments, string Folder)> Started { get; } = [];

        public void Start(string program, string arguments, string workingDirectory) => Started.Add((program, arguments, workingDirectory));
    }
}
