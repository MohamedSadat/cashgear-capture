using CashGear.Capture.Editor.Document;
using CashGear.Capture.Editor.Imaging;
using CashGear.Capture.Editor.Model;
using CashGear.Capture.Editor.Rendering;
using SkiaSharp;

namespace CashGear.Capture.Editor.Interaction;

/// <summary>
/// The editing logic behind the canvas, free of any UI framework: turns pointer input in image coordinates into
/// annotations and commands, and keeps the selection, the shape being drawn and the pending crop. A drag shows
/// live and becomes one undo step when the button is released.
/// </summary>
public sealed class EditorSession
{
    /// <summary>Drags shorter than this (image pixels) are clicks: they don't create shapes.</summary>
    public const float MinShapeSize = 2;

    private Drag? _drag;
    private EditorTool _tool = EditorTool.Arrow;
    private AnnotationId? _selectedId;

    public EditorSession(EditorDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        Document = document;
        Document.Changed += OnDocumentChanged;
    }

    public EditorDocument Document { get; }

    public EditorTool Tool
    {
        get => _tool;
        set
        {
            if (value == _tool)
            {
                return;
            }

            CancelDrag();
            if (_tool == EditorTool.Crop)
            {
                CropArea = null;
            }

            _tool = value;
            Invalidate();
        }
    }

    /// <summary>The style for the next annotation (the host keeps it per tool).</summary>
    public AnnotationStyle Style { get; set; } = new();

    public AnnotationId? SelectedId => _selectedId;

    public Annotation? Selected => _selectedId is { } id ? Document.Find(id) : null;

    /// <summary>The shape being drawn: shown, but not in the document until the button is released.</summary>
    public Annotation? Draft { get; private set; }

    /// <summary>Text being typed in the host's text box (hidden on the canvas meanwhile).</summary>
    public TextEditRequest? TextEdit { get; private set; }

    /// <summary>The crop rectangle the user is adjusting, in image pixels; applied with <see cref="ApplyCrop"/>.</summary>
    public SKRectI? CropArea { get; private set; }

    public bool IsDragging => _drag is not null;

    /// <summary>Something on the canvas changed that the document doesn't know about (draft, selection, crop).</summary>
    public event EventHandler? Invalidated;

    public event EventHandler? SelectionChanged;

    public event EventHandler<TextEditRequest>? TextEditRequested;

    public void Select(AnnotationId? id)
    {
        if (id == _selectedId)
        {
            return;
        }

        _selectedId = id;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    /// <summary>The topmost annotation at <paramref name="p"/> as drawn (effects are under everything else), if any.</summary>
    public Annotation? HitTest(SKPoint p, float tolerance) =>
        DocumentRenderer.PaintOrder(Document.Annotations)
            .Reverse()
            .FirstOrDefault(a => a.Id != TextEdit?.Existing && a.HitTest(p, tolerance));

    /// <param name="p">Image pixels.</param>
    /// <param name="tolerance">Hit slack in image pixels (a few screen pixels divided by the zoom).</param>
    /// <param name="clickCount">2 for a double-click.</param>
    public void PointerDown(SKPoint p, PointerModifiers modifiers, int clickCount, float tolerance)
    {
        if (_drag is not null || TextEdit is not null)
        {
            return;
        }

        switch (Tool)
        {
            case EditorTool.Select:
                if (SelectedHandleAt(p, tolerance) is { } grip)
                {
                    _drag = new ResizeDrag(Selected!, grip.Kind);
                }
                else if (HitTest(p, tolerance) is { } hit)
                {
                    Select(hit.Id);
                    if (clickCount >= 2 && hit is TextAnnotation text)
                    {
                        BeginTextEdit(text);
                        return;
                    }

                    _drag = new MoveDrag(hit, p);
                }
                else
                {
                    Select(null);
                }

                break;

            case EditorTool.Crop:
                if (CropArea is { } crop && Geometry.HandleAt(Geometry.BoxHandles(crop), p, tolerance) is { } cropGrip)
                {
                    _drag = new CropDrag(p, crop, cropGrip.Kind);
                }
                else if (CropArea is { } current && current.Contains((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y)))
                {
                    _drag = new CropDrag(p, current, null);
                }
                else
                {
                    CropArea = null;
                    _drag = new CropDrag(p, null, null);
                }

                break;

            case EditorTool.Text:
                if (HitTest(p, tolerance) is TextAnnotation existing)
                {
                    Select(existing.Id);
                    BeginTextEdit(existing);
                }
                else
                {
                    Select(null);
                    BeginTextEdit(new TextEditRequest(null, p, string.Empty, Style));
                }

                return;

            case EditorTool.StepNumber:
                if (HitTest(p, tolerance) is StepNumberAnnotation step)
                {
                    Select(step.Id);
                    _drag = new MoveDrag(step, p);
                }
                else
                {
                    // Placed on click; holding the button lets the user drag it into place straight away.
                    var placed = new StepNumberAnnotation(Document.NewId(), Style, p, Document.NextStepNumber);
                    Document.Execute(new AddAnnotationCommand(placed));
                    Select(placed.Id);
                    _drag = new MoveDrag(placed, p);
                }

                break;

            default:
                if (SelectedHandleAt(p, tolerance) is { } handle)
                {
                    _drag = new ResizeDrag(Selected!, handle.Kind); // adjust the shape just drawn
                }
                else
                {
                    Select(null);
                    Draft = CreateDraft(p);
                    _drag = new CreateDrag(p);
                }

                break;
        }

        Invalidate();
    }

    public void PointerMove(SKPoint p, PointerModifiers modifiers)
    {
        var constrain = modifiers.HasFlag(PointerModifiers.Shift);
        switch (_drag)
        {
            case CreateDrag create when Draft is not null:
                Draft = UpdateDraft(Draft, create.Origin, p, constrain);
                Invalidate();
                break;

            case MoveDrag move:
                var dx = p.X - move.Origin.X;
                var dy = p.Y - move.Origin.Y;
                if (constrain)
                {
                    (dx, dy) = MathF.Abs(dx) >= MathF.Abs(dy) ? (dx, 0f) : (0f, dy); // horizontal or vertical only
                }

                Document.Preview(move.Before.Offset(dx, dy));
                break;

            case ResizeDrag resize:
                Document.Preview(resize.Before.WithHandleMoved(resize.Handle, p, constrain));
                break;

            case CropDrag crop:
                CropArea = CropRect(crop, p);
                Invalidate();
                break;
        }
    }

    public void PointerUp(SKPoint p, PointerModifiers modifiers)
    {
        if (_drag is null)
        {
            return;
        }

        PointerMove(p, modifiers);
        var drag = _drag;
        _drag = null;
        switch (drag)
        {
            case CreateDrag when Draft is { } draft:
                Draft = null;
                if (IsBigEnough(draft))
                {
                    Document.Execute(new AddAnnotationCommand(draft));
                    Select(draft.Id);
                }

                break;

            case MoveDrag move:
                Commit(move.Before, "move " + move.Before.Kind.DisplayName());
                break;

            case ResizeDrag resize:
                Commit(resize.Before, "resize " + resize.Before.Kind.DisplayName());
                break;

            case CropDrag when CropArea is { } area && (area.Width < MinShapeSize || area.Height < MinShapeSize):
                CropArea = null;
                break;
        }

        Invalidate();
    }

    /// <summary>The cursor for the pointer at <paramref name="p"/> with nothing pressed (or the drag under way).</summary>
    public EditorCursor CursorAt(SKPoint p, float tolerance)
    {
        switch (_drag)
        {
            case MoveDrag:
                return EditorCursor.Move;
            case ResizeDrag resize:
                return CursorFor(resize.Handle);
            case CropDrag { Before: not null, Handle: null }:
                return EditorCursor.Move;
            case CropDrag { Handle: { } cropHandle }:
                return CursorFor(cropHandle);
            case CreateDrag or CropDrag:
                return EditorCursor.Crosshair;
        }

        switch (Tool)
        {
            case EditorTool.Select:
                return SelectedHandleAt(p, tolerance) is { } grip ? CursorFor(grip.Kind)
                    : HitTest(p, tolerance) is not null ? EditorCursor.Move
                    : EditorCursor.Arrow;
            case EditorTool.Crop:
                if (CropArea is { } crop)
                {
                    if (Geometry.HandleAt(Geometry.BoxHandles(crop), p, tolerance) is { } cropGrip)
                    {
                        return CursorFor(cropGrip.Kind);
                    }

                    if (crop.Contains((int)MathF.Floor(p.X), (int)MathF.Floor(p.Y)))
                    {
                        return EditorCursor.Move;
                    }
                }

                return EditorCursor.Crosshair;
            case EditorTool.Text:
                return EditorCursor.IBeam;
            case EditorTool.StepNumber:
                return HitTest(p, tolerance) is StepNumberAnnotation ? EditorCursor.Move : EditorCursor.Crosshair;
            default:
                return SelectedHandleAt(p, tolerance) is { } handle ? CursorFor(handle.Kind) : EditorCursor.Crosshair;
        }
    }

    /// <summary>Esc: cancels a drag, else the pending crop, else the selection. False if there was nothing to cancel.</summary>
    public bool Cancel()
    {
        if (_drag is not null)
        {
            CancelDrag();
            return true;
        }

        if (CropArea is not null)
        {
            CropArea = null;
            Invalidate();
            return true;
        }

        if (_selectedId is not null)
        {
            Select(null);
            return true;
        }

        return false;
    }

    public void DeleteSelection()
    {
        if (Selected is { } selected && !IsDragging)
        {
            Document.Execute(new RemoveAnnotationCommand(selected));
            Select(null);
        }
    }

    /// <summary>Moves the selection by whole pixels (arrow keys); repeated nudges are one undo step.</summary>
    public void Nudge(float dx, float dy)
    {
        if (Selected is { } selected && !IsDragging)
        {
            Document.Execute(new ReplaceAnnotationCommand(
                selected, selected.Offset(dx, dy), "move " + selected.Kind.DisplayName(), "nudge"));
        }
    }

    /// <summary>
    /// Restyles the selection. Changes with the same <paramref name="mergeKey"/> (one slider drag) are one undo step.
    /// </summary>
    public void ApplyStyle(AnnotationStyle style, string? mergeKey = null)
    {
        ArgumentNullException.ThrowIfNull(style);
        if (Selected is { } selected && selected.Style != style && !IsDragging)
        {
            Document.Execute(new ReplaceAnnotationCommand(
                selected, selected with { Style = style }, "change " + selected.Kind.DisplayName(), mergeKey is null ? null : "style:" + mergeKey));
        }
    }

    public void BringToFront() => Reorder(Document.Annotations.Count - 1, "bring to front");

    public void SendToBack() => Reorder(0, "send to back");

    /// <summary>Ends text typing: adds, changes or (when emptied) deletes the text annotation.</summary>
    public void CommitTextEdit(string text)
    {
        if (TextEdit is not { } edit)
        {
            return;
        }

        TextEdit = null;
        var blank = string.IsNullOrWhiteSpace(text);
        var existing = edit.Existing is { } id ? Document.Find(id) as TextAnnotation : null;
        if (existing is null)
        {
            if (!blank)
            {
                var created = new TextAnnotation(Document.NewId(), edit.Style, edit.Position, text);
                Document.Execute(new AddAnnotationCommand(created));
                Select(created.Id);
            }
        }
        else if (blank)
        {
            Document.Execute(new RemoveAnnotationCommand(existing));
            Select(null);
        }
        else if (existing.Text != text)
        {
            Document.Execute(new ReplaceAnnotationCommand(existing, existing with { Text = text }, "edit text"));
        }

        Invalidate();
    }

    public void CancelTextEdit()
    {
        if (TextEdit is not null)
        {
            TextEdit = null;
            Invalidate();
        }
    }

    public void ApplyCrop()
    {
        if (CropArea is { } area && !IsDragging)
        {
            CropArea = null;
            RunImageEdit("crop", s => ImageOperations.Crop(s, area));
        }
    }

    public void Rotate(bool clockwise) => RunImageEdit(clockwise ? "rotate right" : "rotate left", s => ImageOperations.Rotate(s, clockwise));

    public void ResizeImage(int width, int height) => RunImageEdit("resize image", s => ImageOperations.Resize(s, width, height));

    public void AddBorder(int thickness, SKColor color) => RunImageEdit("add border", s => ImageOperations.AddBorder(s, thickness, color));

    public void AddShadow() => RunImageEdit("add shadow", ImageOperations.AddShadow);

    public void Undo()
    {
        if (!IsDragging && TextEdit is null)
        {
            Document.History.Undo();
        }
    }

    public void Redo()
    {
        if (!IsDragging && TextEdit is null)
        {
            Document.History.Redo();
        }
    }

    private void RunImageEdit(string name, Func<DocumentState, DocumentState> edit)
    {
        if (IsDragging || TextEdit is not null)
        {
            return;
        }

        CropArea = null;
        var before = Document.State;
        Document.Execute(new ImageEditCommand(name, before, edit(before)));
    }

    private void Reorder(int index, string name)
    {
        if (Selected is { } selected && !IsDragging && Document.IndexOf(selected.Id) != index)
        {
            Document.Execute(new ReorderAnnotationCommand(selected.Id, index, name + " " + selected.Kind.DisplayName()));
        }
    }

    private void BeginTextEdit(TextAnnotation text) => BeginTextEdit(new TextEditRequest(text.Id, text.Position, text.Text, text.Style));

    private void BeginTextEdit(TextEditRequest request)
    {
        TextEdit = request;
        Invalidate();
        TextEditRequested?.Invoke(this, request);
    }

    /// <summary>Records the dragged annotation's final state against where it started (nothing if it didn't change).</summary>
    private void Commit(Annotation before, string name)
    {
        if (Document.Find(before.Id) is { } after && after != before)
        {
            Document.Execute(new ReplaceAnnotationCommand(before, after, name));
        }
    }

    private void CancelDrag()
    {
        switch (_drag)
        {
            case MoveDrag move:
                Document.Preview(move.Before);
                break;
            case ResizeDrag resize:
                Document.Preview(resize.Before);
                break;
            case CropDrag crop:
                CropArea = crop.Before;
                break;
        }

        _drag = null;
        Draft = null;
        Invalidate();
    }

    private Handle? SelectedHandleAt(SKPoint p, float tolerance) =>
        Selected is { } selected ? Geometry.HandleAt(selected.GetHandles(), p, tolerance) : null;

    private Annotation CreateDraft(SKPoint p)
    {
        var id = Document.NewId();
        var empty = new SKRect(p.X, p.Y, p.X, p.Y);
        return Tool switch
        {
            EditorTool.Arrow => new ArrowAnnotation(id, Style, p, p),
            EditorTool.Line => new LineAnnotation(id, Style, p, p),
            EditorTool.Rectangle => new RectangleAnnotation(id, Style, empty),
            EditorTool.Ellipse => new EllipseAnnotation(id, Style, empty),
            EditorTool.Freehand => new FreehandAnnotation(id, Style, [p]),
            EditorTool.Highlight => new HighlightAnnotation(id, Style, empty),
            EditorTool.Blur => new BlurAnnotation(id, Style, empty),
            EditorTool.Pixelate => new PixelateAnnotation(id, Style, empty),
            _ => throw new InvalidOperationException($"{Tool} does not draw shapes."),
        };
    }

    private static Annotation UpdateDraft(Annotation draft, SKPoint origin, SKPoint p, bool constrain) => draft switch
    {
        TwoPointAnnotation line => line with { End = constrain ? Geometry.SnapAngle(origin, p) : p },
        FreehandAnnotation pen when Geometry.Distance(pen.Points[^1], p) >= 1 => pen with { Points = [.. pen.Points, p] },
        FreehandAnnotation pen => pen,
        BoxAnnotation box => box with { Rect = Geometry.RectFromPoints(origin, constrain ? Geometry.SquareCorner(origin, p) : p) },
        _ => draft,
    };

    private static bool IsBigEnough(Annotation draft) => draft switch
    {
        TwoPointAnnotation line => line.Length >= MinShapeSize,
        BoxAnnotation box => box.Rect.Width >= MinShapeSize && box.Rect.Height >= MinShapeSize,
        _ => true, // a freehand click is a dot
    };

    private SKRectI CropRect(CropDrag drag, SKPoint p)
    {
        SKRect rect;
        if (drag.Before is { } before && drag.Handle is { } handle)
        {
            rect = Geometry.ResizeBox(ToRect(before), handle, p, keepAspect: false);
        }
        else if (drag.Before is { } moving)
        {
            // Moving: whole-pixel steps, kept inside the image.
            var dx = (int)MathF.Round(p.X - drag.Origin.X);
            var dy = (int)MathF.Round(p.Y - drag.Origin.Y);
            dx = Math.Clamp(dx, -moving.Left, Document.Width - moving.Right);
            dy = Math.Clamp(dy, -moving.Top, Document.Height - moving.Bottom);
            return SKRectI.Create(moving.Left + dx, moving.Top + dy, moving.Width, moving.Height);
        }
        else
        {
            rect = Geometry.RectFromPoints(drag.Origin, p);
        }

        return SKRectI.Intersect(SKRectI.Round(rect), SKRectI.Create(Document.Width, Document.Height));
    }

    private static SKRect ToRect(SKRectI r) => new(r.Left, r.Top, r.Right, r.Bottom);

    private static EditorCursor CursorFor(HandleKind handle) => handle switch
    {
        HandleKind.TopLeft or HandleKind.BottomRight => EditorCursor.SizeNWSE,
        HandleKind.TopRight or HandleKind.BottomLeft => EditorCursor.SizeNESW,
        HandleKind.Top or HandleKind.Bottom => EditorCursor.SizeNS,
        HandleKind.Left or HandleKind.Right => EditorCursor.SizeWE,
        _ => EditorCursor.Move,
    };

    private void OnDocumentChanged(object? sender, EventArgs e)
    {
        // Undo can remove the selected annotation; a crop area may no longer fit a smaller image.
        if (_selectedId is { } id && Document.Find(id) is null)
        {
            Select(null);
        }

        if (CropArea is { } crop && !SKRectI.Create(Document.Width, Document.Height).Contains(crop))
        {
            CropArea = null;
        }
    }

    private void Invalidate() => Invalidated?.Invoke(this, EventArgs.Empty);

    private abstract record Drag;

    private sealed record CreateDrag(SKPoint Origin) : Drag;

    private sealed record MoveDrag(Annotation Before, SKPoint Origin) : Drag;

    private sealed record ResizeDrag(Annotation Before, HandleKind Handle) : Drag;

    /// <summary>A new crop (no <paramref name="Before"/>), moving it (no handle) or resizing it.</summary>
    private sealed record CropDrag(SKPoint Origin, SKRectI? Before, HandleKind? Handle) : Drag;
}
