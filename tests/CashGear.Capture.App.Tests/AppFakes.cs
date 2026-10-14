using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Settings;
using CashGear.Capture.App.Tray;
using CashGear.Capture.Core.Hotkeys;
using CashGear.Capture.Core.Settings;

namespace CashGear.Capture.App.Tests;

internal sealed class FakeHotkeyService : IHotkeyService
{
    public event EventHandler<HotkeyAction>? Pressed;

    public HashSet<HotkeyGesture> Taken { get; } = [];

    public Dictionary<HotkeyAction, HotkeyGesture> Registered { get; } = [];

    public int RegisterCalls { get; private set; }

    public bool TryRegister(HotkeyAction action, HotkeyGesture gesture, out string? error)
    {
        RegisterCalls++;
        if (Taken.Contains(gesture))
        {
            error = $"{gesture} is already used by another app or by Windows.";
            return false;
        }

        Registered[action] = gesture;
        error = null;
        return true;
    }

    public void UnregisterAll() => Registered.Clear();

    public void Press(HotkeyAction action) => Pressed?.Invoke(this, action);
}

internal sealed class FakeNotifier : ITrayNotifier
{
    public List<(string Title, string Text, NotificationKind Kind)> Messages { get; } = [];

    public void Notify(string title, string message, NotificationKind kind = NotificationKind.Info) =>
        Messages.Add((title, message, kind));
}

internal sealed class MutableSettings : ISettingsService
{
    public event EventHandler? Changed;

    public AppSettings Current { get; set; } = new();

    public void Update(Func<AppSettings, AppSettings> update)
    {
        Current = update(Current).Normalize();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task SaveAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken ct = default)
    {
        Update(update);
        return Task.CompletedTask;
    }
}

internal sealed class InlineUiDispatcher : IUiDispatcher
{
    public bool CheckAccess() => true;

    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }

    public Task<T> InvokeAsync<T>(Func<T> func) => Task.FromResult(func());

    public Task InvokeAsync(Func<Task> func) => func();

    public Task<T> InvokeAsync<T>(Func<Task<T>> func) => func();
}

internal sealed class FakeStartupRegistration : IStartupRegistration
{
    public bool IsEnabled { get; set; }

    public int Changes { get; private set; }

    public void SetEnabled(bool enabled)
    {
        IsEnabled = enabled;
        Changes++;
    }
}

internal sealed class FakeFolderPicker : IFolderPicker
{
    public string? Result { get; set; }

    public string? LastInitialFolder { get; private set; }

    public string? PickFolder(string? initialFolder)
    {
        LastInitialFolder = initialFolder;
        return Result;
    }
}
