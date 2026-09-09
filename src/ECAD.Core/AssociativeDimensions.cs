namespace ECAD.Core;

public enum ReferenceKind { Vertex, Edge }
public sealed record GeometryReference(Guid ElementId, ReferenceKind Kind, int Index, double Parameter = 0);
public sealed record GeometryPick(PointMm Point, GeometryReference? Reference);

public static class AssociativeDimensions
{
    public static PointMm[] Vertices(DrawingElement e) => e.Kind switch
    {
        ElementKind.Rectangle => [e.A, new(e.B.X, e.A.Y), e.B, new(e.A.X, e.B.Y)],
        ElementKind.Circle => [e.A],
        ElementKind.Line => [e.A, e.B],
        ElementKind.Wire => e.Points!,
        ElementKind.Symbol => SymbolLibrary.PinPositions(e),
        ElementKind.Junction or ElementKind.Text => [e.A],
        _ => []
    };

    public static (PointMm A, PointMm B)[] Edges(DrawingElement e) => e.Kind switch
    {
        ElementKind.Line => [(e.A, e.B)],
        ElementKind.Wire => e.Points!.Zip(e.Points!.Skip(1), (a, b) => (a, b)).ToArray(),
        ElementKind.Rectangle => [(e.A, new(e.B.X, e.A.Y)), (new(e.B.X, e.A.Y), e.B),
            (e.B, new(e.A.X, e.B.Y)), (new(e.A.X, e.B.Y), e.A)],
        _ => []
    };

    public static GeometryPick Pick(IEnumerable<DrawingElement> elements, PointMm p, double tolerance)
    {
        var list = elements.Where(e => e.Kind is not (ElementKind.Dimension or ElementKind.Text)).ToArray();
        GeometryPick? best = null; var distance = tolerance;
        // Vertices (including a circle centre) take priority over edges and grid.
        foreach (var e in list)
        {
            var vertices = Vertices(e);
            for (var i = 0; i < vertices.Length; i++)
                if ((vertices[i] - p).Length <= distance)
                { distance = (vertices[i] - p).Length; best = new(vertices[i], new(e.Id, ReferenceKind.Vertex, i)); }
        }
        if (best is not null) return best;
        foreach (var e in list)
        {
            var edges = Edges(e);
            for (var i = 0; i < edges.Length; i++)
            {
                var (a, b) = edges[i]; var t = Math.Clamp(Parameter(p, a, b), 0, 1);
                var point = a + (b - a) * t;
                if ((point - p).Length <= distance)
                { distance = (point - p).Length; best = new(point, new(e.Id, ReferenceKind.Edge, i, t)); }
            }
        }
        return best ?? new(p, null);
    }

    public static double Parameter(PointMm p, PointMm a, PointMm b)
    {
        var v = b - a;
        return ((p.X - a.X) * v.X + (p.Y - a.Y) * v.Y) / (v.X * v.X + v.Y * v.Y);
    }

    private static DrawingElement Owner(GeometryReference r, IReadOnlyDictionary<Guid, DrawingElement> elements)
    {
        if (!elements.TryGetValue(r.ElementId, out var e) || e.Kind is ElementKind.Dimension or ElementKind.Text ||
            !Enum.IsDefined(r.Kind) || r.Index < 0 || !double.IsFinite(r.Parameter) || r.Parameter < 0 || r.Parameter > 1)
            throw new InvalidDataException("Некоректна прив’язка розміру.");
        var count = r.Kind == ReferenceKind.Vertex ? Vertices(e).Length : Edges(e).Length;
        if (r.Index >= count) throw new InvalidDataException("Не знайдено геометрію прив’язки.");
        return e;
    }

    public static PointMm ResolvePoint(GeometryReference r, IReadOnlyDictionary<Guid, DrawingElement> elements)
    {
        var e = Owner(r, elements);
        if (r.Kind == ReferenceKind.Vertex) return Vertices(e)[r.Index];
        var (a, b) = Edges(e)[r.Index]; return a + (b - a) * r.Parameter;
    }

    public static DrawingElement Resolve(DrawingElement d, IReadOnlyDictionary<Guid, DrawingElement> elements)
    {
        var a = d.StartReference is { } ar ? ResolvePoint(ar, elements) : d.A;
        var b = d.EndReference is { } br ? ResolvePoint(br, elements) : d.B;
        if (d.StartReference?.Kind == ReferenceKind.Edge && d.EndReference?.Kind == ReferenceKind.Edge)
        {
            var first = Edges(Owner(d.StartReference, elements))[d.StartReference.Index];
            var second = Edges(Owner(d.EndReference, elements))[d.EndReference.Index];
            var v = first.B - first.A; var w = second.B - second.A;
            if (Math.Abs(v.X * w.Y - v.Y * w.X) > 1e-7 * v.Length * w.Length)
                throw new InvalidDataException("Для відстані між лініями обери паралельні лінії або їхні кінцеві точки.");
            b = second.A + w * Parameter(a, second.A, second.B);
        }
        else if (d.StartReference?.Kind == ReferenceKind.Edge)
        {
            var edge = Edges(Owner(d.StartReference, elements))[d.StartReference.Index];
            a = edge.A + (edge.B - edge.A) * Parameter(b, edge.A, edge.B);
        }
        else if (d.EndReference?.Kind == ReferenceKind.Edge)
        {
            var edge = Edges(Owner(d.EndReference, elements))[d.EndReference.Index];
            b = edge.A + (edge.B - edge.A) * Parameter(a, edge.A, edge.B);
        }
        return d with { A = a, B = b };
    }

    public static DrawingElement[] ResolveAll(DrawingElement[] elements)
    {
        var map = elements.ToDictionary(e => e.Id);
        return elements.Select(e => e.Kind == ElementKind.Dimension ? Resolve(e, map) : e).ToArray();
    }
}
