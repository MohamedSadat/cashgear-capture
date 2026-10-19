using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Selection;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.Core.Tests.Settings;

public sealed class AppStateStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public AppStateStoreTests()
    {
        _paths = new AppPaths(Path.Combine(_root, "settings"), Path.Combine(_root, "data", "logs"), Path.Combine(_root, "captures"));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Nothing_is_remembered_at_first()
    {
        var store = Create();
        store.Current.LastCapture.Should().BeNull();
        File.Exists(store.FilePath).Should().BeFalse("reading writes nothing");
    }

    [Fact]
    public async Task A_polygon_capture_is_remembered_exactly_across_runs()
    {
        var shape = CaptureShape.Polygon([new(-100, 5), new(40, -20), new(10, 300), new(-90, 250)]);
        var last = new RepeatableCapture(CaptureMode.Polygon, shape.Bounds) { Shape = shape, WindowTitle = "Notepad" };
        await Create().UpdateAsync(s => s with { LastCapture = last }, Ct);

        var read = Create().Current.LastCapture!;
        read.Mode.Should().Be(CaptureMode.Polygon);
        read.Bounds.Should().Be(shape.Bounds);
        read.Shape!.Kind.Should().Be(ShapeKind.Polygon);
        read.Shape.Points.Should().Equal(shape.Points);
        read.WindowTitle.Should().Be("Notepad");
        File.ReadAllText(Path.Combine(_paths.DataDirectory, "state.json")).Should().Contain("\"mode\": \"Polygon\"");
    }

    [Fact]
    public async Task Rounded_and_menu_shapes_window_and_scroll_point_round_trip()
    {
        var store = Create();
        var rounded = CaptureShape.RoundedRectangle(new PixelRect(10, 20, 300, 200), 24);
        await store.UpdateAsync(s => s with { LastCapture = new RepeatableCapture(CaptureMode.RoundedRegion, rounded.Bounds) { Shape = rounded } }, Ct);
        Create().Current.LastCapture!.Shape!.Radius.Should().Be(24);

        var menus = CaptureShape.Union([new PixelRect(0, 0, 100, 200), new PixelRect(100, 40, 80, 60)]);
        await store.UpdateAsync(s => s with { LastCapture = new RepeatableCapture(CaptureMode.Menu, menus.Bounds) { Shape = menus } }, Ct);
        Create().Current.LastCapture!.Shape!.Rectangles.Should().Equal(menus.Rectangles);

        var scrolling = new RepeatableCapture(CaptureMode.Scrolling, new PixelRect(5, 5, 800, 600))
        {
            Window = 0x1234_5678_9ABC,
            WindowClass = "Chrome_WidgetWin_1",
            ProcessId = 4321,
            Point = new PixelPoint(-7, 9),
        };
        await store.UpdateAsync(s => s with { LastCapture = scrolling }, Ct);
        Create().Current.LastCapture.Should().Be(scrolling);
    }

    [Fact]
    public async Task An_unreadable_file_means_nothing_is_remembered()
    {
        Directory.CreateDirectory(_paths.DataDirectory);
        await File.WriteAllTextAsync(Path.Combine(_paths.DataDirectory, "state.json"), "{ \"lastCapture\": { \"mode\": \"Polygon\", \"bounds\": [1, 2] } }", Ct);
        Create().Current.LastCapture.Should().BeNull();

        await File.WriteAllTextAsync(Path.Combine(_paths.DataDirectory, "state.json"), "not json", Ct);
        Create().Current.LastCapture.Should().BeNull();
    }

    [Fact]
    public async Task Writes_leave_no_temporary_file()
    {
        var store = Create();
        await store.UpdateAsync(s => s with { LastCapture = new RepeatableCapture(CaptureMode.FullScreen, new PixelRect(0, 0, 10, 10)) }, Ct);
        Directory.GetFiles(_paths.DataDirectory).Select(Path.GetFileName).Should().Equal("state.json");
    }

    [Fact]
    public void A_window_is_the_same_only_with_its_handle_class_and_process()
    {
        var window = new Core.Display.WindowInfo(42, "Doc 1 - Editor", "EditorClass", 77, new PixelRect(0, 0, 10, 10), false, 0);
        var last = RepeatableCapture.Of(CaptureMode.Window, window);

        last.IsSameWindow(window with { Title = "Doc 2 - Editor" }).Should().BeTrue("titles change");
        last.IsSameWindow(window with { ProcessId = 78 }).Should().BeFalse("the handle was reused by another program");
        last.IsSameWindow(window with { ClassName = "Other" }).Should().BeFalse();
        last.IsSameWindow(null).Should().BeFalse();
    }

    private JsonAppStateStore Create() => new(_paths, NullLogger<JsonAppStateStore>.Instance);
}
