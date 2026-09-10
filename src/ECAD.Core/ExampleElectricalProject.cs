namespace ECAD.Core;

public static class ExampleElectricalProject
{
    public static DrawingDocument Create()
    {
        var libraries = ExampleComponentLibraries.Create();
        var variants = libraries.SelectMany(library => library.DeviceTypes).SelectMany(type => type.Families)
            .SelectMany(family => family.Variants).ToArray();
        var breakerVariant = variants.First(item => item.SymbolKey == "IEC_BREAKER");
        var relayVariant = variants.First(item => item.SymbolKey == "IEC_COIL");
        var vfdVariant = variants.First(item => item.SymbolKey == "IEC_VFD");
        var motorVariant = variants.First(item => item.SymbolKey == "IEC_MOTOR_3P");

        var qfFunction = Function("301", "power", "Силовий полюс", DeviceFunctionKind.PowerPole, "1", "2");
        var kmCoil = Function("302", "coil", "Котушка", DeviceFunctionKind.Coil, "A1", "A2");
        var kmContact = Function("303", "contact-no", "Допоміжний контакт NO", DeviceFunctionKind.NoContact, "13", "14");
        var vfdFunction = Function("304", "drive", "Силовий перетворювач", DeviceFunctionKind.Generic, "L1", "L2", "L3", "U", "V", "W");
        var motorFunction = Function("305", "motor", "Трифазний двигун", DeviceFunctionKind.Motor, "U1", "V1", "W1");
        var devices = new[]
        {
            Device("201", "QF1", "Автомат вводу", breakerVariant, qfFunction),
            Device("202", "KM1", "Дозвіл пуску", relayVariant, kmCoil, kmContact),
            Device("203", "U1", "Перетворювач частоти", vfdVariant, vfdFunction),
            Device("204", "M1", "Привод вентилятора", motorVariant, motorFunction)
        };
        var nets = new[]
        {
            new ProjectNet(G("401"), "L1", "Живлення вводу", "L1", "Power", "BK", 2.5, true),
            new ProjectNet(G("402"), "24", "Керування +24 V", "+24VDC", "DC control", "RD", .75, true),
            new ProjectNet(G("403"), "25", "Повернення котушки", "0VDC", "DC control", "BU", .75),
            new ProjectNet(G("404"), "U", "Жила двигуна U", null, "Motor", "BK", 1.5),
            new ProjectNet(G("405"), "V", "Жила двигуна V", null, "Motor", "BN", 1.5),
            new ProjectNet(G("406"), "W", "Жила двигуна W", null, "Motor", "GY", 1.5),
            new ProjectNet(G("407"), "PE", "Захисний провідник", "PE", "Protective earth", "GN/YE", 1.5, true)
        };
        var terminals = new[]
        {
            new ProjectTerminal(G("501"), "1", 1, TerminalKind.FeedThrough, nets[3].Id, "Фаза U"),
            new ProjectTerminal(G("502"), "2", 1, TerminalKind.FeedThrough, nets[4].Id, "Фаза V"),
            new ProjectTerminal(G("503"), "3", 1, TerminalKind.FeedThrough, nets[5].Id, "Фаза W"),
            new ProjectTerminal(G("504"), "PE", 1, TerminalKind.ProtectiveEarth, nets[6].Id, "Захисне заземлення")
        };
        var strip = new TerminalStrip(G("500"), "X1", "Клеми двигуна", terminals);

        var qf = Symbol("601", "IEC_BREAKER", new(45, 55), devices[0], qfFunction, breakerVariant);
        var contact = Symbol("602", "IEC_NO_CONTACT", new(90, 55), devices[1], kmContact, null);
        var vfd = Symbol("603", "IEC_VFD", new(145, 70), devices[2], vfdFunction, vfdVariant);
        var motor = Symbol("604", "IEC_MOTOR_3P", new(235, 70), devices[3], motorFunction, motorVariant);
        var page1Elements = new List<DrawingElement> { qf, SymbolLabels.Create(qf), contact, SymbolLabels.Create(contact), vfd, SymbolLabels.Create(vfd), motor, SymbolLabels.Create(motor) };
        page1Elements.AddRange(new[]
        {
            Wire("611", nets[0], new(15,55), new(37.5,55)), Wire("612", nets[0], new(52.5,55), new(82.5,55)),
            Wire("613", nets[0], new(97.5,55), new(135,65)),
            Wire("614", nets[3], new(155,65), new(222.5,65)), Wire("615", nets[4], new(155,70), new(222.5,70)),
            Wire("616", nets[5], new(155,75), new(222.5,75))
        });

        var coil = Symbol("701", "IEC_COIL", new(90, 70), devices[1], kmCoil, relayVariant);
        var terminalSymbols = terminals.Select((terminal, index) => new DrawingElement(G($"72{index}"), ElementKind.Symbol,
            new PointMm(170, 55 + index * 15), new PointMm(170, 55 + index * 15))
        {
            SymbolKey = "IEC_TERMINAL", DeviceTag = $"X1:{terminal.Number}", TerminalId = terminal.Id
        }).ToArray();
        var page2Elements = new List<DrawingElement> { coil, SymbolLabels.Create(coil) };
        page2Elements.AddRange(terminalSymbols); page2Elements.AddRange(terminalSymbols.Select(SymbolLabels.Create));
        page2Elements.Add(Wire("711", nets[1], new(35,70), new(82.5,70)));
        page2Elements.Add(Wire("712", nets[2], new(97.5,70), new(135,70)));

        var page1 = new DrawingPage
        {
            Id = G("101"), Number = 1, Name = "Силова схема", Elements = [.. page1Elements],
            TitleBlock = new() { Project = "Демонстрація ECAD 0.17", Drawing = "Силова схема", Author = "ECAD", Revision = "A" }
        };
        var page2 = new DrawingPage
        {
            Id = G("102"), Number = 2, Name = "Керування і клеми", Elements = [.. page2Elements],
            TitleBlock = new() { Project = "Демонстрація ECAD 0.17", Drawing = "Керування і клеми", Author = "ECAD", Revision = "A" }
        };
        var cable = new ProjectCable(G("800"), "W1", "4G1.5 (демо)", 4, 1.5, 12, "X1", "M1",
        [
            Core("801", "1", CableCoreStatus.Used, nets[3], terminalSymbols[0], motor),
            Core("802", "2", CableCoreStatus.Used, nets[4], terminalSymbols[1], motor),
            Core("803", "3", CableCoreStatus.Used, nets[5], terminalSymbols[2], motor),
            Core("804", "GN/YE", CableCoreStatus.ProtectiveEarth, nets[6], terminalSymbols[3], motor)
        ]);
        var document = new DrawingDocument
        {
            SchemaVersion = 12, ActivePageId = page1.Id, Pages = [page1, page2], Elements = page1.Elements,
            ComponentLibraries = libraries, Devices = devices, Nets = nets, TerminalStrips = [strip], Cables = [cable],
            CrossPageReferences = [new(G("901"), page1.Id, contact.Id, page2.Id, coil.Id, "KM1 → 2")]
        };
        document = ElectricalProjectModel.Normalize(document); document.Validate(); return document;
    }

    private static ProjectDevice Device(string id, string tag, string description, DeviceVariant variant, params DeviceFunction[] functions) =>
        new(G(id), tag, description, variant.Id, variant.PhysicalRepresentations[0].Id, functions);
    private static DeviceFunction Function(string id, string key, string name, DeviceFunctionKind kind, params string[] terminals) =>
        new(G(id), key, name, kind, terminals);
    private static DrawingElement Symbol(string id, string key, PointMm at, ProjectDevice device, DeviceFunction function, DeviceVariant? variant) =>
        new(G(id), ElementKind.Symbol, at, at)
        {
            SymbolKey = key, DeviceTag = device.Tag, DeviceId = device.Id, DeviceFunctionId = function.Id,
            ComponentVariantId = variant?.Id, PhysicalRepresentationId = variant?.PhysicalRepresentations[0].Id
        };
    private static DrawingElement Wire(string id, ProjectNet net, PointMm a, PointMm b) =>
        new(G(id), ElementKind.Wire, a, b) { Points = [a, b], NetId = net.Id };
    private static CableCore Core(string id, string designation, CableCoreStatus status, ProjectNet net,
        DrawingElement from, DrawingElement to) => new(G(id), designation, status, net.Id, from.Id, to.Id);
    private static Guid G(string suffix) => Guid.Parse($"00000000-0000-0000-0000-{long.Parse(suffix):000000000000}");
}
