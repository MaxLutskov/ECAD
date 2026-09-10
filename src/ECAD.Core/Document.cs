using System.Text.Json.Serialization;

namespace ECAD.Core;

[JsonConverter(typeof(JsonStringEnumConverter<ElementKind>))]
public enum ElementKind { Line, Rectangle, Circle, Dimension, Junction, Text, Wire, Symbol }

[JsonConverter(typeof(JsonStringEnumConverter<DimensionType>))]
public enum DimensionType { Aligned, Horizontal, Vertical, Radius, Diameter }

[JsonConverter(typeof(JsonStringEnumConverter<DimensionMode>))]
public enum DimensionMode { Reference, Driving }

// For a circle, A is the centre and B a point on the circumference.
// Dimensions store stable references; A/B are resolved measurement positions.
public sealed record DrawingElement(Guid Id, ElementKind Kind, PointMm A, PointMm B, Guid? GroupId = null)
{
    public string? Name { get; init; }
    public GeometryReference? StartReference { get; init; }
    public GeometryReference? EndReference { get; init; }
    public double DimensionOffset { get; init; } = 7;
    public DimensionType DimensionType { get; init; }
    public DimensionMode DimensionMode { get; init; }
    public double? DimensionTargetMm { get; init; }
    public string? Text { get; init; }
    public double TextHeightMm { get; init; } = 3.5;
    public double RotationDegrees { get; init; }
    public PointMm[]? Points { get; init; }
    public string? SymbolKey { get; init; }
    public string? DeviceTag { get; init; }
    public PointMm[]? PinOffsets { get; init; }
    public SymbolStroke[]? SymbolStrokes { get; init; }
    public Guid? LinkedElementId { get; init; }
    [JsonIgnore] public double LengthMm => (B - A).Length;
    [JsonIgnore] public double DimensionValueMm => DimensionType switch
    {
        DimensionType.Horizontal => Math.Abs(B.X - A.X),
        DimensionType.Vertical => Math.Abs(B.Y - A.Y),
        DimensionType.Diameter => LengthMm * 2,
        _ => LengthMm
    };
    [JsonIgnore] public PointMm[] GeometryPoints => Kind == ElementKind.Wire ? Points! : [A, B];
    public DrawingElement Move(PointMm delta) => this with
    {
        A = A + delta, B = B + delta,
        Points = Points?.Select(p => p + delta).ToArray()
    };

    public (PointMm A, PointMm B)[] DimensionSegments()
    {
        if (DimensionType is DimensionType.Radius or DimensionType.Diameter) return [(A, B)];
        if (DimensionType == DimensionType.Horizontal)
        {
            var y = (A.Y + B.Y) / 2 + DimensionOffset;
            var extra = Math.CopySign(2, DimensionOffset == 0 ? 1 : DimensionOffset);
            return [(A, new(A.X, y + extra)), (B, new(B.X, y + extra)), (new(A.X, y), new(B.X, y))];
        }
        if (DimensionType == DimensionType.Vertical)
        {
            var x = (A.X + B.X) / 2 + DimensionOffset;
            var extra = Math.CopySign(2, DimensionOffset == 0 ? 1 : DimensionOffset);
            return [(A, new(x + extra, A.Y)), (B, new(x + extra, B.Y)), (new(x, A.Y), new(x, B.Y))];
        }
        var v = LengthMm > 1e-9 ? (B - A) * (1 / LengthMm) : new PointMm(1, 0);
        var n = new PointMm(-v.Y, v.X);
        var extension = DimensionOffset + Math.CopySign(2, DimensionOffset);
        return [(A, A + n * extension), (B, B + n * extension), (A + n * DimensionOffset, B + n * DimensionOffset)];
    }

    public bool Hit(PointMm p, double tolerance)
    {
        if (Kind == ElementKind.Junction) return (p - A).Length <= tolerance;
        if (Kind == ElementKind.Symbol)
        {
            var lengths = (SymbolStrokes?.SelectMany(s => new[] { s.A.Length, s.B.Length }) ?? [])
                .Concat(PinOffsets?.Select(pin => pin.Length) ?? []);
            return (p - A).Length <= lengths.DefaultIfEmpty(10).Max() + tolerance;
        }
        if (Kind == ElementKind.Text)
        {
            var local = Geometry.Rotate(p, A, -RotationDegrees) - A;
            var lines = Text!.Split('\n');
            var width = Math.Max(TextHeightMm * .6, lines.Max(line => line.Length) * TextHeightMm * .6);
            var height = lines.Length * TextHeightMm * 1.2;
            return local.X >= -tolerance && local.X <= width + tolerance &&
                local.Y >= -TextHeightMm - tolerance && local.Y <= height - TextHeightMm + tolerance;
        }
        if (Kind == ElementKind.Circle) return Math.Abs((p - A).Length - LengthMm) <= tolerance;
        if (Kind == ElementKind.Wire)
            return AssociativeDimensions.Edges(this).Any(edge => Geometry.DistanceToSegment(p, edge.A, edge.B) <= tolerance);
        if (Kind == ElementKind.Dimension)
            return DimensionSegments().Any(edge => Geometry.DistanceToSegment(p, edge.A, edge.B) <= tolerance);
        if (Kind != ElementKind.Rectangle) return Geometry.DistanceToSegment(p, A, B) <= tolerance;
        PointMm c = new(B.X, A.Y), d = new(A.X, B.Y);
        return Geometry.DistanceToSegment(p, A, c) <= tolerance ||
            Geometry.DistanceToSegment(p, c, B) <= tolerance ||
            Geometry.DistanceToSegment(p, B, d) <= tolerance ||
            Geometry.DistanceToSegment(p, d, A) <= tolerance;
    }
}

public sealed record DrawingDocument
{
    public int SchemaVersion { get; init; } = 8;
    public double WidthMm { get; init; } = 420;
    public double HeightMm { get; init; } = 297;
    public DrawingElement[] Elements { get; init; } = [];
    public SymbolDefinition[] CustomSymbols { get; init; } = [];

    public void Validate()
    {
        if (SchemaVersion is < 1 or > 8) throw new InvalidDataException("Непідтримувана версія документа.");
        if (!double.IsFinite(WidthMm) || !double.IsFinite(HeightMm) || WidthMm <= 0 || HeightMm <= 0 ||
            WidthMm > 10000 || HeightMm > 10000)
            throw new InvalidDataException("Некоректний розмір аркуша.");
        if (Elements is null || Elements.Length > 100000)
            throw new InvalidDataException("Некоректна кількість елементів.");
        if (CustomSymbols is null || CustomSymbols.Length > 1000 || !ValidCustomSymbols(CustomSymbols))
            throw new InvalidDataException("Некоректна бібліотека власних символів.");
        var symbolKeys = SymbolLibrary.All.Select(d => d.Key).Concat(CustomSymbols.Select(d => d.Key)).ToHashSet();
        var ids = new HashSet<Guid>();
        foreach (var e in Elements)
            if (e is null || e.Id == Guid.Empty || !ids.Add(e.Id) || !Enum.IsDefined(e.Kind) ||
                !e.A.IsFinite || !e.B.IsFinite ||
                (e.Name is not null && (string.IsNullOrWhiteSpace(e.Name) || e.Name.Length > 200)) ||
                (e.LengthMm < 1e-9 && e.Kind is not (ElementKind.Dimension or ElementKind.Junction or ElementKind.Text or ElementKind.Symbol)) ||
                !double.IsFinite(e.LengthMm) || !double.IsFinite(e.DimensionOffset) ||
                !Enum.IsDefined(e.DimensionType) || !Enum.IsDefined(e.DimensionMode) ||
                (e.Kind != ElementKind.Dimension && (e.DimensionType != DimensionType.Aligned ||
                    e.DimensionMode != DimensionMode.Reference || e.DimensionTargetMm is not null)) ||
                (e.Kind == ElementKind.Dimension && e.DimensionMode == DimensionMode.Reference && e.DimensionTargetMm is not null) ||
                (e.Kind == ElementKind.Dimension && e.DimensionMode == DimensionMode.Driving &&
                    (e.DimensionTargetMm is not > 0 || !double.IsFinite(e.DimensionTargetMm.Value))) ||
                !double.IsFinite(e.TextHeightMm) || e.TextHeightMm is < .5 or > 100 ||
                !double.IsFinite(e.RotationDegrees) ||
                (e.Kind == ElementKind.Text ? string.IsNullOrWhiteSpace(e.Text) || e.Text.Length > 4096 : e.Text is not null) ||
                !ValidWire(e) || !ValidSymbol(e, symbolKeys) ||
                (e.LinkedElementId is not null && (e.Kind != ElementKind.Text || e.LinkedElementId == Guid.Empty)) ||
                (e.Kind != ElementKind.Dimension && (e.StartReference is not null || e.EndReference is not null)) ||
                e.GroupId == Guid.Empty || (e.Kind == ElementKind.Rectangle && (e.A.X == e.B.X || e.A.Y == e.B.Y)))
                throw new InvalidDataException("Некоректна геометрія або ID елемента.");
        var byId = Elements.ToDictionary(e => e.Id);
        if (Elements.Where(e => e.LinkedElementId is not null).Any(label =>
            !byId.TryGetValue(label.LinkedElementId!.Value, out var owner) || owner.Kind != ElementKind.Symbol) ||
            Elements.Where(e => e.LinkedElementId is not null).GroupBy(e => e.LinkedElementId).Any(g => g.Count() > 1))
            throw new InvalidDataException("Некоректне посилання текстового позначення.");
        _ = AssociativeDimensions.ResolveAll(Elements);
        DrivingDimensions.Validate(Elements);
    }

    private static bool ValidWire(DrawingElement e)
    {
        if (e.Kind != ElementKind.Wire) return e.Points is null;
        if (e.Points is not { Length: >= 2 and <= 10000 } points || points.Any(p => !p.IsFinite) ||
            points[0] != e.A || points[^1] != e.B) return false;
        return points.Zip(points.Skip(1)).All(pair => pair.First != pair.Second);
    }

    private static bool ValidSymbol(DrawingElement e, HashSet<string> symbolKeys)
    {
        if (e.Kind != ElementKind.Symbol)
            return e.SymbolKey is null && e.DeviceTag is null && e.PinOffsets is null && e.SymbolStrokes is null;
        return e.A == e.B && e.SymbolKey is not null && symbolKeys.Contains(e.SymbolKey) &&
            !string.IsNullOrWhiteSpace(e.DeviceTag) && e.DeviceTag.Length <= 64 &&
            (e.PinOffsets is null || e.PinOffsets is { Length: >= 1 and <= 64 } pins && pins.All(p => p.IsFinite)) &&
            (e.SymbolStrokes is null || e.SymbolStrokes is { Length: >= 1 and <= 1000 } strokes &&
                strokes.All(s => s.A.IsFinite && s.B.IsFinite && s.A != s.B));
    }

    private static bool ValidCustomSymbols(SymbolDefinition[] definitions)
    {
        var keys = new HashSet<string>(SymbolLibrary.All.Select(d => d.Key));
        foreach (var d in definitions)
            if (d is null || string.IsNullOrWhiteSpace(d.Key) || !d.Key.StartsWith("CUSTOM_", StringComparison.Ordinal) ||
                !keys.Add(d.Key) || string.IsNullOrWhiteSpace(d.Name) || d.Name.Length > 100 ||
                string.IsNullOrWhiteSpace(d.Prefix) || d.Prefix.Length > 12 ||
                d.Pins is not { Length: >= 1 and <= 64 } || d.Pins.Any(p => !p.IsFinite) ||
                d.Strokes is not { Length: >= 1 and <= 1000 } || d.Strokes.Any(s => !s.A.IsFinite || !s.B.IsFinite || s.A == s.B))
                return false;
        return true;
    }
}
