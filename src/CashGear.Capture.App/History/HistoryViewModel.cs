using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Core.History;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace CashGear.Capture.App.History;

/// <summary>One capture as the tray menu and the History window show it.</summary>
public sealed class HistoryItem
{
    public HistoryItem(CaptureHistoryEntry entry, ImageSource? thumbnail, DateTimeOffset now)
    {
        Entry = entry;
        Thumbnail = thumbnail;
        When = FormatWhen(entry.CapturedAt.ToLocalTime(), now.ToLocalTime());
    }

    public CaptureHistoryEntry Entry { get; }

    public ImageSource? Thumbnail { get; }

    /// <summary>"Today 10:42", "Yesterday 18:05" or "13 Oct 09:30".</summary>
    public string When { get; }

    public string Description => Entry.Description;

    /// <summary>For the tray menu: when and what.</summary>
    public string Label => $"{When} — {Description}";

    /// <summary>What a screen reader says for it in the History list: when, what, and the file it was saved to.</summary>
    public string SpokenName => Entry.SavedPath is { Length: > 0 } saved
        ? HistoryText.SpokenSaved(When, Description, Path.GetFileName(saved))
        : HistoryText.Spoken(When, Description);

    /// <summary>The list item's name for a screen reader (the History list shows a template, not this text).</summary>
    public override string ToString() => SpokenName;

    /// <remarks>The date and time are in the current culture's formats, the words in the UI language.</remarks>
    public static string FormatWhen(DateTimeOffset at, DateTimeOffset now)
    {
        var days = (now.Date - at.Date).Days;
        return days switch
        {
            0 => HistoryText.TodayAt(at),
            1 => HistoryText.YesterdayAt(at),
            _ => HistoryText.DayAt(at),
        };
    }

    /// <summary>The thumbnail, read fully into memory so the file isn't kept open (null if it can't be read).</summary>
    public static ImageSource? LoadThumbnail(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}

/// <summary>The History window: every capture in the history, newest first, and what can be done with it.</summary>
public sealed partial class HistoryViewModel : ObservableObject, IDisposable
{
    private readonly ICaptureHistory _history;
    private readonly HistoryActions _actions;
    private readonly IUiDispatcher _ui;
    private readonly TimeProvider _time;

    public HistoryViewModel(ICaptureHistory history, HistoryActions actions, IUiDispatcher ui, TimeProvider time)
    {
        _history = history;
        _actions = actions;
        _ui = ui;
        _time = time;
        _history.Changed += OnHistoryChanged;
        Reload();
    }

    /// <summary>Asks for a file to save to (null: cancelled). Set by the window.</summary>
    public Func<CaptureHistoryEntry, string?>? AskSavePath { get; set; }

    /// <summary>Asks whether to clear the whole history. Set by the window.</summary>
    public Func<int, bool>? ConfirmClear { get; set; }

    public ObservableCollection<HistoryItem> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    public string Summary => Items.Count switch
    {
        0 => HistoryText.NoCaptures,
        _ => HistoryText.CaptureCount(Items.Count),
    };

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenCommand), nameof(CopyCommand), nameof(ShowInFolderCommand), nameof(SaveAsCommand), nameof(RemoveCommand))]
    private HistoryItem? _selected;

    private bool HasSelection => Selected is not null;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task OpenAsync() => Selected is { } item ? _actions.OpenAsync(item.Entry) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task CopyAsync() => Selected is { } item ? _actions.CopyAsync(item.Entry) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void ShowInFolder()
    {
        if (Selected is { } item)
        {
            _actions.ShowInFolder(item.Entry);
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task SaveAsAsync() =>
        Selected is { } item && AskSavePath?.Invoke(item.Entry) is { } path ? _actions.SaveAsAsync(item.Entry, path) : Task.CompletedTask;

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private Task RemoveAsync() => Selected is { } item ? _history.RemoveAsync(item.Entry.Id) : Task.CompletedTask;

    [RelayCommand]
    private Task ClearAsync() =>
        Items.Count > 0 && (ConfirmClear?.Invoke(Items.Count) ?? true) ? _history.ClearAsync() : Task.CompletedTask;

    public void Dispose() => _history.Changed -= OnHistoryChanged;

    private void OnHistoryChanged(object? sender, EventArgs e) => _ = _ui.InvokeAsync(Reload);

    /// <summary>Rebuilds the list, keeping the selection (or selecting the neighbour of a removed capture).</summary>
    private void Reload()
    {
        var selectedId = Selected?.Entry.Id;
        var selectedIndex = Selected is null ? -1 : Items.IndexOf(Selected);
        var now = _time.GetLocalNow();
        Items.Clear();
        foreach (var entry in _history.Entries)
        {
            Items.Add(new HistoryItem(entry, HistoryItem.LoadThumbnail(_history.ThumbnailPath(entry)), now));
        }

        Selected = Items.FirstOrDefault(i => i.Entry.Id == selectedId)
            ?? (selectedIndex >= 0 && Items.Count > 0 ? Items[Math.Min(selectedIndex, Items.Count - 1)] : null);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
        ClearCommand.NotifyCanExecuteChanged();
    }
}
