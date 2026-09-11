namespace ECAD.Core;

public static class LibraryUsage
{
    public static bool UsesAny(DrawingDocument document, IReadOnlySet<Guid> variants) =>
        document.Pages.SelectMany(page => page.Elements).Any(element =>
            element.ComponentVariantId is { } id && variants.Contains(id)) ||
        document.Devices.Any(device => device.ComponentVariantId is { } id && variants.Contains(id));

    public static DrawingDocument Update(DrawingDocument document, ComponentLibrary previous, ComponentLibrary next)
    {
        var oldVariants = previous.DeviceTypes.SelectMany(type => type.Families)
            .SelectMany(family => family.Variants).ToDictionary(variant => variant.Id);
        var variants = next.DeviceTypes.SelectMany(type => type.Families)
            .SelectMany(family => family.Variants).ToDictionary(variant => variant.Id);
        var removed = oldVariants.Keys.Where(id => !variants.ContainsKey(id)).ToHashSet();
        if (UsesAny(document, removed))
            throw new InvalidDataException("Не можна видалити варіант, який використовується у проєкті.");
        foreach (var element in document.Pages.SelectMany(page => page.Elements))
            if (element.ComponentVariantId is { } id && variants.TryGetValue(id, out var variant) && variant.SymbolKey != element.SymbolKey)
                throw new InvalidDataException("Не можна змінити умовне позначення використаного варіанта.");
        foreach (var device in document.Devices)
            if (device.ComponentVariantId is { } id && oldVariants.TryGetValue(id, out var oldVariant) &&
                variants.TryGetValue(id, out var variant) && oldVariant.SymbolKey != variant.SymbolKey)
                throw new InvalidDataException("Не можна змінити умовне позначення варіанта пристрою проєкту.");

        Guid? Physical(Guid? variantId, Guid? physicalId) =>
            variantId is { } id && variants.TryGetValue(id, out var variant)
                ? variant.PhysicalRepresentations.Any(item => item.Id == physicalId)
                    ? physicalId : variant.PhysicalRepresentations[0].Id
                : physicalId;
        var pages = document.Pages.Select(page => page with
        {
            Elements = page.Elements.Select(element => element with
            { PhysicalRepresentationId = Physical(element.ComponentVariantId, element.PhysicalRepresentationId) }).ToArray()
        }).ToArray();
        return document with
        {
            ComponentLibraries = document.ComponentLibraries.Select(library => library.Id == next.Id ? next : library).ToArray(),
            Pages = pages, Elements = pages.Single(page => page.Id == document.ActivePageId).Elements,
            Devices = document.Devices.Select(device => device with
            { PhysicalRepresentationId = Physical(device.ComponentVariantId, device.PhysicalRepresentationId) }).ToArray()
        };
    }
}
