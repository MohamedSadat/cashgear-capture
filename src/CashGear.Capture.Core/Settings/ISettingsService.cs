namespace CashGear.Capture.Core.Settings;

public interface ISettingsService
{
    /// <summary>The current settings snapshot. Never null.</summary>
    AppSettings Current { get; }

    /// <summary>Writes <see cref="Current"/> to disk.</summary>
    Task SaveAsync(CancellationToken ct = default);

    /// <summary>Applies <paramref name="update"/>, normalizes, persists and raises <see cref="Changed"/> if anything changed.</summary>
    Task UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken ct = default);

    /// <summary>Raised after <see cref="Current"/> changes. May be raised on any thread.</summary>
    event EventHandler? Changed;
}
