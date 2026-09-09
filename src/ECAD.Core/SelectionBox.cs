namespace ECAD.Core;

public readonly record struct SelectionBox(double Left, double Top, double Right, double Bottom)
{
    public static SelectionBox From(PointMm a, PointMm b) => new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
    public bool Contains(PointMm p) => p.X >= Left && p.X <= Right && p.Y >= Top && p.Y <= Bottom;
    private bool Crosses(PointMm a, PointMm b)
    {
        double lo = 0, hi = 1;
        var v = b - a;
        bool Clip(double p, double q)
        {
            if (Math.Abs(p) < 1e-12) return q >= 0;
            var t = q / p;
            if (p < 0) lo = Math.Max(lo, t); else hi = Math.Min(hi, t);
            return lo <= hi;
        }
        return Clip(-v.X, a.X - Left) && Clip(v.X, Right - a.X) && Clip(-v.Y, a.Y - Top) && Clip(v.Y, Bottom - a.Y);
    }

    public bool Matches(DrawingElement e, bool crossing)
    {
        if (e.Kind is ElementKind.Junction or ElementKind.Text or ElementKind.Symbol)
            return Contains(e.A) || crossing && e.Hit(new(Math.Clamp(e.A.X, Left, Right), Math.Clamp(e.A.Y, Top, Bottom)), 0);
        if (e.Kind == ElementKind.Circle)
        {
            var r = e.LengthMm;
            if (!crossing) return e.A.X - r >= Left && e.A.X + r <= Right && e.A.Y - r >= Top && e.A.Y + r <= Bottom;
            var nearest = new PointMm(Math.Clamp(e.A.X, Left, Right), Math.Clamp(e.A.Y, Top, Bottom));
            var farthest = new PointMm(Math.Max(Math.Abs(e.A.X - Left), Math.Abs(e.A.X - Right)), Math.Max(Math.Abs(e.A.Y - Top), Math.Abs(e.A.Y - Bottom)));
            return (nearest - e.A).Length <= r && farthest.Length >= r;
        }
        var edges = e.Kind == ElementKind.Dimension ? e.DimensionSegments() : AssociativeDimensions.Edges(e);
        var box = this;
        return crossing ? edges.Any(edge => box.Crosses(edge.A, edge.B)) : edges.All(edge => box.Contains(edge.A) && box.Contains(edge.B));
    }
}
