using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Media;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Timeline;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkiaSharp.Views.WPF;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>A row of the media panel: a file of the project, with its thumbnail once there is one.</summary>
public sealed partial class MediaItemViewModel(MediaAsset asset, string details) : ObservableObject
{
    public MediaAsset Asset { get; private set; } = asset;

    public string Name => Asset.Name;

    /// <summary>What it is: its kind, size and length.</summary>
    public string Details { get; private set; } = details;

    /// <summary>What a screen reader says for the row.</summary>
    public string AccessibleName => VideoEditorText.MediaItemName(Name, Details);

    [ObservableProperty]
    private ImageSource? _thumbnail;

    /// <summary>The asset after an edit, with what is said about it.</summary>
    internal void Update(MediaAsset asset, string details)
    {
        Asset = asset;
        Details = details;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>A row of the transitions' library.</summary>
public sealed record TransitionChoice(ClipTransition Transition, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// The project's media: every file in it, added but not necessarily used, to put on the timeline by dragging, or with
/// Enter at the playhead. Adding and removing files are undo steps of the editor's history.
/// </summary>
public sealed partial class MediaPanelViewModel : ObservableObject
{
    private readonly VideoEditorViewModel _owner;
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;

    internal MediaPanelViewModel(VideoEditorViewModel owner)
    {
        _owner = owner;
        Refresh();
        _owner.Previews.Changed += OnPreviewsChanged;
    }

    public ObservableCollection<MediaItemViewModel> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddToTimelineCommand))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private MediaItemViewModel? _selected;

    /// <summary>Whether the panel is shown beside the preview.</summary>
    [ObservableProperty]
    private bool _isShown;

    public bool IsEmpty => Items.Count == 0;

    /// <summary>The transitions to drag onto a cut or an edge.</summary>
    public IReadOnlyList<TransitionChoice> Transitions { get; } =
    [
        new(new ClipTransition(TransitionKind.Dissolve), VideoEditorText.TransitionDissolve),
        new(new ClipTransition(TransitionKind.DipToColor, Color: ClipTransition.DefaultColor), VideoEditorText.TransitionDip),
        new(new ClipTransition(TransitionKind.Wipe, Direction: TransitionDirection.Left), VideoEditorText.TransitionWipeLeft),
        new(new ClipTransition(TransitionKind.Wipe, Direction: TransitionDirection.Right), VideoEditorText.TransitionWipeRight),
        new(new ClipTransition(TransitionKind.Wipe, Direction: TransitionDirection.Up), VideoEditorText.TransitionWipeUp),
        new(new ClipTransition(TransitionKind.Wipe, Direction: TransitionDirection.Down), VideoEditorText.TransitionWipeDown),
        new(new ClipTransition(TransitionKind.Slide), VideoEditorText.TransitionSlide),
        new(new ClipTransition(TransitionKind.Push), VideoEditorText.TransitionPush),
        new(new ClipTransition(TransitionKind.Zoom), VideoEditorText.TransitionZoom),
        new(new ClipTransition(TransitionKind.Blur), VideoEditorText.TransitionBlur),
        new(new ClipTransition(TransitionKind.Circle), VideoEditorText.TransitionCircle),
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyTransitionCommand))]
    private TransitionChoice? _selectedTransition;

    /// <summary>The files in the project; each row stays while its asset does.</summary>
    internal void Refresh()
    {
        var project = _owner.Project;
        var assets = project.Assets.Where(a => a.Streams.Count > 0).ToList();
        foreach (var gone in Items.Where(i => assets.All(a => a.Id != i.Asset.Id)).ToList())
        {
            Items.Remove(gone);
        }

        for (var i = 0; i < assets.Count; i++)
        {
            var asset = assets[i];
            var details = Describe(project, asset);
            var row = Items.FirstOrDefault(r => r.Asset.Id == asset.Id);
            if (row is null)
            {
                Items.Insert(Math.Min(i, Items.Count), new MediaItemViewModel(asset, details));
            }
            else if (!ReferenceEquals(row.Asset, asset) || row.Details != details)
            {
                row.Update(asset, details);
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
        if (IsShown)
        {
            LoadThumbnails();
        }
    }

    partial void OnIsShownChanged(bool value)
    {
        if (value)
        {
            LoadThumbnails();
        }
    }

    /// <summary>Stops listening when the window closes.</summary>
    internal void Release() => _owner.Previews.Changed -= OnPreviewsChanged;

    /// <summary>What is said about a file: its kind, size and length, and whether the timeline uses it.</summary>
    internal static string Describe(VideoProject project, MediaAsset asset)
    {
        if (!File.Exists(asset.Path))
        {
            return VideoEditorText.MediaMissingDetails;
        }

        var length = TimelineRenderer.Format(asset.Duration, 30, frames: false);
        var picture = asset.Streams.FirstOrDefault(s => s.Kind == StreamKind.Video);
        var text = asset.IsPicture
            ? asset.Duration > 0
                ? VideoEditorText.MediaAnimationDetails(Number(picture?.Width), Number(picture?.Height), length)
                : VideoEditorText.MediaPictureDetails(Number(picture?.Width), Number(picture?.Height))
            : picture is not null
                ? VideoEditorText.MediaVideoDetails(Number(picture.Width), Number(picture.Height), length)
                : VideoEditorText.MediaSoundDetails(length);
        return MediaEdits.IsUsed(project, asset.Id) ? VideoEditorText.MediaUsedDetails(text) : text;

        static string Number(int? value) => (value ?? 0).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Adds files to the project, without putting them on the timeline.</summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        if (_owner.PickMediaFile(VideoEditorText.ImportMediaTitle, VideoEditorText.AnyMediaFilter) is { } path)
        {
            await _owner.ImportFilesAsync([path], null, null);
        }
    }

    /// <summary>Enter: the chosen file on the timeline at the playhead.</summary>
    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void AddToTimeline()
    {
        if (Selected is { } row)
        {
            _owner.AddMediaAt(row.Asset.Id, _owner.Position, null);
        }
    }

    /// <summary>Delete: the chosen file out of the project, unless the timeline uses it.</summary>
    [RelayCommand(CanExecute = nameof(HasSelected))]
    private void Remove()
    {
        if (Selected is { } row)
        {
            _owner.RemoveMedia(row.Asset.Id);
        }
    }

    [RelayCommand]
    private void AddColour() => _owner.AddSolidAtPlayhead();

    private bool HasSelected() => Selected is not null;

    private bool HasTransition() => SelectedTransition is not null;

    /// <summary>Enter on a transition: on the chosen clip's edge nearest the playhead.</summary>
    [RelayCommand(CanExecute = nameof(HasTransition))]
    private void ApplyTransition()
    {
        if (SelectedTransition is { } row)
        {
            _owner.ApplyTransition(row.Transition, null, null);
        }
    }

    private void OnPreviewsChanged(object? sender, EventArgs e)
    {
        if (IsShown)
        {
            _dispatcher.BeginInvoke(LoadThumbnails);
        }
    }

    /// <summary>The rows' pictures, as the thumbnails come in: the middle one of a video, none for sound.</summary>
    private void LoadThumbnails()
    {
        foreach (var row in Items.Where(r => r.Thumbnail is null))
        {
            var picture = row.Asset.Streams.FirstOrDefault(s => s.Kind == StreamKind.Video);
            if (picture is null || !File.Exists(row.Asset.Path))
            {
                continue;
            }

            var strip = _owner.Previews.ThumbnailsOf(row.Asset, picture.Index);
            if (strip is { Thumbnails.Count: > 0 } && (strip.Complete || strip.Thumbnails.Count > 1))
            {
                var bitmap = strip.Thumbnails[strip.Thumbnails.Count / 2].Image.ToWriteableBitmap();
                bitmap.Freeze();
                row.Thumbnail = bitmap;
            }
        }
    }
}
