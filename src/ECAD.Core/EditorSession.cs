namespace ECAD.Core;

// Small immutable snapshots keep an entire user action atomic. Replace with
// differential commands if profiling large drawings shows a memory bottleneck.
public sealed class EditorSession
{
    private readonly List<DrawingDocument> undo = [];
    private readonly Stack<DrawingDocument> redo = [];
    private DrawingElement[] clipboard = [];
    private int pasteCount;
    public DrawingDocument Document { get; private set; } = new();
    public HashSet<Guid> Selection { get; } = [];
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public bool CanPaste => clipboard.Length > 0;
    public event Action? Changed;

    public void Load(DrawingDocument document)
    {
        if (document.SchemaVersion <= 5) document = SymbolLabels.Ensure(document);
        document.Validate();
        Document = document with { SchemaVersion = 7, Elements = AssociativeDimensions.ResolveAll(document.Elements) };
        undo.Clear(); redo.Clear(); Selection.Clear(); Changed?.Invoke();
    }

    public void Apply(DrawingElement[] elements)
    {
        if (Document.Elements.SequenceEqual(elements)) return;
        var next = Document with { Elements = [.. elements] };
        next.Validate();
        undo.Add(Document);
        if (undo.Count > 200) undo.RemoveAt(0);
        Document = next with { SchemaVersion = 7, Elements = AssociativeDimensions.ResolveAll(elements) }; redo.Clear(); Notify();
    }

    public void ApplyDocument(DrawingDocument document)
    {
        document.Validate();
        if (ReferenceEquals(Document, document)) return;
        undo.Add(Document); if (undo.Count > 200) undo.RemoveAt(0);
        Document = document with { SchemaVersion = 7, Elements = AssociativeDimensions.ResolveAll(document.Elements) };
        redo.Clear(); Notify();
    }

    public SymbolDefinition CreateCustomSymbol(string name, string prefix)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(prefix))
            throw new InvalidDataException("Вкажи назву та префікс символу.");
        var graphics = Document.Elements.Where(e => Selection.Contains(e.Id) && e.Kind is ElementKind.Line or ElementKind.Rectangle).ToArray();
        var pins = Document.Elements.Where(e => Selection.Contains(e.Id) && e.Kind == ElementKind.Junction).Select(e => e.A).ToArray();
        var edges = graphics.SelectMany(AssociativeDimensions.Edges).ToArray();
        if (edges.Length == 0 || pins.Length == 0)
            throw new InvalidDataException("Для символу виділи лінії/прямокутники та хоча б один вузол-контакт.");
        var points = edges.SelectMany(e => new[] { e.A, e.B }).Concat(pins).ToArray();
        var centre = new PointMm((points.Min(p => p.X) + points.Max(p => p.X)) / 2,
            (points.Min(p => p.Y) + points.Max(p => p.Y)) / 2);
        var definition = new SymbolDefinition("CUSTOM_" + Guid.NewGuid().ToString("N"), name.Trim(), prefix.Trim(),
            pins.Select(p => p - centre).ToArray(), edges.Select(e => new SymbolStroke(e.A - centre, e.B - centre)).ToArray());
        ApplyDocument(Document with { CustomSymbols = [.. Document.CustomSymbols, definition] });
        return definition;
    }

    public void Add(DrawingElement element) => Apply([.. Document.Elements, element]);
    public void Delete() => Apply(Document.Elements.Where(e => !Selection.Contains(e.Id) &&
        !(e.LinkedElementId is { } owner && Selection.Contains(owner)) &&
        !(e.StartReference is { } a && Selection.Contains(a.ElementId)) &&
        !(e.EndReference is { } b && Selection.Contains(b.ElementId))).ToArray());
    public DrawingElement[] PreviewMove(PointMm delta) => AssociativeDimensions.ResolveAll(Document.Elements.Select(e =>
    {
        var followsOwner = e.LinkedElementId is { } owner && Selection.Contains(owner);
        if (!Selection.Contains(e.Id) && !followsOwner) return e;
        if (e.Kind != ElementKind.Dimension || (e.StartReference is null && e.EndReference is null)) return e.Move(delta);
        var sourcesMoving = (e.StartReference is null || Selection.Contains(e.StartReference.ElementId)) &&
            (e.EndReference is null || Selection.Contains(e.EndReference.ElementId));
        if (sourcesMoving) return e with { A = e.StartReference is null ? e.A + delta : e.A, B = e.EndReference is null ? e.B + delta : e.B };
        var v = e.LengthMm > 1e-9 ? (e.B - e.A) * (1 / e.LengthMm) : new PointMm(1, 0);
        return e with { DimensionOffset = e.DimensionOffset - v.Y * delta.X + v.X * delta.Y };
    }).ToArray());
    public void Move(PointMm delta) => Apply(PreviewMove(delta));

    public void SelectBox(SelectionBox box, bool crossing, bool additive)
    {
        if (!additive) Selection.Clear();
        // Existing groups are selected as units: window selection must enclose
        // the whole group, crossing selection only needs to touch one member.
        foreach (var group in Document.Elements.GroupBy(e => e.GroupId ?? e.Id))
            if (crossing ? group.Any(e => box.Matches(e, true)) : group.All(e => box.Matches(e, false)))
                Selection.UnionWith(group.Select(e => e.Id));
        Changed?.Invoke();
    }
    public void Group()
    {
        if (Selection.Count < 2) return;
        var id = Guid.NewGuid();
        Apply(Document.Elements.Select(e => Selection.Contains(e.Id) ? e with { GroupId = id } : e).ToArray());
    }
    public void Ungroup() => Apply(Document.Elements.Select(e => Selection.Contains(e.Id) ? e with { GroupId = null } : e).ToArray());

    public void Copy()
    {
        clipboard = Document.Elements.Where(e => Selection.Contains(e.Id) ||
            e.LinkedElementId is { } owner && Selection.Contains(owner)).ToArray();
        pasteCount = 0;
    }

    public void Paste()
    {
        if (!CanPaste) return;
        pasteCount++;
        var offset = new PointMm(5 * pasteCount, 5 * pasteCount);
        var ids = clipboard.ToDictionary(e => e.Id, _ => Guid.NewGuid());
        var groupIds = clipboard.Where(e => e.GroupId is not null).Select(e => e.GroupId!.Value)
            .Distinct().ToDictionary(id => id, _ => Guid.NewGuid());
        GeometryReference? Remap(GeometryReference? reference) => reference is null ? null :
            reference with { ElementId = ids.GetValueOrDefault(reference.ElementId, reference.ElementId) };
        var copies = clipboard.Select(e => e with
        {
            Id = ids[e.Id],
            GroupId = e.GroupId is { } group ? groupIds[group] : null,
            A = e.A + offset,
            B = e.B + offset,
            Points = e.Points?.Select(p => p + offset).ToArray(),
            StartReference = Remap(e.StartReference),
            EndReference = Remap(e.EndReference)
            ,LinkedElementId = e.LinkedElementId is { } owner && ids.TryGetValue(owner, out var newOwner) ? newOwner : null
        }).ToArray();
        Apply([.. Document.Elements, .. copies]);
        Selection.Clear(); Selection.UnionWith(copies.Select(e => e.Id)); Changed?.Invoke();
    }

    public void RotateSelection90()
    {
        var selected = Document.Elements.Where(e => Selection.Contains(e.Id)).ToArray();
        if (selected.Length == 0) return;
        var points = selected.Where(e => e.Kind != ElementKind.Dimension ||
                (e.StartReference is null && e.EndReference is null))
            .SelectMany(e => e.Kind == ElementKind.Circle
                ? new[] { e.A - new PointMm(e.LengthMm, e.LengthMm), e.A + new PointMm(e.LengthMm, e.LengthMm) }
                : e.Kind == ElementKind.Wire ? e.Points! : new[] { e.A, e.B }).ToArray();
        if (points.Length == 0) return;
        var centre = new PointMm((points.Min(p => p.X) + points.Max(p => p.X)) / 2,
            (points.Min(p => p.Y) + points.Max(p => p.Y)) / 2);
        PointMm Rotate(PointMm p)
        {
            var v = p - centre;
            return centre + new PointMm(-v.Y, v.X);
        }
        var rotated = Document.Elements.Select(e =>
        {
            var followsOwner = e.LinkedElementId is { } owner && Selection.Contains(owner);
            if (!Selection.Contains(e.Id) && !followsOwner) return e;
            if (e.Kind == ElementKind.Dimension && (e.StartReference is not null || e.EndReference is not null)) return e;
            if (e.Kind == ElementKind.Rectangle)
            {
                var a = Rotate(e.A); var b = Rotate(e.B);
                return e with { A = new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), B = new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)) };
            }
            if (e.Kind == ElementKind.Wire)
            {
                var points = e.Points!.Select(Rotate).ToArray();
                return e with { A = points[0], B = points[^1], Points = points };
            }
            return e with
            {
                A = Rotate(e.A), B = Rotate(e.B),
                RotationDegrees = e.Kind == ElementKind.Symbol || e.Kind == ElementKind.Text && !followsOwner
                    ? (e.RotationDegrees + 90) % 360 : e.RotationDegrees
            };
        }).Select(e =>
        {
            GeometryReference? Remap(GeometryReference? reference)
            {
                if (reference is null || !Selection.Contains(reference.ElementId)) return reference;
                var owner = Document.Elements.Single(source => source.Id == reference.ElementId);
                return owner.Kind == ElementKind.Rectangle
                    ? reference with { Index = (reference.Index + 1) % 4 }
                    : reference;
            }
            return e.Kind == ElementKind.Dimension ? e with
            {
                StartReference = Remap(e.StartReference),
                EndReference = Remap(e.EndReference)
            } : e;
        }).ToArray();
        Apply(rotated);
    }

    public void Select(DrawingElement? element, bool additive)
    {
        if (!additive) Selection.Clear();
        if (element is not null)
            foreach (var e in Document.Elements.Where(e => e.Id == element.Id ||
                (element.GroupId is not null && e.GroupId == element.GroupId))) Selection.Add(e.Id);
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (!CanUndo) return;
        redo.Push(Document); Document = undo[^1]; undo.RemoveAt(undo.Count - 1); Notify();
    }
    public void Redo()
    {
        if (!CanRedo) return;
        undo.Add(Document); Document = redo.Pop(); Notify();
    }
    private void Notify()
    {
        Selection.IntersectWith(Document.Elements.Select(e => e.Id)); Changed?.Invoke();
    }
}
