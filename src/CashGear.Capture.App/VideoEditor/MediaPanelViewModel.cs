using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Media;
using System.Windows.Threading;
using CashGear.Capture.App.Editor;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Media;
using CashGear.Capture.VideoEditor.Editing;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Rendering;
using CashGear.Capture.VideoEditor.Timeline;
using SkiaSharp;
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

/// <summary>A row of the templates' library: built in, or saved in the library.</summary>
public sealed record TemplateRow(TitleTemplate Template, bool IsSaved)
{
    public string Name => Template.Name;

    /// <summary>What a screen reader says for the row.</summary>
    public string AccessibleName => VideoEditorText.TemplateItemName(Template.Name, IsSaved ? VideoEditorText.TemplateSaved : VideoEditorText.TemplateBuiltIn);

    public override string ToString() => Name;
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
        RefreshTemplates();
        _owner.Previews.Changed += OnPreviewsChanged;
    }

    /// <summary>The Titles tab's place among the tabs.</summary>
    public const int TemplatesTabIndex = 2;

    /// <summary>The tab shown.</summary>
    [ObservableProperty]
    private int _shownTab;

    /// <summary>The templates to drag onto the timeline: those that come with the app, then the saved ones.</summary>
    public ObservableCollection<TemplateRow> Templates { get; } = [];

    /// <summary>The project's brand kit, as settings.</summary>
    public ObservableCollection<EditParameter> BrandProperties { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(InsertTemplateCommand))]
    [NotifyCanExecuteChangedFor(nameof(DeleteTemplateCommand))]
    private TemplateRow? _selectedTemplate;

    private BrandKit? _brandShown;

    /// <summary>The library's list, made again after saving or importing.</summary>
    internal void RefreshTemplates()
    {
        var chosen = SelectedTemplate?.Template.Id;
        Templates.Clear();
        foreach (var template in BuiltInTemplates.All)
        {
            Templates.Add(new TemplateRow(template, false));
        }

        foreach (var template in _owner.Library.Templates())
        {
            Templates.Add(new TemplateRow(template, true));
        }

        SelectedTemplate = Templates.FirstOrDefault(t => t.Template.Id == chosen);
    }

    /// <summary>The brand's settings, made again when something else changed the kit (undo).</summary>
    private void RefreshBrand()
    {
        var kit = _owner.Project.Brand ?? BrandKit.Default;
        if (BrandProperties.Count > 0 && ReferenceEquals(_brandShown, kit))
        {
            return;
        }

        _brandShown = kit;
        BrandProperties.Clear();
        void Colour(string label, string value, [Localizable(false)] string key, Func<BrandKit, string, BrandKit> set)
        {
            var parameter = new ColorParameter(label, FrameCompositor.Color(value, SKColors.Black));
            parameter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == "Value")
                {
                    var swatch = parameter.Value.Value;
                    _keepBrand = true;
                    _owner.ChangeBrand(key, b => set(b, $"#{swatch.Red:X2}{swatch.Green:X2}{swatch.Blue:X2}"));
                    _keepBrand = false;
                }
            };
            BrandProperties.Add(parameter);
        }

        void Font(string label, string value, [Localizable(false)] string key, Func<BrandKit, string, BrandKit> set)
        {
            var parameter = new FontParameter(label, ItemProperties.Fonts, value);
            parameter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == "Value")
                {
                    _keepBrand = true;
                    _owner.ChangeBrand(key, b => set(b, parameter.Value));
                    _keepBrand = false;
                }
            };
            BrandProperties.Add(parameter);
        }

        void File(string label, string? assetId, [Localizable(false)] string key, Func<BrandKit, string, BrandKit> set)
        {
            var parameter = new FileParameter(label, assetId is null ? null : _owner.Project.Asset(assetId)?.Path, _owner.PickBrandPath);
            parameter.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == "Path")
                {
                    _owner.PickBrandFile(parameter.Path, key, set);
                }
            };
            BrandProperties.Add(parameter);
        }

        Colour(VideoEditorText.BrandPrimary, kit.Primary, "primary", (b, c) => b with { Primary = c });
        Colour(VideoEditorText.BrandSecondary, kit.Secondary, "secondary", (b, c) => b with { Secondary = c });
        Colour(VideoEditorText.BrandAccent, kit.Accent, "accent", (b, c) => b with { Accent = c });
        Colour(VideoEditorText.BrandText, kit.Text, "text", (b, c) => b with { Text = c });
        Font(VideoEditorText.BrandHeadingFont, kit.HeadingFont, "heading", (b, f) => b with { HeadingFont = f });
        Font(VideoEditorText.BrandBodyFont, kit.BodyFont, "body", (b, f) => b with { BodyFont = f });
        File(VideoEditorText.BrandLogo, kit.LogoAssetId, "logo", (b, id) => b with { LogoAssetId = id });
        File(VideoEditorText.BrandIntro, kit.IntroAssetId, "intro", (b, id) => b with { IntroAssetId = id });
        File(VideoEditorText.BrandOutro, kit.OutroAssetId, "outro", (b, id) => b with { OutroAssetId = id });
        var looks = _owner.LooksToChoose();
        if (looks.Count > 0)
        {
            var look = new ChoiceParameter(VideoEditorText.BrandLook, [.. looks.Select(l => new Choice(l.Id, l.Name))], kit.Look ?? looks[0].Id);
            look.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == "Selected")
                {
                    _keepBrand = true;
                    _owner.ChangeBrand("look", b => b with { Look = look.As<string>() });
                    _keepBrand = false;
                }
            };
            BrandProperties.Add(look);
            BrandProperties.Add(new ActionParameter(VideoEditorText.BrandUseLook, new RelayCommand(_owner.UseBrandLook)));
        }

        BrandProperties.Add(new ActionParameter(VideoEditorText.BrandInsertLogo, new RelayCommand(_owner.InsertBrandLogo)));
        BrandProperties.Add(new ActionParameter(VideoEditorText.BrandAddIntro, new RelayCommand(() => _owner.InsertBrandClip(intro: true))));
        BrandProperties.Add(new ActionParameter(VideoEditorText.BrandAddOutro, new RelayCommand(() => _owner.InsertBrandClip(intro: false))));
        BrandProperties.Add(new ActionParameter(VideoEditorText.BrandSave, new RelayCommand(_owner.SaveBrandToLibrary)));
        BrandProperties.Add(new ActionParameter(VideoEditorText.BrandUseSaved, new RelayCommand(_owner.UseSavedBrand)));
    }

    /// <summary>A change from the brand's own settings: they stay, so a colour being chosen isn't remade under the pointer.</summary>
    private bool _keepBrand;

    private bool HasTemplate() => SelectedTemplate is not null;

    private bool HasSavedTemplate() => SelectedTemplate is { IsSaved: true };

    /// <summary>Enter on a template: at the playhead.</summary>
    [RelayCommand(CanExecute = nameof(HasTemplate))]
    private void InsertTemplate()
    {
        if (SelectedTemplate is { } row)
        {
            _owner.InsertTemplate(row.Template, _owner.Position);
        }
    }

    [RelayCommand]
    private void SaveTemplate() => _owner.SaveSelectionAsTemplate();

    [RelayCommand(CanExecute = nameof(HasSavedTemplate))]
    private void DeleteTemplate()
    {
        if (SelectedTemplate is { IsSaved: true } row)
        {
            _owner.DeleteSavedTemplate(row.Template);
        }
    }

    [RelayCommand]
    private void ExportPack() => _owner.ExportPack();

    [RelayCommand]
    private void ImportPack() => _owner.ImportPack();

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

        if (_keepBrand)
        {
            _brandShown = project.Brand ?? BrandKit.Default;
        }
        else
        {
            RefreshBrand();
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
