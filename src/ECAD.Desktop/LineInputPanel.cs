using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ECAD.Core;

namespace ECAD.Desktop;

// Real text controls provide caret, clipboard, selection and keyboard support.
public sealed class LineInputPanel : Border
{
    public TextBox LengthBox { get; } = new() { Width = 116, Name = "LineLength" };
    public TextBox AngleBox { get; } = new() { Width = 116, Name = "LineAngle" };
    private readonly TextBlock firstLabel = new() { FontSize = 12 };
    private readonly TextBlock secondLabel = new() { FontSize = 12 };
    private readonly TextBlock hint = new() { FontSize = 11, Text = "Клік: кінцева точка · Enter: значення · Esc: скасувати" };
    private ElementKind mode = ElementKind.Line;
    private bool updating;
    private bool lengthLocked;
    private bool angleLocked;
    private double liveLength;
    private double liveAngle;
    public double? Length { get; private set; }
    public double? Angle { get; private set; }
    public double? FirstValue => Length;
    public double? SecondValue => Angle;
    public bool HasInput => lengthLocked || angleLocked;
    public bool Valid { get; private set; } = true;
    public event Action? Edited;
    public event Action? Confirm;
    public event Action? Cancelled;

    public LineInputPanel()
    {
        Background = Brushes.White; BorderBrush = Brushes.Teal; BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(5); Padding = new Thickness(10); IsVisible = false;
        var body = new StackPanel { Spacing = 6 };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var (label, field) in new[] { (firstLabel, LengthBox), (secondLabel, AngleBox) })
        {
            var column = new StackPanel { Spacing = 3 };
            column.Children.Add(label); column.Children.Add(field); row.Children.Add(column);
            field.TextChanged += (_, _) =>
            {
                if (updating) return;
                if (field == LengthBox) lengthLocked = !string.IsNullOrWhiteSpace(field.Text);
                else angleLocked = !string.IsNullOrWhiteSpace(field.Text);
                if (mode == ElementKind.Circle && !string.IsNullOrWhiteSpace(field.Text))
                {
                    updating = true;
                    if (field == LengthBox) { AngleBox.Text = ""; angleLocked = false; }
                    else { LengthBox.Text = ""; lengthLocked = false; }
                    updating = false;
                }
                Read(); Edited?.Invoke();
            };
            field.GotFocus += (_, _) => field.SelectAll();
        }
        body.Children.Add(row); body.Children.Add(hint); Child = body;
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Tab) { FocusField(!AngleBox.IsFocused); e.Handled = true; }
            else if (e.Key == Key.Enter) { if (Valid) Confirm?.Invoke(); e.Handled = true; }
            else if (e.Key == Key.Escape) { Cancelled?.Invoke(); e.Handled = true; }
        };
        Configure(ElementKind.Line);
    }

    private static double? Parse(string? text)
    {
        if (double.TryParse(text?.Replace(',', '.'), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)) return value;
        return null;
    }

    private void Read()
    {
        Length = lengthLocked ? Parse(LengthBox.Text) : null;
        Angle = angleLocked ? Parse(AngleBox.Text) : null;
        Valid = mode switch
        {
            ElementKind.Line => (!lengthLocked || Length is > 0 and <= 10000) &&
                (!angleLocked || Angle is >= -36000 and <= 36000),
            ElementKind.Rectangle => (!lengthLocked || Length is > 0 and <= 10000) &&
                (!angleLocked || Angle is > 0 and <= 10000),
            ElementKind.Circle => (!lengthLocked || Length is > 0 and <= 5000) &&
                (!angleLocked || Angle is > 0 and <= 10000),
            ElementKind.Wire => (!lengthLocked || Length is > 0 and <= 10000) &&
                (!angleLocked || Angle is >= -36000 and <= 36000),
            _ => false
        };
        BorderBrush = Valid ? Brushes.Teal : Brushes.IndianRed;
        hint.Text = Valid ? "Клік: довільно · Enter: за значеннями · Tab: поле" : mode switch
        {
            ElementKind.Line => "Довжина: 0…10000 мм (>0). Кут: число.",
            ElementKind.Rectangle => "Ширина й висота: 0…10000 мм (>0).",
            ElementKind.Circle => "Радіус або діаметр має бути більшим за 0.",
            ElementKind.Wire => "Довжина: 0…10000 мм (>0). Кут: число.",
            _ => "Некоректне значення."
        };
    }

    public void Begin(double? length = null, double? angle = null) => Begin(ElementKind.Line, length, angle);

    public void Begin(ElementKind kind, double? first = null, double? second = null)
    {
        Configure(kind);
        updating = true;
        lengthLocked = first.HasValue; angleLocked = second.HasValue;
        LengthBox.Text = first?.ToString("0.###############", CultureInfo.InvariantCulture) ?? "";
        AngleBox.Text = second?.ToString("0.###############", CultureInfo.InvariantCulture) ?? "";
        updating = false; Read(); IsVisible = false;
        // Live values are drawn by the canvas. Real fields appear only after
        // keyboard input, so they cannot intercept the endpoint click.
        IsHitTestVisible = false;
    }

    public void UpdateLive(double length, double angle)
    {
        liveLength = length;
        liveAngle = angle;
    }
    public void FocusField(bool angle)
    {
        IsVisible = true; IsHitTestVisible = true;
        var box = angle ? AngleBox : LengthBox; box.Focus(); box.SelectAll();
        if (string.IsNullOrWhiteSpace(box.Text))
        {
            updating = true;
            box.Text = (angle ? liveAngle : liveLength).ToString("0.###", CultureInfo.InvariantCulture);
            if (angle)
            {
                angleLocked = true;
                if (mode == ElementKind.Circle) { LengthBox.Text = ""; lengthLocked = false; }
            }
            else
            {
                lengthLocked = true;
                if (mode == ElementKind.Circle) { AngleBox.Text = ""; angleLocked = false; }
            }
            updating = false; Read(); Edited?.Invoke(); box.SelectAll();
        }
    }
    public void StartTyping(string text)
    {
        IsVisible = true; IsHitTestVisible = true;
        LengthBox.Focus(); LengthBox.Text = text; LengthBox.CaretIndex = text.Length;
    }

    private void Configure(ElementKind kind)
    {
        if (kind is not (ElementKind.Line or ElementKind.Rectangle or ElementKind.Circle or ElementKind.Wire))
            throw new ArgumentOutOfRangeException(nameof(kind));
        mode = kind;
        (firstLabel.Text, secondLabel.Text) = kind switch
        {
            ElementKind.Line => ("Довжина, мм", "Кут, °"),
            ElementKind.Rectangle => ("Ширина, мм", "Висота, мм"),
            ElementKind.Circle => ("Радіус, мм", "Діаметр, мм"),
            ElementKind.Wire => ("Довжина сегмента, мм", "Кут, °"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
    }
}
