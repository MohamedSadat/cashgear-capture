using System.IO;
using System.Windows;
using CashGear.Capture.App.Recording;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Overlays;
using CashGear.Capture.Core.Recording;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Engine.Capture.Wgc;
using CashGear.Capture.Engine.Display;
using CashGear.Capture.Engine.Tests;
using CashGear.Capture.Media;
using CashGear.Capture.Overlays.Tests;
using CashGear.Capture.Recording;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CashGear.Capture.App.Tests.Recording;

public sealed class DrawingShapeTests
{
    [Fact]
    public void A_rectangle_is_closed_and_an_arrow_has_two_barbs_at_its_head()
    {
        DrawingShapes.Rectangle(new Point(10, 20), new Point(50, 60)).Should().Equal(
            new Point(10, 20), new Point(50, 20), new Point(50, 60), new Point(10, 60), new Point(10, 20));

        var arrow = DrawingShapes.Arrow(new Point(0, 0), new Point(100, 0), width: 4);
        arrow.Should().HaveCount(5);
        arrow[1].Should().Be(new Point(100, 0));
        arrow[3].Should().Be(new Point(100, 0));
        (arrow[2].X, arrow[4].X).Should().Match<(double A, double B)>(x => x.A < 100 && Math.Abs(x.A - x.B) < 1e-9, "both barbs point back");
        (arrow[2].Y + arrow[4].Y).Should().BeApproximately(0, 1e-9, "one each side");
        Math.Sqrt(Math.Pow(100 - arrow[2].X, 2) + Math.Pow(arrow[2].Y, 2)).Should().BeApproximately(16, 1e-9, "four widths long");
        DrawingShapes.Arrow(new Point(5, 5), new Point(5, 5), 4).Should().HaveCount(2, "a click is just a dot");
    }

    [Fact]
    public void Drawings_fade_over_their_last_half_second()
    {
        DrawingShapes.Opacity(TimeSpan.FromSeconds(100), 0).Should().Be(1, "0 keeps them");
        DrawingShapes.Opacity(TimeSpan.FromSeconds(2), 3).Should().Be(1);
        DrawingShapes.Opacity(TimeSpan.FromSeconds(2.75), 3).Should().BeApproximately(0.5, 1e-9);
        DrawingShapes.Opacity(TimeSpan.FromSeconds(3.1), 3).Should().Be(0);
    }

    [Fact]
    public void The_pen_switches_tools_colours_and_asks_the_layer_to_undo_and_clear()
    {
        var drawing = new DrawingViewModel(new DrawingSettings("#00FF00", 6, 0));
        var undone = 0;
        var cleared = 0;
        drawing.UndoRequested += (_, _) => undone++;
        drawing.ClearRequested += (_, _) => cleared++;

        (drawing.Color, drawing.Width, drawing.IsPen).Should().Be(("#00FF00", 6, true));
        drawing.ToggleCommand.Execute(null);
        drawing.IsActive.Should().BeTrue();
        drawing.IsArrow = true;
        drawing.Tool.Should().Be(DrawingTool.Arrow);
        drawing.IsPen.Should().BeFalse();
        drawing.UseColorCommand.Execute("#2F80FF");
        drawing.Color.Should().Be("#2F80FF");
        drawing.UndoCommand.Execute(null);
        drawing.ClearCommand.Execute(null);
        (undone, cleared).Should().Be((1, 1));
        drawing.DoneCommand.Execute(null);
        drawing.IsActive.Should().BeFalse();
    }
}

/// <summary>Drawing on the screen while recording: the layer is in the recording.</summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class DrawingLayerTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly WgcInterop _interop = new();
    private readonly ILoggerFactory _loggers = TestLog.Create();

    public void Dispose()
    {
        _interop.Dispose();
        _loggers.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    /// <summary>
    /// The pen on, a stroke dragged with the mouse across the area: the layer takes it, it is in the recording (red
    /// where it was drawn), and the click that drew it isn't drawn as a click (the metadata notes the drawing).
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task A_stroke_drawn_with_the_mouse_is_in_the_recording()
    {
        InteractiveDesktop.Require();
        TestFfmpeg.Require();
        using var exclusive = TestInput.Exclusive();
        var ct = TestContext.Current.CancellationToken;
        var monitor = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors().First(m => m.IsPrimary);
        var area = new PixelRect(monitor.Bounds.X + 100, monitor.Bounds.Y + 100, 320, 240);
        var recorder = new ScreenRecorder(TestFfmpeg.Encoders, new RecordingStore(TestFfmpeg.Paths(_folder)), _interop, new FakeSound(), _loggers);
        var options = new RecordingOptions(
            monitor, area, 30, QualityPreset.High, IncludeCursor: false, EncoderPresets.Software, Path.Combine(_folder, "drawing.mp4"),
            Overlays: new OverlaySettings(Clicks: new ClickOverlaySettings(LeftColor: "#00FF00")));
        RecordingResult? result = null;
        var strokes = 0;
        await StaDispatcher.RunAsync(async () =>
        {
            var scale = monitor.Scale.ScaleX;
            var background = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                Left = (area.X - 20) / scale,
                Top = (area.Y - 20) / scale,
                Width = 400 / scale,
                Height = 320 / scale,
                Background = System.Windows.Media.Brushes.White,
            };
            background.Show();
            var drawing = new DrawingViewModel(new DrawingSettings("#FF0000", 12, 0));
            var layer = new DrawingLayerWindow(drawing, area);
            drawing.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DrawingViewModel.IsActive))
                {
                    recorder.SetDrawing(drawing.IsActive);
                }
            };
            layer.Show();
            try
            {
                await Task.Delay(300, ct);
                await recorder.StartAsync(options, ct);
                while (recorder.Elapsed.TotalSeconds < 0.6)
                {
                    await Task.Delay(5, ct);
                }

                drawing.IsActive = true;
                await Task.Delay(200, ct);
                var y = area.Y + 120;
                Assert.SkipUnless(TestInput.Drag([(area.X + 60, y), (area.X + 160, y), (area.X + 260, y)]), "SendInput was blocked.");
                await Task.Delay(300, ct);
                strokes = layer.Strokes.Count;
                drawing.IsActive = false;
                while (recorder.Elapsed.TotalSeconds < 1.8)
                {
                    await Task.Delay(5, ct);
                }

                result = await recorder.StopAsync(ct);
            }
            finally
            {
                layer.Close();
                background.Close();
            }
        });

        strokes.Should().Be(1, "the pen took the drag");
        var red = 0;
        var green = 0;
        using (var reader = new MediaReader(result!.Path))
        {
            reader.ReadVideo((time, width, height, bgra) =>
            {
                if (Math.Abs(time - 1.5) < 0.017)
                {
                    for (var x = 70; x < 250; x++)
                    {
                        var i = ((120 * width) + x) * 4;
                        red += bgra[i + 2] > 180 && bgra[i + 1] < 80 && bgra[i] < 80 ? 1 : 0;
                    }
                }

                for (var x = 40; x < 80; x++)
                {
                    var i = ((120 * width) + x) * 4;
                    green += bgra[i + 1] > 180 && bgra[i + 2] < 80 && bgra[i] < 80 ? 1 : 0;
                }
            });
        }

        OverlayRecordingTests.Report($"Drawing: {red} red pixels along the stroke; {green} green click-ring pixels");
        red.Should().BeGreaterThan(150, "the stroke is in the recording, across the area");
        green.Should().Be(0, "the click that drew it isn't shown as a click");
        var metadata = RecordingMetadata.Load(result.MetadataPath!);
        metadata.Drawing.Select(d => d.On).Should().Equal(true, false);
        metadata.Clicks.Should().ContainSingle("the click is still in the metadata");
    }
}
