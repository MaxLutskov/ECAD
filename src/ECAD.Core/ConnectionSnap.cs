namespace ECAD.Core;

public static class ConnectionSnap
{
    // Explicit junctions snap to existing nodes, vertices, exact segment
    // intersections, then segment projections. The grid is the final fallback.
    public static PointMm Pick(IEnumerable<DrawingElement> elements, PointMm raw, double tolerance, PointMm fallback)
    {
        var list = elements.ToArray();
        var existing = list.Where(e => e.Kind == ElementKind.Junction)
            .OrderBy(e => (e.A - raw).Length).FirstOrDefault();
        if (existing is not null && (existing.A - raw).Length <= tolerance) return existing.A;

        var pick = AssociativeDimensions.Pick(list, raw, tolerance);
        if (pick.Reference?.Kind == ReferenceKind.Vertex) return pick.Point;

        var edges = list.Where(e => e.Kind is ElementKind.Line or ElementKind.Wire)
            .SelectMany(AssociativeDimensions.Edges).ToArray();
        PointMm? best = null; var bestDistance = tolerance;
        for (var i = 0; i < edges.Length; i++)
            for (var j = i + 1; j < edges.Length; j++)
                if (Geometry.SegmentIntersection(edges[i].A, edges[i].B, edges[j].A, edges[j].B, out var p) &&
                    (p - raw).Length <= bestDistance)
                { best = p; bestDistance = (p - raw).Length; }
        if (best is not null) return best.Value;
        return pick.Reference is not null ? pick.Point : fallback;
    }
}
