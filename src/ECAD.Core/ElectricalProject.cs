namespace ECAD.Core;

public enum DeviceFunctionKind { Generic, Coil, NoContact, NcContact, PowerPole, Terminal, Motor, Supply }
public enum TerminalKind { FeedThrough, ProtectiveEarth, Disconnect, Fuse, MultiLevel }
public enum CableCoreStatus { Used, Spare, ProtectiveEarth }

public sealed record DeviceFunction(Guid Id, string Key, string Name, DeviceFunctionKind Kind,
    string[] Terminals);

public sealed record ProjectDevice(Guid Id, string Tag, string? Description,
    Guid? ComponentVariantId, Guid? PhysicalRepresentationId, DeviceFunction[] Functions);

public sealed record ProjectNet(Guid Id, string Number, string? Description = null,
    string? Potential = null, string? SignalClass = null, string? Color = null,
    double? CrossSectionMm2 = null, bool NumberLocked = false);

public sealed record ProjectTerminal(Guid Id, string Number, int Level, TerminalKind Kind,
    Guid? NetId = null, string? Description = null);

public sealed record TerminalStrip(Guid Id, string Tag, string? Description,
    ProjectTerminal[] Terminals);

public sealed record CableCore(Guid Id, string Designation, CableCoreStatus Status,
    Guid? NetId = null, Guid? FromElementId = null, Guid? ToElementId = null,
    string? Description = null);

public sealed record ProjectCable(Guid Id, string Tag, string? Type, int CoreCount,
    double? CrossSectionMm2, double? LengthM, string? From, string? To, CableCore[] Cores);

public static class ElectricalProjectModel
{
    public static DrawingDocument Normalize(DrawingDocument document)
    {
        document = DrawingPages.Normalize(document);
        var devices = document.Devices?.ToList() ?? [];
        var nets = document.Nets?.ToList() ?? [];
        var deviceByTag = new Dictionary<string, ProjectDevice>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
            if (!deviceByTag.ContainsKey(device.Tag)) deviceByTag.Add(device.Tag, device);
        var pages = new List<DrawingPage>();

        foreach (var page in document.Pages.OrderBy(page => page.Number))
        {
            var elements = page.Elements.ToArray();
            for (var index = 0; index < elements.Length; index++)
            {
                var element = elements[index];
                if (element.Kind != ElementKind.Symbol) continue;
                var tag = element.DeviceTag!;
                ProjectDevice device;
                if (element.DeviceId is { } deviceId)
                    device = devices.FirstOrDefault(item => item.Id == deviceId) ?? CreateDevice(deviceId, tag, element);
                else if (!deviceByTag.TryGetValue(tag, out device!))
                    device = CreateDevice(Guid.NewGuid(), tag, element);
                if (!devices.Any(item => item.Id == device.Id)) { devices.Add(device); deviceByTag[tag] = device; }

                var function = element.DeviceFunctionId is { } functionId
                    ? device.Functions.FirstOrDefault(item => item.Id == functionId) : null;
                if (element.DeviceFunctionId is not null && function is null)
                    throw new InvalidDataException("Функція символу не належить його пристрою.");
                if (function is null)
                {
                    function = CreateFunction(element, device.Functions.Length + 1);
                    device = device with { Functions = [.. device.Functions, function] };
                    var deviceIndex = devices.FindIndex(item => item.Id == device.Id); devices[deviceIndex] = device;
                    deviceByTag[tag] = device;
                }
                elements[index] = element with { DeviceId = device.Id, DeviceFunctionId = function.Id };
            }

            pages.Add(page with { Elements = elements });
        }

        for (var deviceIndex = 0; deviceIndex < devices.Count; deviceIndex++)
        {
            var device = devices[deviceIndex];
            var representations = pages.SelectMany(page => page.Elements)
                .Where(element => element.Kind == ElementKind.Symbol && element.DeviceId == device.Id).ToArray();
            var changedTags = representations.Select(element => element.DeviceTag!).Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(tag => !string.Equals(tag, device.Tag, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (changedTags.Length == 1)
            {
                var tag = changedTags[0]; device = device with { Tag = tag }; devices[deviceIndex] = device;
                pages = pages.Select(page =>
                {
                    var owned = page.Elements.Where(element => element.Kind == ElementKind.Symbol && element.DeviceId == device.Id)
                        .Select(element => element.Id).ToHashSet();
                    return page with { Elements = page.Elements.Select(element =>
                        element.Kind == ElementKind.Symbol && element.DeviceId == device.Id ? element with { DeviceTag = tag } :
                        element.LinkedElementId is { } owner && owned.Contains(owner) ? element with { Text = tag } : element).ToArray() };
                }).ToList();
            }
            var modeled = representations.FirstOrDefault(element => element.ComponentVariantId is not null);
            if (modeled is not null && (device.ComponentVariantId != modeled.ComponentVariantId || device.PhysicalRepresentationId != modeled.PhysicalRepresentationId))
                devices[deviceIndex] = device with { ComponentVariantId = modeled.ComponentVariantId, PhysicalRepresentationId = modeled.PhysicalRepresentationId };
        }
        var active = pages.Single(page => page.Id == document.ActivePageId);
        return NetReconciliation.Apply(document with
        {
            SchemaVersion = DocumentFormat.Current, Devices = [.. devices], Nets = [.. nets], Pages = [.. pages],
            Elements = active.Elements, WidthMm = active.WidthMm, HeightMm = active.HeightMm
        });
    }

    public static void Validate(DrawingDocument document)
    {
        if (document.Devices is null || document.Nets is null || document.TerminalStrips is null || document.Cables is null ||
            document.Devices.Length > 100000 || document.Nets.Length > 100000 ||
            document.TerminalStrips.Length > 10000 || document.Cables.Length > 10000)
            throw Invalid();
        var ids = new HashSet<Guid>();
        bool Add(Guid id) => id != Guid.Empty && ids.Add(id);
        var deviceTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in document.Devices)
        {
            if (device is null || !Add(device.Id) || string.IsNullOrWhiteSpace(device.Tag) || device.Tag.Length > 64 ||
                !deviceTags.Add(device.Tag) || device.Description is { Length: > 500 } || device.Functions is null ||
                device.Functions.Length > 1000) throw Invalid();
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var function in device.Functions)
                if (function is null || !Add(function.Id) || string.IsNullOrWhiteSpace(function.Key) || !keys.Add(function.Key) ||
                    string.IsNullOrWhiteSpace(function.Name) || !Enum.IsDefined(function.Kind) || function.Terminals is null ||
                    function.Terminals.Length > 100 || function.Terminals.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 32))
                    throw Invalid();
        }
        var netIds = new HashSet<Guid>(); var netNumbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var net in document.Nets)
            if (net is null || !Add(net.Id) || !netIds.Add(net.Id) || string.IsNullOrWhiteSpace(net.Number) || !netNumbers.Add(net.Number) ||
                net.Number.Length > 64 || net.Description is { Length: > 500 } || net.Potential is { Length: > 100 } ||
                net.SignalClass is { Length: > 100 } || net.Color is { Length: > 100 } ||
                net.CrossSectionMm2 is { } section && (!double.IsFinite(section) || section <= 0 || section > 10000)) throw Invalid();
        var terminalIds = new HashSet<Guid>(); var stripTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var strip in document.TerminalStrips)
        {
            if (strip is null || !Add(strip.Id) || string.IsNullOrWhiteSpace(strip.Tag) || !stripTags.Add(strip.Tag) ||
                strip.Tag.Length > 64 || strip.Description is { Length: > 500 } || strip.Terminals is null || strip.Terminals.Length > 10000) throw Invalid();
            var numbers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var terminal in strip.Terminals)
                if (terminal is null || !Add(terminal.Id) || !terminalIds.Add(terminal.Id) || string.IsNullOrWhiteSpace(terminal.Number) ||
                    !numbers.Add($"{terminal.Number}:{terminal.Level}") || terminal.Number.Length > 64 || terminal.Level is < 1 or > 100 ||
                    !Enum.IsDefined(terminal.Kind) || terminal.NetId is { } netId && !netIds.Contains(netId) || terminal.Description is { Length: > 500 }) throw Invalid();
        }
        var cableTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var elementIds = document.Pages.SelectMany(page => page.Elements).Select(element => element.Id).ToHashSet();
        foreach (var cable in document.Cables)
        {
            if (cable is null || !Add(cable.Id) || string.IsNullOrWhiteSpace(cable.Tag) || !cableTags.Add(cable.Tag) || cable.Tag.Length > 64 ||
                cable.CoreCount is < 1 or > 10000 || cable.Cores is null || cable.Cores.Length != cable.CoreCount ||
                cable.CrossSectionMm2 is { } section && (!double.IsFinite(section) || section <= 0 || section > 10000) ||
                cable.LengthM is { } length && (!double.IsFinite(length) || length < 0 || length > 1000000)) throw Invalid();
            var designations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var core in cable.Cores)
                if (core is null || !Add(core.Id) || string.IsNullOrWhiteSpace(core.Designation) || !designations.Add(core.Designation) ||
                    !Enum.IsDefined(core.Status) || core.NetId is { } netId && !netIds.Contains(netId) ||
                    core.FromElementId is { } from && !elementIds.Contains(from) || core.ToElementId is { } to && !elementIds.Contains(to)) throw Invalid();
        }
        var functions = document.Devices.SelectMany(device => device.Functions.Select(function => (function.Id, DeviceId: device.Id)))
            .ToDictionary(item => item.Id, item => item.DeviceId);
        var variants = ComponentCatalog.Variants(document).ToDictionary(item => item.Variant.Id, item => item.Variant);
        foreach (var device in document.Devices)
            if (device.ComponentVariantId is { } variantId
                ? !variants.TryGetValue(variantId, out var variant) || device.PhysicalRepresentationId is { } physicalId &&
                    !variant.PhysicalRepresentations.Any(physical => physical.Id == physicalId)
                : device.PhysicalRepresentationId is not null)
                throw Invalid();
        var devicesById = document.Devices.ToDictionary(device => device.Id);
        foreach (var element in document.Pages.SelectMany(page => page.Elements))
            if (element.Kind == ElementKind.Symbol && (element.DeviceId is { } deviceId && (!devicesById.TryGetValue(deviceId, out var device) ||
                    !string.Equals(device.Tag, element.DeviceTag, StringComparison.OrdinalIgnoreCase)) ||
                    element.DeviceFunctionId is { } functionId && (!functions.TryGetValue(functionId, out var ownerId) || ownerId != element.DeviceId) || element.TerminalId is { } terminalId && !terminalIds.Contains(terminalId)) ||
                element.Kind == ElementKind.Wire && element.NetId is { } wireNetId && !netIds.Contains(wireNetId) ||
                element.Kind != ElementKind.Symbol && (element.DeviceId is not null || element.DeviceFunctionId is not null || element.TerminalId is not null) ||
                element.Kind != ElementKind.Wire && element.NetId is not null)
                throw Invalid();
    }

    private static ProjectDevice CreateDevice(Guid id, string tag, DrawingElement element) =>
        new(id, tag, element.Name, element.ComponentVariantId, element.PhysicalRepresentationId, []);

    private static DeviceFunction CreateFunction(DrawingElement element, int sequence)
    {
        var kind = element.SymbolKey switch
        {
            "IEC_COIL" or "ANSI_COIL" => DeviceFunctionKind.Coil,
            "IEC_NO_CONTACT" or "ANSI_NO_CONTACT" => DeviceFunctionKind.NoContact,
            "IEC_NC_CONTACT" or "ANSI_NC_CONTACT" => DeviceFunctionKind.NcContact,
            "IEC_TERMINAL" => DeviceFunctionKind.Terminal,
            "IEC_MOTOR_3P" => DeviceFunctionKind.Motor,
            _ => DeviceFunctionKind.Generic
        };
        var terminals = kind switch
        {
            DeviceFunctionKind.Coil => new[] { "A1", "A2" },
            DeviceFunctionKind.NoContact => new[] { $"{sequence * 10 + 3}", $"{sequence * 10 + 4}" },
            DeviceFunctionKind.NcContact => new[] { $"{sequence * 10 + 1}", $"{sequence * 10 + 2}" },
            _ => Enumerable.Range(1, SymbolLibrary.PinPositions(element).Length).Select(index => index.ToString()).ToArray()
        };
        var name = SymbolLibrary.TryGet(element.SymbolKey!, out var definition) ? definition.Name : element.SymbolKey!;
        return new(Guid.NewGuid(), $"{element.SymbolKey}:{sequence}", name, kind, terminals);
    }

    private static InvalidDataException Invalid() => new("Некоректна електрична модель проєкту.");
}
