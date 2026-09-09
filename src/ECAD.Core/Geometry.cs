namespace ECAD.Core;

public readonly record struct PointMm(double X, double Y)
{
    public static PointMm operator +(PointMm a, PointMm b) => new(a.X + b.X, a.Y + b.Y);
    public static PointMm operator -(PointMm a, PointMm b) => new(a.X - b.X, a.Y - b.Y);
    public static PointMm operator *(PointMm a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public bool IsFinite => double.IsFinite(X) && double.IsFinite(Y);
}

public static class Geometry
{
    // CAD angles: 0 points right, +90 points up (screen Y points down).
    public static PointMm Polar(PointMm start, double length, double degrees)
    {
        if (!start.IsFinite || !double.IsFinite(length) || length <= 0 || !double.IsFinite(degrees))
            throw new ArgumentOutOfRangeException(nameof(length));
        var radians = (degrees % 360) * Math.PI / 180;
        return start + new PointMm(Math.Cos(radians), -Math.Sin(radians)) * length;
    }
    public static double Angle(PointMm delta) => (Math.Atan2(-delta.Y, delta.X) * 180 / Math.PI + 360) % 360;
    public static PointMm Snap(PointMm p, double step)
    {
        if (!double.IsFinite(step) || step <= 0 || !p.IsFinite)
            throw new ArgumentOutOfRangeException(nameof(step));
        return new(Math.Round(p.X / step, MidpointRounding.AwayFromZero) * step,
            Math.Round(p.Y / step, MidpointRounding.AwayFromZero) * step);
    }

    // An exact length is resolved AFTER choosing the start point and direction.
    // Do not snap this endpoint again: that would silently change its length.
    public static PointMm AtLength(PointMm start, PointMm directionPoint, double length)
    {
        if (!double.IsFinite(length) || length <= 0 || !start.IsFinite || !directionPoint.IsFinite)
            throw new ArgumentOutOfRangeException(nameof(length));
        var v = directionPoint - start;
        if (v.Length < 1e-9) v = new(1, 0);
        return start + v * (length / v.Length);
    }

    public static double DistanceToSegment(PointMm p, PointMm a, PointMm b)
    {
        var v = b - a;
        var w = p - a;
        var squared = v.X * v.X + v.Y * v.Y;
        var t = squared == 0 ? 0 : Math.Clamp((w.X * v.X + w.Y * v.Y) / squared, 0, 1);
        return (p - (a + v * t)).Length;
    }

    public static PointMm Rotate(PointMm point, PointMm centre, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var v = point - centre;
        return centre + new PointMm(v.X * Math.Cos(radians) + v.Y * Math.Sin(radians),
            -v.X * Math.Sin(radians) + v.Y * Math.Cos(radians));
    }

    public static bool SegmentIntersection(PointMm a, PointMm b, PointMm c, PointMm d, out PointMm point)
    {
        static double Cross(PointMm x, PointMm y) => x.X * y.Y - x.Y * y.X;
        var r = b - a; var s = d - c; var denominator = Cross(r, s);
        if (Math.Abs(denominator) < 1e-10) { point = default; return false; }
        var t = Cross(c - a, s) / denominator;
        var u = Cross(c - a, r) / denominator;
        if (t < -1e-9 || t > 1 + 1e-9 || u < -1e-9 || u > 1 + 1e-9)
        { point = default; return false; }
        point = a + r * Math.Clamp(t, 0, 1); return true;
    }
}
