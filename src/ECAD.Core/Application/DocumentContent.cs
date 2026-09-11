namespace ECAD.Core;

public static class DocumentContent
{
    // Active-page root fields are a view of Pages, not a separate content change.
    // Arrays have structural equality here, without serializing the whole document.
    public static bool Equals(DrawingDocument left, DrawingDocument right)
    {
        if (ReferenceEquals(left, right)) return true;
        return left.SchemaVersion == right.SchemaVersion &&
            Sequence(left.Pages, right.Pages, (a, b) => a with { Elements = b.Elements } == b && Sequence(a.Elements, b.Elements, Element)) &&
            Sequence(left.CrossPageReferences, right.CrossPageReferences) && Sequence(left.Nets, right.Nets) &&
            Sequence(left.Devices, right.Devices, (a, b) => a with { Functions = b.Functions } == b &&
                Sequence(a.Functions, b.Functions, (x, y) => x with { Terminals = y.Terminals } == y && Sequence(x.Terminals, y.Terminals))) &&
            Sequence(left.TerminalStrips, right.TerminalStrips, (a, b) => a with { Terminals = b.Terminals } == b && Sequence(a.Terminals, b.Terminals)) &&
            Sequence(left.Cables, right.Cables, (a, b) => a with { Cores = b.Cores } == b && Sequence(a.Cores, b.Cores)) &&
            Sequence(left.CustomSymbols, right.CustomSymbols, (a, b) => a with { Pins = b.Pins, Strokes = b.Strokes } == b && Sequence(a.Pins, b.Pins) && Sequence(a.Strokes, b.Strokes)) &&
            Sequence(left.ComponentLibraries, right.ComponentLibraries, Library);
    }

    private static bool Element(DrawingElement a, DrawingElement b) =>
        a with { Points = b.Points, PinOffsets = b.PinOffsets, SymbolStrokes = b.SymbolStrokes } == b &&
        Sequence(a.Points, b.Points) && Sequence(a.PinOffsets, b.PinOffsets) && Sequence(a.SymbolStrokes, b.SymbolStrokes);

    private static bool Library(ComponentLibrary a, ComponentLibrary b) => a with { DeviceTypes = b.DeviceTypes } == b &&
        Sequence(a.DeviceTypes, b.DeviceTypes, (x, y) => x with { ConfigurationFields = y.ConfigurationFields, Families = y.Families } == y &&
            Sequence(x.ConfigurationFields, y.ConfigurationFields, (u, v) => u with { AllowedValues = v.AllowedValues } == v && Sequence(u.AllowedValues, v.AllowedValues)) &&
            Sequence(x.Families, y.Families, (u, v) => u with { SharedParameters = v.SharedParameters, Variants = v.Variants } == v &&
                Sequence(u.SharedParameters, v.SharedParameters) && Sequence(u.Variants, v.Variants, Variant)));

    private static bool Variant(DeviceVariant a, DeviceVariant b) => a with
    { Configuration = b.Configuration, Parameters = b.Parameters, Contacts = b.Contacts, PhysicalRepresentations = b.PhysicalRepresentations } == b &&
        Sequence(a.Configuration, b.Configuration) && Sequence(a.Parameters, b.Parameters) && Sequence(a.Contacts, b.Contacts) &&
        Sequence(a.PhysicalRepresentations, b.PhysicalRepresentations, (x, y) => x with { Outline = y.Outline } == y && Sequence(x.Outline, y.Outline));

    private static bool Sequence<T>(T[]? a, T[]? b, Func<T, T, bool>? equal = null)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null || a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
            if (!(equal?.Invoke(a[i], b[i]) ?? EqualityComparer<T>.Default.Equals(a[i], b[i]))) return false;
        return true;
    }
}
