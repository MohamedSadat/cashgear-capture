using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using CashGear.Capture.App.Resources;
using CashGear.Capture.Editor.Model;

namespace CashGear.Capture.App.Editor;

/// <summary>
/// The picture being edited, for screen readers: a custom control whose children are the annotations (bottom to top,
/// the order Tab goes through them), one selectable item each, named by kind and text ("Arrow", "Text: Hello").
/// </summary>
internal sealed class EditorCanvasAutomationPeer(EditorCanvas owner) : FrameworkElementAutomationPeer(owner), ISelectionProvider
{
    /// <summary>Groups the "selected" announcements, so a newer one replaces one not yet spoken.</summary>
    [Localizable(false)]
    private const string SelectionActivity = "CashGear.Editor.Selection";

    /// <summary>One peer per annotation for as long as it exists, so screen readers can tell the same item again.</summary>
    private readonly Dictionary<AnnotationId, AnnotationAutomationPeer> _peers = [];

    internal EditorCanvas Canvas => (EditorCanvas)Owner;

    public bool CanSelectMultiple => false;

    public bool IsSelectionRequired => false;

    public override object GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.Selection ? this : base.GetPattern(patternInterface);

    public IRawElementProviderSimple[] GetSelection() =>
        Canvas.ViewModel?.Session.SelectedId is { } id && PeerFor(id) is { } peer ? [ProviderFromPeer(peer)] : [];

    /// <summary>The annotation's peer (made if needed); null when it isn't in the document.</summary>
    internal AnnotationAutomationPeer? PeerFor(AnnotationId id)
    {
        if (!_peers.ContainsKey(id))
        {
            ResetChildrenCache();
        }

        return _peers.GetValueOrDefault(id);
    }

    /// <summary>
    /// The session's selection changed: the items' IsSelected changes, and with <paramref name="announce"/> (the canvas
    /// has the keyboard focus, so Tab did it) the new one's name is spoken.
    /// </summary>
    internal void RaiseSelectionChanged(AnnotationId? before, AnnotationId? after, bool announce)
    {
        if (before is { } old && _peers.TryGetValue(old, out var deselected))
        {
            deselected.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, true, false);
        }

        if (after is not { } id || PeerFor(id) is not { } selected)
        {
            return;
        }

        selected.RaisePropertyChangedEvent(SelectionItemPatternIdentifiers.IsSelectedProperty, false, true);
        if (ListenerExists(AutomationEvents.SelectionItemPatternOnElementSelected))
        {
            selected.RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
        }

        if (announce)
        {
            RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.MostRecent, selected.GetName(), SelectionActivity);
        }
    }

    protected override string GetClassNameCore() => nameof(EditorCanvas);

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Custom;

    protected override string GetLocalizedControlTypeCore() => EditorText.CanvasControlType;

    protected override string GetNameCore() => base.GetNameCore() is { Length: > 0 } name ? name : EditorText.Picture;

    protected override string GetHelpTextCore() => base.GetHelpTextCore() is { Length: > 0 } help ? help : EditorText.CanvasHelp;

    protected override List<AutomationPeer>? GetChildrenCore()
    {
        var annotations = Canvas.ViewModel?.Document.Annotations;
        if (annotations is not { Count: > 0 })
        {
            _peers.Clear();
            return null;
        }

        var children = new List<AutomationPeer>(annotations.Count);
        foreach (var annotation in annotations)
        {
            if (!_peers.TryGetValue(annotation.Id, out var peer))
            {
                peer = new AnnotationAutomationPeer(this, annotation.Id);
                _peers.Add(annotation.Id, peer);
            }

            children.Add(peer);
        }

        foreach (var gone in _peers.Keys.Where(id => annotations.All(a => a.Id != id)).ToList())
        {
            _peers.Remove(gone);
        }

        return children;
    }
}

/// <summary>One annotation on the canvas: named by kind and text, selectable (selecting it selects it in the editor).</summary>
internal sealed class AnnotationAutomationPeer(EditorCanvasAutomationPeer canvas, AnnotationId id) : AutomationPeer, ISelectionItemProvider
{
    public AnnotationId Id => id;

    public bool IsSelected => canvas.Canvas.ViewModel?.Session.SelectedId == id;

    public IRawElementProviderSimple SelectionContainer => ProviderFromPeer(canvas);

    private Annotation? Annotation => canvas.Canvas.ViewModel?.Document.Find(id);

    public override object? GetPattern(PatternInterface patternInterface) =>
        patternInterface == PatternInterface.SelectionItem ? this : null;

    /// <summary>Selects it in the editor (the selection is single: adding to it selects this one instead).</summary>
    public void Select()
    {
        if (canvas.Canvas.ViewModel is { } viewModel && Annotation is not null)
        {
            viewModel.Session.Select(id);
        }
    }

    public void AddToSelection() => Select();

    public void RemoveFromSelection()
    {
        if (IsSelected)
        {
            canvas.Canvas.ViewModel?.Session.Select(null);
        }
    }

    protected override string GetNameCore() => Annotation?.SpokenName() ?? string.Empty;

    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;

    protected override string GetClassNameCore() => nameof(Annotation);

    protected override string GetAutomationIdCore() => id.Value.ToString(CultureInfo.InvariantCulture);

    protected override Rect GetBoundingRectangleCore() => Annotation is { } annotation ? canvas.Canvas.ImageToScreen(annotation.Bounds) : Rect.Empty;

    protected override bool IsOffscreenCore()
    {
        var bounds = GetBoundingRectangleCore();
        return bounds.IsEmpty || !bounds.IntersectsWith(canvas.Canvas.ScreenBounds);
    }

    protected override Point GetClickablePointCore()
    {
        var bounds = GetBoundingRectangleCore();
        bounds.Intersect(canvas.Canvas.ScreenBounds);
        return bounds.IsEmpty ? new Point(double.NaN, double.NaN) : new Point(bounds.X + (bounds.Width / 2), bounds.Y + (bounds.Height / 2));
    }

    protected override bool HasKeyboardFocusCore() => canvas.Canvas.IsKeyboardFocused && IsSelected;

    protected override bool IsKeyboardFocusableCore() => canvas.Canvas.Focusable;

    /// <summary>Selects it and gives the canvas the focus (the keys then act on it).</summary>
    protected override void SetFocusCore()
    {
        Select();
        canvas.Canvas.Focus();
    }

    protected override bool IsEnabledCore() => canvas.Canvas.IsEnabled;

    protected override bool IsContentElementCore() => true;

    protected override bool IsControlElementCore() => true;

    protected override bool IsPasswordCore() => false;

    protected override bool IsRequiredForFormCore() => false;

    protected override List<AutomationPeer>? GetChildrenCore() => null;

    protected override string GetAcceleratorKeyCore() => string.Empty;

    protected override string GetAccessKeyCore() => string.Empty;

    protected override string GetHelpTextCore() => string.Empty;

    protected override string GetItemStatusCore() => string.Empty;

    protected override string GetItemTypeCore() => string.Empty;

    protected override AutomationPeer? GetLabeledByCore() => null;

    protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;
}
