using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using CashGear.Capture.App.Hosting;
using CashGear.Capture.App.Platform;
using CashGear.Capture.App.Resources;
using CashGear.Capture.App.VideoEditor;
using CashGear.Capture.Core.Output;
using CashGear.Capture.Core.Settings;
using CashGear.Capture.Outputs.Sharing;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Hosting;

namespace CashGear.Capture.App.Output;

/// <summary>What is going on in the background, for the tray: uploads and exports together.</summary>
/// <param name="Active">How many are in line or running.</param>
/// <param name="Fraction">How far the running ones have got together.</param>
public sealed record BackgroundSummary(int Active, double Fraction)
{
    public static BackgroundSummary Idle { get; } = new(0, 0);

    public bool IsBusy => Active > 0;
}

/// <summary>
/// The background queues as one: started with the app, stopped with it, and summed up for the tray. Uploads go on at the next
/// start; exports don't.
/// </summary>
public sealed class BackgroundWork(UploadQueue uploads, ExportQueue exports, OutputWorkflow workflow, UiReadySignal? ready = null) : IHostedService
{
    public UploadQueue Uploads => uploads;

    public ExportQueue Exports => exports;

    /// <summary>Something changed (on any thread): progress a few times a second.</summary>
    public event EventHandler? Changed
    {
        add
        {
            uploads.Changed += value;
            exports.Changed += value;
        }

        remove
        {
            uploads.Changed -= value;
            exports.Changed -= value;
        }
    }

    public BackgroundSummary Summary
    {
        get
        {
            var sending = uploads.Jobs.Where(j => j.IsActive).ToList();
            var making = exports.Jobs.Where(j => j.IsActive).ToList();
            var count = sending.Count + making.Count;
            if (count == 0)
            {
                return BackgroundSummary.Idle;
            }

            var done = sending.Sum(j => j.Fraction) + making.Sum(j => j.Fraction);
            return new BackgroundSummary(count, done / count);
        }
    }

    /// <summary>An export is running, which closing the app would stop.</summary>
    public bool IsExporting => exports.Jobs.Any(j => j.IsActive);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // The host starts before the windows can exist, and what goes on from last time may finish at once.
        workflow.WatchUploads();
        if (ready is null)
        {
            uploads.Start();
        }
        else
        {
            _ = ready.WhenReady.ContinueWith(_ => uploads.Start(), CancellationToken.None, TaskContinuationOptions.RunContinuationsAsynchronously, TaskScheduler.Default);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        exports.Stop();
        await uploads.StopAsync().ConfigureAwait(false);
    }
}

/// <summary>One line of the window: an upload or an export.</summary>
public sealed partial class BackgroundRow(string id, bool isUpload, BackgroundWorkViewModel owner) : ObservableObject
{
    public string Id => id;

    public bool IsUpload => isUpload;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _where = string.Empty;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private double _fraction;

    /// <summary>It is waiting: the bar shows nothing to go by.</summary>
    [ObservableProperty]
    private bool _isUnknown;

    /// <summary>It can be stopped.</summary>
    [ObservableProperty]
    private bool _canCancel;

    [ObservableProperty]
    private bool _canRetry;

    /// <summary>It has ended: it can be taken off the list.</summary>
    [ObservableProperty]
    private bool _canRemove;

    [ObservableProperty]
    private bool _failed;

    /// <summary>The upload's link.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLink))]
    private string? _link;

    /// <summary>The export's file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFile))]
    private string? _file;

    public bool HasLink => Link is not null;

    public bool HasFile => File is not null;

    /// <summary>What a screen reader says for the line.</summary>
    public string AccessibleName => SharingText.RowName(Name, Where, Status);

    partial void OnNameChanged(string value) => OnPropertyChanged(nameof(AccessibleName));

    partial void OnWhereChanged(string value) => OnPropertyChanged(nameof(AccessibleName));

    partial void OnStatusChanged(string value) => OnPropertyChanged(nameof(AccessibleName));

    [RelayCommand]
    private void Cancel() => owner.Cancel(this);

    [RelayCommand]
    private void Retry() => owner.Retry(this);

    [RelayCommand]
    private void Remove() => owner.Remove(this);

    [RelayCommand]
    private Task CopyLinkAsync() => owner.CopyLinkAsync(this);

    [RelayCommand]
    private void OpenLink() => owner.OpenLink(this);

    [RelayCommand]
    private void ShowFile() => owner.ShowFile(this);
}

/// <summary>Tray › Uploads and Exports: what is in line, running and ended, with Cancel, Retry and the links.</summary>
public sealed partial class BackgroundWorkViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan RefreshEvery = TimeSpan.FromMilliseconds(250);

    private readonly BackgroundWork _work;
    private readonly IUiDispatcher _ui;
    private readonly IOutputService _output;
    private readonly IShell _shell;
    private int _waiting;

    public BackgroundWorkViewModel(BackgroundWork work, IUiDispatcher ui, IOutputService output, IShell shell)
    {
        _work = work;
        _ui = ui;
        _output = output;
        _shell = shell;
        _work.Changed += OnChanged;
        Refresh();
    }

    public ObservableCollection<BackgroundRow> Rows { get; } = [];

    /// <summary>There is nothing on the list.</summary>
    public bool IsEmpty => Rows.Count == 0;

    public string SummaryText => _work.Summary is { IsBusy: true } summary
        ? SharingText.GoingOn(summary.Active, Percent(summary.Fraction))
        : SharingText.NothingGoingOn;

    public void Dispose() => _work.Changed -= OnChanged;

    /// <summary>The list as the queues have it now. UI thread.</summary>
    public void Refresh()
    {
        var uploads = _work.Uploads.Jobs;
        var exports = _work.Exports.Jobs;
        var keep = uploads.Select(u => u.Id).Concat(exports.Select(e => e.Id)).ToHashSet();
        foreach (var gone in Rows.Where(r => !keep.Contains(r.Id)).ToList())
        {
            Rows.Remove(gone);
        }

        foreach (var job in uploads)
        {
            var row = Row(job.Id, true);
            row.Name = job.FileName;
            row.Where = job.DestinationName;
            row.Fraction = job.Fraction;
            row.IsUnknown = job.State == UploadState.Waiting || (job.State == UploadState.Sending && job.Sent == 0);
            row.CanCancel = job.IsActive;
            row.CanRetry = job.State is UploadState.Failed or UploadState.Cancelled;
            row.CanRemove = !job.IsActive;
            row.Failed = job.State == UploadState.Failed;
            row.Link = job.Link;
            row.Status = job.State switch
            {
                UploadState.Waiting => SharingText.StateWaiting,
                UploadState.Sending => SharingText.StateSending(Percent(job.Fraction), Size(job.Sent), Size(job.Total)),
                UploadState.Retrying => SharingText.StateRetrying(Percent(job.Fraction)),
                UploadState.Cancelled => SharingText.StateCancelled,
                UploadState.Done => job.Link is null ? SharingText.StateDone : SharingText.StateDoneLink(job.Link),
                _ => job.Message ?? SharingText.StateFailed,
            };
        }

        foreach (var job in exports)
        {
            var row = Row(job.Id, false);
            row.Name = job.Name;
            row.Where = job.Preset;
            row.Fraction = job.Fraction;
            row.IsUnknown = job.State == ExportJobState.Waiting;
            row.CanCancel = job.IsActive;
            row.CanRetry = job.State is ExportJobState.Failed or ExportJobState.Cancelled;
            row.CanRemove = !job.IsActive;
            row.Failed = job.State == ExportJobState.Failed;
            row.File = job.State == ExportJobState.Done ? job.Path : null;
            row.Status = job.State switch
            {
                ExportJobState.Waiting => SharingText.StateWaiting,
                ExportJobState.Running => SharingText.StateExporting(Percent(job.Fraction)),
                ExportJobState.Cancelled => SharingText.StateCancelled,
                ExportJobState.Done => job.Message ?? SharingText.StateDone,
                _ => job.Message ?? SharingText.StateFailed,
            };
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SummaryText));
    }

    internal void Cancel(BackgroundRow row)
    {
        if (row.IsUpload)
        {
            _work.Uploads.Cancel(row.Id);
        }
        else
        {
            _work.Exports.Cancel(row.Id);
        }
    }

    internal void Retry(BackgroundRow row)
    {
        if (row.IsUpload)
        {
            _work.Uploads.Retry(row.Id);
        }
        else
        {
            _work.Exports.Retry(row.Id);
        }
    }

    internal void Remove(BackgroundRow row)
    {
        if (row.IsUpload)
        {
            _work.Uploads.Remove(row.Id);
        }
        else
        {
            _work.Exports.Remove(row.Id);
        }

        Refresh();
    }

    internal Task CopyLinkAsync(BackgroundRow row) => row.Link is { } link ? _output.CopyTextToClipboardAsync(link) : Task.CompletedTask;

    internal void OpenLink(BackgroundRow row)
    {
        if (row.Link is { } link)
        {
            _shell.OpenLink(link);
        }
    }

    internal void ShowFile(BackgroundRow row)
    {
        if (row.File is { } file)
        {
            _shell.ShowInFolder(file);
        }
    }

    [RelayCommand]
    private void ClearEnded()
    {
        _work.Uploads.ClearEnded();
        _work.Exports.ClearEnded();
        Refresh();
    }

    private BackgroundRow Row(string id, bool isUpload)
    {
        if (Rows.FirstOrDefault(r => r.Id == id) is { } row)
        {
            return row;
        }

        row = new BackgroundRow(id, isUpload, this);
        Rows.Add(row);
        return row;
    }

    /// <summary>Progress comes fast and from anywhere: the list follows a few times a second.</summary>
    private void OnChanged(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _waiting, 1) == 0)
        {
            _ = Task.Delay(RefreshEvery).ContinueWith(_ =>
            {
                Interlocked.Exchange(ref _waiting, 0);
                return _ui.InvokeAsync(Refresh);
            }, TaskScheduler.Default);
        }
    }

    private static string Percent(double fraction) => string.Create(CultureInfo.CurrentCulture, $"{fraction:P0}");

    private static string Size(long bytes) => CommonText.Megabytes(bytes / 1024.0 / 1024.0);
}

/// <summary>Opens the window, or brings the open one to the front. UI thread only.</summary>
public sealed class BackgroundWorkWindowService(BackgroundWork work, IUiDispatcher ui, IOutputService output, IShell shell, ISettingsService settings)
{
    private BackgroundWorkWindow? _window;

    public void Show()
    {
        if (_window is { } open)
        {
            open.Activate();
            return;
        }

        var viewModel = new BackgroundWorkViewModel(work, ui, output, shell);
        var window = new BackgroundWorkWindow(viewModel, settings.Current.Appearance.Theme);
        window.Closed += (_, _) =>
        {
            viewModel.Dispose();
            _window = null;
        };
        _window = window;
        window.Show();
        window.Activate();
    }
}
