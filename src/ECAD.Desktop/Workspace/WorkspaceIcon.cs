using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ECAD.Desktop;

// Original ECAD vector paths, in a shared 24-DIP coordinate system.
internal sealed class WorkspaceIcon(string key) : Control
{
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["new"] = "M5,3 L15,3 L19,7 L19,21 L5,21 Z M12,9 L12,17 M8,13 L16,13",
        ["open"] = "M3,19 L3,6 L10,6 L12,9 L21,9 L18,19 Z M3,12 L20,12",
        ["save"] = "M4,3 L18,3 L21,6 L21,21 L3,21 L3,3 Z M7,3 L7,10 L17,10 L17,3 M7,21 L7,14 L17,14 L17,21",
        ["undo"] = "M9,5 L3,11 L9,17 M3,11 L15,11 Q21,11 21,20",
        ["redo"] = "M15,5 L21,11 L15,17 M21,11 L9,11 Q3,11 3,20",
        ["select"] = "M5,3 L5,20 L10,15 L14,22 L17,20 L13,13 L20,12 Z",
        ["line"] = "M3,21 L21,3 M2,18 L6,22 M18,2 L22,6",
        ["rectangle"] = "M3,5 L21,5 L21,19 L3,19 Z",
        ["circle"] = "M12,3 A9,9 0 1 1 11.99,3 Z",
        ["arc"] = "M3,20 Q3,3 21,3",
        ["polyline"] = "M3,20 L8,6 L16,17 L21,3",
        ["dimension"] = "M4,3 L4,21 M20,3 L20,21 M4,12 L20,12 M1,15 L7,9 M17,15 L23,9",
        ["junction"] = "M2,12 L22,12 M12,2 L12,22 M9,9 L15,9 L15,15 L9,15 Z",
        ["text"] = "M3,5 L21,5 M12,5 L12,21 M8,21 L16,21",
        ["symbol"] = "M6,6 L18,6 L18,18 L6,18 Z M1,12 L6,12 M18,12 L23,12",
        ["delete"] = "M3,6 L21,6 M7,6 L7,21 L17,21 L17,6 M9,6 L9,3 L15,3 L15,6",
        ["fit"] = "M3,9 L3,3 L9,3 M15,3 L21,3 L21,9 M21,15 L21,21 L15,21 M9,21 L3,21 L3,15",
        ["check"] = "M3,12 L9,18 L21,5",
        ["search"] = "M10,3 A7,7 0 1 1 9.99,3 M15,15 L22,22",
        ["copy"] = "M3,3 L16,3 L16,7 M3,3 L3,16 L7,16 M7,7 L21,7 L21,21 L7,21 Z",
        ["settings"] = "M3,6 L21,6 M3,12 L21,12 M3,18 L21,18 M8,3 L8,9 M16,9 L16,15 M8,15 L8,21",
        ["group"] = "M2,2 L9,2 L9,9 L2,9 Z M15,15 L22,15 L22,22 L15,22 Z M3,15 L3,21 L9,21 M15,3 L21,3 L21,9"
    };
    protected override Size MeasureOverride(Size availableSize) => new(20, 20);
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        using (context.PushTransform(Matrix.CreateScale(Bounds.Width / 24, Bounds.Height / 24)))
            context.DrawGeometry(null, new Pen(Brushes.SlateGray, 1.7), Geometry.Parse(Paths.GetValueOrDefault(key, Paths["settings"])));
    }
}
