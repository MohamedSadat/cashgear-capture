using System.Windows;
using CashGear.Capture.App.Region;

namespace CashGear.Capture.App.Hosting;

/// <summary>Gets the app's own windows (e.g. Settings) out of the way while a capture is taken.</summary>
public interface IOwnWindows
{
    /// <summary>Hides every visible app window; disposing the result shows them again.</summary>
    Task<IDisposable> HideAsync();
}

internal sealed class WpfOwnWindows(IUiDispatcher ui) : IOwnWindows
{
    public Task<IDisposable> HideAsync() => ui.InvokeAsync<IDisposable>(() =>
    {
        var hidden = Application.Current.Windows
            .OfType<Window>()
            .Where(w => w.IsVisible && w is not RegionOverlayWindow)
            .ToList();
        foreach (var window in hidden)
        {
            window.Hide();
        }

        return new Restore(ui, hidden);
    });

    private sealed class Restore(IUiDispatcher ui, List<Window> windows) : IDisposable
    {
        public void Dispose()
        {
            if (windows.Count == 0)
            {
                return;
            }

            _ = ui.InvokeAsync(() =>
            {
                foreach (var window in windows.Where(w => Application.Current.Windows.OfType<Window>().Contains(w)))
                {
                    window.Show();
                }
            });
        }
    }
}
