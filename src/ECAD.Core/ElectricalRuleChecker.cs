namespace ECAD.Core;

public enum ElectricalIssueKind { UnconnectedPin, DanglingWireEnd, DuplicateDeviceTag }
public sealed record ElectricalIssue(ElectricalIssueKind Kind, Guid ElementId, string Message);

public static class ElectricalRuleChecker
{
    private const double Epsilon = 1e-7;

    public static ElectricalIssue[] Check(IEnumerable<DrawingElement> elements)
    {
        var all = elements.ToArray(); var issues = new List<ElectricalIssue>();
        var nets = ElectricalConnectivity.Build(all);
        var connectedPins = nets.SelectMany(n => n.Pins).ToHashSet();
        foreach (var symbol in all.Where(e => e.Kind == ElementKind.Symbol))
            for (var pin = 0; pin < SymbolLibrary.PinPositions(symbol).Length; pin++)
                if (!connectedPins.Contains(new(symbol.Id, pin)))
                    issues.Add(new(ElectricalIssueKind.UnconnectedPin, symbol.Id,
                        $"{symbol.DeviceTag}: контакт {pin + 1} не підключений."));

        var wires = all.Where(e => e.Kind == ElementKind.Wire).ToArray();
        var pins = all.Where(e => e.Kind == ElementKind.Symbol).SelectMany(SymbolLibrary.PinPositions).ToArray();
        var junctions = all.Where(e => e.Kind == ElementKind.Junction).Select(e => e.A).ToArray();
        bool Same(PointMm a, PointMm b) => (a - b).Length <= Epsilon;
        foreach (var wire in wires)
            foreach (var end in new[] { wire.A, wire.B })
            {
                var connected = pins.Any(p => Same(p, end)) ||
                    wires.Any(other => other.Id != wire.Id && (Same(other.A, end) || Same(other.B, end))) ||
                    junctions.Any(node => Same(node, end) && wires.Any(other => other.Id != wire.Id &&
                        AssociativeDimensions.Edges(other).Any(edge => Geometry.DistanceToSegment(node, edge.A, edge.B) <= Epsilon)));
                if (!connected) issues.Add(new(ElectricalIssueKind.DanglingWireEnd, wire.Id,
                    $"Вільний кінець провідника: X {end.X:0.###}, Y {end.Y:0.###} мм."));
            }

        foreach (var group in all.Where(e => e.Kind == ElementKind.Symbol)
            .GroupBy(e => (Tag: e.DeviceTag!.ToUpperInvariant(), e.SymbolKey, e.A)).Where(g => g.Count() > 1))
            foreach (var symbol in group)
                issues.Add(new(ElectricalIssueKind.DuplicateDeviceTag, symbol.Id,
                    $"Дубль символу в одній точці: {symbol.DeviceTag}."));
        return [.. issues];
    }
}
