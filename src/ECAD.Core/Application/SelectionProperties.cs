namespace ECAD.Core;

public static class SelectionProperties
{
    public static DrawingElement[] Move(DrawingDocument document, IReadOnlySet<Guid> selected, PointMm delta) => AssociativeDimensions.ResolveAll(document.Elements.Select(e =>
    {
        var followsOwner = e.LinkedElementId is { } owner && selected.Contains(owner);
        if (!selected.Contains(e.Id) && !followsOwner) return e;
        if (e.Kind != ElementKind.Dimension || (e.StartReference is null && e.EndReference is null)) return e.Move(delta);
        var sourcesMoving = (e.StartReference is null || selected.Contains(e.StartReference.ElementId)) &&
            (e.EndReference is null || selected.Contains(e.EndReference.ElementId));
        if (sourcesMoving) return e with { A = e.StartReference is null ? e.A + delta : e.A, B = e.EndReference is null ? e.B + delta : e.B };
        var v = e.LengthMm > 1e-9 ? (e.B - e.A) * (1 / e.LengthMm) : new PointMm(1, 0);
        return e with { DimensionOffset = e.DimensionOffset - v.Y * delta.X + v.X * delta.Y };
    }).ToArray());

    public static DrawingDocument Apply(DrawingDocument document, IReadOnlySet<Guid> selected,
        bool setName, string? name, PointMm offset, double? textHeight)
    {
        if (!double.IsFinite(offset.X) || !double.IsFinite(offset.Y) || textHeight is { } height && (!double.IsFinite(height) || height <= 0))
            throw new InvalidDataException("Параметри мають бути скінченними; висота тексту — додатною.");
        if (textHeight is not null && document.Elements.Any(e => selected.Contains(e.Id) && e.Kind != ElementKind.Text))
            throw new InvalidDataException("Висоту тексту можна застосувати лише до текстових об’єктів.");
        return document with { Elements = Move(document, selected, offset).Select(e => selected.Contains(e.Id)
            ? e with { Name = setName ? name : e.Name, TextHeightMm = textHeight ?? e.TextHeightMm }
            : e).ToArray() };
    }
}
