namespace ECAD.Core;

public static class ArcGeometry
{
    public static DrawingElement FromThreePoints(Guid id, PointMm start, PointMm through, PointMm end)
    {
        var d = 2 * (start.X * (through.Y - end.Y) + through.X * (end.Y - start.Y) + end.X * (start.Y - through.Y));
        if (Math.Abs(d) < 1e-9) throw new InvalidDataException("Три точки дуги не можуть лежати на одній прямій.");
        var s2 = start.X * start.X + start.Y * start.Y;
        var t2 = through.X * through.X + through.Y * through.Y;
        var e2 = end.X * end.X + end.Y * end.Y;
        var centre = new PointMm(
            (s2 * (through.Y - end.Y) + t2 * (end.Y - start.Y) + e2 * (start.Y - through.Y)) / d,
            (s2 * (end.X - through.X) + t2 * (start.X - end.X) + e2 * (through.X - start.X)) / d);
        var startAngle = Geometry.Angle(start - centre);
        var throughAngle = Geometry.Angle(through - centre);
        var endAngle = Geometry.Angle(end - centre);
        var positiveSweep = Positive(endAngle - startAngle);
        var throughSweep = Positive(throughAngle - startAngle);
        var sweep = throughSweep <= positiveSweep + 1e-7 ? positiveSweep : positiveSweep - 360;
        if (Math.Abs(sweep) < 1e-7) throw new InvalidDataException("Початок і кінець дуги мають відрізнятися.");
        return new DrawingElement(id, ElementKind.Arc, centre, start) { ArcSweepDegrees = sweep };
    }

    public static PointMm EndPoint(DrawingElement arc) =>
        Geometry.Polar(arc.A, arc.LengthMm, Geometry.Angle(arc.B - arc.A) + arc.ArcSweepDegrees);

    public static PointMm PointAt(DrawingElement arc, double parameter) =>
        Geometry.Polar(arc.A, arc.LengthMm, Geometry.Angle(arc.B - arc.A) + arc.ArcSweepDegrees * Math.Clamp(parameter, 0, 1));

    public static PointMm[] Sample(DrawingElement arc, double maximumStepDegrees = 5)
    {
        var count = Math.Max(2, (int)Math.Ceiling(Math.Abs(arc.ArcSweepDegrees) / maximumStepDegrees) + 1);
        return Enumerable.Range(0, count).Select(i => PointAt(arc, i / (double)(count - 1))).ToArray();
    }

    public static (PointMm Point, double Parameter, double Distance) NearestPoint(DrawingElement arc, PointMm point)
    {
        var startAngle = Geometry.Angle(arc.B - arc.A);
        var pointAngle = Geometry.Angle(point - arc.A);
        var angular = arc.ArcSweepDegrees > 0 ? Positive(pointAngle - startAngle) : -Positive(startAngle - pointAngle);
        var onSweep = arc.ArcSweepDegrees > 0 ? angular <= arc.ArcSweepDegrees : angular >= arc.ArcSweepDegrees;
        var candidates = new List<(PointMm Point, double Parameter)> { (arc.B, 0), (EndPoint(arc), 1) };
        if (onSweep) candidates.Add((PointAt(arc, angular / arc.ArcSweepDegrees), angular / arc.ArcSweepDegrees));
        return candidates.Select(c => (c.Point, c.Parameter, Distance: (c.Point - point).Length)).MinBy(c => c.Distance);
    }

    public static double DistanceToArc(DrawingElement arc, PointMm point) => NearestPoint(arc, point).Distance;
    private static double Positive(double angle) => (angle % 360 + 360) % 360;
}
