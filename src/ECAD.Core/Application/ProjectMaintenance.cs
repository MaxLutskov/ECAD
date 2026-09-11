namespace ECAD.Core;

public static class ProjectMaintenance
{
    public static DrawingDocument RemoveUnusedNets(DrawingDocument document)
    {
        var used = document.Pages.SelectMany(page => page.Elements).Select(element => element.NetId)
            .Concat(document.TerminalStrips.SelectMany(strip => strip.Terminals).Select(terminal => terminal.NetId))
            .Concat(document.Cables.SelectMany(cable => cable.Cores).Select(core => core.NetId)).OfType<Guid>().ToHashSet();
        // Devices/functions may be intentionally unplaced catalog definitions.
        // This explicit command only removes unreferenced nets, never those definitions.
        return document with { Nets = document.Nets.Where(net => used.Contains(net.Id)).ToArray() };
    }

    public static DrawingDocument DeleteElements(DrawingDocument document, IReadOnlySet<Guid> selection)
    {
        var kept = document.Elements.Where(element => !selection.Contains(element.Id) &&
            !(element.LinkedElementId is { } owner && selection.Contains(owner)) &&
            !(element.StartReference is { } a && selection.Contains(a.ElementId)) &&
            !(element.EndReference is { } b && selection.Contains(b.ElementId))).ToArray();
        var removed = document.Elements.Select(element => element.Id).Except(kept.Select(element => element.Id)).ToHashSet();
        return document with { Elements = kept,
            CrossPageReferences = document.CrossPageReferences.Where(reference =>
                !removed.Contains(reference.FromElementId) && !removed.Contains(reference.ToElementId)).ToArray(),
            Cables = document.Cables.Select(cable => cable with { Cores = cable.Cores.Select(core => core with
            { FromElementId = core.FromElementId is { } from && removed.Contains(from) ? null : core.FromElementId,
                ToElementId = core.ToElementId is { } to && removed.Contains(to) ? null : core.ToElementId }).ToArray() }).ToArray() };
    }
}
