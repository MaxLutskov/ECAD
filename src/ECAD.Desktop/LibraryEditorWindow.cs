using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ECAD.Core;

namespace ECAD.Desktop;

public sealed class LibraryEditorWindow : Window
{
    private readonly EditorSession session;
    private readonly TextBox search = new() { Name = "LibrarySearch", PlaceholderText = "Пошук за назвою, виробником, артикулом або параметром…" };
    private readonly ListBox libraries = new() { Name = "LibraryList" };
    private readonly ListBox types = new() { Name = "DeviceTypeList" };
    private readonly ListBox families = new() { Name = "DeviceFamilyList" };
    private readonly ListBox variants = new() { Name = "DeviceVariantList" };
    private readonly StackPanel editor = new() { Name = "LibraryItemEditor", Spacing = 7, Margin = new Thickness(12) };
    private readonly TextBlock message = new() { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
    private Guid? libraryId, typeId, familyId, variantId;
    private int editLevel;
    private bool refreshing;

    public LibraryEditorWindow(EditorSession session)
    {
        this.session = session;
        Title = "Бібліотеки пристроїв"; Width = 1220; Height = 820; MinWidth = 900; MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(12) };
        var top = new Grid { ColumnDefinitions = new("*,Auto,Auto,Auto,Auto,Auto"), ColumnSpacing = 7 };
        top.Children.Add(search);
        var add = Button("Нова бібліотека", NewLibrary, "NewLibrary"); Grid.SetColumn(add, 1); top.Children.Add(add);
        var copy = Button("Копіювати", CopyLibrary, "CopyLibrary"); Grid.SetColumn(copy, 2); top.Children.Add(copy);
        var import = AsyncButton("Імпорт…", ImportLibrary, "ImportLibrary"); Grid.SetColumn(import, 3); top.Children.Add(import);
        var export = AsyncButton("Експорт…", ExportLibrary, "ExportLibrary"); Grid.SetColumn(export, 4); top.Children.Add(export);
        var examples = Button("Додати приклади", AddExamples, "AddExampleLibraries"); Grid.SetColumn(examples, 5); top.Children.Add(examples);
        DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);

        var browser = new Grid
        {
            Height = 280, Margin = new Thickness(0, 10, 0, 0), ColumnSpacing = 8,
            ColumnDefinitions = new("*,*,*,1.25*")
        };
        browser.Children.Add(Column("Бібліотеки", libraries, "Додати", NewLibrary, 0));
        browser.Children.Add(Column("Типи / категорії", types, "Додати тип", NewType, 1));
        browser.Children.Add(Column("Сімейства / серії", families, "Додати сімейство", NewFamily, 2));
        browser.Children.Add(Column("Варіанти", variants, "Додати варіант", NewVariant, 3));
        DockPanel.SetDock(browser, Dock.Top); root.Children.Add(browser);

        var bottom = new DockPanel();
        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12, 7, 12, 12) };
        var remove = Button("Видалити вибране", DeleteSelected, "DeleteLibraryItem"); footer.Children.Add(remove);
        footer.Children.Add(message);
        DockPanel.SetDock(footer, Dock.Bottom); bottom.Children.Add(footer);
        bottom.Children.Add(new ScrollViewer { Content = editor }); root.Children.Add(bottom);
        Content = root;

        search.TextChanged += (_, _) => { message.Text = ""; Refresh(); };
        libraries.SelectionChanged += (_, _) => SelectLibrary();
        types.SelectionChanged += (_, _) => SelectType();
        families.SelectionChanged += (_, _) => SelectFamily();
        variants.SelectionChanged += (_, _) => SelectVariant();
        session.Changed += SessionChanged;
        Closed += (_, _) => session.Changed -= SessionChanged;
        Refresh();
    }

    private Border Column(string title, ListBox list, string addText, Action add, int column)
    {
        var panel = new DockPanel();
        var button = Button(addText, add, $"AddLevel{column}"); DockPanel.SetDock(button, Dock.Bottom); panel.Children.Add(button);
        var heading = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 2, 4, 6) };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading); panel.Children.Add(list);
        var border = new Border
        {
            BorderBrush = new SolidColorBrush(Color.Parse("#d8e0e8")), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(5), Padding = new Thickness(6), Child = panel
        };
        Grid.SetColumn(border, column); return border;
    }

    private void SessionChanged() => Refresh();
    private void SelectLibrary()
    {
        if (refreshing) return; message.Text = ""; libraryId = Choice(libraries)?.Id; typeId = familyId = variantId = null; editLevel = 0; Refresh();
    }
    private void SelectType()
    {
        if (refreshing) return; message.Text = ""; typeId = Choice(types)?.Id; familyId = variantId = null; editLevel = 1; Refresh();
    }
    private void SelectFamily()
    {
        if (refreshing) return; message.Text = ""; familyId = Choice(families)?.Id; variantId = null; editLevel = 2; Refresh();
    }
    private void SelectVariant()
    {
        if (refreshing) return; message.Text = ""; variantId = Choice(variants)?.Id; editLevel = 3; BuildEditor();
    }

    private void Refresh()
    {
        refreshing = true;
        try
        {
            var query = search.Text?.Trim() ?? "";
            var visibleLibraries = session.Document.ComponentLibraries.Where(library => Matches(library, query)).ToArray();
            libraryId = KeepOrFirst(visibleLibraries.Select(item => item.Id), libraryId);
            libraries.ItemsSource = visibleLibraries.Select(item => new CatalogChoice(item.Id, $"{item.Name}  v{item.Version}")).ToArray();
            libraries.SelectedItem = libraries.ItemsSource.Cast<CatalogChoice>().FirstOrDefault(item => item.Id == libraryId);

            var library = CurrentLibrary();
            var libraryMatch = library is not null && OwnMatches(library, query);
            var visibleTypes = library?.DeviceTypes.Where(type => libraryMatch || Matches(type, query)).ToArray() ?? [];
            typeId = KeepOrFirst(visibleTypes.Select(item => item.Id), typeId);
            types.ItemsSource = visibleTypes.Select(item => new CatalogChoice(item.Id, item.Name)).ToArray();
            types.SelectedItem = types.ItemsSource.Cast<CatalogChoice>().FirstOrDefault(item => item.Id == typeId);

            var type = CurrentType();
            var typeMatch = libraryMatch || type is not null && OwnMatches(type, query);
            var visibleFamilies = type?.Families.Where(family => typeMatch || Matches(family, query)).ToArray() ?? [];
            familyId = KeepOrFirst(visibleFamilies.Select(item => item.Id), familyId);
            families.ItemsSource = visibleFamilies.Select(item => new CatalogChoice(item.Id,
                $"{item.Manufacturer ?? "—"} · {item.Series ?? item.Name}")).ToArray();
            families.SelectedItem = families.ItemsSource.Cast<CatalogChoice>().FirstOrDefault(item => item.Id == familyId);

            var family = CurrentFamily();
            var familyMatch = typeMatch || family is not null && OwnMatches(family, query);
            var visibleVariants = family?.Variants.Where(variant => familyMatch || Matches(variant, query)).ToArray() ?? [];
            variantId = KeepOrFirst(visibleVariants.Select(item => item.Id), variantId);
            variants.ItemsSource = visibleVariants.Select(item => new CatalogChoice(item.Id,
                $"{item.Name} · {item.CatalogNumber ?? "без артикулу"}")).ToArray();
            variants.SelectedItem = variants.ItemsSource.Cast<CatalogChoice>().FirstOrDefault(item => item.Id == variantId);
        }
        finally { refreshing = false; }
        BuildEditor();
    }

    private void BuildEditor()
    {
        editor.Children.Clear();
        var library = CurrentLibrary(); if (library is null) { editor.Children.Add(Note("Створи або імпортуй бібліотеку.")); return; }
        var type = CurrentType(); var family = CurrentFamily(); var variant = CurrentVariant();
        if (editLevel >= 3 && variant is not null && type is not null && family is not null) { BuildVariantEditor(library, type, family, variant); return; }
        if (editLevel >= 2 && family is not null && type is not null) { BuildFamilyEditor(library, type, family); return; }
        if (editLevel >= 1 && type is not null) { BuildTypeEditor(library, type); return; }
        BuildLibraryEditor(library);
    }

    private void BuildLibraryEditor(ComponentLibrary library)
    {
        Header($"Бібліотека · версія {library.Version}");
        var name = Field("Назва", library.Name); var description = Field("Опис", library.Description, true);
        SaveButton(() => session.UpdateComponentLibrary(library with { Name = Required(name.Text), Description = Optional(description.Text) }));
    }

    private void BuildTypeEditor(ComponentLibrary library, DeviceTypeDefinition type)
    {
        Header("Тип пристрою / категорія");
        var name = Field("Назва", type.Name); var description = Field("Опис", type.Description, true);
        editor.Children.Add(Note("Поля конфігурації: ключ | назва | обов’язкове так/ні | дозволені значення через кому | одиниця. Порожній список значень дозволяє довільний текст."));
        var configuration = Field("Поля конфігурації", FormatConfigurationFields(type.ConfigurationFields), true, 110, "TypeConfigurationFields");
        SaveButton(() =>
        {
            var updated = type with { Name = Required(name.Text), Description = Optional(description.Text), ConfigurationFields = ParseConfigurationFields(configuration.Text) };
            session.UpdateComponentLibrary(library with { DeviceTypes = library.DeviceTypes.Select(item => item.Id == type.Id ? updated : item).ToArray() });
        });
    }

    private void BuildFamilyEditor(ComponentLibrary library, DeviceTypeDefinition type, DeviceFamily family)
    {
        Header("Сімейство / серія");
        var name = Field("Назва сімейства", family.Name); var manufacturer = Field("Виробник", family.Manufacturer);
        var series = Field("Серія", family.Series);
        editor.Children.Add(Note("Спільні параметри: ключ | назва | значення | одиниця. Варіант може перевизначити параметр із тим самим ключем."));
        var parameters = Field("Спільні параметри", FormatParameters(family.SharedParameters), true, 90, "FamilySharedParameters");
        SaveButton(() =>
        {
            var updated = family with { Name = Required(name.Text), Manufacturer = Optional(manufacturer.Text), Series = Optional(series.Text), SharedParameters = ParseParameters(parameters.Text) };
            var updatedType = type with { Families = type.Families.Select(item => item.Id == family.Id ? updated : item).ToArray() };
            session.UpdateComponentLibrary(library with { DeviceTypes = library.DeviceTypes.Select(item => item.Id == type.Id ? updatedType : item).ToArray() });
        });
    }

    private void BuildVariantEditor(ComponentLibrary library, DeviceTypeDefinition type, DeviceFamily family, DeviceVariant variant)
    {
        Header("Варіант пристрою");
        var name = Field("Назва варіанта", variant.Name); var catalog = Field("Артикул", variant.CatalogNumber);
        var rating = Field("Номінал / опис виконання", variant.RatingText);
        editor.Children.Add(new TextBlock { Text = "Умовне позначення" });
        var definitions = SymbolLibrary.Definitions(session.Document);
        var symbol = new ComboBox { Name = "VariantSymbol", ItemsSource = definitions.Select(item => new SymbolChoice(item.Key, item.Name)).ToArray() };
        symbol.SelectedItem = symbol.ItemsSource.Cast<SymbolChoice>().First(item => item.Key == variant.SymbolKey); editor.Children.Add(symbol);
        editor.Children.Add(Note("Конфігурація: ключ | значення. Ключі задаються у типі пристрою."));
        var configuration = Field("Значення конфігурації", FormatConfiguration(variant.Configuration), true, 85, "VariantConfiguration");
        editor.Children.Add(Note("Параметри: ключ | назва | значення | одиниця."));
        var parameters = Field("Параметри варіанта", FormatParameters(variant.Parameters), true, 85, "VariantParameters");
        editor.Children.Add(Note("Контакти: ID | позначення | функція | електричний тип."));
        var contacts = Field("Контакти", FormatContacts(variant.Contacts), true, 85, "VariantContacts");
        editor.Children.Add(Note("Фізичні виконання: назва | ширина | висота | глибина | монтаж | DIN-модулі. Щонайменше один рядок; десятковий роздільник — крапка або кома."));
        var physical = Field("Фізичні виконання", FormatPhysical(variant.PhysicalRepresentations), true, 100, "VariantPhysical");
        SaveButton(() =>
        {
            var updated = variant with
            {
                Name = Required(name.Text), CatalogNumber = Optional(catalog.Text), RatingText = Optional(rating.Text),
                SymbolKey = ((SymbolChoice?)symbol.SelectedItem)?.Key ?? throw new InvalidDataException("Обери умовне позначення."),
                Configuration = ParseConfiguration(configuration.Text), Parameters = ParseParameters(parameters.Text),
                Contacts = ParseContacts(contacts.Text), PhysicalRepresentations = ParsePhysical(physical.Text, variant.PhysicalRepresentations)
            };
            var updatedFamily = family with { Variants = family.Variants.Select(item => item.Id == variant.Id ? updated : item).ToArray() };
            var updatedType = type with { Families = type.Families.Select(item => item.Id == family.Id ? updatedFamily : item).ToArray() };
            session.UpdateComponentLibrary(library with { DeviceTypes = library.DeviceTypes.Select(item => item.Id == type.Id ? updatedType : item).ToArray() });
        });
    }

    private void NewLibrary() => Run(() =>
    {
        var item = session.CreateComponentLibrary(Unique("Нова бібліотека", session.Document.ComponentLibraries.Select(x => x.Name)));
        libraryId = item.Id; typeId = familyId = variantId = null; editLevel = 0;
    });
    private void CopyLibrary() => Run(() =>
    {
        var library = CurrentLibrary() ?? throw new InvalidDataException("Обери бібліотеку.");
        var copy = session.DuplicateComponentLibrary(library.Id); libraryId = copy.Id; typeId = familyId = variantId = null; editLevel = 0;
    });
    private void AddExamples() => Run(() =>
    {
        var examples = ExampleComponentLibraries.Create().Where(example => !session.Document.ComponentLibraries.Any(existing =>
            existing.Id == example.Id || string.Equals(existing.Name, example.Name, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (examples.Length == 0) throw new InvalidDataException("Демонстраційні бібліотеки вже додано.");
        session.ImportComponentLibraries(examples); libraryId = examples[0].Id; typeId = familyId = variantId = null; editLevel = 0;
    });
    private void NewType() => Run(() =>
    {
        var library = CurrentLibrary() ?? throw new InvalidDataException("Спочатку створи бібліотеку.");
        var item = session.CreateDeviceType(library.Id, Unique("Новий тип", library.DeviceTypes.Select(x => x.Name)), null, []);
        typeId = item.Id; familyId = variantId = null; editLevel = 1;
    });
    private void NewFamily() => Run(() =>
    {
        var library = CurrentLibrary() ?? throw new InvalidDataException("Обери бібліотеку.");
        var type = CurrentType() ?? throw new InvalidDataException("Спочатку створи тип пристрою.");
        var item = session.CreateDeviceFamily(library.Id, type.Id, Unique("Нове сімейство", type.Families.Select(x => x.Name)), null, null);
        familyId = item.Id; variantId = null; editLevel = 2;
    });
    private void NewVariant() => Run(() =>
    {
        var library = CurrentLibrary() ?? throw new InvalidDataException("Обери бібліотеку.");
        var type = CurrentType() ?? throw new InvalidDataException("Обери тип пристрою.");
        var family = CurrentFamily() ?? throw new InvalidDataException("Спочатку створи сімейство.");
        var configuration = type.ConfigurationFields.Where(field => field.Required).Select(field =>
            new ConfigurationValue(field.Key, field.AllowedValues?.FirstOrDefault() ?? "значення")).ToArray();
        var item = session.CreateDeviceVariant(library.Id, type.Id, family.Id,
            new(Guid.Empty, Unique("Новий варіант", family.Variants.Select(x => x.Name)), null, null,
                SymbolLibrary.Definitions(session.Document)[0].Key, configuration, [], [],
                [new(Guid.NewGuid(), "Основне виконання", 10, 10, 10, "Не вказано")]));
        variantId = item.Id; editLevel = 3;
    });

    private void DeleteSelected() => Run(() =>
    {
        var library = CurrentLibrary() ?? throw new InvalidDataException("Немає вибраного елемента.");
        var type = CurrentType(); var family = CurrentFamily(); var variant = CurrentVariant();
        if (editLevel >= 3 && variant is not null && family is not null && type is not null)
        {
            var nextFamily = family with { Variants = family.Variants.Where(item => item.Id != variant.Id).ToArray() };
            var nextType = type with { Families = type.Families.Select(item => item.Id == family.Id ? nextFamily : item).ToArray() };
            session.UpdateComponentLibrary(library with { DeviceTypes = library.DeviceTypes.Select(item => item.Id == type.Id ? nextType : item).ToArray() }); variantId = null;
        }
        else if (editLevel >= 2 && family is not null && type is not null)
        {
            var nextType = type with { Families = type.Families.Where(item => item.Id != family.Id).ToArray() };
            session.UpdateComponentLibrary(library with { DeviceTypes = library.DeviceTypes.Select(item => item.Id == type.Id ? nextType : item).ToArray() }); familyId = null;
        }
        else if (editLevel >= 1 && type is not null)
        {
            session.UpdateComponentLibrary(library with { DeviceTypes = library.DeviceTypes.Where(item => item.Id != type.Id).ToArray() }); typeId = null;
        }
        else { session.DeleteComponentLibrary(library.Id); libraryId = null; }
    });

    private async Task ImportLibrary()
    {
        try
        {
            var libraries = await WorkspaceFolders.Libraries(StorageProvider);
            var files = await StorageProvider.OpenFilePickerAsync(new()
            {
                Title = "Імпортувати бібліотеку", AllowMultiple = false, FileTypeFilter = [LibraryFileType], SuggestedStartLocation = libraries
            });
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath() ?? throw new IOException("Потрібен локальний файл.");
            var keys = SymbolLibrary.Definitions(session.Document).Select(item => item.Key);
            var library = ComponentLibraryFile.Open(path, keys); session.ImportComponentLibrary(library); libraryId = library.Id;
            message.Text = "Бібліотеку імпортовано.";
        }
        catch (Exception ex) { Error(ex, "Імпорт бібліотеки"); }
    }

    private async Task ExportLibrary()
    {
        try
        {
            var library = CurrentLibrary() ?? throw new InvalidDataException("Обери бібліотеку.");
            var libraries = await WorkspaceFolders.Libraries(StorageProvider);
            var file = await StorageProvider.SaveFilePickerAsync(new()
            {
                Title = "Експортувати бібліотеку", SuggestedFileName = SafeName(library.Name) + ".ecadlib",
                DefaultExtension = "ecadlib", FileTypeChoices = [LibraryFileType], ShowOverwritePrompt = true,
                SuggestedStartLocation = libraries
            });
            if (file is null) return;
            ComponentLibraryFile.Save(file.TryGetLocalPath() ?? throw new IOException("Потрібен локальний файл."), library);
            message.Text = "Бібліотеку експортовано.";
        }
        catch (Exception ex) { Error(ex, "Експорт бібліотеки"); }
    }

    private void SaveButton(Action save)
    {
        var button = Button("Застосувати зміни", () => Run(save), "SaveLibraryItem");
        button.HorizontalAlignment = HorizontalAlignment.Left; editor.Children.Add(button);
    }
    private TextBox Field(string label, string? value, bool multiline = false, double height = 70, string? name = null)
    {
        editor.Children.Add(new TextBlock { Text = label });
        var box = new TextBox { Name = name, Text = value ?? "", AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.NoWrap : TextWrapping.Wrap };
        if (multiline) box.Height = height; editor.Children.Add(box); return box;
    }
    private void Header(string text) => editor.Children.Add(new TextBlock { Text = text, FontSize = 18, FontWeight = FontWeight.SemiBold });
    private static TextBlock Note(string text) => new() { Text = text, Foreground = Brushes.DimGray, TextWrapping = TextWrapping.Wrap };
    private static Button Button(string text, Action action, string? name = null)
    {
        var button = new Button { Name = name, Content = text, Padding = new Thickness(10, 5), Margin = new Thickness(2) };
        button.Click += (_, _) => action(); return button;
    }
    private Button AsyncButton(string text, Func<Task> action, string name)
    {
        var button = new Button { Name = name, Content = text, Padding = new Thickness(10, 5), Margin = new Thickness(2) };
        button.Click += async (_, _) => await action(); return button;
    }
    private void Run(Action action)
    {
        message.Text = "";
        try { action(); message.Text = "Зміни застосовано."; Refresh(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or ArgumentException or OverflowException)
        { Error(ex, "Редагування бібліотеки"); }
    }
    private void Error(Exception ex, string context) { AppLog.Write(context, ex); message.Text = ex.Message; }

    private ComponentLibrary? CurrentLibrary() => session.Document.ComponentLibraries.FirstOrDefault(item => item.Id == libraryId);
    private DeviceTypeDefinition? CurrentType() => CurrentLibrary()?.DeviceTypes.FirstOrDefault(item => item.Id == typeId);
    private DeviceFamily? CurrentFamily() => CurrentType()?.Families.FirstOrDefault(item => item.Id == familyId);
    private DeviceVariant? CurrentVariant() => CurrentFamily()?.Variants.FirstOrDefault(item => item.Id == variantId);
    private static CatalogChoice? Choice(ListBox list) => list.SelectedItem as CatalogChoice;
    private static Guid? KeepOrFirst(IEnumerable<Guid> ids, Guid? selected)
    {
        var values = ids.ToArray(); return selected is { } id && values.Contains(id) ? id : values.Cast<Guid?>().FirstOrDefault();
    }

    private static bool Matches(ComponentLibrary item, string q) => OwnMatches(item, q) || item.DeviceTypes.Any(type => Matches(type, q));
    private static bool Matches(DeviceTypeDefinition item, string q) => OwnMatches(item, q) || item.Families.Any(family => Matches(family, q));
    private static bool Matches(DeviceFamily item, string q) => OwnMatches(item, q) || item.Variants.Any(variant => Matches(variant, q));
    private static bool Matches(DeviceVariant item, string q) => Empty(q) || Contains(item.Name, q) || Contains(item.CatalogNumber, q) || Contains(item.RatingText, q) ||
        item.Configuration.Any(value => Contains(value.Value, q)) || item.Parameters.Any(parameter => Contains(parameter.Name, q) || Contains(parameter.Value, q)) ||
        item.Contacts.Any(contact => Contains(contact.Designation, q) || Contains(contact.Function, q)) || item.PhysicalRepresentations.Any(physical => Contains(physical.Name, q));
    private static bool OwnMatches(ComponentLibrary item, string q) => Empty(q) || Contains(item.Name, q) || Contains(item.Description, q);
    private static bool OwnMatches(DeviceTypeDefinition item, string q) => Empty(q) || Contains(item.Name, q) || Contains(item.Description, q) ||
        item.ConfigurationFields.Any(field => Contains(field.Key, q) || Contains(field.Name, q) || field.AllowedValues?.Any(value => Contains(value, q)) == true);
    private static bool OwnMatches(DeviceFamily item, string q) => Empty(q) || Contains(item.Name, q) || Contains(item.Manufacturer, q) || Contains(item.Series, q) ||
        item.SharedParameters.Any(parameter => Contains(parameter.Name, q) || Contains(parameter.Value, q));
    private static bool Empty(string q) => q.Length == 0;
    private static bool Contains(string? value, string q) => value?.Contains(q, StringComparison.CurrentCultureIgnoreCase) == true;

    private static string FormatConfigurationFields(ConfigurationField[] values) => string.Join(Environment.NewLine,
        values.Select(item => $"{item.Key} | {item.Name} | {(item.Required ? "так" : "ні")} | {string.Join(",", item.AllowedValues ?? [])} | {item.Unit}"));
    private static string FormatConfiguration(ConfigurationValue[] values) => string.Join(Environment.NewLine, values.Select(item => $"{item.Key} | {item.Value}"));
    private static string FormatParameters(DeviceParameter[] values) => string.Join(Environment.NewLine, values.Select(item => $"{item.Key} | {item.Name} | {item.Value} | {item.Unit}"));
    private static string FormatContacts(DeviceContact[] values) => string.Join(Environment.NewLine, values.Select(item => $"{item.Id} | {item.Designation} | {item.Function} | {item.ElectricalType}"));
    private static string FormatPhysical(PhysicalRepresentation[] values) => string.Join(Environment.NewLine, values.Select(item =>
        $"{item.Name} | {N(item.WidthMm)} | {N(item.HeightMm)} | {N(item.DepthMm)} | {item.Mounting} | {(item.DinModules is { } modules ? N(modules) : "")}"));

    private static ConfigurationField[] ParseConfigurationFields(string? text) => Lines(text).Select((line, index) =>
    {
        var p = Parts(line, 5, index); var required = p[2].Equals("так", StringComparison.OrdinalIgnoreCase) || p[2].Equals("true", StringComparison.OrdinalIgnoreCase);
        return new ConfigurationField(Required(p[0]), Required(p[1]), required,
            string.IsNullOrWhiteSpace(p[3]) ? null : p[3].Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), Optional(p[4]));
    }).ToArray();
    private static ConfigurationValue[] ParseConfiguration(string? text) => Lines(text).Select((line, index) => { var p = Parts(line, 2, index); return new ConfigurationValue(Required(p[0]), Required(p[1])); }).ToArray();
    private static DeviceParameter[] ParseParameters(string? text) => Lines(text).Select((line, index) => { var p = Parts(line, 4, index); return new DeviceParameter(Required(p[0]), Required(p[1]), Required(p[2]), Optional(p[3])); }).ToArray();
    private static DeviceContact[] ParseContacts(string? text) => Lines(text).Select((line, index) => { var p = Parts(line, 4, index); return new DeviceContact(Required(p[0]), Required(p[1]), Required(p[2]), Required(p[3])); }).ToArray();
    private static PhysicalRepresentation[] ParsePhysical(string? text, PhysicalRepresentation[] previous) => Lines(text).Select((line, index) =>
    {
        var p = Parts(line, 6, index); var id = index < previous.Length ? previous[index].Id : Guid.NewGuid();
        return new PhysicalRepresentation(id, Required(p[0]), Number(p[1], index), Number(p[2], index), Number(p[3], index), Required(p[4]),
            string.IsNullOrWhiteSpace(p[5]) ? null : Number(p[5], index), index < previous.Length ? previous[index].Outline : null);
    }).ToArray();
    private static string[] Lines(string? text) => (text ?? "").Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
    private static string[] Parts(string line, int count, int index)
    {
        var parts = line.Split('|', StringSplitOptions.TrimEntries); if (parts.Length > count) throw new FormatException($"Забагато колонок у рядку {index + 1}.");
        return [.. parts, .. Enumerable.Repeat("", count - parts.Length)];
    }
    private static double Number(string text, int index)
    {
        if ((double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
            double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value)) && double.IsFinite(value)) return value;
        throw new FormatException($"Некоректне число у рядку {index + 1}.");
    }
    private static string Required(string? value) => string.IsNullOrWhiteSpace(value) ? throw new InvalidDataException("Обов’язкове поле не заповнено.") : value.Trim();
    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Unique(string basis, IEnumerable<string> names) { var used = names.ToHashSet(StringComparer.OrdinalIgnoreCase); var value = basis; var i = 2; while (used.Contains(value)) value = $"{basis} {i++}"; return value; }
    private static string N(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
    private static string SafeName(string value) => string.Concat(value.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
    private static FilePickerFileType LibraryFileType => new("Бібліотека ECAD") { Patterns = ["*.ecadlib"] };
    private sealed record CatalogChoice(Guid Id, string Label) { public override string ToString() => Label; }
    private sealed record SymbolChoice(string Key, string Label) { public override string ToString() => Label; }
}
