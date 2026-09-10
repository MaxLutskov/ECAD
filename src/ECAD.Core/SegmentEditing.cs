namespace ECAD.Core;

public static class SegmentEditing
{
    public static DrawingElement[] Trim(DrawingElement[] elements, GeometryReference target, GeometryReference boundary) =>
        ChangeLineEnd(elements, target, boundary, extend: false);

    public static DrawingElement[] Extend(DrawingElement[] elements, GeometryReference target, GeometryReference boundary) =>
        ChangeLineEnd(elements, target, boundary, extend: true);

    public static DrawingElement[] Split(DrawingElement[] elements, GeometryReference reference)
    {
        if (reference.Kind != ReferenceKind.Edge || reference.Parameter is <= 1e-7 or >= .9999999)
            throw new InvalidDataException("Обери внутрішню точку сегмента, а не його кінець.");
        var owner = elements.SingleOrDefault(e => e.Id == reference.ElementId)
            ?? throw new InvalidDataException("Не знайдено сегмент.");
        if (owner.Kind == ElementKind.Polyline)
        {
            var points = owner.Points!.ToList();
            if (reference.Index < 0 || reference.Index >= points.Count - 1)
                throw new InvalidDataException("Не знайдено сегмент полілінії.");
            var point = points[reference.Index] + (points[reference.Index + 1] - points[reference.Index]) * reference.Parameter;
            points.Insert(reference.Index + 1, point);
            return elements.Select(e => e.Id == owner.Id
                ? owner with { A = points[0], B = points[^1], Points = [.. points] }
                : RemapPolylineReferences(e, owner.Id, reference.Index, reference.Parameter)).ToArray();
        }
        if (owner.Kind != ElementKind.Line)
            throw new InvalidDataException("Розбиття зараз доступне для лінії та полілінії.");
        if (elements.Any(e => e.Kind == ElementKind.Dimension && e.DimensionMode == DimensionMode.Driving &&
            (e.StartReference?.ElementId == owner.Id || e.EndReference?.ElementId == owner.Id)))
            throw new InvalidDataException("Спочатку переведи керувальний розмір цієї лінії у довідковий режим.");
        var split = owner.A + (owner.B - owner.A) * reference.Parameter;
        var secondId = Guid.NewGuid();
        var first = owner with { B = split };
        var second = owner with { Id = secondId, A = split, Name = owner.Name is null ? null : owner.Name + " (2)" };
        var result = new List<DrawingElement>(elements.Length + 1);
        foreach (var element in elements)
        {
            if (element.Id == owner.Id) { result.Add(first); result.Add(second); continue; }
            result.Add(RemapLineReferences(element, owner.Id, secondId, reference.Parameter));
        }
        return [.. result];
    }

    private static DrawingElement RemapLineReferences(DrawingElement element, Guid ownerId, Guid secondId, double split)
    {
        GeometryReference? Remap(GeometryReference? reference)
        {
            if (reference is null || reference.ElementId != ownerId) return reference;
            if (reference.Kind == ReferenceKind.Vertex && reference.Index == 1)
                return reference with { ElementId = secondId };
            if (reference.Kind != ReferenceKind.Edge) return reference;
            return reference.Parameter <= split
                ? reference with { Parameter = reference.Parameter / split }
                : reference with { ElementId = secondId, Parameter = (reference.Parameter - split) / (1 - split) };
        }
        return element.Kind == ElementKind.Dimension
            ? element with { StartReference = Remap(element.StartReference), EndReference = Remap(element.EndReference) }
            : element;
    }

    private static DrawingElement RemapPolylineReferences(DrawingElement element, Guid ownerId, int segment, double split)
    {
        GeometryReference? Remap(GeometryReference? reference)
        {
            if (reference is null || reference.ElementId != ownerId) return reference;
            if (reference.Kind == ReferenceKind.Vertex && reference.Index > segment)
                return reference with { Index = reference.Index + 1 };
            if (reference.Kind != ReferenceKind.Edge) return reference;
            if (reference.Index > segment) return reference with { Index = reference.Index + 1 };
            if (reference.Index != segment) return reference;
            return reference.Parameter <= split
                ? reference with { Parameter = reference.Parameter / split }
                : reference with { Index = segment + 1, Parameter = (reference.Parameter - split) / (1 - split) };
        }
        return element.Kind == ElementKind.Dimension
            ? element with { StartReference = Remap(element.StartReference), EndReference = Remap(element.EndReference) }
            : element;
    }

    private static DrawingElement[] ChangeLineEnd(DrawingElement[] elements, GeometryReference targetReference,
        GeometryReference boundaryReference, bool extend)
    {
        if (targetReference.Kind != ReferenceKind.Edge || boundaryReference.Kind != ReferenceKind.Edge ||
            targetReference.ElementId == boundaryReference.ElementId)
            throw new InvalidDataException("Спочатку обери цільову лінію, потім іншу пряму межу.");
        var target = elements.SingleOrDefault(e => e.Id == targetReference.ElementId);
        var boundaryOwner = elements.SingleOrDefault(e => e.Id == boundaryReference.ElementId);
        if (target?.Kind != ElementKind.Line || boundaryOwner is null)
            throw new InvalidDataException("Обрізання і продовження зараз доступні для прямої лінії.");
        if (elements.Any(e => e.Kind == ElementKind.Dimension && e.DimensionMode == DimensionMode.Driving &&
            (e.StartReference?.ElementId == target.Id || e.EndReference?.ElementId == target.Id)))
            throw new InvalidDataException("Спочатку переведи керувальний розмір цієї лінії у довідковий режим.");
        var boundaryEdges = AssociativeDimensions.Edges(boundaryOwner);
        if (boundaryReference.Index < 0 || boundaryReference.Index >= boundaryEdges.Length)
            throw new InvalidDataException("Не знайдено межу.");
        var boundary = boundaryEdges[boundaryReference.Index];
        var r = target.B - target.A; var s = boundary.B - boundary.A;
        var denominator = Cross(r, s);
        if (Math.Abs(denominator) < 1e-10)
            throw new InvalidDataException("Цільова лінія та межа паралельні.");
        var delta = boundary.A - target.A;
        var t = Cross(delta, s) / denominator;
        var u = Cross(delta, r) / denominator;
        if (u < -1e-7 || u > 1 + 1e-7)
            throw new InvalidDataException("Межа не перетинає напрям цільової лінії.");
        if (!extend && (t <= 1e-7 || t >= .9999999))
            throw new InvalidDataException("Для обрізання межа повинна перетинати лінію всередині.");
        if (extend && t is >= -1e-7 and <= 1.0000001)
            throw new InvalidDataException("Лінія вже доходить до межі; для видалення частини використай обрізання.");
        var intersection = target.A + r * t;
        var moveStart = targetReference.Parameter < .5;
        if (extend && moveStart != (t < 0))
            throw new InvalidDataException("Обери кінець лінії, спрямований до межі.");
        var updated = moveStart ? target with { A = intersection } : target with { B = intersection };
        var movedFrom = moveStart ? target.A : target.B;
        var result = elements.Select(e => e.Id == target.Id ? updated :
            RemapChangedLineReferences(MoveConnected(e, movedFrom, intersection), target, updated)).ToArray();
        return result;
    }

    private static DrawingElement RemapChangedLineReferences(DrawingElement element, DrawingElement oldLine, DrawingElement newLine)
    {
        GeometryReference? Remap(GeometryReference? reference)
        {
            if (reference is not { Kind: ReferenceKind.Edge } || reference.ElementId != oldLine.Id) return reference;
            var oldPoint = oldLine.A + (oldLine.B - oldLine.A) * reference.Parameter;
            return reference with { Parameter = Math.Clamp(AssociativeDimensions.Parameter(oldPoint, newLine.A, newLine.B), 0, 1) };
        }
        return element.Kind == ElementKind.Dimension
            ? element with { StartReference = Remap(element.StartReference), EndReference = Remap(element.EndReference) }
            : element;
    }

    private static DrawingElement MoveConnected(DrawingElement element, PointMm from, PointMm to)
    {
        bool Matches(PointMm p) => (p - from).Length <= 1e-7;
        if (element.Kind == ElementKind.Junction && Matches(element.A)) return element with { A = to, B = to };
        if (element.Kind != ElementKind.Wire) return element;
        var points = element.Points!.Select(p => Matches(p) ? to : p).ToArray();
        return element with { A = points[0], B = points[^1], Points = points };
    }

    private static double Cross(PointMm a, PointMm b) => a.X * b.Y - a.Y * b.X;
}
