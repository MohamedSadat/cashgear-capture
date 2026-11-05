using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media.Imaging;
using CashGear.Capture.App.Resources;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Export;
using CashGear.Capture.VideoEditor.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp;
using SkiaSharp.Views.WPF;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>A title to draw over the frame, or none.</summary>
public sealed record ThumbnailTitle(TitleTemplate? Template, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A text of the title.</summary>
public sealed partial class ThumbnailField(TemplateField source, Action changed) : ObservableObject
{
    public string Key => source.Key;

    public string Label => source.Label;

    [ObservableProperty]
    private string _text = source.Default;

    partial void OnTextChanged(string value) => changed();
}

/// <summary>
/// Pick a frame, add a title from the templates, save it as a PNG or use it for the upload. The picture is drawn by the
/// compositor the export uses, off the UI thread, and only the newest choice counts.
/// </summary>
public sealed partial class ThumbnailViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(120);

    private readonly VideoProject _project;
    private readonly ThumbnailMaker _maker;
    private readonly IVideoEditorDialogs? _dialogs;
    private readonly string _suggestedName;
    private readonly string _workFolder;
    private readonly string? _assetsFolder;
    private CancellationTokenSource? _render;
    private Task _rendered = Task.CompletedTask;
    private SKBitmap? _picture;

    public ThumbnailViewModel(VideoProject project, double time, ThumbnailMaker maker, IEnumerable<TitleTemplate> templates, string? assetsFolder, IVideoEditorDialogs? dialogs, string suggestedName, string workFolder, bool forUpload)
    {
        _project = project;
        _maker = maker;
        _assetsFolder = assetsFolder;
        _dialogs = dialogs;
        _suggestedName = suggestedName;
        _workFolder = workFolder;
        ForUpload = forUpload;
        Duration = Math.Max(0, project.Duration);
        _time = Math.Clamp(time, 0, Duration);
        Titles = [new ThumbnailTitle(null, VideoEditorText.ThumbnailNoTitle), .. templates.Where(t => t.Kind != TemplateKind.Callout).Select(t => new ThumbnailTitle(t, t.Name))];
        _selectedTitle = Titles[0];
        RenderSoon();
    }

    public double Duration { get; }

    /// <summary>A step of the slider: a frame.</summary>
    public double FrameStep => 1.0 / _project.Canvas.Fps;

    /// <summary>Opened from the export window: the picture can go with the upload.</summary>
    public bool ForUpload { get; }

    public IReadOnlyList<ThumbnailTitle> Titles { get; }

    public ObservableCollection<ThumbnailField> Fields { get; } = [];

    public bool HasFields => Fields.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    private double _time;

    [ObservableProperty]
    private ThumbnailTitle _selectedTitle;

    /// <summary>The picture as it is now.</summary>
    [ObservableProperty]
    private BitmapSource? _preview;

    [ObservableProperty]
    private string _status = string.Empty;

    [ObservableProperty]
    private bool _failed;

    /// <summary>The file chosen for the upload, once there is one.</summary>
    public string? Chosen { get; private set; }

    public string TimeText => FormatTime(Time);

    /// <summary>Raised when the window should close.</summary>
    public event EventHandler? CloseRequested;

    partial void OnTimeChanged(double value) => RenderSoon();

    partial void OnSelectedTitleChanged(ThumbnailTitle value)
    {
        Fields.Clear();
        foreach (var field in value.Template?.Fields ?? [])
        {
            Fields.Add(new ThumbnailField(field, RenderSoon));
        }

        OnPropertyChanged(nameof(HasFields));
        RenderSoon();
    }

    /// <summary>Done when the newest picture has been drawn (tests).</summary>
    public Task WhenDrawnAsync() => _rendered;

    [RelayCommand]
    private void SaveAs()
    {
        if (_picture is not { } picture || _dialogs is null)
        {
            return;
        }

        var path = _dialogs.AskSavePath(VideoEditorText.ThumbnailSaveTitle, VideoEditorText.ThumbnailFilter, _suggestedName + ".png", null);
        if (path is null)
        {
            return;
        }

        try
        {
            ThumbnailMaker.SavePng(picture, path);
            Status = VideoEditorText.ThumbnailSaved(Path.GetFileName(path));
            Failed = false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = e.Message;
            Failed = true;
        }
    }

    [RelayCommand]
    private void UseForUpload()
    {
        if (_picture is not { } picture)
        {
            return;
        }

        try
        {
            Chosen = ThumbnailMaker.SaveForYouTube(picture, Path.Combine(_workFolder, Guid.NewGuid().ToString("N"), _suggestedName));
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Status = e.Message;
            Failed = true;
        }
    }

    [RelayCommand]
    private void Close() => CloseRequested?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _render?.Cancel();
        _render?.Dispose();
        _render = null;
        _picture?.Dispose();
        _picture = null;
    }

    private void RenderSoon()
    {
        _render?.Cancel();
        _render?.Dispose();
        _render = new CancellationTokenSource();
        _rendered = RenderAsync(_render.Token);
    }

    private async Task RenderAsync(CancellationToken ct)
    {
        var values = Fields.ToDictionary(f => f.Key, f => f.Text);
        var request = new ThumbnailRequest(_project, Time, SelectedTitle.Template, values, _assetsFolder);
        try
        {
            await Task.Delay(Settle, ct);
            var picture = await Task.Run(() => _maker.Render(request), ct);
            if (ct.IsCancellationRequested)
            {
                picture.Dispose();
                return;
            }

            _picture?.Dispose();
            _picture = picture;
            Preview = picture.ToWriteableBitmap();
            Status = string.Empty;
            Failed = false;
        }
        catch (OperationCanceledException)
        {
            // a newer choice took over
        }
        catch (ExportException e)
        {
            Status = e.Message;
            Failed = true;
        }
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? string.Create(CultureInfo.CurrentCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds / 10:00}")
            : string.Create(CultureInfo.CurrentCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}.{time.Milliseconds / 10:00}");
    }
}
