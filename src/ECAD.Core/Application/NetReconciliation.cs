namespace ECAD.Core;

// Reconciles a complete proposed document; callers commit only after validation.
public static class NetReconciliation
{
    public static DrawingDocument Apply(DrawingDocument document)
    {
        var nets = document.Nets.ToList();
        var byId = nets.ToDictionary(net => net.Id);
        var parent = nets.ToDictionary(net => net.Id, net => net.Id);
        Guid Root(Guid id) { while (parent[id] != id) id = parent[id]; return id; }
        var components = document.Pages.Select(page => (Page: page,
            Groups: ElectricalConnectivity.Build(page.Elements).Where(group => group.WireIds.Length > 0).ToArray())).ToArray();
        foreach (var (page, groups) in components)
        {
            var elements = page.Elements.ToDictionary(element => element.Id);
            foreach (var group in groups)
            {
                var ids = group.WireIds.Select(id => elements[id].NetId).OfType<Guid>().Distinct().ToArray();
                if (ids.Any(id => !byId.ContainsKey(id))) throw new InvalidDataException("Провідник посилається на відсутнє коло.");
                foreach (var id in ids.Skip(1)) parent[Root(id)] = Root(ids[0]);
            }
        }
        var mapping = new Dictionary<Guid, Guid>();
        var merged = new List<ProjectNet>();
        foreach (var group in nets.GroupBy(net => Root(net.Id)))
        {
            var members = group.ToArray();
            var locked = members.Where(net => net.NumberLocked).ToArray();
            if (locked.Length > 1) throw new InvalidDataException("Не можна об’єднати кола з різними закріпленими номерами. Спочатку зніми закріплення одного номера.");
            var survivor = locked.FirstOrDefault() ?? members[0];
            string? Field(Func<ProjectNet, string?> select, string name)
            {
                var values = members.Select(select).Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
                if (values.Length > 1) throw new InvalidDataException($"Конфлікт кіл {string.Join(", ", members.Select(net => net.Number))}: {name}. Узгодь параметри перед з’єднанням.");
                return values.FirstOrDefault();
            }
            var sections = members.Select(net => net.CrossSectionMm2).OfType<double>().Distinct().ToArray();
            if (sections.Length > 1) throw new InvalidDataException("Кола мають різні перерізи. Узгодь їх перед з’єднанням.");
            merged.Add(survivor with { Description = Field(net => net.Description, "опис"),
                Potential = Field(net => net.Potential, "потенціал"), SignalClass = Field(net => net.SignalClass, "клас сигналу"),
                Color = Field(net => net.Color, "колір"), CrossSectionMm2 = sections.Length == 0 ? null : sections[0] });
            foreach (var member in members) mapping[member.Id] = survivor.Id;
        }
        Guid? Remap(Guid? id) => id is { } value ? mapping.GetValueOrDefault(value, value) : null;
        var referenced = document.TerminalStrips.SelectMany(strip => strip.Terminals).Select(terminal => Remap(terminal.NetId))
            .Concat(document.Cables.SelectMany(cable => cable.Cores).Select(core => Remap(core.NetId))).OfType<Guid>().ToHashSet();
        var numbers = merged.Select(net => net.Number).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string NextNumber() { var number = 1; while (!numbers.Add(number.ToString(System.Globalization.CultureInfo.InvariantCulture))) number++; return number.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        var pages = new List<DrawingPage>();
        foreach (var (page, groups) in components)
        {
            var elements = page.Elements.ToDictionary(element => element.Id);
            var claimed = new HashSet<Guid>();
            foreach (var group in groups.OrderBy(group => group.WireIds.Min()))
            {
                var id = group.WireIds.Select(wireId => Remap(elements[wireId].NetId)).OfType<Guid>().FirstOrDefault();
                if (id == Guid.Empty) { id = Guid.NewGuid(); merged.Add(new(id, NextNumber())); }
                else if (claimed.Contains(id))
                {
                    if (referenced.Contains(id)) throw new InvalidDataException("Коло призначене клемі або жилі. Перед розділенням зніми призначення, щоб явно вибрати нове коло.");
                    var original = merged.Single(net => net.Id == id);
                    id = Guid.NewGuid(); merged.Add(original with { Id = id, Number = NextNumber(), NumberLocked = false });
                }
                claimed.Add(id);
                foreach (var wireId in group.WireIds) elements[wireId] = elements[wireId] with { NetId = id };
            }
            pages.Add(page with { Elements = page.Elements.Select(element => elements[element.Id]).ToArray() });
        }
        return document with { Nets = [.. merged], Pages = [.. pages], Elements = pages.Single(page => page.Id == document.ActivePageId).Elements,
            TerminalStrips = document.TerminalStrips.Select(strip => strip with
            { Terminals = strip.Terminals.Select(terminal => terminal with { NetId = Remap(terminal.NetId) }).ToArray() }).ToArray(),
            Cables = document.Cables.Select(cable => cable with
            { Cores = cable.Cores.Select(core => core with { NetId = Remap(core.NetId) }).ToArray() }).ToArray() };
    }
}
