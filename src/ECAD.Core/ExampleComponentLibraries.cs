namespace ECAD.Core;

public static class ExampleComponentLibraries
{
    public static ComponentLibrary[] Create() => [ProtectionAndControl(), DrivesAndMotors()];

    private static ComponentLibrary ProtectionAndControl() => new(G("1001"), "IEC — автоматика (демо)",
        "Навчальна бібліотека для демонстрації моделі ECAD. Номінали й артикули позначені як DEMO та потребують звірки з каталогом виробника.", 1,
    [
        new(G("1101"), "Автоматичний вимикач", "Модульні автоматичні вимикачі з довільною кількістю полюсів",
        [
            new("poles", "Кількість полюсів", true, ["1P", "2P", "3P", "4P"]),
            new("trip-curve", "Характеристика", true, ["B", "C", "D"]),
            new("execution", "Виконання", false)
        ],
        [
            new(G("1201"), "System pro M compact", "ABB (демо)", "S200",
                [new("standard", "Нормативний профіль", "IEC 60898-1 (приклад)"), new("mounting", "Типовий монтаж", "DIN-рейка")],
            [
                new(G("1301"), "S201 C16", "DEMO-ABB-S201-C16", "C16 · 230 V · 6 kA", "IEC_BREAKER",
                    [new("poles", "1P"), new("trip-curve", "C"), new("execution", "стаціонарне")],
                    [new("rated-current", "Номінальний струм", "16", "A"), new("rated-voltage", "Номінальна напруга", "230", "V"), new("breaking-capacity", "Вимикальна здатність", "6", "kA")],
                    [new("1", "1", "Вхід L", "Power"), new("2", "2", "Вихід T", "Power")],
                    [
                        Physical("1401", "DIN 1 модуль", 17.5, 85, 69, "DIN-рейка", 1),
                        Physical("1402", "Макет з боковим аксесуаром", 26.25, 85, 69, "DIN-рейка", 1.5)
                    ]),
                new(G("1302"), "S204 C63", "DEMO-ABB-S204-C63", "C63 · 400 V · 6 kA", "IEC_BREAKER",
                    [new("poles", "4P"), new("trip-curve", "C"), new("execution", "4NO силових")],
                    [new("rated-current", "Номінальний струм", "63", "A"), new("rated-voltage", "Номінальна напруга", "400", "V"), new("breaking-capacity", "Вимикальна здатність", "6", "kA")],
                    PowerContacts(4), [Physical("1403", "DIN 4 модулі", 70, 85, 69, "DIN-рейка", 4)])
            ]),
            new(G("1202"), "Acti9", "Schneider Electric (демо)", "iC60",
                [new("standard", "Нормативний профіль", "IEC 60898-1 (приклад)"), new("mounting", "Типовий монтаж", "DIN-рейка")],
            [
                new(G("1303"), "iC60N C32", "DEMO-SE-IC60N-C32", "C32 · 400 V · 6 kA", "IEC_BREAKER",
                    [new("poles", "3P"), new("trip-curve", "C"), new("execution", "допоміжний контакт OF")],
                    [new("rated-current", "Номінальний струм", "32", "A"), new("breaking-capacity", "Вимикальна здатність", "6", "kA")],
                    PowerContacts(3), [Physical("1404", "DIN 3 модулі", 54, 85, 78.5, "DIN-рейка", 3)])
            ])
        ]),
        new(G("1501"), "Проміжне реле", "Реле з вільною напругою котушки й різними наборами контактів",
        [new("coil-voltage", "Напруга котушки", true, null, "V"), new("coil-kind", "Тип котушки", true, ["DC", "AC"]),
            new("contacts", "Набір контактів", true, ["1CO", "2CO", "4CO"])],
        [
            new(G("1601"), "R-серія", "Example Controls", "R",
                [new("mounting", "Типовий монтаж", "Розетка на DIN-рейку")],
            [
                Relay("1701", "R24-2CO", "24", "DC", "2CO", "IEC_COIL", "1801"),
                Relay("1702", "R230-4CO", "230", "AC", "4CO", "IEC_COIL", "1802")
            ])
        ])
    ]);

    private static ComponentLibrary DrivesAndMotors() => new(G("2001"), "Приводи та двигуни (демо)",
        "Приклад бібліотеки з перетворювачами частоти, двигунами й ANSI-реле. Дані демонстраційні.", 1,
    [
        new(G("2101"), "Перетворювач частоти", "Варіанти за живленням, потужністю та інтерфейсом керування",
        [new("supply", "Живлення", true, ["1~ 230 V", "3~ 400 V"]), new("power", "Потужність", true, null, "kW"),
            new("control", "Інтерфейс керування", true)],
        [
            new(G("2201"), "SINAMICS V20 (демо)", "Siemens (демо)", "V20",
                [new("protection", "Ступінь захисту", "IP20"), new("mounting", "Типовий монтаж", "Монтажна панель")],
            [
                Vfd("2301", "V20 1.5 kW", "DEMO-V20-15", "1~ 230 V", "1.5", "Клеми + Modbus RTU", "2401", 90, 150, 145),
                Vfd("2302", "V20 7.5 kW", "DEMO-V20-75", "3~ 400 V", "7.5", "Клеми + USS", "2402", 140, 220, 180)
            ])
        ]),
        new(G("2501"), "Асинхронний двигун", "Трифазні двигуни з довільним механічним виконанням",
        [new("poles", "Кількість полюсів", true, null), new("mounting-form", "Монтажне виконання", true, ["IM B3", "IM B5", "IM B35"]),
            new("cooling", "Охолодження", false)],
        [
            new(G("2601"), "M3BP (демо)", "ABB (демо)", "M3BP",
                [new("efficiency-class", "Клас ефективності", "IE3")],
            [
                new(G("2701"), "M3BP 132S 4", "DEMO-M3BP-132S4", "5.5 kW · 400/690 V · 4 полюси", "IEC_MOTOR_3P",
                    [new("poles", "4"), new("mounting-form", "IM B3"), new("cooling", "IC411")],
                    [new("power", "Номінальна потужність", "5.5", "kW"), new("speed", "Швидкість", "1470", "rpm"), new("mass", "Маса", "55", "kg")],
                    [new("U1", "U1", "Фаза U", "Power"), new("V1", "V1", "Фаза V", "Power"), new("W1", "W1", "Фаза W", "Power"),
                        new("PE", "PE", "Захисне заземлення", "ProtectiveEarth")],
                    [Physical("2801", "Лапи IM B3", 350, 260, 520, "Монтажна плита"), Physical("2802", "Фланець IM B5", 360, 360, 500, "Фланець")])
            ])
        ]),
        new(G("2901"), "Керувальне реле ANSI", "Приклад того, що стандарт символу не визначає структуру каталогу",
        [new("coil-voltage", "Напруга котушки", true), new("contact-form", "Контактна схема", true, ["1NO", "1NC", "2NO+2NC"])],
        [
            new(G("2911"), "CR demo", "Example Controls", "CR",
                [new("standard-profile", "Профіль символу", "ANSI draft")],
            [
                new(G("2921"), "CR24 2NO+2NC", "DEMO-CR24", "24 V DC · 2NO+2NC", "ANSI_COIL",
                    [new("coil-voltage", "24 V DC"), new("contact-form", "2NO+2NC")],
                    [new("coil-power", "Споживання котушки", "0.9", "W")],
                    [new("A1", "A1", "Котушка +", "Coil"), new("A2", "A2", "Котушка -", "Coil"),
                        new("13", "13", "NO 1 common", "Control"), new("14", "14", "NO 1 switched", "Control"),
                        new("21", "21", "NC 1 common", "Control"), new("22", "22", "NC 1 switched", "Control")],
                    [Physical("2931", "Реле з розеткою", 15.8, 78, 72, "DIN-рейка", 1), Physical("2932", "Реле без розетки", 13, 29, 35, "PCB")])
            ])
        ])
    ]);

    private static DeviceVariant Relay(string id, string name, string voltage, string kind, string contacts, string symbol, string physicalId) =>
        new(G(id), name, "DEMO-" + name.Replace(' ', '-'), $"{voltage} V {kind} · {contacts}", symbol,
            [new("coil-voltage", voltage), new("coil-kind", kind), new("contacts", contacts)],
            [new("coil-power", "Споживання котушки", kind == "DC" ? "0.8" : "1.2", "W")],
            [new("A1", "A1", "Котушка", "Coil"), new("A2", "A2", "Котушка", "Coil"),
                new("13", "13", "NO common", "Control"), new("14", "14", "NO switched", "Control")],
            [Physical(physicalId, "Реле з розеткою", 15.8, 78, 72, "DIN-рейка", 1)]);

    private static DeviceVariant Vfd(string id, string name, string article, string supply, string power, string control,
        string physicalId, double width, double height, double depth) => new(G(id), name, article, $"{power} kW · {supply}", "IEC_VFD",
        [new("supply", supply), new("power", power), new("control", control)],
        [new("output-power", "Потужність двигуна", power, "kW"), new("output-frequency", "Вихідна частота", "0–550", "Hz")],
        [new("L1", "L1", "Вхід живлення", "Power"), new("L2", "L2", "Вхід живлення", "Power"), new("L3", "L3", "Вхід живлення", "Power"),
            new("U", "U", "Вихід двигуна", "Power"), new("V", "V", "Вихід двигуна", "Power"), new("W", "W", "Вихід двигуна", "Power")],
        [Physical(physicalId, "Настінне виконання", width, height, depth, "Монтажна панель"),
            Physical((int.Parse(physicalId) + 10).ToString(), "Виконання з боковим фільтром", width + 35, height, depth + 15, "Монтажна панель")]);

    private static DeviceContact[] PowerContacts(int poles) => Enumerable.Range(1, poles).SelectMany(index => new[]
    {
        new DeviceContact($"{index}L", $"{index}", $"Вхід L{index}", "Power"),
        new DeviceContact($"{index}T", $"{index + poles}", $"Вихід T{index}", "Power")
    }).ToArray();

    private static PhysicalRepresentation Physical(string id, string name, double width, double height, double depth,
        string mounting, double? modules = null) => new(G(id), name, width, height, depth, mounting, modules,
        [new(new(0, 0), new(width, 0)), new(new(width, 0), new(width, height)),
            new(new(width, height), new(0, height)), new(new(0, height), new(0, 0))]);

    private static Guid G(string suffix) => Guid.Parse($"00000000-0000-0000-0000-{suffix.PadLeft(12, '0')}");
}
