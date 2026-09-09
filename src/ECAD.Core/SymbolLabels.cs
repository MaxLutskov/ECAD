namespace ECAD.Core;

public static class SymbolLabels
{
    public static DrawingElement Create(DrawingElement symbol)
    {
        var at = symbol.A + new PointMm(-3, -10);
        return new DrawingElement(Guid.NewGuid(), ElementKind.Text, at, at)
        { Text = symbol.DeviceTag, TextHeightMm = 3.5, LinkedElementId = symbol.Id };
    }

    public static DrawingDocument Ensure(DrawingDocument document)
    {
        var labelled = document.Elements.Where(e => e.Kind == ElementKind.Text && e.LinkedElementId is not null)
            .Select(e => e.LinkedElementId!.Value).ToHashSet();
        var missing = document.Elements.Where(e => e.Kind == ElementKind.Symbol && !labelled.Contains(e.Id))
            .Select(Create).ToArray();
        return missing.Length == 0 ? document : document with { Elements = [.. document.Elements, .. missing] };
    }
}
