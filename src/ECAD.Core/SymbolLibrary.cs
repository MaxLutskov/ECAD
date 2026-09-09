namespace ECAD.Core;

public sealed record SymbolStroke(PointMm A, PointMm B);
public sealed record SymbolDefinition(string Key, string Name, string Prefix, PointMm[] Pins, SymbolStroke[]? Strokes = null);

public static class SymbolLibrary
{
    public static readonly SymbolDefinition[] All =
    [
        new("IEC_NO_CONTACT", "IEC — контакт NO", "K", [new(-7.5, 0), new(7.5, 0)]),
        new("IEC_NC_CONTACT", "IEC — контакт NC", "K", [new(-7.5, 0), new(7.5, 0)]),
        new("IEC_COIL", "IEC — котушка", "K", [new(-7.5, 0), new(7.5, 0)]),
        new("IEC_TERMINAL", "IEC — клема", "X", [new(0, 0)]),
        new("IEC_FUSE", "IEC — запобіжник", "F", [new(-7.5, 0), new(7.5, 0)]),
        new("IEC_BREAKER", "IEC — автоматичний вимикач", "QF", [new(-7.5, 0), new(7.5, 0)]),
        new("IEC_MOTOR_3P", "IEC — двигун 3~", "M", [new(-12.5, -5), new(-12.5, 0), new(-12.5, 5)]),
        new("IEC_GROUND", "IEC — захисне заземлення", "PE", [new(0, -7.5)]),
        new("IEC_VFD", "IEC — перетворювач частоти", "U", [new(-10, -5), new(-10, 0), new(-10, 5), new(10, -5), new(10, 0), new(10, 5)]),
        new("ANSI_NO_CONTACT", "ANSI — контакт NO", "CR", [new(-7.5, 0), new(7.5, 0)]),
        new("ANSI_NC_CONTACT", "ANSI — контакт NC", "CR", [new(-7.5, 0), new(7.5, 0)]),
        new("ANSI_COIL", "ANSI — котушка", "CR", [new(-7.5, 0), new(7.5, 0)])
    ];

    public static bool TryGet(string key, out SymbolDefinition definition)
    {
        definition = All.FirstOrDefault(d => d.Key == key)!;
        return definition is not null;
    }

    public static SymbolDefinition Get(string key) => TryGet(key, out var definition)
        ? definition : throw new InvalidDataException("Невідомий символ бібліотеки.");

    public static SymbolDefinition[] Definitions(DrawingDocument document) => [.. All, .. document.CustomSymbols];
    public static SymbolDefinition Get(DrawingDocument document, string key) =>
        Definitions(document).FirstOrDefault(d => d.Key == key) ?? throw new InvalidDataException("Невідомий символ бібліотеки.");

    public static PointMm[] PinPositions(DrawingElement element) => (element.PinOffsets ?? Get(element.SymbolKey!).Pins)
        .Select(pin => Geometry.Rotate(element.A + pin, element.A, element.RotationDegrees)).ToArray();
}
