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
                    Field("Length", "Загальна довжина, мм", WireLength(element), true);
                    Field("Segments", "Кількість сегментів", element.Points.Length - 1, true);
                }
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
                panel.Children.Add(SectionNote("Дані компонента", "Ця секція призначена для опису, номіналів, фізичних габаритів, артикулу та наборів контактів бібліотечного компонента."));
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
                Apply(element.Id, fields, pathKind, dimensionType, dimensionMode);
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
        ComboBox? dimensionType, ComboBox? dimensionMode)
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
                updated = updated with { A = Position(), B = Position(), DeviceTag = tag, RotationDegrees = Number(fields, "Rotation") };
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
        session.Apply(elements);
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

    private static string Format(double value) => value.ToString("0.###", CultureInfo.CurrentCulture);
    private static double WireLength(DrawingElement element)
    {
        var points = element.Points!;
        return points.Zip(points.Skip(1), (a, b) => (b - a).Length).Sum();
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
        ElementKind.Rectangle => "Прямокутник", ElementKind.Circle => "Коло",
        ElementKind.Dimension => "Розмір", ElementKind.Junction => "Точка з’єднання",
        ElementKind.Text => "Текст", ElementKind.Symbol => "Символ", _ => kind.ToString()
    };
}
