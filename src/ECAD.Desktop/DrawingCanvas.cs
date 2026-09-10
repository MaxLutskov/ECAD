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
    private readonly EditorSession session;
    private PointMm? anchor;
    private PointMm cursor;
    private PointMm? dragStart;
    private PointMm dragDelta;
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
    private bool wireHorizontalFirst = true;
    private Point inputPosition;
    public LineInputPanel LineInput { get; } = new();
    public ElementKind? Tool { get; private set; }
    public bool IsDrawing => anchor is not null;
    public double GridStep { get; set; } = 2.5;
    public double ExactLength { get; set; }
    public bool SnapEnabled { get; set; } = true;
    public bool GridVisible { get; set; } = true;
    public string ActiveSymbolKey { get; set; } = SymbolLibrary.All[0].Key;
    public event Action<string>? Status;
    public event Action<TextPlacementRequest>? TextPlacementRequested;

    public DrawingCanvas(EditorSession session)
    {
        this.session = session;
        Focusable = true; ClipToBounds = true;
        session.Changed += InvalidateVisual;
        Child = LineInput;
        LineInput.Edited += () => { UpdateGeometryInput(); InvalidateVisual(); };
        LineInput.Confirm += () => { if (Tool == ElementKind.Wire) CommitWireParameter(); else CommitGeometry(); };
        LineInput.Cancelled += EscapeToSelection;
    }

    private Point Screen(PointMm p) => new(origin.X + p.X * scale, origin.Y + p.Y * scale);
    protected override Size MeasureOverride(Size availableSize)
    { LineInput.Measure(new Size(300, double.PositiveInfinity)); return default; }
    protected override Size ArrangeOverride(Size finalSize)
    { LineInput.Arrange(new Rect(inputPosition, LineInput.DesiredSize)); return finalSize; }
    private PointMm World(Point p) => new((p.X - origin.X) / scale, (p.Y - origin.Y) / scale);
    private PointMm Snap(PointMm p) => SnapEnabled ? Geometry.Snap(p, GridStep) : p;
    public void SetTool(ElementKind? tool) { Cancel(); Tool = tool; Focus(); InvalidateVisual(); }
    public void EscapeToSelection()
    {
        Cancel(); Tool = null; Focus();
        Status?.Invoke("Режим вибору / переміщення.");
    }
    public void Cancel()
    {
        var hadFocus = LineInput.IsKeyboardFocusWithin;
        anchor = null; dragStart = null; dragDelta = default; panStart = null;
        boxStart = null; firstPick = null; hoverPick = null; pendingDimension = null; editingElementId = null;
        wirePoints.Clear();
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
        return Geometry.Polar(start, length, LineInput.SecondValue ?? Geometry.Angle(end - start));
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
    private bool HasParameterInput => HasGeometryInput || Tool == ElementKind.Wire && wirePoints.Count > 0;

    private bool FromInput(object? source) => source is Visual v && (v == LineInput || v.GetVisualAncestors().Contains(LineInput));

    private void UpdateGeometryInput()
    {
        if (anchor is not { } a || !HasParameterInput) return;
        var end = Tool == ElementKind.Wire ? WireInputEnd(a, cursor) : EndPoint(a, cursor);
        var delta = end - a;
        if (Tool == ElementKind.Rectangle) LineInput.UpdateLive(Math.Abs(delta.X), Math.Abs(delta.Y));
        else if (Tool == ElementKind.Circle) LineInput.UpdateLive(delta.Length, delta.Length * 2);
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

    private PointMm[] PreviewWire()
    {
        if (wirePoints.Count == 0) return [];
        if (LineInput.HasInput) return [.. wirePoints, WireInputEnd(wirePoints[^1], cursor)];
        return [.. wirePoints, .. OrthogonalRoute(wirePoints[^1], cursor)];
    }

    private PointMm WireInputEnd(PointMm start, PointMm end)
    {
        var delta = end - start;
        var angle = LineInput.SecondValue ?? CardinalAngle(delta);
        var length = LineInput.FirstValue ?? ProjectedCardinalLength(delta, angle);
        if (length <= 1e-9) return start;
        return Geometry.Polar(start, length, angle);
    }

    private static double CardinalAngle(PointMm delta) => Math.Abs(delta.X) >= Math.Abs(delta.Y)
        ? delta.X < 0 ? 180 : 0
        : delta.Y < 0 ? 90 : 270;

    private static double ProjectedCardinalLength(PointMm delta, double angle)
    {
        var normalized = ((angle % 360) + 360) % 360;
        return normalized is 0 or 180 ? Math.Abs(delta.X) : Math.Abs(delta.Y);
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
        var route = LineInput.HasInput ? new[] { WireInputEnd(wirePoints[^1], pick.Point) } : OrthogonalRoute(wirePoints[^1], pick.Point);
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
        var used = session.Document.Elements.Count(e => e.Kind == ElementKind.Symbol && e.DeviceTag!.StartsWith(definition.Prefix, StringComparison.Ordinal));
        var symbol = new DrawingElement(Guid.NewGuid(), ElementKind.Symbol, p, p)
        {
            SymbolKey = definition.Key, DeviceTag = definition.Prefix + (used + 1),
            PinOffsets = definition.Strokes is null ? null : definition.Pins,
            SymbolStrokes = definition.Strokes
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
        context.FillRectangle(new SolidColorBrush(Color.Parse("#e8edf3")), Bounds.WithX(0).WithY(0));
        var doc = session.Document;
        var paper = new Rect(Screen(new(0, 0)), new Size(doc.WidthMm * scale, doc.HeightMm * scale));
        context.DrawRectangle(Brushes.White, new Pen(Brushes.SlateGray, 1), paper);
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
        var displayed = dragStart is not null ? session.PreviewMove(dragDelta) : doc.Elements;
        foreach (var element in displayed)
        {
            if (element.Id == editingElementId) continue;
            var selected = session.Selection.Contains(element.Id);
            Draw(context, element, selected ? Brushes.RoyalBlue : Brushes.Black);
        }
        if (anchor is { } a && Tool is { } kind && kind is not (ElementKind.Wire or ElementKind.Symbol))
            Draw(context, new(Guid.Empty, kind, a, EndPoint(a, cursor)), Brushes.Teal);
        var previewWire = PreviewWire();
        if (Tool == ElementKind.Wire && previewWire.Length >= 2)
            for (var i = 1; i < previewWire.Length; i++)
                context.DrawLine(new Pen(Brushes.Teal, Math.Max(1, .25 * scale)), Screen(previewWire[i - 1]), Screen(previewWire[i]));
        if (Tool == ElementKind.Wire && wirePoints.Count > 0 && !LineInput.IsVisible)
        {
            var end = LineInput.HasInput ? WireInputEnd(wirePoints[^1], cursor) : previewWire[^1];
            DrawLiveGeometryValues(context, Screen(end), ElementKind.Wire, end - wirePoints[^1]);
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

    private static void DrawLiveGeometryValues(DrawingContext context, Point at, ElementKind kind, PointMm delta)
    {
        var value = kind switch
        {
            ElementKind.Line => $"L {delta.Length:0.###} мм    ∠ {Geometry.Angle(delta):0.###}°",
            ElementKind.Rectangle => $"Ш {Math.Abs(delta.X):0.###} мм    В {Math.Abs(delta.Y):0.###} мм",
            ElementKind.Circle => $"R {delta.Length:0.###} мм    ⌀ {delta.Length * 2:0.###} мм",
            ElementKind.Wire => $"L {delta.Length:0.###} мм    ∠ {Geometry.Angle(delta):0.###}°",
            _ => ""
        };
        var text = new FormattedText(value, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 13, Brushes.DarkSlateGray);
        var x = at.X + 16; var y = at.Y + 16;
        context.DrawRectangle(Brushes.White, new Pen(Brushes.Teal, 1),
            new RoundedRect(new Rect(x - 6, y - 4, text.Width + 12, text.Height + 8), 4));
        context.DrawText(text, new Point(x, y));
    }

    private void Draw(DrawingContext ctx, DrawingElement e, IBrush brush)
    {
        var a = Screen(e.A); var b = Screen(e.B);
        var pen = new Pen(brush, Math.Max(1, .25 * scale));
        switch (e.Kind)
        {
            case ElementKind.Line: ctx.DrawLine(pen, a, b); break;
            case ElementKind.Wire:
                for (var i = 1; i < e.Points!.Length; i++) ctx.DrawLine(pen, Screen(e.Points[i - 1]), Screen(e.Points[i]));
                break;
            case ElementKind.Rectangle:
                ctx.DrawRectangle(null, pen, new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)));
                break;
            case ElementKind.Circle:
                ctx.DrawEllipse(null, pen, a, e.LengthMm * scale, e.LengthMm * scale); break;
            case ElementKind.Junction:
                var radius = Math.Max(2.5, .9 * scale);
                ctx.DrawEllipse(brush, null, a, radius, radius); break;
            case ElementKind.Text:
                var label = new FormattedText(e.Text!, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, Typeface.Default, Math.Max(6, e.TextHeightMm * scale), brush);
                using (ctx.PushTransform(new RotateTransform(e.RotationDegrees, a.X, a.Y).Value))
                    ctx.DrawText(label, new Point(a.X, a.Y - label.Height));
                break;
            case ElementKind.Symbol:
                DrawSymbol(ctx, e, pen, brush); break;
            case ElementKind.Dimension:
                var v = e.LengthMm > 1e-9 ? (e.B - e.A) * (1 / e.LengthMm) : new PointMm(1, 0);
                var n = new PointMm(-v.Y, v.X);
                var aa = e.A + n * e.DimensionOffset; var bb = e.B + n * e.DimensionOffset;
                foreach (var edge in e.DimensionSegments()) ctx.DrawLine(pen, Screen(edge.A), Screen(edge.B));
                foreach (var p in new[] { aa, bb })
                    ctx.DrawLine(pen, Screen(p - (v + n) * 1.2), Screen(p + (v + n) * 1.2));
                var text = new FormattedText($"{e.LengthMm:0.###} мм", CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, Typeface.Default, 13, brush);
                var middle = Screen((aa + bb) * .5);
                ctx.DrawRectangle(Brushes.White, null, new Rect(middle.X - text.Width / 2 - 3, middle.Y - 20, text.Width + 6, 18));
                ctx.DrawText(text, new Point(middle.X - text.Width / 2, middle.Y - 20));
                break;
        }
        if (session.Selection.Contains(e.Id))
            foreach (var p in AssociativeDimensions.Vertices(e).DefaultIfEmpty(e.A).Distinct().Select(Screen))
                ctx.DrawRectangle(Brushes.White, new Pen(brush, 1), new Rect(p.X - 3, p.Y - 3, 6, 6));
    }

    private void DrawSymbol(DrawingContext ctx, DrawingElement e, Pen pen, IBrush brush)
    {
        PointMm P(double x, double y) => Geometry.Rotate(e.A + new PointMm(x, y), e.A, e.RotationDegrees);
        void Line(double x1, double y1, double x2, double y2) => ctx.DrawLine(pen, Screen(P(x1, y1)), Screen(P(x2, y2)));
        if (e.SymbolStrokes is { } strokes)
            foreach (var stroke in strokes) Line(stroke.A.X, stroke.A.Y, stroke.B.X, stroke.B.Y);
        else switch (e.SymbolKey)
        {
            case "IEC_NO_CONTACT":
                Line(-7.5, 0, -2.5, 0); Line(-2.5, 0, 2.5, -3); Line(2.5, 0, 7.5, 0); break;
            case "IEC_NC_CONTACT":
                Line(-7.5, 0, -2.5, 0); Line(-2.5, 0, 2.5, 0); Line(2.5, 0, 7.5, 0); Line(-1.5, -3, 1.5, 3); break;
            case "IEC_COIL":
                Line(-7.5, 0, -3, 0); Line(3, 0, 7.5, 0);
                Line(-3, -4, 3, -4); Line(3, -4, 3, 4); Line(3, 4, -3, 4); Line(-3, 4, -3, -4);
                break;
            case "IEC_TERMINAL":
                ctx.DrawEllipse(null, pen, Screen(e.A), 2.2 * scale, 2.2 * scale); break;
            case "IEC_FUSE":
                Line(-7.5, 0, -3.5, 0); Line(3.5, 0, 7.5, 0);
                Line(-3.5, -2, 3.5, -2); Line(3.5, -2, 3.5, 2); Line(3.5, 2, -3.5, 2); Line(-3.5, 2, -3.5, -2); break;
            case "IEC_BREAKER":
                Line(-7.5, 0, -3, 0); Line(-3, 0, 2.5, -3); Line(3, 0, 7.5, 0); Line(-1, -4.5, 1, -2.5); break;
            case "IEC_MOTOR_3P":
                const double motorRadius = 7.5;
                var motorEdge = Math.Sqrt(motorRadius * motorRadius - 25);
                Line(-12.5, -5, -motorEdge, -5); Line(-12.5, 0, -motorRadius, 0); Line(-12.5, 5, -motorEdge, 5);
                ctx.DrawEllipse(null, pen, Screen(e.A), motorRadius * scale, motorRadius * scale);
                DrawCentredText(ctx, "M", Screen(P(0, -1)), 12, brush);
                DrawCentredText(ctx, "3~", Screen(P(0, 4)), 9, brush); break;
            case "IEC_GROUND":
                Line(0, -7.5, 0, 0); Line(-4, 0, 4, 0); Line(-2.8, 2, 2.8, 2); Line(-1.4, 4, 1.4, 4); break;
            case "IEC_VFD":
                Line(-10, -5, -7, -5); Line(-10, 0, -7, 0); Line(-10, 5, -7, 5);
                Line(7, -5, 10, -5); Line(7, 0, 10, 0); Line(7, 5, 10, 5);
                Line(-7, -8, 7, -8); Line(7, -8, 7, 8); Line(7, 8, -7, 8); Line(-7, 8, -7, -8);
                Line(-5, 4, 5, -4); break;
            case "ANSI_NO_CONTACT":
                Line(-7.5, 0, -2.5, 0); Line(-2.5, 0, 2.5, -3); Line(2.5, 0, 7.5, 0); break;
            case "ANSI_NC_CONTACT":
                Line(-7.5, 0, -2.5, 0); Line(-2.5, 0, 2.5, 0); Line(2.5, 0, 7.5, 0); Line(-1.5, -3, 1.5, 3); break;
            case "ANSI_COIL":
                Line(-7.5, 0, -4, 0); Line(4, 0, 7.5, 0);
                ctx.DrawEllipse(null, pen, Screen(e.A), 4 * scale, 4 * scale); break;
        }
    }

    private static void DrawCentredText(DrawingContext ctx, string value, Point centre, double size, IBrush brush)
    {
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
        ctx.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
    }

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
        var raw = World(point.Position); cursor = Tool is ElementKind.Line or ElementKind.Rectangle or ElementKind.Circle or ElementKind.Dimension or ElementKind.Wire ? Pick(raw).Point : Snap(raw);
        if (Tool == ElementKind.Wire)
        {
            var pick = Pick(raw); AddWirePoint(pick, e.ClickCount >= 2);
            InvalidateVisual(); e.Handled = true; return;
        }
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

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (FromInput(e.Source)) return;
        var p = e.GetPosition(this);
        if (panStart is { } previous) { origin += p - previous; panStart = p; }
        var raw = World(p);
        hoverPick = Tool is ElementKind.Dimension or ElementKind.Line or ElementKind.Rectangle or ElementKind.Circle or ElementKind.Wire ? Pick(raw) :
            Tool == ElementKind.Junction ? new(ConnectionSnap.Pick(session.Document.Elements, raw, 8 / scale, Snap(raw)), null) : null;
        cursor = hoverPick?.Point ?? Snap(raw);
        if (boxStart is not null) boxEnd = raw;
        if (dragStart is { } start) dragDelta = cursor - start;
        PlaceDimension(); UpdateGeometryInput();
        var details = anchor is { } a ? GeometryStatus(a, Tool == ElementKind.Wire ? WireInputEnd(a, cursor) : EndPoint(a, cursor)) : "";
        Status?.Invoke($"X {cursor.X:0.###} мм   Y {cursor.Y:0.###} мм{details}");
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
        panStart = null;
        e.Pointer.Capture(null);
        InvalidateVisual();
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        dragStart = null; dragDelta = default; panStart = null; boxStart = null; InvalidateVisual();
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
        if (e.Key == Key.Escape) { EscapeToSelection(); e.Handled = true; }
        else if (e.Key == Key.Delete) { Cancel(); session.Delete(); e.Handled = true; }
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
