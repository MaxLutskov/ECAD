namespace ECAD.Core;

public static class PathEditing
{
    public static DrawingElement Convert(DrawingElement element, ElementKind target)
    {
        if (element.Kind is not (ElementKind.Line or ElementKind.Wire) ||
            target is not (ElementKind.Line or ElementKind.Wire))
            throw new InvalidDataException("Можна змінювати тип лише лінії або провідника.");
        if (element.Kind == target) return element;
        if (element.Kind == ElementKind.Wire && element.Points?.Length != 2)
            throw new InvalidDataException("На лінію можна перетворити лише односегментний провідник.");
        return target == ElementKind.Line
            ? element with { Kind = target, Points = null, NetId = null }
            : element with { Kind = target, Points = [element.A, element.B], NetId = null };
    }
}
