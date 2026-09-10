namespace ECAD.Core;

// Configuration fields are data, not enums. A breaker can use "poles" with
// 1P/2P/3P/4P, while another device can define channels, contact arrangement,
// control type, enclosure or any other independent variation axis.
public sealed record ConfigurationField(string Key, string Name, bool Required = false,
    string[]? AllowedValues = null, string? Unit = null);
public sealed record ConfigurationValue(string Key, string Value);
public sealed record DeviceParameter(string Key, string Name, string Value, string? Unit = null);
public sealed record DeviceContact(string Id, string Designation, string Function, string ElectricalType);
public sealed record PhysicalRepresentation(Guid Id, string Name, double WidthMm, double HeightMm, double DepthMm,
    string Mounting, double? DinModules = null, SymbolStroke[]? Outline = null);
public sealed record DeviceVariant(Guid Id, string Name, string? CatalogNumber, string? RatingText, string SymbolKey,
    ConfigurationValue[] Configuration, DeviceParameter[] Parameters, DeviceContact[] Contacts,
    PhysicalRepresentation[] PhysicalRepresentations);
public sealed record DeviceFamily(Guid Id, string Name, string? Manufacturer, string? Series,
    DeviceParameter[] SharedParameters, DeviceVariant[] Variants);
public sealed record DeviceTypeDefinition(Guid Id, string Name, string? Description,
    ConfigurationField[] ConfigurationFields, DeviceFamily[] Families);
public sealed record ComponentLibrary(Guid Id, string Name, string? Description, int Version,
    DeviceTypeDefinition[] DeviceTypes);
public sealed record DeviceVariantContext(ComponentLibrary Library, DeviceTypeDefinition DeviceType,
    DeviceFamily Family, DeviceVariant Variant);

public static class ComponentCatalog
{
    public static DeviceVariantContext[] Variants(DrawingDocument document) => document.ComponentLibraries
        .SelectMany(library => library.DeviceTypes.SelectMany(type => type.Families.SelectMany(family =>
            family.Variants.Select(variant => new DeviceVariantContext(library, type, family, variant)))))
        .ToArray();

    public static DeviceVariantContext? FindVariant(DrawingDocument document, Guid? id) => id is null ? null :
        Variants(document).FirstOrDefault(item => item.Variant.Id == id);

    public static DeviceParameter[] EffectiveParameters(DeviceVariantContext context) =>
        [.. context.Family.SharedParameters.Where(shared => !context.Variant.Parameters.Any(variant =>
            string.Equals(variant.Key, shared.Key, StringComparison.OrdinalIgnoreCase))), .. context.Variant.Parameters];

    public static void Validate(ComponentLibrary[] libraries, HashSet<string> symbolKeys)
    {
        if (libraries is null || libraries.Length > 1000) throw Invalid();
        var ids = new HashSet<Guid>();
        var libraryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in libraries)
        {
            if (library is null || !AddId(ids, library.Id) || !Text(library.Name, 120) || !OptionalText(library.Description, 2000) ||
                library.Version <= 0 || library.DeviceTypes is null || library.DeviceTypes.Length > 1000 ||
                !libraryNames.Add(library.Name.Trim())) throw Invalid();
            var typeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var type in library.DeviceTypes)
            {
                if (type is null || !AddId(ids, type.Id) || !Text(type.Name, 120) || !OptionalText(type.Description, 2000) ||
                    type.ConfigurationFields is null || type.ConfigurationFields.Length > 100 ||
                    type.Families is null || type.Families.Length > 10000 || !typeNames.Add(type.Name.Trim())) throw Invalid();
                var fieldKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var field in type.ConfigurationFields)
                    if (field is null || !Key(field.Key) || !Text(field.Name, 120) || !OptionalText(field.Unit, 32) ||
                        !fieldKeys.Add(field.Key) || field.AllowedValues is { Length: > 1000 } ||
                        field.AllowedValues?.Any(value => !Text(value, 200)) == true ||
                        field.AllowedValues?.Distinct(StringComparer.OrdinalIgnoreCase).Count() != field.AllowedValues?.Length)
                        throw Invalid();
                var familyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var family in type.Families)
                {
                    if (family is null || !AddId(ids, family.Id) || !Text(family.Name, 160) || !OptionalText(family.Manufacturer, 160) ||
                        !OptionalText(family.Series, 160) || family.SharedParameters is null || family.SharedParameters.Length > 1000 ||
                        family.Variants is null || family.Variants.Length > 100000 || !familyNames.Add(family.Name.Trim()) ||
                        !ValidParameters(family.SharedParameters)) throw Invalid();
                    var variantNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var variant in family.Variants)
                    {
                        if (variant is null || !AddId(ids, variant.Id) || !Text(variant.Name, 200) || !OptionalText(variant.CatalogNumber, 200) ||
                            !OptionalText(variant.RatingText, 1000) || !symbolKeys.Contains(variant.SymbolKey) ||
                            variant.Configuration is null || variant.Configuration.Length > 100 ||
                            variant.Parameters is null || variant.Parameters.Length > 1000 ||
                            variant.Contacts is null || variant.Contacts.Length > 1000 ||
                            variant.PhysicalRepresentations is not { Length: >= 1 and <= 1000 } ||
                            !variantNames.Add(variant.Name.Trim()) || !ValidParameters(variant.Parameters)) throw Invalid();
                        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var value in variant.Configuration)
                        {
                            if (value is null) throw Invalid();
                            var field = type.ConfigurationFields.FirstOrDefault(item =>
                                string.Equals(item.Key, value.Key, StringComparison.OrdinalIgnoreCase));
                            if (field is null || !values.Add(value.Key) || !Text(value.Value, 200) ||
                                field.AllowedValues is { Length: > 0 } allowed &&
                                !allowed.Contains(value.Value, StringComparer.OrdinalIgnoreCase)) throw Invalid();
                        }
                        if (type.ConfigurationFields.Any(field => field.Required && !values.Contains(field.Key))) throw Invalid();
                        var contactIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var contact in variant.Contacts)
                            if (contact is null || !Key(contact.Id) || !Text(contact.Designation, 80) || !Text(contact.Function, 160) ||
                                !Text(contact.ElectricalType, 80) || !contactIds.Add(contact.Id)) throw Invalid();
                        foreach (var physical in variant.PhysicalRepresentations)
                            if (physical is null || !AddId(ids, physical.Id) || !Text(physical.Name, 200) || !Text(physical.Mounting, 120) ||
                                !Size(physical.WidthMm) || !Size(physical.HeightMm) || !Size(physical.DepthMm) ||
                                physical.DinModules is { } modules && (!double.IsFinite(modules) || modules <= 0 || modules > 1000) ||
                                physical.Outline is { Length: > 10000 } || physical.Outline?.Any(stroke =>
                                    stroke is null || !stroke.A.IsFinite || !stroke.B.IsFinite || stroke.A == stroke.B) == true) throw Invalid();
                    }
                }
            }
        }
    }

    private static bool ValidParameters(DeviceParameter[] parameters)
    {
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return parameters.All(parameter => parameter is not null && Key(parameter.Key) && Text(parameter.Name, 120) && Text(parameter.Value, 1000) &&
            OptionalText(parameter.Unit, 32) && keys.Add(parameter.Key));
    }
    private static bool Key(string value) => Text(value, 80) && value.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.');
    private static bool Text(string? value, int maximum) => !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
    private static bool OptionalText(string? value, int maximum) => value is null || Text(value, maximum);
    private static bool Size(double value) => double.IsFinite(value) && value > 0 && value <= 10000;
    private static bool AddId(HashSet<Guid> ids, Guid id) => id != Guid.Empty && ids.Add(id);
    private static InvalidDataException Invalid() => new("Некоректні дані бібліотеки пристроїв.");
}
