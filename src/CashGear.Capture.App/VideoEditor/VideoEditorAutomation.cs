using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using CashGear.Capture.App.Resources;
using CashGear.Capture.VideoEditor.Model;
using CashGear.Capture.VideoEditor.Timeline;

namespace CashGear.Capture.App.VideoEditor;

/// <summary>
/// The timeline for screen readers: a list of its tracks (groups named after them, in the rows' order from the top),
/// each holding its items, named "name, start – end" as the timeline and the status line write them. An item can be
/// selected, which selects it in the editor. The peer follows the project (<see cref="ProjectChanged"/>) and the
/// selection (<see cref="TimelineChanged"/>): the item selected while the timeline has the focus takes the
/// screen reader's focus, so Tab on the timeline reads it out.
/// </summary>
internal sealed class TimelineAutomationPeer(TimelineControl owner) : FrameworkElementAutomationPeer(owner), ISelectionProvider
{
    private Dictionary<string, TrackAutomationPeer> _tracks = [];
    private string? _selected = owner.ViewModel?.Timeline.SelectedId;

    internal TimelineControl Timeline => (TimelineControl)Owner;

    internal VideoEditorViewModel? ViewModel => Timeline.ViewModel;

    bool ISelectionProvider.CanSelectMultiple => false;

    bool ISelectionProvider.IsSelectionRequired => false;

    public override object GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Selection ? this : base.GetPattern(patternInterface);

    IRawElementProviderSimple[] ISelectionProvider.GetSelection() =>
        ViewModel?.Timeline.SelectedId is { } id && ConnectedItem(id) is { } item ? [ProviderFromPeer(item)] : [];

    /// <summary>Items were added, removed, moved or renamed: the tree is read again (after the edit settles).</summary>
    internal void ProjectChanged() => InvalidatePeer();

    /// <summary>The selection may have changed: the screen reader hears which item is selected now.</summary>
    internal void TimelineChanged()
    {
        var id = ViewModel?.Timeline.SelectedId;
        if (id == _selected)
        {
            return;
        }

        var before = _selected;
        _selected = id;
        if (!ListenerExists(AutomationEvents.PropertyChanged) && !ListenerExists(AutomationEvents.SelectionItemPatternOnElementSelected)
            && !ListenerExists(AutomationEvents.AutomationFocusChanged))
        {
            return;
        }

        if (before is not null && _tracks.Values.Select(t => t.Existing(before)).FirstOrDefault(p => p is not null) is { } old)
        {
            old.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, true, false);
        }

        if (id is not null && ConnectedItem(id) is { } now)
        {
            now.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, false, true);
            now.RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
            if (Timeline.IsKeyboardFocused)
            {
                now.RaiseAutomationEvent(AutomationEvents.AutomationFocusChanged);
            }
        }
    }

    /// <summary>A rectangle of the timeline (view pixels) on the screen, cut to the rows that show; empty when none of it shows.</summary>
    internal Rect OnScreen(Rect local)
    {
        var shown = new Rect(0, TimelineView.RulerHeight, Timeline.ActualWidth, Math.Max(0, Timeline.ActualHeight - TimelineView.RulerHeight));
        local.Intersect(shown);
        if (local.IsEmpty || local.Width <= 0 || local.Height <= 0 || PresentationSource.FromVisual(Timeline) is null)
        {
            return Rect.Empty;
        }

        return new Rect(Timeline.PointToScreen(local.TopLeft), Timeline.PointToScreen(local.BottomRight));
    }

    /// <summary>The row of track <paramref name="trackId"/>, if it is still there.</summary>
    internal TrackRow? Row(string trackId) => ViewModel is { } vm ? TimelineView.Rows(vm.Project).FirstOrDefault(r => r.Track.Id == trackId) : null;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;

    protected override string GetClassNameCore() => nameof(TimelineControl);

    protected override string GetNameCore() => base.GetNameCore() is { Length: > 0 } name ? name : VideoEditorText.TimelineName;

    protected override string GetHelpTextCore() => base.GetHelpTextCore() is { Length: > 0 } help ? help : VideoEditorText.TimelineHelp;

    protected override List<AutomationPeer>? GetChildrenCore()
    {
        if (ViewModel is not { } vm)
        {
            return null;
        }

        var tracks = new Dictionary<string, TrackAutomationPeer>();
        var children = new List<AutomationPeer>();
        foreach (var row in TimelineView.Rows(vm.Project))
        {
            var peer = _tracks.GetValueOrDefault(row.Track.Id) ?? new TrackAutomationPeer(this, row.Track.Id);
            tracks[row.Track.Id] = peer;
            children.Add(peer);
        }

        _tracks = tracks;
        return children;
    }

    /// <summary>The peer of item <paramref name="id"/>, in the tree (its track's children read again), or null.</summary>
    private ClipAutomationPeer? ConnectedItem(string id)
    {
        if (ViewModel?.Project.Find(id) is not { } found)
        {
            return null;
        }

        var track = Track(found.Track.Id);
        if (track is null)
        {
            ResetChildrenCache();
            track = Track(found.Track.Id);
        }

        if (track is null)
        {
            return null;
        }

        track.ResetChildrenCache();
        return track.GetChildren()?.OfType<ClipAutomationPeer>().FirstOrDefault(c => c.ItemId == id);
    }

    private TrackAutomationPeer? Track(string trackId) => GetChildren()?.OfType<TrackAutomationPeer>().FirstOrDefault(t => t.TrackId == trackId);
}

/// <summary>A track on the timeline, for screen readers: a group named after the track, holding its items.</summary>
internal sealed class TrackAutomationPeer(TimelineAutomationPeer timeline, string trackId) : AutomationPeer
{
    private Dictionary<string, ClipAutomationPeer> _items = [];

    public string TrackId => trackId;

    /// <summary>The peer of item <paramref name="id"/> if this track gave it out.</summary>
    internal ClipAutomationPeer? Existing(string id) => _items.GetValueOrDefault(id);

    public override object? GetPattern(PatternInterface patternInterface) => null;

    protected override List<AutomationPeer>? GetChildrenCore()
    {
        if (timeline.ViewModel?.Project.Track(trackId) is not { } track)
        {
            return null;
        }

        var items = new Dictionary<string, ClipAutomationPeer>();
        var children = new List<AutomationPeer>();
        foreach (var item in track.Items.OrderBy(i => i.Start))
        {
            var peer = _items.GetValueOrDefault(item.Id) ?? new ClipAutomationPeer(timeline, item.Id);
            items[item.Id] = peer;
            children.Add(peer);
        }

        _items = items;
        return children;
    }

    protected override string GetNameCore() => timeline.ViewModel?.Project.Track(trackId)?.Name ?? "";

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Group;

    protected override string GetAutomationIdCore() => trackId;

    protected override string GetClassNameCore() => nameof(Track);

    protected override Rect GetBoundingRectangleCore() =>
        timeline.Row(trackId) is { } row
            ? timeline.OnScreen(new Rect(0, timeline.ViewModel!.View.RowTop(row), timeline.Timeline.ActualWidth, row.Height))
            : Rect.Empty;

    protected override bool IsOffscreenCore() => GetBoundingRectangleCore().IsEmpty;

    protected override Point GetClickablePointCore() => new(double.NaN, double.NaN);

    protected override string GetAcceleratorKeyCore() => "";

    protected override string GetAccessKeyCore() => "";

    protected override string GetHelpTextCore() => "";

    protected override string GetItemStatusCore() => "";

    protected override string GetItemTypeCore() => "";

    protected override AutomationPeer? GetLabeledByCore() => null;

    protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.Horizontal;

    protected override bool HasKeyboardFocusCore() => false;

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    protected override bool IsEnabledCore() => true;

    protected override bool IsKeyboardFocusableCore() => false;

    protected override bool IsPasswordCore() => false;

    protected override bool IsRequiredForFormCore() => false;

    protected override void SetFocusCore()
    {
    }
}

/// <summary>An item on the timeline (a clip, an annotation, a caption…), for screen readers: a list item that can be selected.</summary>
internal sealed class ClipAutomationPeer(TimelineAutomationPeer timeline, string itemId) : AutomationPeer, ISelectionItemProvider
{
    public string ItemId => itemId;

    bool ISelectionItemProvider.IsSelected => IsSelected;

    IRawElementProviderSimple ISelectionItemProvider.SelectionContainer => ProviderFromPeer(timeline);

    private bool IsSelected => timeline.ViewModel?.Timeline.SelectedId == itemId;

    public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.SelectionItem ? this : null;

    void ISelectionItemProvider.Select() => Select();

    void ISelectionItemProvider.AddToSelection()
    {
        if (timeline.ViewModel?.Timeline.SelectedId is { } other && other != itemId)
        {
            throw new InvalidOperationException("Only one item can be selected.");
        }

        Select();
    }

    void ISelectionItemProvider.RemoveFromSelection()
    {
        if (IsSelected)
        {
            timeline.ViewModel!.Timeline.Select(null);
        }
    }

    protected override string GetNameCore()
    {
        if (timeline.ViewModel is not { } vm || vm.Project.Find(itemId) is not { Item: var item })
        {
            return "";
        }

        var fps = vm.Project.Canvas.Fps;
        return VideoEditorText.ItemAutomationName(TimelineRenderer.Label(vm.Project, item), TimelineRenderer.Format(item.Start, fps, frames: true), TimelineRenderer.Format(item.End, fps, frames: true));
    }

    protected override string GetItemTypeCore() =>
        timeline.ViewModel is { } vm && vm.Project.Find(itemId) is { Item: var item } ? ItemProperties.Title(vm.Project, item) : "";

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

    protected override string GetAutomationIdCore() => itemId;

    protected override string GetClassNameCore() => nameof(TimelineItem);

    protected override Rect GetBoundingRectangleCore()
    {
        if (timeline.ViewModel is not { } vm || vm.Project.Find(itemId) is not { } found || timeline.Row(found.Track.Id) is not { } row)
        {
            return Rect.Empty;
        }

        var view = vm.View;
        var top = view.RowTop(row) + 3;
        var left = view.X(found.Item.Start);
        return timeline.OnScreen(new Rect(new Point(left, top), new Point(Math.Max(left + 2, view.X(found.Item.End)), top + row.Height - 6)));
    }

    protected override bool IsOffscreenCore() => GetBoundingRectangleCore().IsEmpty;

    protected override Point GetClickablePointCore()
    {
        var bounds = GetBoundingRectangleCore();
        return bounds.IsEmpty ? new Point(double.NaN, double.NaN) : new Point(bounds.Left + (bounds.Width / 2), bounds.Top + (bounds.Height / 2));
    }

    protected override bool HasKeyboardFocusCore() => timeline.Timeline.IsKeyboardFocused && IsSelected;

    protected override bool IsKeyboardFocusableCore() => true;

    protected override void SetFocusCore()
    {
        Select();
        timeline.Timeline.Focus();
    }

    protected override List<AutomationPeer>? GetChildrenCore() => null;

    protected override string GetAcceleratorKeyCore() => "";

    protected override string GetAccessKeyCore() => "";

    protected override string GetHelpTextCore() => "";

    protected override string GetItemStatusCore() => "";

    protected override AutomationPeer? GetLabeledByCore() => null;

    protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    protected override bool IsEnabledCore() => true;

    protected override bool IsPasswordCore() => false;

    protected override bool IsRequiredForFormCore() => false;

    private void Select()
    {
        if (timeline.ViewModel is not { } vm || vm.Project.Find(itemId) is null)
        {
            throw new ElementNotAvailableException();
        }

        vm.SelectItem(itemId);
    }
}

/// <summary>The preview for screen readers: an image called "Preview", with the playhead's time as its status.</summary>
internal sealed class PreviewAutomationPeer(PreviewHost owner) : FrameworkElementAutomationPeer(owner)
{
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Image;

    protected override string GetClassNameCore() => nameof(PreviewHost);

    protected override string GetNameCore() => base.GetNameCore() is { Length: > 0 } name ? name : VideoEditorText.PreviewName;

    protected override string GetHelpTextCore() => base.GetHelpTextCore() is { Length: > 0 } help ? help : VideoEditorText.PreviewHelp;

    protected override string GetItemStatusCore() => ((PreviewHost)Owner).ViewModel?.TimeText ?? "";

    /// <summary>The drawing surface inside is part of the picture, not something to reach.</summary>
    protected override List<AutomationPeer>? GetChildrenCore() => null;
}
