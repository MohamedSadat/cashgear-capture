using CashGear.Capture.Core.Recording;
using Microsoft.Win32;

namespace CashGear.Capture.App.Platform;

/// <summary>The PC's sleep and wake-up from <see cref="SystemEvents.PowerModeChanged"/>.</summary>
internal sealed class WindowsPowerEvents : IPowerEvents, IDisposable
{
    public WindowsPowerEvents() => SystemEvents.PowerModeChanged += OnPowerModeChanged;

    public event EventHandler? Suspending;

    public event EventHandler? Resumed;

    public void Dispose() => SystemEvents.PowerModeChanged -= OnPowerModeChanged;

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                Suspending?.Invoke(this, EventArgs.Empty);
                break;
            case PowerModes.Resume:
                Resumed?.Invoke(this, EventArgs.Empty);
                break;
        }
    }
}
