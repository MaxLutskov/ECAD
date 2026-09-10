namespace ECAD.Core;

public enum PaperFormat { A4Portrait, A4Landscape, A3Portrait, A3Landscape, Custom }

public sealed record PageTitleBlock
{
    public string Project { get; init; } = "";
    public string Drawing { get; init; } = "";
    public string Author { get; init; } = "";
    public string Revision { get; init; } = "";
}

public sealed record DrawingPage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int Number { get; init; } = 1;
    public string Name { get; init; } = "Аркуш 1";
    public PaperFormat Format { get; init; } = PaperFormat.A3Landscape;
    public double WidthMm { get; init; } = 420;
    public double HeightMm { get; init; } = 297;
    public bool ShowFrame { get; init; } = true;
    public int HorizontalZones { get; init; } = 8;
    public int VerticalZones { get; init; } = 6;
    public PageTitleBlock TitleBlock { get; init; } = new();
    public DrawingElement[] Elements { get; init; } = [];
}

public sealed record CrossPageReference(Guid Id, Guid FromPageId, Guid FromElementId,
    Guid ToPageId, Guid ToElementId, string? Label = null);

public static class DrawingPages
{
    public static DrawingDocument Normalize(DrawingDocument document)
    {
        if (document.Pages is not { Length: > 0 })
        {
            var page = new DrawingPage
            {
                Name = "Аркуш 1", WidthMm = document.WidthMm, HeightMm = document.HeightMm,
                Format = DetectFormat(document.WidthMm, document.HeightMm), Elements = document.Elements ?? []
            };
            return document with { SchemaVersion = 11, ActivePageId = page.Id, Pages = [page] };
        }

        var activeId = document.Pages.Any(page => page.Id == document.ActivePageId)
            ? document.ActivePageId : document.Pages[0].Id;
        var pages = document.Pages.Select(page => page.Id == activeId
            ? page with
            {
                WidthMm = document.WidthMm, HeightMm = document.HeightMm,
                Format = DetectFormat(document.WidthMm, document.HeightMm, page.Format),
                Elements = document.Elements ?? []
            }
            : page).ToArray();
        var active = pages.Single(page => page.Id == activeId);
        return document with
        {
            SchemaVersion = 11, ActivePageId = activeId, Pages = pages,
            WidthMm = active.WidthMm, HeightMm = active.HeightMm, Elements = active.Elements
        };
    }

    public static (double Width, double Height) Size(PaperFormat format) => format switch
    {
        PaperFormat.A4Portrait => (210, 297),
        PaperFormat.A4Landscape => (297, 210),
        PaperFormat.A3Portrait => (297, 420),
        PaperFormat.A3Landscape => (420, 297),
        _ => throw new ArgumentOutOfRangeException(nameof(format), "Для довільного формату потрібно задати розміри.")
    };

    public static PaperFormat DetectFormat(double width, double height, PaperFormat fallback = PaperFormat.Custom)
    {
        const double tolerance = .001;
        foreach (var format in Enum.GetValues<PaperFormat>().Where(value => value != PaperFormat.Custom))
        {
            var size = Size(format);
            if (Math.Abs(width - size.Width) < tolerance && Math.Abs(height - size.Height) < tolerance) return format;
        }
        return fallback == PaperFormat.Custom ? PaperFormat.Custom : fallback;
    }
}
