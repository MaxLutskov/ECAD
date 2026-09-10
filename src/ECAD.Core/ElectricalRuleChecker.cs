namespace ECAD.Core;

public enum ElectricalIssueKind { UnconnectedPin, DanglingWireEnd, DuplicateDeviceTag, FunctionUsedTwice, MissingDeviceModel, InvalidCableCore }
public sealed record ElectricalIssue(ElectricalIssueKind Kind, Guid ElementId, string Message, Guid? PageId = null);

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

    public static ElectricalIssue[] Check(DrawingDocument document)
    {
        var issues = document.Pages.SelectMany(page => Check(page.Elements)
            .Select(issue => issue with { PageId = page.Id, Message = $"Аркуш {page.Number}, {issue.Message}" })).ToList();
        foreach (var group in document.Pages.SelectMany(page => page.Elements.Select(element => (page, element)))
            .Where(item => item.element.Kind == ElementKind.Symbol && item.element.DeviceFunctionId is not null)
            .GroupBy(item => item.element.DeviceFunctionId).Where(group => group.Count() > 1))
            foreach (var item in group)
                issues.Add(new(ElectricalIssueKind.FunctionUsedTwice, item.element.Id,
                    $"Аркуш {item.page.Number}: функцію пристрою використано більше одного разу.", item.page.Id));
        foreach (var item in document.Pages.SelectMany(page => page.Elements.Select(element => (page, element)))
            .Where(item => item.element.Kind == ElementKind.Symbol && item.element.ComponentVariantId is null))
            issues.Add(new(ElectricalIssueKind.MissingDeviceModel, item.element.Id,
                $"Аркуш {item.page.Number}: {item.element.DeviceTag} не має варіанта пристрою з бібліотеки.", item.page.Id));
        foreach (var cable in document.Cables)
            foreach (var core in cable.Cores.Where(core => core.Status == CableCoreStatus.Used && core.NetId is null ||
                core.Status == CableCoreStatus.Spare && core.NetId is not null))
                issues.Add(new(ElectricalIssueKind.InvalidCableCore, core.FromElementId ?? core.ToElementId ?? Guid.Empty,
                    $"Кабель {cable.Tag}, жила {core.Designation}: статус не відповідає призначенню кола."));
        return [.. issues];
    }
}
