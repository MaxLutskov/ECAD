namespace ECAD.Core;

public static class DrivingDimensions
{
    private const double Tolerance = 1e-7;

    public static void Validate(DrawingElement[] elements)
    {
        var drivers = elements.Where(e => e.Kind == ElementKind.Dimension && e.DimensionMode == DimensionMode.Driving).ToArray();
        var keys = new HashSet<string>();
        foreach (var dimension in drivers)
        {
            _ = OwnerAndReferences(dimension, elements);
            var key = ConstraintKey(dimension, elements);
            if (!keys.Add(key))
                throw new InvalidDataException("Два керувальні розміри задають той самий параметр геометрії.");
        }
        var preview = elements;
        foreach (var dimension in drivers) preview = ApplyOne(preview, dimension.Id);
    }

    public static DrawingElement[] ApplyAll(DrawingElement[] elements)
    {
        Validate(elements);
        var result = elements;
        foreach (var dimension in elements.Where(e => e.Kind == ElementKind.Dimension && e.DimensionMode == DimensionMode.Driving))
            result = ApplyOne(result, dimension.Id);
        return AssociativeDimensions.ResolveAll(result);
    }

    private static DrawingElement[] ApplyOne(DrawingElement[] elements, Guid dimensionId)
    {
        var dimension = elements.Single(e => e.Id == dimensionId);
        var (owner, start, end) = OwnerAndReferences(dimension, elements);
        var value = dimension.DimensionTargetMm!.Value;
        var replacements = new Dictionary<PointMm, PointMm>();
        DrawingElement resized;

        if (owner.Kind is ElementKind.Line or ElementKind.Wire)
        {
            if (owner.Kind == ElementKind.Wire && owner.Points!.Length != 2)
                throw new InvalidDataException("Керувальний розмір підтримує лише прямий провідник з одним сегментом.");
            if (start.Kind != ReferenceKind.Vertex || end.Kind != ReferenceKind.Vertex || start.Index == end.Index ||
                start.Index is < 0 or > 1 || end.Index is < 0 or > 1)
                throw new InvalidDataException("Для керування лінією прив’яжи розмір до двох її кінців.");
            var points = owner.Kind == ElementKind.Wire ? owner.Points! : new[] { owner.A, owner.B };
            var fixedPoint = points[start.Index]; var movingPoint = points[end.Index];
            var moved = dimension.DimensionType switch
            {
                DimensionType.Horizontal => new(fixedPoint.X + Signed(movingPoint.X - fixedPoint.X, value), movingPoint.Y),
                DimensionType.Vertical => new(movingPoint.X, fixedPoint.Y + Signed(movingPoint.Y - fixedPoint.Y, value)),
                DimensionType.Aligned => fixedPoint + Direction(movingPoint - fixedPoint) * value,
                _ => throw new InvalidDataException("Для лінії доступний вирівняний, горизонтальний або вертикальний керувальний розмір.")
            };
            var changed = points.ToArray(); changed[end.Index] = moved;
            resized = owner with { A = changed[0], B = changed[^1], Points = owner.Kind == ElementKind.Wire ? changed : null };
            replacements[movingPoint] = moved;
        }
        else if (owner.Kind == ElementKind.Rectangle)
        {
            if (start.Kind != ReferenceKind.Vertex || end.Kind != ReferenceKind.Vertex || start.Index == end.Index)
                throw new InvalidDataException("Для керування прямокутником прив’яжи розмір до двох його кутів.");
            var vertices = AssociativeDimensions.Vertices(owner);
            var fixedPoint = vertices[start.Index]; var movingPoint = vertices[end.Index];
            var horizontal = dimension.DimensionType == DimensionType.Horizontal ||
                dimension.DimensionType == DimensionType.Aligned && Math.Abs(movingPoint.Y - fixedPoint.Y) <= Tolerance;
            var vertical = dimension.DimensionType == DimensionType.Vertical ||
                dimension.DimensionType == DimensionType.Aligned && Math.Abs(movingPoint.X - fixedPoint.X) <= Tolerance;
            if (horizontal == vertical)
                throw new InvalidDataException("Керувальний розмір прямокутника має задавати ширину або висоту між сусідніми кутами.");
            var a = owner.A; var b = owner.B;
            if (horizontal)
            {
                var x = fixedPoint.X + Signed(movingPoint.X - fixedPoint.X, value);
                if (end.Index is 1 or 2) b = new(x, b.Y); else a = new(x, a.Y);
            }
            else
            {
                var y = fixedPoint.Y + Signed(movingPoint.Y - fixedPoint.Y, value);
                if (end.Index is 2 or 3) b = new(b.X, y); else a = new(a.X, y);
            }
            resized = owner with { A = a, B = b };
            var after = AssociativeDimensions.Vertices(resized);
            for (var i = 0; i < vertices.Length; i++) if (vertices[i] != after[i]) replacements[vertices[i]] = after[i];
        }
        else if (owner.Kind == ElementKind.Circle)
        {
            if (dimension.DimensionType is not (DimensionType.Radius or DimensionType.Diameter) ||
                !new[] { start.Kind, end.Kind }.Contains(ReferenceKind.Curve) ||
                !new[] { start.Kind, end.Kind }.Contains(ReferenceKind.Vertex))
                throw new InvalidDataException("Для кола обери центр і контур, потім тип «Радіус» або «Діаметр».");
            var radius = dimension.DimensionType == DimensionType.Diameter ? value / 2 : value;
            resized = owner with { B = owner.A + Direction(owner.B - owner.A) * radius };
        }
        else throw new InvalidDataException("Цей тип геометрії ще не підтримує керувальні розміри.");

        return elements.Select(e => e.Id == owner.Id ? resized : MoveConnectedPoint(e, replacements)).ToArray();
    }

    private static DrawingElement MoveConnectedPoint(DrawingElement element, Dictionary<PointMm, PointMm> replacements)
    {
        PointMm Replace(PointMm point)
        {
            foreach (var pair in replacements)
                if ((point - pair.Key).Length <= Tolerance) return pair.Value;
            return point;
        }
        if (element.Kind == ElementKind.Junction)
        {
            var p = Replace(element.A); return element with { A = p, B = p };
        }
        if (element.Kind != ElementKind.Wire) return element;
        var points = element.Points!.Select(Replace).ToArray();
        return element with { A = points[0], B = points[^1], Points = points };
    }

    private static (DrawingElement Owner, GeometryReference Start, GeometryReference End) OwnerAndReferences(
        DrawingElement dimension, DrawingElement[] elements)
    {
        if (dimension.DimensionTargetMm is not > 0 || !double.IsFinite(dimension.DimensionTargetMm.Value))
            throw new InvalidDataException("Керувальний розмір має містити додатне числове значення.");
        if (dimension.StartReference is not { } start || dimension.EndReference is not { } end || start.ElementId != end.ElementId)
            throw new InvalidDataException("Керувальний розмір повинен бути прив’язаний до однієї фігури.");
        var owner = elements.FirstOrDefault(e => e.Id == start.ElementId)
            ?? throw new InvalidDataException("Не знайдено геометрію керувального розміру.");
        return (owner, start, end);
    }

    private static string ConstraintKey(DrawingElement d, DrawingElement[] elements)
    {
        var (owner, start, end) = OwnerAndReferences(d, elements);
        var axis = owner.Kind switch
        {
            ElementKind.Rectangle when d.DimensionType == DimensionType.Aligned =>
                Math.Abs(AssociativeDimensions.ResolvePoint(start, elements.ToDictionary(e => e.Id)).X -
                    AssociativeDimensions.ResolvePoint(end, elements.ToDictionary(e => e.Id)).X) <= Tolerance ? "V" : "H",
            ElementKind.Rectangle => d.DimensionType == DimensionType.Vertical ? "V" : "H",
            _ => "ALL"
        };
        return $"{owner.Id:N}:{axis}";
    }

    private static double Signed(double current, double value) => current < 0 ? -value : value;
    private static PointMm Direction(PointMm value) => value.Length > Tolerance ? value * (1 / value.Length) : new PointMm(1, 0);
}
