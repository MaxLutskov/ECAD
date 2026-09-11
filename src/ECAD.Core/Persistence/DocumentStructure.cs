namespace ECAD.Core;

// Validate the shape before migrations and graph algorithms enumerate user data.
public static class DocumentStructure
{
    public static void Validate(DrawingDocument document)
    {
        static void Items<T>(T[]? items, string name) where T : class
        {
            if (items is null || items.Any(item => item is null))
                throw new InvalidDataException($"Пошкоджена структура: {name} містить порожні записи.");
        }
        Items(document.Elements, "Elements"); Items(document.Pages, "Pages");
        Items(document.CrossPageReferences, "CrossPageReferences"); Items(document.CustomSymbols, "CustomSymbols");
        Items(document.Devices, "Devices"); Items(document.Nets, "Nets");
        Items(document.TerminalStrips, "TerminalStrips"); Items(document.Cables, "Cables");
        if (document.Pages.Length > 0 && (document.Pages.Any(page => page.Id == Guid.Empty) ||
            document.Pages.Select(page => page.Id).Distinct().Count() != document.Pages.Length ||
            document.Pages.All(page => page.Id != document.ActivePageId)))
            throw new InvalidDataException("Некоректний ID активного аркуша або повторні ID аркушів.");
        foreach (var page in document.Pages) Items(page.Elements, "Page.Elements");
        foreach (var elements in new[] { document.Elements }.Concat(document.Pages.Select(page => page.Elements)))
            if (elements.Any(element => element.Id == Guid.Empty) || elements.Select(element => element.Id).Distinct().Count() != elements.Length)
                throw new InvalidDataException("Повторний або порожній ID елемента аркуша.");
        foreach (var element in document.Elements.Concat(document.Pages.SelectMany(page => page.Elements)))
        {
            if (element.Kind == ElementKind.Symbol && (string.IsNullOrWhiteSpace(element.DeviceTag) || string.IsNullOrWhiteSpace(element.SymbolKey)))
                throw new InvalidDataException("Символ не має позначення або ключа бібліотеки.");
            if (element.Kind is ElementKind.Wire or ElementKind.Polyline && element.Points is not { Length: >= 2 })
                throw new InvalidDataException("Провідник або полілінія не має точок.");
        }
        foreach (var symbol in document.CustomSymbols)
        {
            if (symbol.Pins is null || string.IsNullOrWhiteSpace(symbol.Key))
                throw new InvalidDataException("Пошкоджене визначення символу.");
            if (symbol.Strokes is not null) Items(symbol.Strokes, "Symbol.Strokes");
        }
        ComponentCatalog.Validate(document.ComponentLibraries, SymbolLibrary.All.Select(symbol => symbol.Key)
            .Concat(document.CustomSymbols.Select(symbol => symbol.Key)).ToHashSet(StringComparer.Ordinal));
        var deviceIds = new HashSet<Guid>(); var functionIds = new HashSet<Guid>();
        foreach (var device in document.Devices)
        {
            if (!deviceIds.Add(device.Id) || device.Id == Guid.Empty || string.IsNullOrWhiteSpace(device.Tag))
                throw new InvalidDataException("Некоректний пристрій або повторний ID пристрою.");
            Items(device.Functions, "Device.Functions");
            foreach (var function in device.Functions)
            {
                if (!functionIds.Add(function.Id) || function.Id == Guid.Empty)
                    throw new InvalidDataException("Повторний або порожній ID функції.");
                Items(function.Terminals, "Function.Terminals");
            }
        }
        if (document.Nets.Select(net => net.Id).Distinct().Count() != document.Nets.Length)
            throw new InvalidDataException("Повторний ID електричного кола.");
        foreach (var strip in document.TerminalStrips) Items(strip.Terminals, "TerminalStrip.Terminals");
        foreach (var cable in document.Cables) Items(cable.Cores, "Cable.Cores");
    }
}
