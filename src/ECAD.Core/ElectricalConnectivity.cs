namespace ECAD.Core;

public sealed record PinAddress(Guid ElementId, int PinIndex);
public sealed record ElectricalNet(int Number, Guid[] WireIds, PinAddress[] Pins);

public static class ElectricalConnectivity
{
    private const double Epsilon = 1e-7;

    public static ElectricalNet[] Build(IEnumerable<DrawingElement> elements)
    {
        var all = elements.ToArray();
        var wires = all.Where(e => e.Kind == ElementKind.Wire).ToArray();
        var parent = Enumerable.Range(0, wires.Length).ToArray();
        int Find(int x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        void Union(int a, int b) { a = Find(a); b = Find(b); if (a != b) parent[b] = a; }
        bool Same(PointMm a, PointMm b) => (a - b).Length <= Epsilon;
        var junctions = all.Where(e => e.Kind == ElementKind.Junction).Select(e => e.A).ToArray();

        for (var i = 0; i < wires.Length; i++)
            for (var j = i + 1; j < wires.Length; j++)
            {
                var sharedEndpoint = new[] { wires[i].A, wires[i].B }
                    .Any(a => new[] { wires[j].A, wires[j].B }.Any(b => Same(a, b)));
                var explicitIntersection = AssociativeDimensions.Edges(wires[i]).Any(first =>
                    AssociativeDimensions.Edges(wires[j]).Any(second =>
                        Geometry.SegmentIntersection(first.A, first.B, second.A, second.B, out var p) &&
                        junctions.Any(node => Same(node, p))));
                if (sharedEndpoint || explicitIntersection) Union(i, j);
            }

        var pinGroups = new Dictionary<int, List<PinAddress>>();
        foreach (var symbol in all.Where(e => e.Kind == ElementKind.Symbol))
            foreach (var (pin, pinIndex) in SymbolLibrary.PinPositions(symbol).Select((p, i) => (p, i)))
                for (var wireIndex = 0; wireIndex < wires.Length; wireIndex++)
                {
                    var endpoint = Same(pin, wires[wireIndex].A) || Same(pin, wires[wireIndex].B);
                    var onExplicitNode = junctions.Any(node => Same(node, pin)) &&
                        AssociativeDimensions.Edges(wires[wireIndex]).Any(edge => Geometry.DistanceToSegment(pin, edge.A, edge.B) <= Epsilon);
                    if (!endpoint && !onExplicitNode) continue;
                    var root = Find(wireIndex);
                    if (!pinGroups.TryGetValue(root, out var pins)) pinGroups[root] = pins = [];
                    if (!pins.Contains(new(symbol.Id, pinIndex))) pins.Add(new(symbol.Id, pinIndex));
                }

        return wires.Select((_, i) => Find(i)).Distinct().OrderBy(i => i).Select((root, number) =>
            new ElectricalNet(number + 1,
                wires.Where((_, i) => Find(i) == root).Select(w => w.Id).ToArray(),
                pinGroups.GetValueOrDefault(root, []).ToArray())).ToArray();
    }
}
