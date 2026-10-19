using System.IO;
using System.Windows;
using System.Windows.Media;
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

/// <summary>
/// Overlays in real recordings. The acceptance check: a click and a shortcut, made with SendInput into a test window
/// over the recorded area, are drawn from the frame of their time (taken from the hooks), and no later than the
/// window's own reaction to them shows on the screen.
/// </summary>
[Trait("Category", InteractiveDesktop.Category)]
[Collection(DesktopCollection.Name)]
public sealed class OverlayRecordingTests : IDisposable
{
    private const int Fps = 30;
    private static readonly PixelPoint ClickAt = new(60, 60);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "cashgear-tests", Guid.NewGuid().ToString("N"));
    private readonly WgcInterop _interop = new();
    private readonly ILoggerFactory _loggers = TestLog.Create();
    private readonly RecordingStore _store;

    public OverlayRecordingTests() => _store = new RecordingStore(TestFfmpeg.Paths(_folder));

    public void Dispose()
    {
        _interop.Dispose();
        _loggers.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    /// <summary>When things first show in a recording (frame numbers), and when the hooks say they happened.</summary>
    internal sealed record SyncResult(int Ring, int WindowClicked, int ClickFrame, int Caption, int WindowKeyed, int KeyFrame);

    [Fact(Timeout = 90_000)]
    public async Task Clicks_and_keys_are_drawn_on_the_frame_they_happen_while_recording()
    {
        var overlays = new OverlaySettings(Keystrokes: new KeystrokeOverlaySettings(KeystrokeMode.Shortcuts));
        var (result, options) = await RecordClickAndShortcut(overlays, TestContext.Current.CancellationToken);

        result.MetadataPath.Should().NotBeNull();
        var sync = Measure(result.Path, RecordingMetadata.Load(result.MetadataPath!), options);
        Report($"Live sync: {sync}");
        Check(sync);
    }

    /// <summary>
    /// Click sounds land in the sound where the click was (their own track when it is the only sound), and the
    /// pointer drawn by the app (twice its size) is in the video where the pointer was.
    /// </summary>
    [Fact(Timeout = 90_000)]
    public async Task Click_sounds_land_on_the_click_and_the_app_draws_a_bigger_pointer()
    {
        var overlays = new OverlaySettings(
            Clicks: new ClickOverlaySettings(Sound: true),
            Pointer: new PointerOverlaySettings(Scale: 2),
            Keystrokes: new KeystrokeOverlaySettings(KeystrokeMode.Shortcuts));
        var (result, options) = await RecordClickAndShortcut(overlays, TestContext.Current.CancellationToken, includeCursor: true);

        var metadata = RecordingMetadata.Load(result.MetadataPath!);
        metadata.CursorInVideo.Should().BeTrue("the app drew it into the video");
        metadata.Cursors.Should().NotBeEmpty();
        var click = metadata.Clicks.Single();
        var streams = TestFfmpeg.Streams(result.Path);
        streams.Where(s => s.Type == "audio").Should().ContainSingle("the click sounds are the only sound");
        var onsets = TestFfmpeg.Onsets(TestFfmpeg.DecodeSound(result.Path), 0.05f);
        onsets.Should().ContainSingle().Which.Should().BeApproximately(click.T, 0.01, "the tick is placed at the click's time");
        Report($"Click sound: tick at {onsets[0]:0.0000} s, click at {click.T:0.0000} s");

        // Half a second after the click the pointer stands where it clicked: a white arrow with a black edge, twice the size.
        var white = 0;
        var black = 0;
        using (var reader = new MediaReader(result.Path))
        {
            reader.ReadVideo((time, width, height, bgra) =>
            {
                if (Math.Abs(time - (click.T + 0.6)) < 0.5 / Fps)
                {
                    for (var y = ClickAt.Y; y < ClickAt.Y + 50; y++)
                    {
                        for (var x = ClickAt.X; x < ClickAt.X + 35; x++)
                        {
                            var (b, g, r) = Pixel(bgra, width, x, y);
                            white += r > 220 && g > 220 && b > 220 ? 1 : 0;
                            black += r < 40 && g < 40 && b < 40 ? 1 : 0;
                        }
                    }
                }
            });
        }

        Report($"Bigger pointer: {white} white and {black} black pixels below and right of the click");
        white.Should().BeGreaterThan(100, "an arrow twice its size is filled white");
        black.Should().BeGreaterThan(15, "with a black edge (thin, and softened by the encoder)");
    }

    /// <summary>Records a test window that turns blue on a click and green on a key, clicking it and pressing Ctrl+S.</summary>
    internal async Task<(RecordingResult Result, RecordingOptions Options)> RecordClickAndShortcut(OverlaySettings overlays, CancellationToken ct, bool includeCursor = false)
    {
        InteractiveDesktop.Require();
        TestFfmpeg.Require();
        var monitor = new Win32MonitorService(NullLogger<Win32MonitorService>.Instance).GetMonitors().First(m => m.IsPrimary);
        var area = new PixelRect(monitor.Bounds.X + 100, monitor.Bounds.Y + 100, 320, 240);
        var recorder = new ScreenRecorder(TestFfmpeg.Encoders, _store, _interop, new FakeSound(), _loggers);
        var options = new RecordingOptions(
            monitor, area, Fps, QualityPreset.High, includeCursor, EncoderPresets.Software, Path.Combine(_folder, "overlays.mp4"), Overlays: overlays);
        RecordingResult? result = null;
        await StaDispatcher.RunAsync(async () =>
        {
            var scale = monitor.Scale.ScaleX;
            var window = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Topmost = true,
                Left = (area.X - 20) / scale,
                Top = (area.Y - 20) / scale,
                Width = 400 / scale,
                Height = 320 / scale,
                Background = Brushes.Black,
            };
            window.MouseLeftButtonDown += (_, _) => window.Background = Brushes.Blue;
            window.KeyDown += (_, _) => window.Background = new SolidColorBrush(Color.FromRgb(0, 255, 0));
            window.Show();
            try
            {
                await Task.Delay(300, ct);
                await recorder.StartAsync(options, ct);
                await Until(recorder, 0.8, ct);
                Assert.SkipUnless(TestInput.Click(area.X + ClickAt.X, area.Y + ClickAt.Y), "SendInput was blocked.");
                await Until(recorder, 1.6, ct);
                Assert.SkipUnless(window.IsActive, "The test window didn't get the focus, so the keys would go elsewhere.");
                TestInput.Keys([0x53], 0xA2); // Ctrl+S
                await Until(recorder, 2.6, ct);
                result = await recorder.StopAsync(ct);
            }
            finally
            {
                window.Close();
            }
        });

        return (result!, options);
    }

    /// <summary>Finds the first frame with the ring, the caption and each colour of the window, and the frames of the hooks' times.</summary>
    internal static SyncResult Measure(string video, RecordingMetadata metadata, RecordingOptions options)
    {
        var click = metadata.Clicks.Should().ContainSingle().Subject;
        var key = metadata.Keys.Should().ContainSingle().Subject;
        key.Text.Should().Be("Ctrl + S");
        var ring = -1;
        var blue = -1;
        var caption = -1;
        var green = -1;
        var index = 0;
        using (var reader = new MediaReader(video))
        {
            reader.ReadVideo((time, width, height, bgra) =>
            {
                var frame = (int)Math.Round(time * Fps);
                var probe = Pixel(bgra, width, 250, 200);
                if (blue < 0 && probe.B > 180 && probe.R < 60 && probe.G < 60)
                {
                    blue = frame;
                }

                if (green < 0 && probe.G > 180 && probe.R < 60 && probe.B < 60)
                {
                    green = frame;
                }

                if (ring < 0 && HasRing(bgra, width, height))
                {
                    ring = frame;
                }

                if (caption < 0 && WhitePixels(bgra, width, height - 90, height - 5) > 10)
                {
                    caption = frame;
                }

                index++;
            });
        }

        return new SyncResult(ring, blue, FrameOf(click.T), caption, green, FrameOf(key.T));
    }

    /// <summary>Within a frame: drawn from the frame its time falls on (the first at or after it), never after the window reacts.</summary>
    internal static void Check(SyncResult sync)
    {
        sync.Ring.Should().BeInRange(sync.ClickFrame, sync.ClickFrame + 1, "the ring shows from the frame of the click");
        sync.Ring.Should().BeLessThanOrEqualTo(sync.WindowClicked + 1, "and isn't later than the window's own reaction");
        sync.Caption.Should().BeInRange(sync.KeyFrame, sync.KeyFrame + 1, "the caption shows from the frame of the key");
        sync.Caption.Should().BeLessThanOrEqualTo(sync.WindowKeyed + 1);
    }

    /// <summary>Notes a measurement in <c>%TEMP%\cashgear-tests\overlay-sync.txt</c> (for TESTING.md) and the test output.</summary>
    internal static void Report(string line)
    {
        TestContext.Current.TestOutputHelper?.WriteLine(line);
        var file = Path.Combine(Path.GetTempPath(), "cashgear-tests", "overlay-sync.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
    }

    /// <summary>The first frame shown at or after <paramref name="seconds"/>.</summary>
    private static int FrameOf(double seconds) => (int)Math.Ceiling((seconds * Fps) - 1e-6);

    private static (byte B, byte G, byte R) Pixel(ReadOnlySpan<byte> bgra, int width, int x, int y)
    {
        var i = ((y * width) + x) * 4;
        return (bgra[i], bgra[i + 1], bgra[i + 2]);
    }

    /// <summary>The left button's yellow anywhere within 45 pixels of the click.</summary>
    private static bool HasRing(ReadOnlySpan<byte> bgra, int width, int height)
    {
        for (var y = Math.Max(0, ClickAt.Y - 45); y < Math.Min(height, ClickAt.Y + 45); y++)
        {
            for (var x = Math.Max(0, ClickAt.X - 45); x < Math.Min(width, ClickAt.X + 45); x++)
            {
                var (b, g, r) = Pixel(bgra, width, x, y);
                if (r > 180 && g > 120 && b < 90)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int WhitePixels(ReadOnlySpan<byte> bgra, int width, int top, int bottom)
    {
        var count = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var (b, g, r) = Pixel(bgra, width, x, y);
                count += r > 200 && g > 200 && b > 200 ? 1 : 0;
            }
        }

        return count;
    }

    private static async Task Until(IScreenRecorder recorder, double seconds, CancellationToken ct)
    {
        while (recorder.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(2, ct);
        }
    }
}
