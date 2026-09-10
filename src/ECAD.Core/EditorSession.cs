namespace ECAD.Core;

// Small immutable snapshots keep an entire user action atomic. Replace with
// differential commands if profiling large drawings shows a memory bottleneck.
public sealed class EditorSession
{
    private readonly List<DrawingDocument> undo = [];
    private readonly Stack<DrawingDocument> redo = [];
    private DrawingElement[] clipboard = [];
    private int pasteCount;
    public DrawingDocument Document { get; private set; } = ElectricalProjectModel.Normalize(new());
    public HashSet<Guid> Selection { get; } = [];
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public bool CanPaste => clipboard.Length > 0;
    public DrawingPage ActivePage => Document.Pages.Single(page => page.Id == Document.ActivePageId);
    public event Action? Changed;

    public void Load(DrawingDocument document)
    {
        if (document.SchemaVersion <= 5) document = SymbolLabels.Ensure(document);
        document = Normalize(document);
        Document = document;
        undo.Clear(); redo.Clear(); Selection.Clear(); Changed?.Invoke();
    }

    public void Apply(DrawingElement[] elements)
    {
        var next = Normalize(Document with { Elements = [.. elements] });
        if (Document.Elements.SequenceEqual(next.Elements)) return;
        undo.Add(Document);
        if (undo.Count > 200) undo.RemoveAt(0);
        Document = next; redo.Clear(); Notify();
    }

    public void ApplyDocument(DrawingDocument document)
    {
        document = Normalize(document);
        if (ReferenceEquals(Document, document)) return;
        undo.Add(Document); if (undo.Count > 200) undo.RemoveAt(0);
        Document = document;
        redo.Clear(); Notify();
    }

    public void AddPage(string? name = null, PaperFormat format = PaperFormat.A3Landscape, bool showFrame = true,
        int horizontalZones = 8, int verticalZones = 6, PageTitleBlock? titleBlock = null)
    {
        var size = DrawingPages.Size(format);
        var number = Document.Pages.Max(page => page.Number) + 1;
        var page = new DrawingPage
        {
            Number = number, Name = string.IsNullOrWhiteSpace(name) ? $"Аркуш {number}" : name.Trim(),
            Format = format, WidthMm = size.Width, HeightMm = size.Height, ShowFrame = showFrame,
            HorizontalZones = horizontalZones, VerticalZones = verticalZones, TitleBlock = titleBlock ?? new()
        };
        ApplyDocument(Document with
        {
            Pages = [.. Document.Pages, page], ActivePageId = page.Id,
            WidthMm = page.WidthMm, HeightMm = page.HeightMm, Elements = page.Elements
        });
        Selection.Clear();
    }

    public void SwitchPage(Guid pageId)
    {
        if (pageId == Document.ActivePageId) return;
        var page = Document.Pages.SingleOrDefault(item => item.Id == pageId)
            ?? throw new InvalidDataException("Не знайдено аркуш.");
        Document = Normalize(Document with
        {
            ActivePageId = page.Id, WidthMm = page.WidthMm, HeightMm = page.HeightMm, Elements = page.Elements
        });
        Selection.Clear(); Changed?.Invoke();
    }

    public void UpdatePage(Guid pageId, string name, PaperFormat format, bool showFrame,
        int horizontalZones, int verticalZones, PageTitleBlock titleBlock)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new InvalidDataException("Вкажи назву аркуша.");
        var old = Document.Pages.SingleOrDefault(page => page.Id == pageId)
            ?? throw new InvalidDataException("Не знайдено аркуш.");
        var size = format == PaperFormat.Custom ? (old.WidthMm, old.HeightMm) : DrawingPages.Size(format);
        var updated = old with
        {
            Name = name.Trim(), Format = format, WidthMm = size.Item1, HeightMm = size.Item2,
            ShowFrame = showFrame, HorizontalZones = horizontalZones, VerticalZones = verticalZones,
            TitleBlock = titleBlock ?? new()
        };
        ApplyDocument(Document with
        {
            Pages = Document.Pages.Select(page => page.Id == pageId ? updated : page).ToArray(),
            WidthMm = pageId == Document.ActivePageId ? updated.WidthMm : Document.WidthMm,
            HeightMm = pageId == Document.ActivePageId ? updated.HeightMm : Document.HeightMm,
            Elements = pageId == Document.ActivePageId ? updated.Elements : Document.Elements
        });
    }

    public void DeletePage(Guid pageId)
    {
        if (Document.Pages.Length == 1) throw new InvalidDataException("У проєкті має залишитися хоча б один аркуш.");
        if (Document.CrossPageReferences.Any(reference => reference.FromPageId == pageId || reference.ToPageId == pageId))
            throw new InvalidDataException("Аркуш використовується у міжсторінкових посиланнях.");
        var pages = Document.Pages.Where(page => page.Id != pageId).OrderBy(page => page.Number)
            .Select((page, index) => page with { Number = index + 1 }).ToArray();
        var active = pageId == Document.ActivePageId ? pages[0] : pages.Single(page => page.Id == Document.ActivePageId);
        ApplyDocument(Document with
        {
            Pages = pages, ActivePageId = active.Id, WidthMm = active.WidthMm,
            HeightMm = active.HeightMm, Elements = active.Elements
        });
        Selection.Clear();
    }

    public void AddCrossPageReference(Guid fromPageId, Guid fromElementId, Guid toPageId, Guid toElementId, string? label = null)
    {
        if (fromPageId == toPageId) throw new InvalidDataException("Міжсторінкове посилання має вести на інший аркуш.");
        var reference = new CrossPageReference(Guid.NewGuid(), fromPageId, fromElementId, toPageId, toElementId,
            string.IsNullOrWhiteSpace(label) ? null : label.Trim());
        ApplyDocument(Document with { CrossPageReferences = [.. Document.CrossPageReferences, reference] });
    }

    public void RemoveCrossPageReferences(Guid pageId, Guid elementId) =>
        ApplyDocument(Document with { CrossPageReferences = Document.CrossPageReferences.Where(reference =>
            !(reference.FromPageId == pageId && reference.FromElementId == elementId) &&
            !(reference.ToPageId == pageId && reference.ToElementId == elementId)).ToArray() });

    public void UpdateNet(Guid netId, string number, string? description, string? potential,
        string? signalClass, string? color, double? crossSectionMm2, bool numberLocked = false)
    {
        if (string.IsNullOrWhiteSpace(number)) throw new InvalidDataException("Вкажи номер провідника.");
        var net = Document.Nets.SingleOrDefault(item => item.Id == netId)
            ?? throw new InvalidDataException("Не знайдено електричне коло.");
        ApplyDocument(Document with { Nets = Document.Nets.Select(item => item.Id == netId ? net with
        {
            Number = number.Trim(), Description = Optional(description), Potential = Optional(potential),
            SignalClass = Optional(signalClass), Color = Optional(color), CrossSectionMm2 = crossSectionMm2,
            NumberLocked = numberLocked
        } : item).ToArray() });
    }

    public void ApplyElementsAndNet(DrawingElement[] elements, ProjectNet updatedNet)
    {
        if (!Document.Nets.Any(net => net.Id == updatedNet.Id)) throw new InvalidDataException("Не знайдено електричне коло.");
        ApplyDocument(Document with
        {
            Elements = elements,
            Nets = Document.Nets.Select(net => net.Id == updatedNet.Id ? updatedNet : net).ToArray()
        });
    }

    public void ApplyElementsAndDevice(DrawingElement[] activeElements, DrawingElement updatedSymbol)
    {
        if (updatedSymbol.DeviceId is not { } deviceId) { Apply(activeElements); return; }
        var device = Document.Devices.SingleOrDefault(item => item.Id == deviceId)
            ?? throw new InvalidDataException("Не знайдено пристрій проєкту.");
        var activePage = Document.ActivePageId;
        var pages = Document.Pages.Select(page => page.Id == activePage ? page with { Elements = activeElements } : page).ToArray();
        pages = pages.Select(page =>
        {
            var owned = page.Elements.Where(element => element.Kind == ElementKind.Symbol && element.DeviceId == deviceId)
                .Select(element => element.Id).ToHashSet();
            return page with { Elements = page.Elements.Select(element =>
                element.Kind == ElementKind.Symbol && element.DeviceId == deviceId ? element with { DeviceTag = updatedSymbol.DeviceTag } :
                element.LinkedElementId is { } owner && owned.Contains(owner) ? element with { Text = updatedSymbol.DeviceTag } : element).ToArray() };
        }).ToArray();
        var current = pages.Single(page => page.Id == activePage);
        ApplyDocument(Document with
        {
            Pages = pages, Elements = current.Elements,
            Devices = Document.Devices.Select(item => item.Id == deviceId ? device with
            {
                Tag = updatedSymbol.DeviceTag!, Description = updatedSymbol.Name,
                ComponentVariantId = updatedSymbol.ComponentVariantId ?? device.ComponentVariantId,
                PhysicalRepresentationId = updatedSymbol.PhysicalRepresentationId ?? device.PhysicalRepresentationId
            } : item).ToArray()
        });
    }

    public void RenumberNets(string prefix = "", int start = 1, int step = 1)
    {
        if (start < 0 || step < 1) throw new InvalidDataException("Некоректні параметри нумерації.");
        var next = start;
        var nets = Document.Nets.Select(net => net.NumberLocked ? net : net with { Number = prefix + next++ }).ToArray();
        ApplyDocument(Document with { Nets = nets });
    }

    public TerminalStrip CreateTerminalStrip(string tag, string? description, int terminalCount,
        TerminalKind kind = TerminalKind.FeedThrough)
    {
        if (string.IsNullOrWhiteSpace(tag) || terminalCount is < 1 or > 10000)
            throw new InvalidDataException("Вкажи позначення та правильну кількість клем.");
        var strip = new TerminalStrip(Guid.NewGuid(), tag.Trim(), Optional(description),
            Enumerable.Range(1, terminalCount).Select(number =>
                new ProjectTerminal(Guid.NewGuid(), number.ToString(), 1, kind)).ToArray());
        ApplyDocument(Document with { TerminalStrips = [.. Document.TerminalStrips, strip] });
        return strip;
    }

    public void AssignTerminal(Guid symbolId, Guid terminalId)
    {
        if (!Document.TerminalStrips.SelectMany(strip => strip.Terminals).Any(item => item.Id == terminalId))
            throw new InvalidDataException("Не знайдено клему.");
        var symbol = Document.Elements.SingleOrDefault(item => item.Id == symbolId && item.Kind == ElementKind.Symbol)
            ?? throw new InvalidDataException("Виділений елемент не є символом.");
        Apply(Document.Elements.Select(item => item.Id == symbol.Id ? item with { TerminalId = terminalId } : item).ToArray());
    }

    public ProjectCable CreateCable(string tag, string? type, int coreCount, double? crossSectionMm2,
        double? lengthM, string? from, string? to)
    {
        if (string.IsNullOrWhiteSpace(tag) || coreCount is < 1 or > 10000)
            throw new InvalidDataException("Вкажи позначення та правильну кількість жил.");
        var cable = new ProjectCable(Guid.NewGuid(), tag.Trim(), Optional(type), coreCount, crossSectionMm2, lengthM,
            Optional(from), Optional(to), Enumerable.Range(1, coreCount).Select(number =>
                new CableCore(Guid.NewGuid(), number.ToString(), CableCoreStatus.Spare)).ToArray());
        ApplyDocument(Document with { Cables = [.. Document.Cables, cable] });
        return cable;
    }

    public void UpdateCable(ProjectCable cable)
    {
        if (!Document.Cables.Any(item => item.Id == cable.Id)) throw new InvalidDataException("Не знайдено кабель.");
        ApplyDocument(Document with { Cables = Document.Cables.Select(item => item.Id == cable.Id ? cable : item).ToArray() });
    }

    public SymbolDefinition CreateCustomSymbol(string name, string prefix)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(prefix))
            throw new InvalidDataException("Вкажи назву та префікс символу.");
        var graphics = Document.Elements.Where(e => Selection.Contains(e.Id) &&
            e.Kind is ElementKind.Line or ElementKind.Rectangle or ElementKind.Polyline or ElementKind.Arc).ToArray();
        var pins = Document.Elements.Where(e => Selection.Contains(e.Id) && e.Kind == ElementKind.Junction).Select(e => e.A).ToArray();
        var edges = graphics.SelectMany(AssociativeDimensions.Edges).ToArray();
        if (edges.Length == 0 || pins.Length == 0)
            throw new InvalidDataException("Для символу виділи лінії/прямокутники та хоча б один вузол-контакт.");
        var points = edges.SelectMany(e => new[] { e.A, e.B }).Concat(pins).ToArray();
        var centre = new PointMm((points.Min(p => p.X) + points.Max(p => p.X)) / 2,
            (points.Min(p => p.Y) + points.Max(p => p.Y)) / 2);
        var definition = new SymbolDefinition("CUSTOM_" + Guid.NewGuid().ToString("N"), name.Trim(), prefix.Trim(),
            pins.Select(p => p - centre).ToArray(), edges.Select(e => new SymbolStroke(e.A - centre, e.B - centre)).ToArray());
        ApplyDocument(Document with { CustomSymbols = [.. Document.CustomSymbols, definition] });
        return definition;
    }

    public ComponentLibrary CreateComponentLibrary(string name, string? description = null)
    {
        var library = new ComponentLibrary(Guid.NewGuid(), Required(name, "Вкажи назву бібліотеки."), Optional(description), 1, []);
        ApplyDocument(Document with { ComponentLibraries = [.. Document.ComponentLibraries, library] });
        return library;
    }

    public void ImportComponentLibrary(ComponentLibrary library)
    {
        if (library is null) throw new InvalidDataException("Порожня бібліотека.");
        ApplyDocument(Document with { ComponentLibraries = [.. Document.ComponentLibraries, library] });
    }

    public void ImportComponentLibraries(IEnumerable<ComponentLibrary> libraries)
    {
        var additions = libraries?.ToArray() ?? throw new InvalidDataException("Порожній набір бібліотек.");
        if (additions.Length == 0) throw new InvalidDataException("Порожній набір бібліотек.");
        ApplyDocument(Document with { ComponentLibraries = [.. Document.ComponentLibraries, .. additions] });
    }

    public void UpdateComponentLibrary(ComponentLibrary library)
    {
        if (library is null) throw new InvalidDataException("Порожня бібліотека.");
        var previous = Document.ComponentLibraries.SingleOrDefault(item => item.Id == library.Id)
            ?? throw new InvalidDataException("Не знайдено бібліотеку.");
        var symbolKeys = SymbolLibrary.Definitions(Document).Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
        ComponentCatalog.Validate([library], symbolKeys);
        var previousVariants = ComponentCatalog.Variants(Document)
            .Where(item => item.Library.Id == previous.Id).ToDictionary(item => item.Variant.Id, item => item.Variant);
        var nextVariants = library.DeviceTypes.SelectMany(type => type.Families)
            .SelectMany(family => family.Variants).ToDictionary(item => item.Id);
        var linked = Document.Elements.Where(item => item.ComponentVariantId is not null).ToArray();
        if (linked.Any(item => !nextVariants.ContainsKey(item.ComponentVariantId!.Value) &&
            previousVariants.ContainsKey(item.ComponentVariantId.Value)))
            throw new InvalidDataException("Не можна видалити варіант, який використовується на схемі.");
        foreach (var item in linked.Where(item => item.ComponentVariantId is { } id && nextVariants.ContainsKey(id)))
            if (nextVariants[item.ComponentVariantId!.Value].SymbolKey != item.SymbolKey)
                throw new InvalidDataException("Не можна змінити умовне позначення варіанта, який використовується на схемі.");

        library = library with { Version = checked(previous.Version + 1) };
        var elements = Document.Elements.Select(item =>
        {
            if (item.ComponentVariantId is not { } variantId || !nextVariants.TryGetValue(variantId, out var variant)) return item;
            return item.PhysicalRepresentationId is { } physicalId &&
                variant.PhysicalRepresentations.Any(physical => physical.Id == physicalId)
                ? item : item with { PhysicalRepresentationId = variant.PhysicalRepresentations[0].Id };
        }).ToArray();
        ApplyDocument(Document with
        {
            ComponentLibraries = Document.ComponentLibraries.Select(item => item.Id == library.Id ? library : item).ToArray(),
            Elements = elements
        });
    }

    public void DeleteComponentLibrary(Guid libraryId)
    {
        var library = Document.ComponentLibraries.SingleOrDefault(item => item.Id == libraryId)
            ?? throw new InvalidDataException("Не знайдено бібліотеку.");
        var variantIds = library.DeviceTypes.SelectMany(type => type.Families).SelectMany(family => family.Variants)
            .Select(variant => variant.Id).ToHashSet();
        if (Document.Elements.Any(item => item.ComponentVariantId is { } id && variantIds.Contains(id)))
            throw new InvalidDataException("Не можна видалити бібліотеку, варіанти якої використані на схемі.");
        ApplyDocument(Document with { ComponentLibraries = Document.ComponentLibraries.Where(item => item.Id != libraryId).ToArray() });
    }

    public ComponentLibrary DuplicateComponentLibrary(Guid libraryId)
    {
        var source = Document.ComponentLibraries.SingleOrDefault(item => item.Id == libraryId)
            ?? throw new InvalidDataException("Не знайдено бібліотеку.");
        var baseName = source.Name + " — копія"; var name = baseName; var suffix = 2;
        while (Document.ComponentLibraries.Any(item => string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName} {suffix++}";
        var copy = source with
        {
            Id = Guid.NewGuid(), Name = name, Version = 1,
            DeviceTypes = source.DeviceTypes.Select(type => type with
            {
                Id = Guid.NewGuid(), Families = type.Families.Select(family => family with
                {
                    Id = Guid.NewGuid(), Variants = family.Variants.Select(variant => variant with
                    {
                        Id = Guid.NewGuid(), PhysicalRepresentations = variant.PhysicalRepresentations
                            .Select(physical => physical with { Id = Guid.NewGuid() }).ToArray()
                    }).ToArray()
                }).ToArray()
            }).ToArray()
        };
        ImportComponentLibrary(copy);
        return copy;
    }

    public DeviceTypeDefinition CreateDeviceType(Guid libraryId, string name, string? description,
        ConfigurationField[] configurationFields)
    {
        var type = new DeviceTypeDefinition(Guid.NewGuid(), Required(name, "Вкажи тип пристрою."), Optional(description),
            configurationFields ?? throw new InvalidDataException("Не задані поля конфігурації."), []);
        ReplaceLibrary(libraryId, library => library with { DeviceTypes = [.. library.DeviceTypes, type] });
        return type;
    }

    public DeviceFamily CreateDeviceFamily(Guid libraryId, Guid typeId, string name, string? manufacturer,
        string? series, DeviceParameter[]? sharedParameters = null)
    {
        var family = new DeviceFamily(Guid.NewGuid(), Required(name, "Вкажи назву сімейства."), Optional(manufacturer),
            Optional(series), sharedParameters ?? [], []);
        ReplaceType(libraryId, typeId, type => type with { Families = [.. type.Families, family] });
        return family;
    }

    public DeviceVariant CreateDeviceVariant(Guid libraryId, Guid typeId, Guid familyId, DeviceVariant variant)
    {
        if (variant.Id == Guid.Empty) variant = variant with { Id = Guid.NewGuid() };
        ReplaceFamily(libraryId, typeId, familyId, family => family with { Variants = [.. family.Variants, variant] });
        return variant;
    }

    public void AssignDeviceVariant(Guid symbolId, Guid? variantId, Guid? physicalRepresentationId = null)
    {
        var symbol = Document.Elements.SingleOrDefault(item => item.Id == symbolId && item.Kind == ElementKind.Symbol)
            ?? throw new InvalidDataException("Виділений елемент не є символом.");
        if (variantId is { } id)
        {
            var context = ComponentCatalog.FindVariant(Document, id)
                ?? throw new InvalidDataException("Не знайдено варіант пристрою.");
            if (context.Variant.SymbolKey != symbol.SymbolKey)
                throw new InvalidDataException("Умовне позначення варіанта не відповідає символу на схемі.");
            physicalRepresentationId ??= context.Variant.PhysicalRepresentations[0].Id;
        }
        Apply(Document.Elements.Select(item => item.Id == symbolId
            ? item with { ComponentVariantId = variantId, PhysicalRepresentationId = physicalRepresentationId }
            : item).ToArray());
    }

    public void Add(DrawingElement element) => Apply([.. Document.Elements, element]);
    public void Delete() => Apply(Document.Elements.Where(e => !Selection.Contains(e.Id) &&
        !(e.LinkedElementId is { } owner && Selection.Contains(owner)) &&
        !(e.StartReference is { } a && Selection.Contains(a.ElementId)) &&
        !(e.EndReference is { } b && Selection.Contains(b.ElementId))).ToArray());
    public DrawingElement[] PreviewMove(PointMm delta) => AssociativeDimensions.ResolveAll(Document.Elements.Select(e =>
    {
        var followsOwner = e.LinkedElementId is { } owner && Selection.Contains(owner);
        if (!Selection.Contains(e.Id) && !followsOwner) return e;
        if (e.Kind != ElementKind.Dimension || (e.StartReference is null && e.EndReference is null)) return e.Move(delta);
        var sourcesMoving = (e.StartReference is null || Selection.Contains(e.StartReference.ElementId)) &&
            (e.EndReference is null || Selection.Contains(e.EndReference.ElementId));
        if (sourcesMoving) return e with { A = e.StartReference is null ? e.A + delta : e.A, B = e.EndReference is null ? e.B + delta : e.B };
        var v = e.LengthMm > 1e-9 ? (e.B - e.A) * (1 / e.LengthMm) : new PointMm(1, 0);
        return e with { DimensionOffset = e.DimensionOffset - v.Y * delta.X + v.X * delta.Y };
    }).ToArray());
    public void Move(PointMm delta) => Apply(PreviewMove(delta));

    public DrawingElement[] PreviewMoveVertex(Guid elementId, int vertexIndex, PointMm target)
    {
        var source = Document.Elements.SingleOrDefault(e => e.Id == elementId)
            ?? throw new InvalidDataException("Не знайдено полілінію.");
        if (source.Kind != ElementKind.Polyline || source.Points is null || vertexIndex < 0 || vertexIndex >= source.Points.Length)
            throw new InvalidDataException("Редагування вершини доступне для полілінії.");
        var points = source.Points.ToArray(); points[vertexIndex] = target;
        if (points.Zip(points.Skip(1)).Any(pair => pair.First == pair.Second))
            throw new InvalidDataException("Сусідні вершини полілінії не можуть збігатися.");
        var updated = source with { A = points[0], B = points[^1], Points = points };
        return AssociativeDimensions.ResolveAll(Document.Elements.Select(e => e.Id == elementId ? updated : e).ToArray());
    }

    public void MoveVertex(Guid elementId, int vertexIndex, PointMm target) =>
        Apply(PreviewMoveVertex(elementId, vertexIndex, target));

    public void SelectBox(SelectionBox box, bool crossing, bool additive)
    {
        if (!additive) Selection.Clear();
        // Existing groups are selected as units: window selection must enclose
        // the whole group, crossing selection only needs to touch one member.
        foreach (var group in Document.Elements.GroupBy(e => e.GroupId ?? e.Id))
            if (crossing ? group.Any(e => box.Matches(e, true)) : group.All(e => box.Matches(e, false)))
                Selection.UnionWith(group.Select(e => e.Id));
        Changed?.Invoke();
    }
    public void Group()
    {
        if (Selection.Count < 2) return;
        var id = Guid.NewGuid();
        Apply(Document.Elements.Select(e => Selection.Contains(e.Id) ? e with { GroupId = id } : e).ToArray());
    }
    public void Ungroup() => Apply(Document.Elements.Select(e => Selection.Contains(e.Id) ? e with { GroupId = null } : e).ToArray());

    public void Copy()
    {
        clipboard = Document.Elements.Where(e => Selection.Contains(e.Id) ||
            e.LinkedElementId is { } owner && Selection.Contains(owner)).ToArray();
        pasteCount = 0;
    }

    public void Paste()
    {
        if (!CanPaste) return;
        pasteCount++;
        var offset = new PointMm(5 * pasteCount, 5 * pasteCount);
        var ids = clipboard.ToDictionary(e => e.Id, _ => Guid.NewGuid());
        var groupIds = clipboard.Where(e => e.GroupId is not null).Select(e => e.GroupId!.Value)
            .Distinct().ToDictionary(id => id, _ => Guid.NewGuid());
        GeometryReference? Remap(GeometryReference? reference) => reference is null ? null :
            reference with { ElementId = ids.GetValueOrDefault(reference.ElementId, reference.ElementId) };
        var copies = clipboard.Select(e => e with
        {
            Id = ids[e.Id],
            GroupId = e.GroupId is { } group ? groupIds[group] : null,
            A = e.A + offset,
            B = e.B + offset,
            Points = e.Points?.Select(p => p + offset).ToArray(),
            StartReference = Remap(e.StartReference),
            EndReference = Remap(e.EndReference)
            ,LinkedElementId = e.LinkedElementId is { } owner && ids.TryGetValue(owner, out var newOwner) ? newOwner : null
        }).ToArray();
        Apply([.. Document.Elements, .. copies]);
        Selection.Clear(); Selection.UnionWith(copies.Select(e => e.Id)); Changed?.Invoke();
    }

    public void RotateSelection90()
    {
        var selected = Document.Elements.Where(e => Selection.Contains(e.Id)).ToArray();
        if (selected.Length == 0) return;
        var points = selected.Where(e => e.Kind != ElementKind.Dimension ||
                (e.StartReference is null && e.EndReference is null))
            .SelectMany(e => e.Kind == ElementKind.Circle
                ? new[] { e.A - new PointMm(e.LengthMm, e.LengthMm), e.A + new PointMm(e.LengthMm, e.LengthMm) }
                : e.Kind == ElementKind.Arc ? ArcGeometry.Sample(e)
                : e.Kind is ElementKind.Wire or ElementKind.Polyline ? e.Points! : new[] { e.A, e.B }).ToArray();
        if (points.Length == 0) return;
        var centre = new PointMm((points.Min(p => p.X) + points.Max(p => p.X)) / 2,
            (points.Min(p => p.Y) + points.Max(p => p.Y)) / 2);
        PointMm Rotate(PointMm p)
        {
            var v = p - centre;
            return centre + new PointMm(-v.Y, v.X);
        }
        var rotated = Document.Elements.Select(e =>
        {
            var followsOwner = e.LinkedElementId is { } owner && Selection.Contains(owner);
            if (!Selection.Contains(e.Id) && !followsOwner) return e;
            if (e.Kind == ElementKind.Dimension && (e.StartReference is not null || e.EndReference is not null)) return e;
            if (e.Kind == ElementKind.Rectangle)
            {
                var a = Rotate(e.A); var b = Rotate(e.B);
                return e with { A = new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y)), B = new(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y)) };
            }
            if (e.Kind is ElementKind.Wire or ElementKind.Polyline)
            {
                var points = e.Points!.Select(Rotate).ToArray();
                return e with { A = points[0], B = points[^1], Points = points };
            }
            return e with
            {
                A = Rotate(e.A), B = Rotate(e.B),
                RotationDegrees = e.Kind == ElementKind.Symbol || e.Kind == ElementKind.Text && !followsOwner
                    ? (e.RotationDegrees + 90) % 360 : e.RotationDegrees
            };
        }).Select(e =>
        {
            GeometryReference? Remap(GeometryReference? reference)
            {
                if (reference is null || !Selection.Contains(reference.ElementId)) return reference;
                var owner = Document.Elements.Single(source => source.Id == reference.ElementId);
                if (reference.Kind == ReferenceKind.Curve)
                    return reference with { Parameter = (reference.Parameter + .25) % 1 };
                return owner.Kind == ElementKind.Rectangle && reference.Kind == ReferenceKind.Vertex
                    ? reference with { Index = (reference.Index + 1) % 4 } : reference;
            }
            if (e.Kind != ElementKind.Dimension) return e;
            var followsRotation = e.StartReference is { } start && Selection.Contains(start.ElementId) ||
                e.EndReference is { } end && Selection.Contains(end.ElementId);
            var type = followsRotation ? e.DimensionType switch
            {
                DimensionType.Horizontal => DimensionType.Vertical,
                DimensionType.Vertical => DimensionType.Horizontal,
                _ => e.DimensionType
            } : e.DimensionType;
            return e with
            {
                StartReference = Remap(e.StartReference),
                EndReference = Remap(e.EndReference),
                DimensionType = type
            };
        }).ToArray();
        Apply(rotated);
    }

    public void Select(DrawingElement? element, bool additive)
    {
        if (!additive) Selection.Clear();
        if (element is not null)
            foreach (var e in Document.Elements.Where(e => e.Id == element.Id ||
                (element.GroupId is not null && e.GroupId == element.GroupId))) Selection.Add(e.Id);
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (!CanUndo) return;
        redo.Push(Document); Document = undo[^1]; undo.RemoveAt(undo.Count - 1); Notify();
    }
    public void Redo()
    {
        if (!CanRedo) return;
        undo.Add(Document); Document = redo.Pop(); Notify();
    }
    private void Notify()
    {
        Selection.IntersectWith(Document.Elements.Select(e => e.Id)); Changed?.Invoke();
    }

    private static DrawingDocument Normalize(DrawingDocument document)
    {
        if (document.SchemaVersion is < 1 or > 12)
            throw new InvalidDataException("Непідтримувана версія документа.");
        var elements = DrivingDimensions.ApplyAll(document.Elements);
        var normalized = ElectricalProjectModel.Normalize(document with { SchemaVersion = 12, Elements = elements });
        normalized.Validate();
        return normalized;
    }

    private void ReplaceLibrary(Guid libraryId, Func<ComponentLibrary, ComponentLibrary> update)
    {
        if (!Document.ComponentLibraries.Any(item => item.Id == libraryId)) throw new InvalidDataException("Не знайдено бібліотеку.");
        ApplyDocument(Document with { ComponentLibraries = Document.ComponentLibraries.Select(item =>
            item.Id == libraryId ? update(item) with { Version = checked(item.Version + 1) } : item).ToArray() });
    }

    private void ReplaceType(Guid libraryId, Guid typeId, Func<DeviceTypeDefinition, DeviceTypeDefinition> update) =>
        ReplaceLibrary(libraryId, library =>
        {
            if (!library.DeviceTypes.Any(item => item.Id == typeId)) throw new InvalidDataException("Не знайдено тип пристрою.");
            return library with { DeviceTypes = library.DeviceTypes.Select(item => item.Id == typeId ? update(item) : item).ToArray() };
        });

    private void ReplaceFamily(Guid libraryId, Guid typeId, Guid familyId, Func<DeviceFamily, DeviceFamily> update) =>
        ReplaceType(libraryId, typeId, type =>
        {
            if (!type.Families.Any(item => item.Id == familyId)) throw new InvalidDataException("Не знайдено сімейство пристроїв.");
            return type with { Families = type.Families.Select(item => item.Id == familyId ? update(item) : item).ToArray() };
        });

    private static string Required(string? value, string message) => string.IsNullOrWhiteSpace(value)
        ? throw new InvalidDataException(message) : value.Trim();
    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
