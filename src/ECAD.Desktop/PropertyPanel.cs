using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ECAD.Core;
using Geometry = ECAD.Core.Geometry;

namespace ECAD.Desktop;

public sealed class PropertyPanel : Border
{
    private readonly EditorSession session;
    private readonly Action<string> report;

    public PropertyPanel(EditorSession session, Action<string> report)
    {
        this.session = session;
        this.report = report;
        Width = 300;
        Padding = new Thickness(14, 12);
        Background = new SolidColorBrush(Color.Parse("#f8fafc"));
        BorderBrush = new SolidColorBrush(Color.Parse("#cbd5e1"));
        BorderThickness = new Thickness(1, 0, 0, 0);
        session.Changed += Refresh;
        Refresh();
    }

    private void Refresh()
    {
        var panel = new StackPanel { Spacing = 9 };
        panel.Children.Add(new TextBlock { Text = "Властивості", FontSize = 18, FontWeight = FontWeight.SemiBold });
        var selected = session.Document.Elements.Where(e => session.Selection.Contains(e.Id)).ToArray();
        if (selected.Length == 0)
        {
            panel.Children.Add(Info("Обери об’єкт на аркуші, щоб переглянути або змінити його параметри."));
            Child = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            return;
        }
        if (selected.Length != 1)
        {
            panel.Children.Add(Info($"Вибрано об’єктів: {selected.Length}. Для редагування властивостей вибери один об’єкт."));
            var groups = selected.Select(e => e.GroupId).Where(id => id is not null).Distinct().Count();
            if (groups > 0) panel.Children.Add(Info($"Груп у виділенні: {groups}."));
            Child = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
            return;
        }

        BuildEditor(panel, selected[0]);
        Child = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    private void BuildEditor(StackPanel panel, DrawingElement element)
    {
        panel.Children.Add(new TextBlock { Text = KindName(element.Kind), Foreground = Brushes.DimGray });
        var fields = new Dictionary<string, TextBox>();
        TextBox Field(string key, string label, object? value, bool readOnly = false)
        {
            panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Foreground = Brushes.DimGray });
            var box = new TextBox
            {
                Name = "Property" + key,
                Text = value is double number ? Format(number) : value?.ToString() ?? "",
                IsReadOnly = readOnly,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            fields[key] = box; panel.Children.Add(box); return box;
        }

        Field("Name", "Назва об’єкта", element.Name);
        ComboBox? pathKind = null;
        ComboBox? dimensionMode = null;
        ComboBox? dimensionType = null;
        ComboBox? componentVariant = null;
        ComboBox? physicalRepresentation = null;
        ComboBox? terminal = null;
        ProjectNet? electricalNet = null;
        if (element.Kind is ElementKind.Line or ElementKind.Wire)
        {
            pathKind = new ComboBox
            {
                Name = "PropertyPathKind", ItemsSource = new[] { "Провідник", "Графічна лінія" },
                SelectedIndex = element.Kind == ElementKind.Wire ? 0 : 1,
                IsEnabled = element.Kind != ElementKind.Wire || element.Points?.Length == 2
            };
            panel.Children.Add(new TextBlock { Text = "Тип", FontSize = 12, Foreground = Brushes.DimGray });
            panel.Children.Add(pathKind);
            if (!pathKind.IsEnabled) panel.Children.Add(Info("Багатосегментний провідник не можна перетворити на одну пряму без втрати геометрії."));
        }

        switch (element.Kind)
        {
            case ElementKind.Line:
                AddPositionAndLine(fields, panel, element, Field);
                break;
            case ElementKind.Wire:
                Field("X", "X початку, мм", element.A.X);
                Field("Y", "Y початку, мм", element.A.Y);
                if (element.Points!.Length == 2)
                {
                    Field("Length", "Довжина, мм", element.LengthMm);
                    Field("Angle", "Кут, °", Geometry.Angle(element.B - element.A));
                }
                else
                {
                    Field("Length", "Загальна довжина, мм", PathLength(element), true);
                    Field("Segments", "Кількість сегментів", element.Points.Length - 1, true);
                }
                electricalNet = session.Document.Nets.FirstOrDefault(net => net.Id == element.NetId);
                if (electricalNet is not null)
                {
                    panel.Children.Add(new TextBlock { Text = "Електричне коло", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
                    Field("NetNumber", "Номер провідника", electricalNet.Number);
                    Field("NetDescription", "Опис сигналу", electricalNet.Description);
                    Field("NetPotential", "Потенціал", electricalNet.Potential);
                    Field("NetClass", "Клас сигналу", electricalNet.SignalClass);
                    Field("NetColor", "Колір", electricalNet.Color);
                    Field("NetSection", "Переріз, мм²", electricalNet.CrossSectionMm2);
                }
                break;
            case ElementKind.Polyline:
                Field("Length", "Загальна довжина, мм", PathLength(element), true);
                Field("Segments", "Кількість сегментів", element.Points!.Length - 1, true);
                var vertices = Field("Vertices", "Вершини: X; Y — по одній у рядку", FormatPoints(element.Points));
                vertices.AcceptsReturn = true; vertices.MinHeight = 90; vertices.TextWrapping = TextWrapping.NoWrap;
                break;
            case ElementKind.Arc:
                Field("X", "X центра, мм", element.A.X); Field("Y", "Y центра, мм", element.A.Y);
                Field("Radius", "Радіус, мм", element.LengthMm);
                Field("StartAngle", "Початковий кут, °", Geometry.Angle(element.B - element.A));
                Field("Sweep", "Кут дуги, °", element.ArcSweepDegrees);
                Field("EndAngle", "Кінцевий кут, °", Geometry.Angle(ArcGeometry.EndPoint(element) - element.A), true);
                break;
            case ElementKind.Rectangle:
                Field("X", "X, мм", element.A.X); Field("Y", "Y, мм", element.A.Y);
                Field("Width", "Ширина, мм", Math.Abs(element.B.X - element.A.X));
                Field("Height", "Висота, мм", Math.Abs(element.B.Y - element.A.Y));
                break;
            case ElementKind.Circle:
                Field("X", "X центра, мм", element.A.X); Field("Y", "Y центра, мм", element.A.Y);
                Field("Radius", "Радіус, мм", element.LengthMm);
                Field("Diameter", "Діаметр, мм", element.LengthMm * 2, true);
                break;
            case ElementKind.Text:
                Field("X", "X, мм", element.A.X); Field("Y", "Y, мм", element.A.Y);
                Field("Text", "Текст", element.Text); Field("TextHeight", "Висота тексту, мм", element.TextHeightMm);
                Field("Rotation", "Кут повороту, °", element.RotationDegrees);
                break;
            case ElementKind.Symbol:
                Field("X", "X вставки, мм", element.A.X); Field("Y", "Y вставки, мм", element.A.Y);
                Field("DeviceTag", "Позиційне позначення", element.DeviceTag);
                Field("Rotation", "Кут повороту, °", element.RotationDegrees);
                var definition = SymbolLibrary.Get(session.Document, element.SymbolKey!);
                Field("LibraryName", "Назва в бібліотеці", definition.Name, true);
                Field("PinCount", "Кількість контактів", (element.PinOffsets ?? definition.Pins).Length, true);
                var projectDevice = session.Document.Devices.FirstOrDefault(device => device.Id == element.DeviceId);
                var deviceFunction = projectDevice?.Functions.FirstOrDefault(function => function.Id == element.DeviceFunctionId);
                if (projectDevice is not null)
                    panel.Children.Add(SectionNote("Функція пристрою",
                        $"Пристрій: {projectDevice.Tag}\nФункція: {deviceFunction?.Name ?? "—"}\n" +
                        $"Тип: {deviceFunction?.Kind.ToString() ?? "—"}\nКонтакти: {(deviceFunction is null ? "—" : string.Join(", ", deviceFunction.Terminals))}"));
                if (element.SymbolKey == "IEC_TERMINAL" && session.Document.TerminalStrips.Length > 0)
                {
                    panel.Children.Add(new TextBlock { Text = "Клема проєкту", FontSize = 12, Foreground = Brushes.DimGray });
                    var terminalChoices = session.Document.TerminalStrips.SelectMany(strip => strip.Terminals.Select(item =>
                        new TerminalChoice(item.Id, $"{strip.Tag}:{item.Number}"))).ToArray();
                    terminal = new ComboBox { Name = "PropertyTerminal", ItemsSource = terminalChoices };
                    terminal.SelectedItem = terminalChoices.FirstOrDefault(item => item.Id == element.TerminalId);
                    panel.Children.Add(terminal);
                }
                panel.Children.Add(new TextBlock { Text = "Варіант пристрою", FontSize = 12, Foreground = Brushes.DimGray });
                var variantChoices = new[] { new VariantChoice(null, "Без моделі пристрою", null) }
                    .Concat(ComponentCatalog.Variants(session.Document).Where(item => item.Variant.SymbolKey == element.SymbolKey)
                        .Select(item => new VariantChoice(item.Variant.Id,
                            $"{item.Family.Manufacturer ?? item.Library.Name} · {item.Family.Series ?? item.Family.Name} · {item.Variant.Name}", item)))
                    .ToArray();
                componentVariant = new ComboBox { Name = "PropertyComponentVariant", ItemsSource = variantChoices };
                componentVariant.SelectedItem = variantChoices.FirstOrDefault(item => item.Id == element.ComponentVariantId) ?? variantChoices[0];
                panel.Children.Add(componentVariant);
                panel.Children.Add(new TextBlock { Text = "Фізичне виконання", FontSize = 12, Foreground = Brushes.DimGray });
                physicalRepresentation = new ComboBox { Name = "PropertyPhysicalRepresentation" };
                panel.Children.Add(physicalRepresentation);
                void UpdatePhysicalChoices()
                {
                    var choice = componentVariant.SelectedItem as VariantChoice;
                    var physicalChoices = choice?.Context?.Variant.PhysicalRepresentations
                        .Select(item => new PhysicalChoice(item.Id, $"{item.Name} · {Format(item.WidthMm)}×{Format(item.HeightMm)}×{Format(item.DepthMm)} мм"))
                        .ToArray() ?? [];
                    physicalRepresentation.ItemsSource = physicalChoices;
                    physicalRepresentation.SelectedItem = physicalChoices.FirstOrDefault(item => item.Id == element.PhysicalRepresentationId)
                        ?? physicalChoices.FirstOrDefault();
                    physicalRepresentation.IsEnabled = physicalChoices.Length > 0;
                }
                componentVariant.SelectionChanged += (_, _) => UpdatePhysicalChoices();
                UpdatePhysicalChoices();
                var linked = ComponentCatalog.FindVariant(session.Document, element.ComponentVariantId);
                if (linked is null)
                    panel.Children.Add(SectionNote("Дані компонента", variantChoices.Length == 1
                        ? "У документі ще немає сумісних варіантів пристрою. Створи їх кнопкою «Бібліотеки пристроїв» (▤)."
                        : "Обери варіант пристрою та фізичне виконання."));
                else
                {
                    var configuration = string.Join(" · ", linked.Variant.Configuration.Select(value =>
                    {
                        var field = linked.DeviceType.ConfigurationFields.First(item => string.Equals(item.Key, value.Key, StringComparison.OrdinalIgnoreCase));
                        return $"{field.Name}: {value.Value}{(field.Unit is null ? "" : " " + field.Unit)}";
                    }));
                    var parameters = string.Join(" · ", ComponentCatalog.EffectiveParameters(linked)
                        .Select(value => $"{value.Name}: {value.Value}{(value.Unit is null ? "" : " " + value.Unit)}"));
                    panel.Children.Add(SectionNote("Дані компонента",
                        $"Бібліотека: {linked.Library.Name}\nТип: {linked.DeviceType.Name}\nСімейство: {linked.Family.Name}\n" +
                        $"Виробник: {linked.Family.Manufacturer ?? "—"}\nСерія: {linked.Family.Series ?? "—"}\n" +
                        $"Артикул: {linked.Variant.CatalogNumber ?? "—"}\nНомінал: {linked.Variant.RatingText ?? "—"}\n" +
                        $"Конфігурація: {(configuration.Length == 0 ? "—" : configuration)}\n" +
                        $"Параметри: {(parameters.Length == 0 ? "—" : parameters)}\nКонтактів: {linked.Variant.Contacts.Length}"));
                }
                break;
            case ElementKind.Junction:
                Field("X", "X, мм", element.A.X); Field("Y", "Y, мм", element.A.Y);
                break;
            case ElementKind.Dimension:
                panel.Children.Add(new TextBlock { Text = "Тип розміру", FontSize = 12, Foreground = Brushes.DimGray });
                dimensionType = new ComboBox
                {
                    Name = "PropertyDimensionType",
                    ItemsSource = new[] { "Вирівняний", "Горизонтальний", "Вертикальний", "Радіус", "Діаметр" },
                    SelectedIndex = (int)element.DimensionType
                };
                panel.Children.Add(dimensionType);
                panel.Children.Add(new TextBlock { Text = "Режим", FontSize = 12, Foreground = Brushes.DimGray });
                dimensionMode = new ComboBox
                {
                    Name = "PropertyDimensionMode", ItemsSource = new[] { "Довідковий", "Керувальний" },
                    SelectedIndex = (int)element.DimensionMode
                };
                panel.Children.Add(dimensionMode);
                Field("Length", "Поточне значення, мм", element.DimensionValueMm, true);
                Field("Target", "Задане значення, мм", element.DimensionTargetMm ?? element.DimensionValueMm);
                Field("Offset", "Відступ розмірної лінії, мм", element.DimensionOffset);
                panel.Children.Add(Info("Для керування лінією обери два її кінці; для прямокутника — сусідні кути; для кола — центр і контур."));
                break;
        }

        var error = new TextBlock { Foreground = Brushes.Firebrick, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(error);
        var apply = new Button { Name = "ApplyProperties", Content = "Застосувати", HorizontalAlignment = HorizontalAlignment.Stretch };
        apply.Click += (_, _) =>
        {
            try
            {
                Apply(element.Id, fields, pathKind, dimensionType, dimensionMode, componentVariant, physicalRepresentation, electricalNet, terminal);
                report("Властивості об’єкта оновлено.");
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or ArgumentOutOfRangeException)
            {
                error.Text = ex.Message;
            }
        };
        panel.Children.Add(apply);
    }

    private static void AddPositionAndLine(Dictionary<string, TextBox> fields, StackPanel panel, DrawingElement element,
        Func<string, string, object?, bool, TextBox> field)
    {
        field("X", "X початку, мм", element.A.X, false); field("Y", "Y початку, мм", element.A.Y, false);
        field("Length", "Довжина, мм", element.LengthMm, false);
        field("Angle", "Кут, °", Geometry.Angle(element.B - element.A), false);
    }

    private void Apply(Guid id, Dictionary<string, TextBox> fields, ComboBox? pathKind,
        ComboBox? dimensionType, ComboBox? dimensionMode, ComboBox? componentVariant,
        ComboBox? physicalRepresentation, ProjectNet? electricalNet, ComboBox? terminal)
    {
        var source = session.Document.Elements.Single(e => e.Id == id);
        var name = fields["Name"].Text?.Trim();
        if (name is { Length: > 200 }) throw new InvalidDataException("Назва не може бути довшою за 200 символів.");
        if (string.IsNullOrWhiteSpace(name)) name = null;
        var updated = source with { Name = name };
        PointMm Position() => new(Number(fields, "X"), Number(fields, "Y"));

        switch (source.Kind)
        {
            case ElementKind.Line:
            {
                var a = Position(); var length = Positive(fields, "Length");
                updated = updated with { A = a, B = Geometry.Polar(a, length, Number(fields, "Angle")) };
                if (pathKind?.SelectedIndex == 0) updated = updated with { Kind = ElementKind.Wire, Points = [updated.A, updated.B] };
                break;
            }
            case ElementKind.Wire:
            {
                var a = Position();
                if (source.Points!.Length == 2)
                {
                    var b = Geometry.Polar(a, Positive(fields, "Length"), Number(fields, "Angle"));
                    updated = updated with { A = a, B = b, Points = [a, b] };
                    if (pathKind?.SelectedIndex == 1) updated = updated with { Kind = ElementKind.Line, Points = null };
                }
                else updated = updated.Move(a - source.A);
                break;
            }
            case ElementKind.Polyline:
            {
                var points = ParsePoints(fields["Vertices"].Text);
                updated = updated with { A = points[0], B = points[^1], Points = points };
                break;
            }
            case ElementKind.Arc:
            {
                var centre = Position(); var radius = Positive(fields, "Radius");
                var sweep = Number(fields, "Sweep");
                if (Math.Abs(sweep) < 1e-7 || Math.Abs(sweep) >= 360)
                    throw new InvalidDataException("Кут дуги має бути в межах від -360° до 360° і не дорівнювати нулю.");
                updated = updated with
                {
                    A = centre, B = Geometry.Polar(centre, radius, Number(fields, "StartAngle")),
                    ArcSweepDegrees = sweep
                };
                break;
            }
            case ElementKind.Rectangle:
            {
                var a = Position(); var width = Positive(fields, "Width"); var height = Positive(fields, "Height");
                var sx = source.B.X < source.A.X ? -1 : 1; var sy = source.B.Y < source.A.Y ? -1 : 1;
                updated = updated with { A = a, B = new(a.X + sx * width, a.Y + sy * height) };
                break;
            }
            case ElementKind.Circle:
            {
                var a = Position(); var radius = Positive(fields, "Radius");
                updated = updated with { A = a, B = Geometry.Polar(a, radius, Geometry.Angle(source.B - source.A)) };
                break;
            }
            case ElementKind.Text:
            {
                var text = fields["Text"].Text?.Trim();
                if (string.IsNullOrWhiteSpace(text)) throw new InvalidDataException("Текст не може бути порожнім.");
                updated = updated with { A = Position(), B = Position(), Text = text, TextHeightMm = Positive(fields, "TextHeight"), RotationDegrees = Number(fields, "Rotation") };
                break;
            }
            case ElementKind.Symbol:
            {
                var tag = fields["DeviceTag"].Text?.Trim();
                if (string.IsNullOrWhiteSpace(tag)) throw new InvalidDataException("Позиційне позначення не може бути порожнім.");
                var variant = componentVariant?.SelectedItem as VariantChoice;
                var physical = physicalRepresentation?.SelectedItem as PhysicalChoice;
                updated = updated with
                {
                    A = Position(), B = Position(), DeviceTag = tag, RotationDegrees = Number(fields, "Rotation"),
                    ComponentVariantId = variant?.Id, PhysicalRepresentationId = variant?.Id is null ? null : physical?.Id,
                    TerminalId = (terminal?.SelectedItem as TerminalChoice)?.Id ?? source.TerminalId
                };
                break;
            }
            case ElementKind.Junction:
                updated = updated with { A = Position(), B = Position() };
                break;
            case ElementKind.Dimension:
                var mode = (DimensionMode)(dimensionMode?.SelectedIndex ?? 0);
                updated = updated with
                {
                    DimensionOffset = Number(fields, "Offset"),
                    DimensionType = (DimensionType)(dimensionType?.SelectedIndex ?? 0),
                    DimensionMode = mode,
                    DimensionTargetMm = mode == DimensionMode.Driving ? Positive(fields, "Target") : null
                };
                break;
        }

        var delta = updated.A - source.A;
        var elements = session.Document.Elements.Select(e =>
        {
            if (e.Id == id) return updated;
            if (source.Kind == ElementKind.Symbol && e.LinkedElementId == id)
                return e.Move(delta) with { Text = updated.DeviceTag };
            if (source.Kind == ElementKind.Text && source.LinkedElementId is { } ownerId && e.Id == ownerId)
                return e with { DeviceTag = updated.Text };
            return e;
        }).ToArray();
        if (source.Kind == ElementKind.Symbol) session.ApplyElementsAndDevice(elements, updated);
        else if (electricalNet is null) session.Apply(elements);
        else
        {
            var number = fields["NetNumber"].Text?.Trim();
            if (string.IsNullOrWhiteSpace(number)) throw new InvalidDataException("Номер провідника не може бути порожнім.");
            var sectionText = fields["NetSection"].Text?.Trim();
            double? section = string.IsNullOrWhiteSpace(sectionText) ? null : Positive(fields, "NetSection");
            session.ApplyElementsAndNet(elements, electricalNet with
            {
                Number = number, Description = Optional(fields["NetDescription"].Text),
                Potential = Optional(fields["NetPotential"].Text), SignalClass = Optional(fields["NetClass"].Text),
                Color = Optional(fields["NetColor"].Text), CrossSectionMm2 = section
            });
        }
    }

    private static double Number(Dictionary<string, TextBox> fields, string key)
    {
        var text = fields[key].Text?.Trim() ?? "";
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var value) ||
            double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            if (double.IsFinite(value)) return value;
        throw new FormatException($"Поле «{key}» містить некоректне число.");
    }

    private static double Positive(Dictionary<string, TextBox> fields, string key)
    {
        var value = Number(fields, key);
        if (value <= 0) throw new InvalidDataException("Розмір має бути більшим за нуль.");
        return value;
    }
    private static string? Optional(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Format(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);
    private static double PathLength(DrawingElement element)
    {
        var points = element.Points!;
        return points.Zip(points.Skip(1), (a, b) => (b - a).Length).Sum();
    }
    private static string FormatPoints(PointMm[] points) => string.Join(Environment.NewLine,
        points.Select(p => $"{Format(p.X)}; {Format(p.Y)}"));
    private static PointMm[] ParsePoints(string? text)
    {
        var points = (text ?? "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select((line, index) =>
            {
                var parts = line.Split([';', '\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length != 2 || !double.TryParse(parts[0].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                    !double.TryParse(parts[1].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                    !double.IsFinite(x) || !double.IsFinite(y))
                    throw new FormatException($"Некоректні координати вершини в рядку {index + 1}.");
                return new PointMm(x, y);
            }).ToArray();
        if (points.Length < 2 || points.Zip(points.Skip(1)).Any(pair => pair.First == pair.Second))
            throw new InvalidDataException("Полілінія повинна мати щонайменше дві різні послідовні вершини.");
        return points;
    }
    private static TextBlock Info(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray };
    private static Border SectionNote(string title, string text) => new()
    {
        Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(10), CornerRadius = new CornerRadius(5),
        Background = new SolidColorBrush(Color.Parse("#e8eef7")),
        Child = new StackPanel { Spacing = 4, Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, Info(text) } }
    };

    private static string KindName(ElementKind kind) => kind switch
    {
        ElementKind.Line => "Графічна лінія", ElementKind.Wire => "Провідник",
        ElementKind.Polyline => "Полілінія", ElementKind.Arc => "Дуга",
        ElementKind.Rectangle => "Прямокутник", ElementKind.Circle => "Коло",
        ElementKind.Dimension => "Розмір", ElementKind.Junction => "Точка з’єднання",
        ElementKind.Text => "Текст", ElementKind.Symbol => "Символ", _ => kind.ToString()
    };

    private sealed record VariantChoice(Guid? Id, string Label, DeviceVariantContext? Context)
    { public override string ToString() => Label; }
    private sealed record PhysicalChoice(Guid Id, string Label)
    { public override string ToString() => Label; }
    private sealed record TerminalChoice(Guid Id, string Label)
    { public override string ToString() => Label; }
}
