using System.Windows;
using CashGear.Capture.App.Recording;
using CashGear.Capture.App.Region;
using CashGear.Capture.App.Scrolling;

namespace CashGear.Capture.App.Hosting;

/// <summary>
/// Gets the app's own windows (e.g. Settings) out of the way while a capture is taken. The overlays, the toolbars
/// and the screen tools stay: they are left out of captures anyway.
/// </summary>
public interface IOwnWindows
{
    /// <summary>Hides every visible app window; disposing the result (once is enough; again does nothing) shows them again.</summary>
    Task<IDisposable> HideAsync();
}

internal sealed class WpfOwnWindows(IUiDispatcher ui) : IOwnWindows
{
    public Task<IDisposable> HideAsync() => ui.InvokeAsync<IDisposable>(() =>
    {
        var hidden = Application.Current.Windows
            .OfType<Window>()
            .Where(w => w.IsVisible && w is not (RegionOverlayWindow or ScrollProgressWindow or RecordingToolbarWindow or Toolbar.CaptureToolbarWindow))
            .ToList();
        foreach (var window in hidden)
        {
            window.Hide();
        }

        return new Restore(ui, hidden);
    });

    private sealed class Restore(IUiDispatcher ui, List<Window> windows) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (windows.Count == 0 || Interlocked.Exchange(ref _disposed, 1) == 1)
            {
                return;
            }

            _ = ui.InvokeAsync(() =>
            {
                // Without activation: a capture may just have opened an editor, which should stay in front.
                foreach (var window in windows.Where(w => Application.Current.Windows.OfType<Window>().Contains(w)))
                {
                    var activates = window.ShowActivated;
                    window.ShowActivated = false;
                    window.Show();
                    window.ShowActivated = activates;
                }
            });
        }
    }
}
