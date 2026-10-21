using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Region;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Capture;
using CashGear.Capture.Core.Display;
using CashGear.Capture.Core.Geometry;
using CashGear.Capture.Core.Imaging;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Selection;
using CashGear.Capture.Core.Settings;
using Microsoft.Extensions.Logging;

namespace CashGear.Capture.App.Tools;

/// <summary>
/// The screen tools: magnifier, colour picker, ruler and crosshair — one of each at a time. UI thread, except
/// <see cref="PickColorAsync"/>, which may be started from anywhere.
/// </summary>
public sealed partial class ScreenTools(
    IUiDispatcher ui,
    ISettingsService settings,
    IAppStateStore state,
    IMonitorService monitors,
    IScreenCaptureService capture,
    RegionSelector selector,
    IOutputService output,
    ITrayNotifier notifier,
    ILogger<ScreenTools> logger,
    Engine.Capture.Wgc.ILiveMonitorCapture? live = null)
{
    private ScreenMagnifier? _magnifier;
    private ColorPickerWindow? _picker;
    private ColorPickerViewModel? _pickerModel;
    private RulerWindow? _ruler;
    private Crosshair? _crosshair;
    private int _picking;

    /// <summary>Raised when the ruler or the crosshair is shown or hidden.</summary>
    public event EventHandler? Changed;

    public bool IsRulerVisible => _ruler is not null;

    public bool IsCrosshairVisible => _crosshair is not null;

    public ScreenMagnifier? Magnifier => _magnifier;

    public RulerWindow? Ruler => _ruler;

    public Crosshair? CrosshairOverlay => _crosshair;

    public ColorPickerViewModel? ColorPicker => _pickerModel;

    internal ColorPickerWindow? ColorPickerWindow => _picker;

    /// <summary>Opens the magnifier (or closes it, when it is open).</summary>
    public void ToggleMagnifier()
    {
        if (_magnifier is { IsOpen: true } open)
        {
            open.Close();
            return;
        }

        _magnifier = ScreenMagnifier.Open(monitors, state, settings.Current.Capture.Hdr == HdrCapture.Auto ? live : null);
        _magnifier.Closed += (_, _) => _magnifier = null;
        LogTool("magnifier", true);
    }

    public void ToggleRuler()
    {
        if (_ruler is not null)
        {
            _ruler.Close();
            return;
        }

        _ruler = new RulerWindow(state, monitors, settings);
        _ruler.Closed += (_, _) =>
        {
            _ruler = null;
            LogTool("ruler", false);
            Changed?.Invoke(this, EventArgs.Empty);
        };
        _ruler.Show();
        _ruler.Activate();
        LogTool("ruler", true);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void ToggleCrosshair()
    {
        if (_crosshair is not null)
        {
            _crosshair.Close();
            _crosshair = null;
            LogTool("crosshair", false);
        }
        else
        {
            _crosshair = Crosshair.Show(monitors);
            LogTool("crosshair", true);
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Opens the colour picker's window (if it isn't) and picks a colour on the frozen screen.</summary>
    public Task PickColorAsync() => ui.InvokeAsync(async () =>
    {
        ShowColorPicker();
        if (Interlocked.Exchange(ref _picking, 1) == 1)
        {
            return; // already picking
        }

        try
        {
            if (await PickAsync() is { } color && _pickerModel is { } model)
            {
                await model.PickedAsync(color.R, color.G, color.B);
            }
        }
        catch (Exception ex) when (ex is CaptureException or InvalidOperationException)
        {
            LogPickFailed(ex);
            notifier.Notify(ToolsText.ColorPicker, ex.Message, NotificationKind.Error);
        }
        finally
        {
            Volatile.Write(ref _picking, 0);
        }
    });

    /// <summary>Closes every tool that is open.</summary>
    public void CloseAll()
    {
        _magnifier?.Close();
        _picker?.Close();
        _ruler?.Close();
        if (_crosshair is not null)
        {
            ToggleCrosshair();
        }
    }

    public void ShowColorPicker()
    {
        if (_picker is not null)
        {
            _picker.Activate();
            return;
        }

        _pickerModel = new ColorPickerViewModel(output, settings, state, ui, PickColorAsync);
        _picker = new ColorPickerWindow(_pickerModel, settings.Current.Appearance.Theme);
        _picker.Closed += (_, _) =>
        {
            _picker = null;
            _pickerModel = null;
        };
        _picker.Show();
    }

    /// <summary>
    /// The colour of the pixel clicked on the frozen screen (without the pointer drawn in), or null when cancelled.
    /// </summary>
    private async Task<(byte R, byte G, byte B)?> PickAsync()
    {
        var displays = monitors.GetMonitors();
        var screen = VirtualScreen.GetBounds(displays.Select(m => m.Bounds));
        using var frozen = await capture.CaptureRegionAsync(screen, includeCursor: false);
        var picked = await selector.SelectAsync(frozen, displays, [], new SelectionOptions(SelectionMode.Point));
        if (picked is null)
        {
            return null;
        }

        var local = VirtualScreen.ToLocal(picked.Bounds.TopLeft, frozen.Metadata.SourceBounds);
        var pixel = frozen.GetRow(local.Y).Slice(local.X * CapturedImage.BytesPerPixel, CapturedImage.BytesPerPixel);
        LogPicked(picked.Bounds.TopLeft, ColorFormats.Format(ColorFormat.Hex, pixel[2], pixel[1], pixel[0]));
        return (pixel[2], pixel[1], pixel[0]);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Screen tool {Tool}: shown {Shown}")]
    private partial void LogTool(string tool, bool shown);

    [LoggerMessage(Level = LogLevel.Information, Message = "Colour picked at {Point}: {Hex}")]
    private partial void LogPicked(PixelPoint point, string hex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The colour could not be picked")]
    private partial void LogPickFailed(Exception ex);
}
