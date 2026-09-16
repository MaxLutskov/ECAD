using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using ECAD.Core;
using Geometry = ECAD.Core.Geometry;

namespace ECAD.Desktop;

public sealed class DrawingCanvas : Decorator
{
    private enum CanvasOperation { None, SplitSegment, Trim, Extend }
    private readonly EditorSession session;
    private PointMm? anchor;
    private PointMm cursor;
    private PointMm? dragStart;
    private PointMm dragDelta;
    private Guid? vertexElementId;
    private int vertexIndex = -1;
    private PointMm vertexTarget;
    private Point? panStart;
    private Point origin = new(36, 36);
    private double scale = 2.4;
    private PointMm? boxStart;
    private PointMm boxEnd;
    private bool boxAdditive;
    private GeometryPick? firstPick;
    private GeometryPick? hoverPick;
    private DrawingElement? pendingDimension;
    private Guid? editingElementId;
    private readonly List<PointMm> wirePoints = [];
    private readonly List<PointMm> arcPoints = [];
    private bool wireHorizontalFirst = true;
    private Point inputPosition;
    private CanvasOperation operation;
    private GeometryReference? operationTarget;
    public LineInputPanel LineInput { get; } = new();
    public ElementKind? Tool { get; private set; }
    public ElementKind ActivePathKind { get; private set; } = ElementKind.Wire;
    public bool IsDrawing => anchor is not null;
    public double GridStep { get; set; } = 2.5;
    public double ExactLength { get; set; }
    public double AngleSnapStep { get; private set; }
    public bool SnapEnabled { get; set; } = true;
    public bool GridVisible { get; set; } = true;
    public string ActiveSymbolKey { get; set; } = SymbolLibrary.All[0].Key;
    public Guid? ActiveVariantId { get; set; }
    public WorkspaceCommands? Commands { get; set; }
    public event Action? ToolChanged;
    public void CenterOn(PointMm point) { origin = new(Bounds.Width / 2 - point.X * scale, Bounds.Height / 2 - point.Y * scale); InvalidateVisual(); }
    public event Action<string>? Status;
    public event Action<TextPlacementRequest>? TextPlacementRequested;

    public DrawingCanvas(EditorSession session)
    {
        this.session = session;
        Focusable = true; ClipToBounds = true;
        SizeChanged += (_, e) =>
        {
            if (e.PreviousSize.Width > 0 && e.PreviousSize.Height > 0)
                origin += new Vector((e.NewSize.Width - e.PreviousSize.Width) / 2, (e.NewSize.Height - e.PreviousSize.Height) / 2);
            InvalidateVisual();
        };
        session.Changed += InvalidateVisual;
        Child = LineInput;
        LineInput.Edited += () => { UpdateGeometryInput(); InvalidateVisual(); };
        LineInput.Confirm += () =>
        {
            if (Tool == ElementKind.Wire) CommitWireParameter();
            else if (Tool == ElementKind.Polyline) CommitPolylineParameter();
            else if (Tool == ElementKind.Arc) CommitArcParameter();
            else CommitGeometry();
        };
        LineInput.Cancelled += EscapeToSelection;
    }

    private Point Screen(PointMm p) => new(origin.X + p.X * scale, origin.Y + p.Y * scale);
    protected override Size MeasureOverride(Size availableSize)
    { LineInput.Measure(new Size(300, double.PositiveInfinity)); return default; }
    protected override Size ArrangeOverride(Size finalSize)
    { LineInput.Arrange(new Rect(inputPosition, LineInput.DesiredSize)); return finalSize; }
    private PointMm World(Point p) => new((p.X - origin.X) / scale, (p.Y - origin.Y) / scale);
    private PointMm Snap(PointMm p) => SnapEnabled ? Geometry.Snap(p, GridStep) : p;
    public void SetTool(ElementKind? tool)
    {
        Cancel();
        Tool = tool;
        if (tool is ElementKind.Line or ElementKind.Wire) ActivePathKind = tool.Value;
        Focus(); InvalidateVisual(); ToolChanged?.Invoke();
    }
    public void BeginSplitSegment()
    {
        Cancel(); operation = CanvasOperation.SplitSegment; Tool = null; Focus();
        Status?.Invoke("Розбиття: клацни внутрішню точку лінії або сегмента полілінії.");
    }
    public void BeginTrim()
    {
        Cancel(); operation = CanvasOperation.Trim; Tool = null; Focus();
        Status?.Invoke("Обрізання: клацни частину прямої лінії, яку треба прибрати, потім межу.");
    }
    public void BeginExtend()
    {
        Cancel(); operation = CanvasOperation.Extend; Tool = null; Focus();
        Status?.Invoke("Продовження: клацни потрібний кінець прямої лінії, потім межу.");
    }
    public void ActivatePathTool() => SetTool(ActivePathKind);
    public void SetPathKind(ElementKind kind)
    {
        if (kind is not (ElementKind.Line or ElementKind.Wire)) throw new ArgumentOutOfRangeException(nameof(kind));
        var wasActive = Tool is ElementKind.Line or ElementKind.Wire;
        Cancel(); ActivePathKind = kind;
        if (wasActive) Tool = kind;
        Focus(); InvalidateVisual();
        Status?.Invoke(kind == ElementKind.Wire
            ? "Тип лінії: провідник. Сегменти беруть участь в електричних з’єднаннях."
            : "Тип лінії: графіка. Лінії не утворюють електричних з’єднань.");
    }
    public void EscapeToSelection()
    {
        Cancel(); Tool = null; Focus(); ToolChanged?.Invoke();
        Status?.Invoke("Режим вибору / переміщення.");
    }
    public void Cancel()
    {
        var hadFocus = LineInput.IsKeyboardFocusWithin;
        anchor = null; dragStart = null; dragDelta = default; panStart = null;
        vertexElementId = null; vertexIndex = -1;
        boxStart = null; firstPick = null; hoverPick = null; pendingDimension = null; editingElementId = null;
        wirePoints.Clear();
        arcPoints.Clear();
        operation = CanvasOperation.None;
        operationTarget = null;
        LineInput.IsVisible = false;
        if (hadFocus) Focus();
        InvalidateVisual();
    }
    public void Fit()
    {
        var doc = session.Document;
        scale = Math.Clamp(Math.Min((Bounds.Width - 72) / doc.WidthMm, (Bounds.Height - 72) / doc.HeightMm), .2, 30);
        origin = new(36, 36); InvalidateVisual();
    }

    private PointMm EndPoint(PointMm start, PointMm end)
    {
        var delta = end - start;
        return Tool switch
        {
            ElementKind.Line => LineEndPoint(start, end),
            ElementKind.Rectangle => new(start.X + Direction(delta.X) * (LineInput.FirstValue ?? Math.Abs(delta.X)),
                start.Y + Direction(delta.Y) * (LineInput.SecondValue ?? Math.Abs(delta.Y))),
            ElementKind.Circle => CircleEndPoint(start, end),
            _ => end
        };
    }

    private PointMm LineEndPoint(PointMm start, PointMm end)
    {
        var length = LineInput.FirstValue ?? (ExactLength > 0 ? ExactLength : (end - start).Length);
        if (length <= 1e-9) return start;
        return Geometry.Polar(start, length, LineInput.SecondValue ?? SnapAngle(end - start));
    }

    private PointMm CircleEndPoint(PointMm start, PointMm end)
    {
        var delta = end - start;
        var radius = LineInput.FirstValue ?? (LineInput.SecondValue is { } diameter ? diameter / 2 : delta.Length);
        if (radius <= 1e-9) return start;
        return Geometry.Polar(start, radius, delta.Length > 1e-9 ? Geometry.Angle(delta) : 0);
    }

    private static double Direction(double value) => value < 0 ? -1 : 1;
    private bool HasGeometryInput => Tool is ElementKind.Line or ElementKind.Rectangle or ElementKind.Circle;
    private bool HasParameterInput => HasGeometryInput ||
        Tool is ElementKind.Wire or ElementKind.Polyline && wirePoints.Count > 0 ||
        Tool == ElementKind.Arc && arcPoints.Count == 1;

    private bool FromInput(object? source) => source is Visual v && (v == LineInput || v.GetVisualAncestors().Contains(LineInput));

    private void UpdateGeometryInput()
    {
        if (anchor is not { } a || !HasParameterInput) return;
        var end = Tool switch
        {
            ElementKind.Wire => WireInputEnd(a, cursor),
            ElementKind.Polyline => PolylineInputEnd(a, cursor),
            _ => EndPoint(a, cursor)
        };
        var delta = end - a;
        if (Tool == ElementKind.Rectangle) LineInput.UpdateLive(Math.Abs(delta.X), Math.Abs(delta.Y));
        else if (Tool == ElementKind.Circle) LineInput.UpdateLive(delta.Length, delta.Length * 2);
        else if (Tool == ElementKind.Arc) LineInput.UpdateLive(delta.Length, 90);
        else LineInput.UpdateLive(delta.Length, Geometry.Angle(delta));
        if (!LineInput.IsKeyboardFocusWithin)
        {
            var p = Screen(end);
            inputPosition = new(Math.Clamp(p.X + 22, 4, Math.Max(4, Bounds.Width - 300)),
                Math.Clamp(p.Y + 22, 4, Math.Max(4, Bounds.Height - 115)));
            // Move the editor immediately. Without this, a fast second click
            // can land on its previous layout position and fail to finish the line.
            LineInput.Measure(new Size(300, double.PositiveInfinity));
            LineInput.Arrange(new Rect(inputPosition, LineInput.DesiredSize));
            InvalidateArrange();
        }
    }

    public void EditSelectedGeometry()
    {
        var selected = session.Document.Elements.Where(e => session.Selection.Contains(e.Id)).ToArray();
        if (selected.Length != 1 || selected[0].Kind is not (ElementKind.Line or ElementKind.Rectangle or ElementKind.Circle))
        { Status?.Invoke("Виділи одну лінію, прямокутник або коло для зміни параметрів."); return; }
        var element = selected[0]; SetTool(element.Kind); editingElementId = element.Id;
        anchor = element.A; cursor = element.B; ExactLength = 0;
        var delta = element.B - element.A;
        if (element.Kind == ElementKind.Line) LineInput.Begin(element.Kind, element.LengthMm, Geometry.Angle(delta));
        else if (element.Kind == ElementKind.Rectangle) LineInput.Begin(element.Kind, Math.Abs(delta.X), Math.Abs(delta.Y));
        else LineInput.Begin(element.Kind, element.LengthMm, null);
        UpdateGeometryInput(); LineInput.FocusField(false);
    }

    public void EditSelectedLine() => EditSelectedGeometry();

    public void SetAngleSnap(double step)
    {
        if (step is not (0 or 15 or 30 or 45)) throw new ArgumentOutOfRangeException(nameof(step));
        AngleSnapStep = step; UpdateGeometryInput(); InvalidateVisual();
        Status?.Invoke(step == 0 ? "Кутова прив’язка вимкнена." : $"Кутова прив’язка: крок {step:0}°.");
    }

    public void CopySelection()
    {
        Cancel(); session.Copy();
        Status?.Invoke(session.CanPaste ? $"Скопійовано: {session.Selection.Count}. Ctrl+V — вставити." : "Немає вибраних елементів.");
    }

    public void PasteSelection()
    {
        Cancel(); session.Paste();
        Status?.Invoke(session.CanPaste ? "Копію вставлено зі зміщенням 5 мм." : "Буфер копіювання порожній.");
    }

    public void RotateSelection90()
    {
        Cancel();
        try { session.RotateSelection90(); Status?.Invoke("Виділення повернуто на 90°."); }
        catch (InvalidDataException ex) { Status?.Invoke(ex.Message); }
    }

    public void CommitText(TextPlacementRequest request, string text, double heightMm)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var element = request.ElementId is { } id
            ? session.Document.Elements.Single(e => e.Id == id) with { Text = text.Trim(), TextHeightMm = heightMm }
            : new DrawingElement(Guid.NewGuid(), ElementKind.Text, request.Position, request.Position)
                { Text = text.Trim(), TextHeightMm = heightMm };
        if (request.ElementId is null) session.Add(element);
        else session.Apply(session.Document.Elements.Select(e => e.Id == element.Id ? element :
            element.LinkedElementId == e.Id && e.Kind == ElementKind.Symbol ? e with { DeviceTag = element.Text } : e).ToArray());
        Focus(); InvalidateVisual();
    }

    public void CommitSymbolTag(Guid elementId, string deviceTag)
    {
        if (string.IsNullOrWhiteSpace(deviceTag)) return;
        var tag = deviceTag.Trim();
        session.Apply(session.Document.Elements.Select(e => e.Id == elementId
            ? e with { DeviceTag = tag }
            : e.LinkedElementId == elementId ? e with { Text = tag } : e).ToArray());
        Focus(); InvalidateVisual();
    }

    private void CommitGeometry()
    {
        if (anchor is not { } a || !HasGeometryInput || !LineInput.Valid) return;
        var b = EndPoint(a, cursor);
        if ((b - a).Length < 1e-9 || Tool == ElementKind.Rectangle && (a.X == b.X || a.Y == b.Y)) return;
        try
        {
            var kind = Tool!.Value;
            if (editingElementId is { } id)
                session.Apply(session.Document.Elements.Select(e => e.Id == id ? e with { A = a, B = b } : e).ToArray());
            else session.Add(new(Guid.NewGuid(), kind, a, b));
            var editing = editingElementId.HasValue;
            Cancel(); if (editing) Tool = null; Focus();
        }
        catch (InvalidDataException ex) { Status?.Invoke(ex.Message); }
    }

    private GeometryPick Pick(PointMm raw)
    {
        var pick = AssociativeDimensions.Pick(session.Document.Elements, raw, 8 / scale);
        return pick.Reference is not null ? pick : new(Snap(raw), null);
    }

    private void AddJunction(PointMm raw)
    {
        var p = ConnectionSnap.Pick(session.Document.Elements, raw, 8 / scale, Snap(raw));
        if (session.Document.Elements.Any(e => e.Kind == ElementKind.Junction && (e.A - p).Length <= 1e-7))
        { Status?.Invoke("У цьому місці вже є вузол з’єднання."); return; }
        session.Add(new(Guid.NewGuid(), ElementKind.Junction, p, p));
        Status?.Invoke($"Додано вузол з’єднання: X {p.X:0.###}, Y {p.Y:0.###} мм.");
    }

    private PointMm[] OrthogonalRoute(PointMm start, PointMm end)
    {
        if (start == end) return [];
        if (start.X == end.X || start.Y == end.Y) return [end];
        var elbow = wireHorizontalFirst ? new PointMm(end.X, start.Y) : new PointMm(start.X, end.Y);
        return [elbow, end];
    }

    private PointMm[] PointerWireRoute(PointMm start, PointMm end)
    {
        if (AngleSnapStep <= 0) return OrthogonalRoute(start, end);
        var length = (end - start).Length;
        return length <= 1e-9 ? [] : [Geometry.Polar(start, length, SnapAngle(end - start))];
    }

    private PointMm[] PreviewWire()
    {
        if (wirePoints.Count == 0) return [];
        if (LineInput.HasInput) return [.. wirePoints, WireInputEnd(wirePoints[^1], cursor)];
        return [.. wirePoints, .. PointerWireRoute(wirePoints[^1], cursor)];
    }

    private PointMm WireInputEnd(PointMm start, PointMm end)
    {
        var delta = end - start;
        var angle = LineInput.SecondValue ?? (AngleSnapStep > 0 ? SnapAngle(delta) : CardinalAngle(delta));
        var length = LineInput.FirstValue ?? delta.Length;
        if (length <= 1e-9) return start;
        return Geometry.Polar(start, length, angle);
    }

    private PointMm PolylineInputEnd(PointMm start, PointMm end)
    {
        var delta = end - start;
        var angle = LineInput.SecondValue ?? (AngleSnapStep > 0 ? SnapAngle(delta) : Geometry.Angle(delta));
        var length = LineInput.FirstValue ?? delta.Length;
        if (length <= 1e-9) return start;
        return Geometry.Polar(start, length, angle);
    }

    private PointMm[] PreviewPolyline()
    {
        if (wirePoints.Count == 0) return [];
        var end = LineInput.HasInput ? PolylineInputEnd(wirePoints[^1], cursor) : cursor;
        return end == wirePoints[^1] ? [.. wirePoints] : [.. wirePoints, end];
    }

    private void AddPolylinePoint(GeometryPick pick, bool finish)
    {
        if (wirePoints.Count == 0)
        {
            wirePoints.Add(pick.Point); anchor = pick.Point;
            LineInput.Begin(ElementKind.Polyline); UpdateGeometryInput();
            Status?.Invoke("Полілінія: клік — вершина, число — довжина сегмента, подвійний клік або Enter — завершити.");
            return;
        }
        if (LineInput.HasInput && !LineInput.Valid) { Status?.Invoke("Виправ довжину або кут сегмента."); return; }
        var target = LineInput.HasInput ? PolylineInputEnd(wirePoints[^1], pick.Point) : pick.Point;
        if (target != wirePoints[^1]) wirePoints.Add(target);
        anchor = wirePoints[^1];
        if (finish) CommitPolyline();
        else { LineInput.Begin(ElementKind.Polyline); UpdateGeometryInput(); Focus(); }
    }

    private void CommitPolylineParameter()
    {
        if (!LineInput.Valid) return;
        if (!LineInput.HasInput) { CommitPolyline(); return; }
        var target = PolylineInputEnd(wirePoints[^1], cursor);
        if (target == wirePoints[^1]) return;
        wirePoints.Add(target); anchor = target;
        LineInput.Begin(ElementKind.Polyline); UpdateGeometryInput(); Focus();
        Status?.Invoke("Точний сегмент полілінії додано. Enter із порожніми полями завершує побудову.");
        InvalidateVisual();
    }

    private void CommitPolyline()
    {
        if (wirePoints.Count < 2) return;
        session.Add(new(Guid.NewGuid(), ElementKind.Polyline, wirePoints[0], wirePoints[^1]) { Points = [.. wirePoints] });
        Cancel(); Status?.Invoke("Полілінію додано.");
    }

    private void AddArcPoint(PointMm point)
    {
        if (arcPoints.Count == 0)
        {
            arcPoints.Add(point); anchor = point;
            LineInput.Begin(ElementKind.Arc); UpdateGeometryInput();
            Status?.Invoke("Дуга: обери проміжну точку або введи радіус → Tab → кут дуги → Enter."); return;
        }
        if (arcPoints.Count == 1 && LineInput.HasInput) { CommitArcParameter(); return; }
        if (point == arcPoints[^1]) return;
        if (arcPoints.Count == 1)
        {
            arcPoints.Add(point); Status?.Invoke("Дуга: обери кінцеву точку."); return;
        }
        try
        {
            session.Add(ArcGeometry.FromThreePoints(Guid.NewGuid(), arcPoints[0], arcPoints[1], point));
            Cancel(); Status?.Invoke("Дугу додано за трьома точками.");
        }
        catch (InvalidDataException ex) { Status?.Invoke(ex.Message); }
    }

    private void CommitArcParameter()
    {
        if (arcPoints.Count != 1 || !LineInput.Valid) return;
        if (LineInput.FirstValue is not { } radius || LineInput.SecondValue is not { } sweep)
        { Status?.Invoke("Для точної дуги введи радіус і кут дуги."); return; }
        try
        {
            var centre = arcPoints[0];
            var start = Geometry.Polar(centre, radius, (cursor - centre).Length > 1e-9 ? Geometry.Angle(cursor - centre) : 0);
            session.Add(new(Guid.NewGuid(), ElementKind.Arc, centre, start) { ArcSweepDegrees = sweep });
            Cancel(); Status?.Invoke("Дугу додано за радіусом і кутом.");
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException) { Status?.Invoke(ex.Message); }
    }

    private static double CardinalAngle(PointMm delta) => Math.Abs(delta.X) >= Math.Abs(delta.Y)
        ? delta.X < 0 ? 180 : 0
        : delta.Y < 0 ? 90 : 270;

    private double SnapAngle(PointMm delta)
    {
        var angle = Geometry.Angle(delta);
        return AngleSnapStep > 0 ? Math.Round(angle / AngleSnapStep, MidpointRounding.AwayFromZero) * AngleSnapStep : angle;
    }

    private void AddWirePoint(GeometryPick pick, bool finish)
    {
        if (wirePoints.Count == 0)
        {
            wirePoints.Add(pick.Point); anchor = pick.Point;
            LineInput.Begin(ElementKind.Wire); UpdateGeometryInput();
            Status?.Invoke("Веди провідник; клік — точка траси, число — довжина сегмента, Enter — підтвердити/завершити.");
            return;
        }
        if (LineInput.HasInput && !LineInput.Valid)
        { Status?.Invoke("Виправ довжину або кут точного сегмента."); return; }
        var route = LineInput.HasInput ? new[] { WireInputEnd(wirePoints[^1], pick.Point) } : PointerWireRoute(wirePoints[^1], pick.Point);
        foreach (var point in route)
            if (point != wirePoints[^1]) wirePoints.Add(point);
        anchor = wirePoints[^1];
        if (finish || !LineInput.HasInput && pick.Reference is not null) CommitWire();
        else { LineInput.Begin(ElementKind.Wire); UpdateGeometryInput(); Focus(); }
    }

    private void CommitWireParameter()
    {
        if (!LineInput.Valid) return;
        if (!LineInput.HasInput) { CommitWire(); return; }
        var target = WireInputEnd(wirePoints[^1], cursor);
        if (target == wirePoints[^1]) return;
        wirePoints.Add(target); anchor = target;
        LineInput.Begin(ElementKind.Wire); UpdateGeometryInput(); Focus();
        Status?.Invoke("Точний сегмент додано. Введи наступний або натисни Enter ще раз для завершення провідника.");
        InvalidateVisual();
    }

    private void CommitWire()
    {
        if (wirePoints.Count < 2) return;
        var points = new List<PointMm>();
        foreach (var p in wirePoints)
        {
            if (points.Count > 0 && p == points[^1]) continue;
            if (points.Count >= 2)
            {
                var a = points[^2]; var b = points[^1];
                if ((a.X == b.X && b.X == p.X) || (a.Y == b.Y && b.Y == p.Y)) points[^1] = p;
                else points.Add(p);
            }
            else points.Add(p);
        }
        if (points.Count < 2) return;
        session.Add(new(Guid.NewGuid(), ElementKind.Wire, points[0], points[^1]) { Points = [.. points] });
        var nets = ElectricalConnectivity.Build(session.Document.Elements).Length;
        Cancel(); Status?.Invoke($"Провідник додано. Електричних кіл у документі: {nets}.");
    }

    private void AddSymbol(PointMm p)
    {
        var definition = SymbolLibrary.Get(session.Document, ActiveSymbolKey);
        var variant = ActiveVariantId is { } id ? ComponentCatalog.FindVariant(session.Document, id)?.Variant : null;
        if (variant?.SymbolKey != definition.Key) variant = null;
        var used = session.Document.Devices.Select(device => device.Tag)
            .Concat(session.Document.Pages.SelectMany(page => page.Elements).Where(e => e.Kind == ElementKind.Symbol).Select(e => e.DeviceTag!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var number = 1; while (used.Contains(definition.Prefix + number)) number++;
        var symbol = new DrawingElement(Guid.NewGuid(), ElementKind.Symbol, p, p)
        {
            SymbolKey = definition.Key, DeviceTag = definition.Prefix + number,
            PinOffsets = definition.Strokes is null ? null : definition.Pins,
            SymbolStrokes = definition.Strokes, ComponentVariantId = variant?.Id,
            PhysicalRepresentationId = variant?.PhysicalRepresentations.FirstOrDefault()?.Id
        };
        session.Apply([.. session.Document.Elements, symbol, SymbolLabels.Create(symbol)]);
        Status?.Invoke($"Додано {definition.Name}. Наступний символ можна ставити одразу.");
    }

    private void PickDimension(GeometryPick pick)
    {
        if (pendingDimension is not null)
        {
            session.Add(pendingDimension); Cancel(); return;
        }
        if (firstPick is null) { firstPick = pick; Status?.Invoke("Обери другу точку або паралельну лінію; Enter — довжина вибраної лінії."); return; }
        var first = firstPick;
        var dimension = new DrawingElement(Guid.NewGuid(), ElementKind.Dimension, first.Point, pick.Point)
        { StartReference = first.Reference, EndReference = pick.Reference };
        try
        {
            dimension = AssociativeDimensions.Resolve(dimension, session.Document.Elements.ToDictionary(e => e.Id));
            if (dimension.LengthMm < 1e-9) { Status?.Invoke("Обери дві різні точки або лінії."); return; }
            pendingDimension = dimension; PlaceDimension();
            Status?.Invoke("Перемісти розмірну лінію та клацни для розміщення.");
        }
        catch (InvalidDataException ex) { Status?.Invoke(ex.Message); }
    }

    private void DimensionSingleEdge()
    {
        if (firstPick?.Reference is not { Kind: ReferenceKind.Edge } reference) return;
        var e = session.Document.Elements.Single(e => e.Id == reference.ElementId);
        var edge = AssociativeDimensions.Edges(e)[reference.Index];
        // Edge endpoints use parametric references but must be measured along
        // the edge, so convert them into the corresponding vertex references.
        var vertices = AssociativeDimensions.Vertices(e);
        firstPick = new(edge.A, new(e.Id, ReferenceKind.Vertex, Array.IndexOf(vertices, edge.A)));
        PickDimension(new(edge.B, new(e.Id, ReferenceKind.Vertex, Array.IndexOf(vertices, edge.B))));
    }

    private void PlaceDimension()
    {
        if (pendingDimension is not { } d) return;
        var v = (d.B - d.A) * (1 / d.LengthMm);
        var offset = (cursor.X - d.A.X) * -v.Y + (cursor.Y - d.A.Y) * v.X;
        pendingDimension = d with { DimensionOffset = offset };
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        sceneRenderer = new(session.Document, session.Selection, scale, origin);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#e8edf3")), Bounds.WithX(0).WithY(0));
        var doc = session.Document;
        var paper = new Rect(Screen(new(0, 0)), new Size(doc.WidthMm * scale, doc.HeightMm * scale));
        context.DrawRectangle(Brushes.White, new Pen(Brushes.SlateGray, 1), paper);
        if (session.ActivePage.ShowFrame) DrawPageFrame(context, session.ActivePage);
        if (GridVisible)
        {
            // Thin the visible grid at low zoom; the snap step stays unchanged.
            var step = GridStep;
            while (step * scale < 8) step *= 2;
            var min = World(new Point(0, 0)); var max = World(new Point(Bounds.Width, Bounds.Height));
            for (var x = Math.Max(0, Math.Ceiling(min.X / step) * step); x <= Math.Min(doc.WidthMm, max.X); x += step)
                for (var y = Math.Max(0, Math.Ceiling(min.Y / step) * step); y <= Math.Min(doc.HeightMm, max.Y); y += step)
                    context.DrawEllipse(Brushes.LightSlateGray, null, Screen(new(x, y)), .65, .65);
        }
        DrawingElement[] displayed;
        try
        {
            displayed = vertexElementId is { } vertexOwner
                ? session.PreviewMoveVertex(vertexOwner, vertexIndex, vertexTarget)
                : dragStart is not null ? session.PreviewMove(dragDelta) : doc.Elements;
        }
        catch (InvalidDataException) { displayed = doc.Elements; }
        foreach (var element in displayed)
        {
            if (element.Id == editingElementId) continue;
            var selected = session.Selection.Contains(element.Id);
            var brush = selected ? Brushes.RoyalBlue : ElementBrush(element.Kind);
            Draw(context, element, brush);
        }
        if (anchor is { } a && Tool is { } kind && kind is not (ElementKind.Wire or ElementKind.Polyline or ElementKind.Arc or ElementKind.Symbol))
            Draw(context, new(Guid.Empty, kind, a, EndPoint(a, cursor)), Brushes.Teal);
        var previewWire = PreviewWire();
        if (Tool == ElementKind.Wire && previewWire.Length >= 2)
            for (var i = 1; i < previewWire.Length; i++)
                context.DrawLine(new Pen(Brushes.SeaGreen, Math.Max(1.5, .42 * scale)), Screen(previewWire[i - 1]), Screen(previewWire[i]));
        if (Tool == ElementKind.Wire && wirePoints.Count > 0 && !LineInput.IsVisible)
        {
            var end = LineInput.HasInput ? WireInputEnd(wirePoints[^1], cursor) : previewWire[^1];
            DrawLiveGeometryValues(context, Screen(end), ElementKind.Wire, end - wirePoints[^1]);
        }
        var previewPolyline = PreviewPolyline();
        if (Tool == ElementKind.Polyline && previewPolyline.Length >= 2)
            for (var i = 1; i < previewPolyline.Length; i++)
                context.DrawLine(new Pen(Brushes.Teal, Math.Max(1, .25 * scale)), Screen(previewPolyline[i - 1]), Screen(previewPolyline[i]));
        if (Tool == ElementKind.Polyline && wirePoints.Count > 0 && !LineInput.IsVisible && previewPolyline.Length > 1)
            DrawLiveGeometryValues(context, Screen(previewPolyline[^1]), ElementKind.Polyline, previewPolyline[^1] - wirePoints[^1]);
        if (Tool == ElementKind.Arc && arcPoints.Count == 1)
        {
            if (LineInput.FirstValue is { } radius && LineInput.SecondValue is { } sweep && LineInput.Valid)
            {
                try
                {
                    var start = Geometry.Polar(arcPoints[0], radius, (cursor - arcPoints[0]).Length > 1e-9 ? Geometry.Angle(cursor - arcPoints[0]) : 0);
                    Draw(context, new(Guid.Empty, ElementKind.Arc, arcPoints[0], start) { ArcSweepDegrees = sweep }, Brushes.Teal);
                }
                catch (ArgumentOutOfRangeException) { }
            }
            else context.DrawLine(new Pen(Brushes.Teal, 1), Screen(arcPoints[0]), Screen(cursor));
        }
        else if (Tool == ElementKind.Arc && arcPoints.Count == 2)
        {
            try { Draw(context, ArcGeometry.FromThreePoints(Guid.Empty, arcPoints[0], arcPoints[1], cursor), Brushes.Teal); }
            catch (InvalidDataException) { }
        }
        if (anchor is { } geometryStart && Tool is ElementKind.Line or ElementKind.Rectangle or ElementKind.Circle && !LineInput.IsVisible)
        {
            var end = EndPoint(geometryStart, cursor);
            DrawLiveGeometryValues(context, Screen(end), Tool.Value, end - geometryStart);
        }
        if (pendingDimension is { } dim) Draw(context, dim, Brushes.Teal);
        foreach (var pick in new[] { firstPick, hoverPick }.OfType<GeometryPick>())
        {
            if (pick.Reference is { Kind: ReferenceKind.Edge } edgeRef)
            {
                var owner = doc.Elements.FirstOrDefault(e => e.Id == edgeRef.ElementId);
                if (owner is not null)
                {
                    var edge = AssociativeDimensions.Edges(owner)[edgeRef.Index];
                    context.DrawLine(new Pen(Brushes.DarkOrange, 2), Screen(edge.A), Screen(edge.B));
                }
            }
            var p = Screen(pick.Point);
            context.DrawRectangle(null, new Pen(Brushes.DarkOrange, 2), new Rect(p.X - 4, p.Y - 4, 8, 8));
        }
        if (boxStart is { } box)
        {
            var left = Screen(box); var right = Screen(boxEnd); var crossing = boxEnd.X < box.X;
            var rectangle = new Rect(Math.Min(left.X, right.X), Math.Min(left.Y, right.Y), Math.Abs(right.X - left.X), Math.Abs(right.Y - left.Y));
            context.DrawRectangle(new SolidColorBrush(Color.Parse(crossing ? "#2033aa66" : "#203366ee")),
                new Pen(crossing ? Brushes.SeaGreen : Brushes.RoyalBlue, 1, crossing ? DashStyle.Dash : null), rectangle);
        }
    }

    private CanvasRenderer? sceneRenderer;
    private CanvasRenderer Renderer => sceneRenderer ?? new(session.Document, session.Selection, scale, origin);
    private void DrawPageFrame(DrawingContext context, DrawingPage page) => Renderer.DrawPageFrame(context, page);
    private void Draw(DrawingContext context, DrawingElement element, IBrush brush) => Renderer.Draw(context, element, brush);
    private static IBrush ElementBrush(ElementKind kind) => CanvasRenderer.ElementBrush(kind);
    private static void DrawLiveGeometryValues(DrawingContext context, Point point, ElementKind kind, PointMm delta) => CanvasRenderer.DrawLiveGeometryValues(context, point, kind, delta);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (FromInput(e.Source)) return;
        Focus();
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsMiddleButtonPressed)
        {
            panStart = point.Position; e.Pointer.Capture(this); e.Handled = true; return;
        }
        if (point.Properties.IsRightButtonPressed) { Cancel(); return; }
        if (!point.Properties.IsLeftButtonPressed) return;
        var raw = World(point.Position); cursor = Tool is ElementKind.Line or ElementKind.Rectangle or ElementKind.Circle or ElementKind.Dimension or ElementKind.Wire or ElementKind.Polyline or ElementKind.Arc ? Pick(raw).Point : Snap(raw);
        if (operation == CanvasOperation.SplitSegment)
        {
            var pick = AssociativeDimensions.Pick(session.Document.Elements, raw, 8 / scale);
            try
            {
                if (pick.Reference is not { Kind: ReferenceKind.Edge } reference)
                    throw new InvalidDataException("Обери внутрішню точку лінії або сегмента полілінії.");
                session.Apply(SegmentEditing.Split(session.Document.Elements, reference));
                Cancel(); Status?.Invoke("Сегмент розбито.");
            }
            catch (InvalidDataException ex) { Status?.Invoke(ex.Message); }
            InvalidateVisual(); e.Handled = true; return;
        }
        if (operation is CanvasOperation.Trim or CanvasOperation.Extend)
        {
            var pick = AssociativeDimensions.Pick(session.Document.Elements, raw, 8 / scale);
            try
            {
                var reference = AsLineEdge(pick.Reference);
                if (reference is null)
                    throw new InvalidDataException("Обери пряму лінію або межу.");
                if (operationTarget is null)
                {
                    var owner = session.Document.Elements.Single(item => item.Id == reference.ElementId);
                    if (owner.Kind != ElementKind.Line) throw new InvalidDataException("Ціль має бути прямою лінією.");
                    operationTarget = reference;
                    Status?.Invoke(operation == CanvasOperation.Trim
                        ? "Тепер клацни пряму межу обрізання."
                        : "Тепер клацни пряму межу продовження.");
                }
                else
                {
                    var wasTrim = operation == CanvasOperation.Trim;
                    var changed = wasTrim
                        ? SegmentEditing.Trim(session.Document.Elements, operationTarget, reference)
                        : SegmentEditing.Extend(session.Document.Elements, operationTarget, reference);
                    session.Apply(changed); Cancel();
                    Status?.Invoke(wasTrim ? "Лінію обрізано." : "Лінію продовжено.");
                }
            }
            catch (InvalidDataException ex) { Status?.Invoke(ex.Message); }
            InvalidateVisual(); e.Handled = true; return;
        }
        if (Tool == ElementKind.Wire)
        {
            var pick = Pick(raw); AddWirePoint(pick, e.ClickCount >= 2);
            InvalidateVisual(); e.Handled = true; return;
        }
        if (Tool == ElementKind.Polyline)
        {
            var pick = Pick(raw); AddPolylinePoint(pick, e.ClickCount >= 2);
            InvalidateVisual(); e.Handled = true; return;
        }
        if (Tool == ElementKind.Arc)
        { AddArcPoint(Pick(raw).Point); InvalidateVisual(); e.Handled = true; return; }
        if (Tool == ElementKind.Symbol)
        { AddSymbol(Snap(raw)); InvalidateVisual(); e.Handled = true; return; }
        if (Tool == ElementKind.Junction)
        { AddJunction(raw); InvalidateVisual(); e.Handled = true; return; }
        if (Tool == ElementKind.Text)
        {
            var p = Snap(raw);
            TextPlacementRequested?.Invoke(new(null, p, "", 3.5));
            e.Handled = true; return;
        }
        if (Tool == ElementKind.Dimension)
        { PickDimension(Pick(raw)); InvalidateVisual(); e.Handled = true; return; }
        if (Tool is { } kind)
        {
            if (anchor is not { } a)
            {
                anchor = cursor;
                LineInput.Begin(kind, kind == ElementKind.Line && ExactLength > 0 ? ExactLength : null);
                UpdateGeometryInput();
            }
            else
            {
                CommitGeometry(); e.Handled = true; return;
            }
        }
        else
        {
            var vertex = session.Document.Elements.Where(item => session.Selection.Contains(item.Id) && item.Kind == ElementKind.Polyline)
                .SelectMany(item => item.Points!.Select((p, index) => (Element: item, Point: p, Index: index)))
                .Where(item => (item.Point - raw).Length <= 6 / scale)
                .OrderBy(item => (item.Point - raw).Length).FirstOrDefault();
            if (vertex.Element is not null)
            {
                vertexElementId = vertex.Element.Id; vertexIndex = vertex.Index; vertexTarget = vertex.Point;
                e.Pointer.Capture(this); Status?.Invoke($"Переміщення вершини {vertex.Index + 1} полілінії.");
                InvalidateVisual(); e.Handled = true; return;
            }
            var hit = session.Document.Elements.LastOrDefault(el => el.Hit(raw, 6 / scale));
            var additive = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
            if (hit is null)
            {
                boxStart = raw; boxEnd = raw; boxAdditive = additive;
                e.Pointer.Capture(this); InvalidateVisual(); e.Handled = true; return;
            }
            // Preserve a multi-selection when dragging an already selected item.
            if (hit is null || additive || !session.Selection.Contains(hit.Id)) session.Select(hit, additive);
            if (hit is not null) { dragStart = cursor; dragDelta = default; e.Pointer.Capture(this); }
            if (e.ClickCount == 2 && hit?.Kind is ElementKind.Line or ElementKind.Rectangle or ElementKind.Circle)
            { dragStart = null; EditSelectedGeometry(); }
            else if (e.ClickCount == 2 && hit?.Kind == ElementKind.Text)
            {
                dragStart = null;
                TextPlacementRequested?.Invoke(new(hit.Id, hit.A, hit.Text!, hit.TextHeightMm));
            }
            else if (e.ClickCount == 2 && hit?.Kind == ElementKind.Symbol)
            {
                dragStart = null;
                var label = session.Document.Elements.FirstOrDefault(item => item.Kind == ElementKind.Text && item.LinkedElementId == hit.Id);
                if (label is not null)
                    TextPlacementRequested?.Invoke(new(label.Id, label.A, label.Text!, label.TextHeightMm));
            }
            else if (hit?.Kind == ElementKind.Wire)
            {
                var net = ElectricalConnectivity.Build(session.Document.Elements).FirstOrDefault(n => n.WireIds.Contains(hit.Id));
                if (net is not null)
                    Status?.Invoke($"Коло {net.Number}: провідників {net.WireIds.Length}, підключених контактів {net.Pins.Length}.");
            }
        }
        UpdateGeometryInput();
        InvalidateVisual(); e.Handled = true;
    }

    private GeometryReference? AsLineEdge(GeometryReference? reference)
    {
        if (reference?.Kind == ReferenceKind.Edge) return reference;
        if (reference is not { Kind: ReferenceKind.Vertex, Index: 0 or 1 }) return null;
        var owner = session.Document.Elements.FirstOrDefault(item => item.Id == reference.ElementId);
        return owner?.Kind == ElementKind.Line
            ? new GeometryReference(owner.Id, ReferenceKind.Edge, 0, reference.Index)
            : null;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (FromInput(e.Source)) return;
        var p = e.GetPosition(this);
        if (panStart is { } previous) { origin += p - previous; panStart = p; }
        var raw = World(p);
        hoverPick = Tool is ElementKind.Dimension or ElementKind.Line or ElementKind.Rectangle or ElementKind.Circle or ElementKind.Wire or ElementKind.Polyline or ElementKind.Arc || operation != CanvasOperation.None ? Pick(raw) :
            Tool == ElementKind.Junction ? new(ConnectionSnap.Pick(session.Document.Elements, raw, 8 / scale, Snap(raw)), null) : null;
        cursor = hoverPick?.Point ?? Snap(raw);
        if (boxStart is not null) boxEnd = raw;
        if (dragStart is { } start) dragDelta = cursor - start;
        if (vertexElementId is not null) vertexTarget = Snap(raw);
        PlaceDimension(); UpdateGeometryInput();
        var details = anchor is { } a ? GeometryStatus(a, Tool switch
        {
            ElementKind.Wire => WireInputEnd(a, cursor),
            ElementKind.Polyline => PolylineInputEnd(a, cursor),
            _ => EndPoint(a, cursor)
        }) : "";
        var snapHint = hoverPick?.Reference is { } reference ? $" · Прив’язка: {reference.Kind}" : SnapEnabled ? " · Сітка" : " · Без прив’язки";
        var selectionHint = boxStart is { } startBox ? (boxEnd.X < startBox.X ? " · Перетин рамкою" : " · Повністю в рамці") : "";
        Status?.Invoke($"X {cursor.X:0.###} мм   Y {cursor.Y:0.###} мм{details} · {scale / 2.4 * 100:0}%{snapHint}{selectionHint}");
        InvalidateVisual();
    }

    private string GeometryStatus(PointMm start, PointMm end)
    {
        var delta = end - start;
        return Tool switch
        {
            ElementKind.Line => $"   Довжина {delta.Length:0.###} мм   Кут {Geometry.Angle(delta):0.###}°",
            ElementKind.Rectangle => $"   Ширина {Math.Abs(delta.X):0.###} мм   Висота {Math.Abs(delta.Y):0.###} мм",
            ElementKind.Circle => $"   Радіус {delta.Length:0.###} мм   Діаметр {delta.Length * 2:0.###} мм",
            ElementKind.Wire => $"   Сегмент {delta.Length:0.###} мм   Кут {Geometry.Angle(delta):0.###}°",
            ElementKind.Polyline => $"   Сегмент {delta.Length:0.###} мм   Кут {Geometry.Angle(delta):0.###}°",
            _ => $"   Довжина {delta.Length:0.###} мм"
        };
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (FromInput(e.Source)) return;
        if (boxStart is { } box && e.InitialPressMouseButton == MouseButton.Left)
        {
            boxEnd = World(e.GetPosition(this));
            session.SelectBox(SelectionBox.From(box, boxEnd), boxEnd.X < box.X, boxAdditive);
            boxStart = null;
        }
        if (dragStart is not null && e.InitialPressMouseButton == MouseButton.Left)
        {
            var delta = dragDelta; dragStart = null; dragDelta = default; session.Move(delta);
        }
        if (vertexElementId is { } vertexOwner && e.InitialPressMouseButton == MouseButton.Left)
        {
            try { session.MoveVertex(vertexOwner, vertexIndex, vertexTarget); Status?.Invoke("Вершину полілінії переміщено."); }
            catch (InvalidDataException ex) { Status?.Invoke(ex.Message); }
            vertexElementId = null; vertexIndex = -1;
        }
        panStart = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        dragStart = null; dragDelta = default; vertexElementId = null; vertexIndex = -1;
        panStart = null; boxStart = null; InvalidateVisual();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (dragStart is not null || boxStart is not null || FromInput(e.Source)) return;
        var p = e.GetPosition(this); var fixedPoint = World(p);
        scale = Math.Clamp(scale * Math.Pow(1.15, e.Delta.Y), .2, 30);
        origin = new(p.X - fixedPoint.X * scale, p.Y - fixedPoint.Y * scale);
        cursor = Snap(World(p)); UpdateGeometryInput(); PlaceDimension(); InvalidateVisual(); e.Handled = true;
    }

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (HasParameterInput && anchor is not null && !LineInput.IsKeyboardFocusWithin &&
            !string.IsNullOrEmpty(e.Text) && e.Text.All(c => char.IsDigit(c) || c is '.' or ',' or '-' or '+'))
        { LineInput.StartTyping(e.Text); e.Handled = true; }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (FromInput(e.Source)) return;
        if (e.Key == Key.Tab && anchor is not null && HasParameterInput)
        { LineInput.FocusField(false); e.Handled = true; return; }
        if (e.Key == Key.Enter)
        {
            if (HasGeometryInput) CommitGeometry();
            else if (Tool == ElementKind.Wire) CommitWireParameter();
            else if (Tool == ElementKind.Polyline) CommitPolylineParameter();
            else if (Tool == ElementKind.Arc) CommitArcParameter();
            else if (Tool == ElementKind.Dimension)
            {
                if (pendingDimension is not null) { session.Add(pendingDimension); Cancel(); }
                else DimensionSingleEdge();
                InvalidateVisual();
            }
            e.Handled = true; return;
        }
        if (e.Key == Key.Space && Tool == ElementKind.Wire && wirePoints.Count > 0)
        {
            wireHorizontalFirst = !wireHorizontalFirst;
            Status?.Invoke(wireHorizontalFirst ? "Кут провідника: спочатку горизонтально." : "Кут провідника: спочатку вертикально.");
            InvalidateVisual(); e.Handled = true; return;
        }
        if (Commands is not null) { Commands.Handle(e); return; }
        if (e.Key == Key.Escape) { EscapeToSelection(); e.Handled = true; }
        else if (e.Key == Key.Delete) { Cancel(); session.Delete(); e.Handled = true; }
        else if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.L or Key.C or Key.R or Key.D or Key.P or Key.A)
        {
            switch (e.Key)
            {
                case Key.L:
                    ActivatePathTool();
                    Status?.Invoke(ActivePathKind == ElementKind.Wire
                        ? "Провідник (L): задай початок траси."
                        : "Графічна лінія (L): задай початок.");
                    break;
                case Key.C: SetTool(ElementKind.Circle); Status?.Invoke("Коло (C): задай центр."); break;
                case Key.R: SetTool(ElementKind.Rectangle); Status?.Invoke("Прямокутник (R): задай перший кут."); break;
                case Key.D: SetTool(ElementKind.Dimension); Status?.Invoke("Розмір (D): обери дві опорні геометрії."); break;
                case Key.P: SetTool(ElementKind.Polyline); Status?.Invoke("Полілінія (P): задай першу вершину."); break;
                case Key.A: SetTool(ElementKind.Arc); Status?.Invoke("Дуга (A): задай початкову точку."); break;
            }
            e.Handled = true;
        }
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Cancel();
            switch (e.Key)
            {
                case Key.Z: session.Undo(); break;
                case Key.Y: session.Redo(); break;
                case Key.G:
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) session.Ungroup(); else session.Group(); break;
                case Key.C: session.Copy(); Status?.Invoke($"Скопійовано: {session.Selection.Count}."); break;
                case Key.V: session.Paste(); Status?.Invoke("Копію вставлено зі зміщенням."); break;
                case Key.R:
                    try { session.RotateSelection90(); }
                    catch (InvalidDataException ex) { Status?.Invoke(ex.Message); }
                    break;
                case Key.A:
                    session.Selection.UnionWith(session.Document.Elements.Select(el => el.Id)); InvalidateVisual(); break;
                default: return;
            }
            e.Handled = true;
        }
    }
}

public sealed record TextPlacementRequest(Guid? ElementId, PointMm Position, string Text, double HeightMm);
