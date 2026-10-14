using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Hosting;

namespace CashGear.Capture.App.Hosting;

/// <summary>
/// WPF owns the process lifetime (the dispatcher loop in <see cref="Program.Main"/>), so the host must not
/// hook console Ctrl+C / process-exit the way the default ConsoleLifetime does.
/// </summary>
internal sealed class WpfHostLifetime : IHostLifetime
{
    public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Runs work on the WPF UI thread from any thread.</summary>
public interface IUiDispatcher
{
    bool CheckAccess();

    Task InvokeAsync(Action action);

    Task<T> InvokeAsync<T>(Func<T> func);

    /// <summary>Runs an async delegate on the UI thread and completes when the delegate's task completes.</summary>
    Task InvokeAsync(Func<Task> func);
}

internal sealed class WpfUiDispatcher : IUiDispatcher
{
    // Resolved per call: this singleton may first be created off the UI thread.
    private static Dispatcher Dispatcher =>
        Application.Current?.Dispatcher ?? throw new InvalidOperationException("The WPF application is not running.");

    public bool CheckAccess() => Dispatcher.CheckAccess();

    public Task InvokeAsync(Action action) => Dispatcher.InvokeAsync(action).Task;

    public Task<T> InvokeAsync<T>(Func<T> func) => Dispatcher.InvokeAsync(func).Task;

    public Task InvokeAsync(Func<Task> func) => Dispatcher.InvokeAsync(func).Task.Unwrap();
}

/// <summary>Requests an orderly application shutdown.</summary>
public interface IAppLifetime
{
    void Shutdown();
}

internal sealed class WpfAppLifetime(IUiDispatcher ui) : IAppLifetime
{
    public void Shutdown() => _ = ui.InvokeAsync(() => Application.Current.Shutdown());
}

/// <summary>Completes once the tray icon exists, so background services don't drive a UI that isn't there yet.</summary>
internal sealed class UiReadySignal
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WhenReady => _ready.Task;

    public void SetReady() => _ready.TrySetResult();
}
