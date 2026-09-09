using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace ECAD.Desktop;

// Real text controls provide caret, clipboard, selection and keyboard support.
public sealed class LineInputPanel : Border
{
    public TextBox LengthBox { get; } = new() { Width = 116, Name = "LineLength" };
    public TextBox AngleBox { get; } = new() { Width = 116, Name = "LineAngle" };
    private readonly TextBlock hint = new() { FontSize = 11, Text = "Клік: кінцева точка · Enter: значення · Esc: скасувати" };
    private bool updating;
    private bool lengthLocked;
    private bool angleLocked;
    private double liveLength;
    private double liveAngle;
    public double? Length { get; private set; }
    public double? Angle { get; private set; }
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
        foreach (var (label, field) in new[] { ("Довжина, мм", LengthBox), ("Кут, °", AngleBox) })
        {
            var column = new StackPanel { Spacing = 3 };
            column.Children.Add(new TextBlock { Text = label, FontSize = 12 }); column.Children.Add(field); row.Children.Add(column);
            field.TextChanged += (_, _) =>
            {
                if (updating) return;
                if (field == LengthBox) lengthLocked = !string.IsNullOrWhiteSpace(field.Text);
                else angleLocked = !string.IsNullOrWhiteSpace(field.Text);
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
        Valid = (!lengthLocked || Length is > 0 and <= 10000) && (!angleLocked || Angle is >= -36000 and <= 36000);
        BorderBrush = Valid ? Brushes.Teal : Brushes.IndianRed;
        hint.Text = Valid ? "Клік: кінцева точка · Enter: значення · Tab: поле" : "Довжина: 0…10000 мм (>0). Кут: число.";
    }

    public void Begin(double? length = null, double? angle = null)
    {
        updating = true;
        lengthLocked = length.HasValue; angleLocked = angle.HasValue;
        LengthBox.Text = length?.ToString("0.###############", CultureInfo.InvariantCulture) ?? "";
        AngleBox.Text = angle?.ToString("0.###############", CultureInfo.InvariantCulture) ?? "";
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
            if (angle) angleLocked = true; else lengthLocked = true;
            updating = false; Read(); Edited?.Invoke(); box.SelectAll();
        }
    }
    public void StartTyping(string text)
    {
        IsVisible = true; IsHitTestVisible = true;
        LengthBox.Focus(); LengthBox.Text = text; LengthBox.CaretIndex = text.Length;
    }
}
