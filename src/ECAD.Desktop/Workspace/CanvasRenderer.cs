using System.Globalization;
using Avalonia;
using Avalonia.Media;
using ECAD.Core;
using Geometry = ECAD.Core.Geometry;

namespace ECAD.Desktop;

// Reads a scene snapshot; owns no input, controls, session mutations or tool state.
internal sealed class CanvasRenderer(DrawingDocument document, IReadOnlySet<Guid> selection, double scale, Point origin)
{
    private Point Screen(PointMm p) => new(origin.X + p.X * scale, origin.Y + p.Y * scale);
    public void DrawPageFrame(DrawingContext context, DrawingPage page)
    {
        const double margin = 10;
        var framePen = new Pen(Brushes.Black, Math.Max(1, .3 * scale));
        var topLeft = Screen(new(margin, margin));
        var bottomRight = Screen(new(page.WidthMm - margin, page.HeightMm - margin));
        context.DrawRectangle(null, framePen, new Rect(topLeft, bottomRight));

        var zoneFont = Math.Max(6, 2.5 * scale);
        for (var i = 0; i < page.HorizontalZones; i++)
        {
            var x = margin + (i + .5) * (page.WidthMm - 2 * margin) / page.HorizontalZones;
            DrawPageText(context, ((char)('A' + i % 26)).ToString(), new(x, margin - 2), zoneFont, true);
            DrawPageText(context, ((char)('A' + i % 26)).ToString(), new(x, page.HeightMm - margin + 4), zoneFont, true);
        }
        for (var i = 0; i < page.VerticalZones; i++)
        {
            var y = margin + (i + .5) * (page.HeightMm - 2 * margin) / page.VerticalZones;
            DrawPageText(context, (i + 1).ToString(CultureInfo.InvariantCulture), new(margin - 3, y), zoneFont, true);
            DrawPageText(context, (i + 1).ToString(CultureInfo.InvariantCulture), new(page.WidthMm - margin + 3, y), zoneFont, true);
        }

        var blockWidth = Math.Min(180, page.WidthMm - 2 * margin);
        const double blockHeight = 40;
        var x0 = page.WidthMm - margin - blockWidth; var y0 = page.HeightMm - margin - blockHeight;
        context.DrawRectangle(Brushes.White, framePen, new Rect(Screen(new(x0, y0)), Screen(new(page.WidthMm - margin, page.HeightMm - margin))));
        for (var row = 1; row < 4; row++)
            context.DrawLine(framePen, Screen(new(x0, y0 + row * 10)), Screen(new(page.WidthMm - margin, y0 + row * 10)));
        var title = page.TitleBlock;
        DrawPageText(context, string.IsNullOrWhiteSpace(title.Project) ? "Проєкт" : title.Project, new(x0 + 2, y0 + 7), Math.Max(1, 2.7 * scale));
        DrawPageText(context, string.IsNullOrWhiteSpace(title.Drawing) ? page.Name : title.Drawing, new(x0 + 2, y0 + 17), Math.Max(1, 2.7 * scale));
        DrawPageText(context, $"Автор: {title.Author}    Рев.: {title.Revision}", new(x0 + 2, y0 + 27), Math.Max(1, 2.5 * scale));
        DrawPageText(context, $"Аркуш {page.Number} · {page.WidthMm:0.#}×{page.HeightMm:0.#} мм", new(x0 + 2, y0 + 37), Math.Max(1, 2.5 * scale));
    }

    private void DrawPageText(DrawingContext context, string value, PointMm point, double fontSize, bool centered = false)
    {
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, fontSize, Brushes.Black);
        var at = Screen(point);
        context.DrawText(text, new Point(centered ? at.X - text.Width / 2 : at.X, at.Y - text.Height));
    }

    public static void DrawLiveGeometryValues(DrawingContext context, Point at, ElementKind kind, PointMm delta)
    {
        var value = kind switch
        {
            ElementKind.Line => $"L {delta.Length:0.###} мм    ∠ {Geometry.Angle(delta):0.###}°",
            ElementKind.Rectangle => $"Ш {Math.Abs(delta.X):0.###} мм    В {Math.Abs(delta.Y):0.###} мм",
            ElementKind.Circle => $"R {delta.Length:0.###} мм    ⌀ {delta.Length * 2:0.###} мм",
            ElementKind.Wire => $"L {delta.Length:0.###} мм    ∠ {Geometry.Angle(delta):0.###}°",
            ElementKind.Polyline => $"L {delta.Length:0.###} мм    ∠ {Geometry.Angle(delta):0.###}°",
            _ => ""
        };
        var text = new FormattedText(value, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 13, Brushes.DarkSlateGray);
        var x = at.X + 16; var y = at.Y + 16;
        context.DrawRectangle(Brushes.White, new Pen(Brushes.Teal, 1),
            new RoundedRect(new Rect(x - 6, y - 4, text.Width + 12, text.Height + 8), 4));
        context.DrawText(text, new Point(x, y));
    }

    public void Draw(DrawingContext ctx, DrawingElement e, IBrush brush)
    {
        var a = Screen(e.A); var b = Screen(e.B);
        var pen = new Pen(brush, e.Kind == ElementKind.Wire ? Math.Max(1.5, .42 * scale) : Math.Max(1, .25 * scale));
        switch (e.Kind)
        {
            case ElementKind.Line: ctx.DrawLine(pen, a, b); break;
            case ElementKind.Wire:
                for (var i = 1; i < e.Points!.Length; i++) ctx.DrawLine(pen, Screen(e.Points[i - 1]), Screen(e.Points[i]));
                DrawNetLabel(ctx, e);
                break;
            case ElementKind.Polyline:
                for (var i = 1; i < e.Points!.Length; i++) ctx.DrawLine(pen, Screen(e.Points[i - 1]), Screen(e.Points[i]));
                break;
            case ElementKind.Arc:
                var arc = ArcGeometry.Sample(e, Math.Max(1, 8 / scale));
                for (var i = 1; i < arc.Length; i++) ctx.DrawLine(pen, Screen(arc[i - 1]), Screen(arc[i]));
                break;
            case ElementKind.Rectangle:
                ctx.DrawRectangle(null, pen, new Rect(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y)));
                break;
            case ElementKind.Circle:
                ctx.DrawEllipse(null, pen, a, e.LengthMm * scale, e.LengthMm * scale); break;
            case ElementKind.Junction:
                var radius = Math.Max(2.5, .9 * scale);
                ctx.DrawEllipse(brush, null, a, radius, radius); break;
            case ElementKind.Text:
                var label = new FormattedText(e.Text!, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, Typeface.Default, Math.Max(6, e.TextHeightMm * scale), brush);
                using (ctx.PushTransform(new RotateTransform(e.RotationDegrees, a.X, a.Y).Value))
                    ctx.DrawText(label, new Point(a.X, a.Y - label.Height));
                break;
            case ElementKind.Symbol:
                DrawSymbol(ctx, e, pen, brush); break;
            case ElementKind.Dimension:
                var dimensionLine = e.DimensionSegments()[^1];
                var lineLength = (dimensionLine.B - dimensionLine.A).Length;
                var v = lineLength > 1e-9 ? (dimensionLine.B - dimensionLine.A) * (1 / lineLength) : new PointMm(1, 0);
                var n = new PointMm(-v.Y, v.X);
                var aa = dimensionLine.A; var bb = dimensionLine.B;
                foreach (var edge in e.DimensionSegments()) ctx.DrawLine(pen, Screen(edge.A), Screen(edge.B));
                foreach (var p in new[] { aa, bb })
                    ctx.DrawLine(pen, Screen(p - (v + n) * 1.2), Screen(p + (v + n) * 1.2));
                var prefix = e.DimensionMode == DimensionMode.Driving ? "◆ " : "";
                var symbol = e.DimensionType == DimensionType.Diameter ? "⌀ " : e.DimensionType == DimensionType.Radius ? "R " : "";
                var text = new FormattedText($"{prefix}{symbol}{e.DimensionValueMm:0.###} мм", CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, Typeface.Default, 13, brush);
                var middle = Screen((aa + bb) * .5);
                ctx.DrawRectangle(Brushes.White, null, new Rect(middle.X - text.Width / 2 - 3, middle.Y - 20, text.Width + 6, 18));
                ctx.DrawText(text, new Point(middle.X - text.Width / 2, middle.Y - 20));
                break;
        }
        if (selection.Contains(e.Id))
            foreach (var p in AssociativeDimensions.Vertices(e).DefaultIfEmpty(e.A).Distinct().Select(Screen))
                ctx.DrawRectangle(Brushes.White, new Pen(brush, 1), new Rect(p.X - 3, p.Y - 3, 6, 6));
        DrawCrossPageMarker(ctx, e);
    }

    private void DrawNetLabel(DrawingContext context, DrawingElement wire)
    {
        var net = document.Nets.FirstOrDefault(item => item.Id == wire.NetId);
        if (net is null || wire.Points is not { Length: >= 2 }) return;
        var middle = (wire.Points[0] + wire.Points[1]) * .5;
        var text = new FormattedText(net.Number, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, Math.Max(7, 2.8 * scale), Brushes.DarkGreen);
        var at = Screen(middle + new PointMm(0, -1.5));
        context.DrawRectangle(Brushes.White, null, new Rect(at.X - 2, at.Y - text.Height, text.Width + 4, text.Height + 2));
        context.DrawText(text, new Point(at.X, at.Y - text.Height));
    }

    private void DrawCrossPageMarker(DrawingContext context, DrawingElement element)
    {
        var currentPage = document.ActivePageId;
        var reference = document.CrossPageReferences.FirstOrDefault(item =>
            item.FromPageId == currentPage && item.FromElementId == element.Id ||
            item.ToPageId == currentPage && item.ToElementId == element.Id);
        if (reference is null) return;
        var outgoing = reference.FromPageId == currentPage;
        var otherPageId = outgoing ? reference.ToPageId : reference.FromPageId;
        var other = document.Pages.FirstOrDefault(page => page.Id == otherPageId);
        if (other is null) return;
        var value = reference.Label ?? $"{(outgoing ? "→" : "←")}{other.Number}";
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            Typeface.Default, Math.Max(8, 3 * scale), Brushes.RoyalBlue);
        var at = Screen(element.B + new PointMm(2, -2));
        context.DrawRectangle(Brushes.White, null, new Rect(at.X - 2, at.Y - text.Height, text.Width + 4, text.Height + 2));
        context.DrawText(text, new Point(at.X, at.Y - text.Height));
    }

    public static IBrush ElementBrush(ElementKind kind) => kind switch
    {
        ElementKind.Wire or ElementKind.Junction => new SolidColorBrush(Color.Parse("#176b3a")),
        ElementKind.Line => new SolidColorBrush(Color.Parse("#4b5563")),
        _ => Brushes.Black
    };

    private void DrawSymbol(DrawingContext ctx, DrawingElement e, Pen pen, IBrush brush)
    {
        PointMm P(double x, double y) => Geometry.Rotate(e.A + new PointMm(x, y), e.A, e.RotationDegrees);
        void Line(double x1, double y1, double x2, double y2) => ctx.DrawLine(pen, Screen(P(x1, y1)), Screen(P(x2, y2)));
        if (e.SymbolStrokes is { } strokes)
            foreach (var stroke in strokes) Line(stroke.A.X, stroke.A.Y, stroke.B.X, stroke.B.Y);
        else switch (e.SymbolKey)
        {
            case "IEC_NO_CONTACT":
                Line(-7.5, 0, -2.5, 0); Line(-2.5, 0, 2.5, -3); Line(2.5, 0, 7.5, 0); break;
            case "IEC_NC_CONTACT":
                Line(-7.5, 0, -2.5, 0); Line(-2.5, 0, 2.5, 0); Line(2.5, 0, 7.5, 0); Line(-1.5, -3, 1.5, 3); break;
            case "IEC_COIL":
                Line(-7.5, 0, -3, 0); Line(3, 0, 7.5, 0);
                Line(-3, -4, 3, -4); Line(3, -4, 3, 4); Line(3, 4, -3, 4); Line(-3, 4, -3, -4);
                break;
            case "IEC_TERMINAL":
                ctx.DrawEllipse(null, pen, Screen(e.A), 2.2 * scale, 2.2 * scale); break;
            case "IEC_FUSE":
                Line(-7.5, 0, -3.5, 0); Line(3.5, 0, 7.5, 0);
                Line(-3.5, -2, 3.5, -2); Line(3.5, -2, 3.5, 2); Line(3.5, 2, -3.5, 2); Line(-3.5, 2, -3.5, -2); break;
            case "IEC_BREAKER":
                Line(-7.5, 0, -3, 0); Line(-3, 0, 2.5, -3); Line(3, 0, 7.5, 0); Line(-1, -4.5, 1, -2.5); break;
            case "IEC_MOTOR_3P":
                const double motorRadius = 7.5;
                var motorEdge = Math.Sqrt(motorRadius * motorRadius - 25);
                Line(-12.5, -5, -motorEdge, -5); Line(-12.5, 0, -motorRadius, 0); Line(-12.5, 5, -motorEdge, 5);
                ctx.DrawEllipse(null, pen, Screen(e.A), motorRadius * scale, motorRadius * scale);
                DrawCentredText(ctx, "M", Screen(P(0, -1)), 12, brush);
                DrawCentredText(ctx, "3~", Screen(P(0, 4)), 9, brush); break;
            case "IEC_GROUND":
                Line(0, -7.5, 0, 0); Line(-4, 0, 4, 0); Line(-2.8, 2, 2.8, 2); Line(-1.4, 4, 1.4, 4); break;
            case "IEC_VFD":
                Line(-10, -5, -7, -5); Line(-10, 0, -7, 0); Line(-10, 5, -7, 5);
                Line(7, -5, 10, -5); Line(7, 0, 10, 0); Line(7, 5, 10, 5);
                Line(-7, -8, 7, -8); Line(7, -8, 7, 8); Line(7, 8, -7, 8); Line(-7, 8, -7, -8);
                Line(-5, 4, 5, -4); break;
            case "ANSI_NO_CONTACT":
                Line(-7.5, 0, -2.5, 0); Line(-2.5, 0, 2.5, -3); Line(2.5, 0, 7.5, 0); break;
            case "ANSI_NC_CONTACT":
                Line(-7.5, 0, -2.5, 0); Line(-2.5, 0, 2.5, 0); Line(2.5, 0, 7.5, 0); Line(-1.5, -3, 1.5, 3); break;
            case "ANSI_COIL":
                Line(-7.5, 0, -4, 0); Line(4, 0, 7.5, 0);
                ctx.DrawEllipse(null, pen, Screen(e.A), 4 * scale, 4 * scale); break;
        }
    }

    private static void DrawCentredText(DrawingContext ctx, string value, Point centre, double size, IBrush brush)
    {
        var text = new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, Typeface.Default, size, brush);
        ctx.DrawText(text, new Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
    }

}
