namespace ECAD.Core;

public static class DocumentFormat
{
    public const int Current = 13;
}

// Storage contract: page geometry and dimensions exist only inside Pages.
internal sealed record ProjectSnapshot
{
    public int SchemaVersion { get; init; } = DocumentFormat.Current;
    public Guid ActivePageId { get; init; }
    public DrawingPage[] Pages { get; init; } = [];
    public CrossPageReference[] CrossPageReferences { get; init; } = [];
    public ProjectDevice[] Devices { get; init; } = [];
    public ProjectNet[] Nets { get; init; } = [];
    public TerminalStrip[] TerminalStrips { get; init; } = [];
    public ProjectCable[] Cables { get; init; } = [];
    public SymbolDefinition[] CustomSymbols { get; init; } = [];
    public ComponentLibrary[] ComponentLibraries { get; init; } = [];

    public static ProjectSnapshot From(DrawingDocument document) => new()
    {
        ActivePageId = document.ActivePageId, Pages = document.Pages, CrossPageReferences = document.CrossPageReferences,
        Devices = document.Devices, Nets = document.Nets, TerminalStrips = document.TerminalStrips,
        Cables = document.Cables, CustomSymbols = document.CustomSymbols, ComponentLibraries = document.ComponentLibraries
    };

    public DrawingDocument ToDocument()
    {
        if (Pages is null || Pages.Any(page => page is null) || Pages.Select(page => page.Id).Distinct().Count() != Pages.Length)
            throw new InvalidDataException("Некоректні аркуші документа.");
        var active = Pages.SingleOrDefault(page => page.Id == ActivePageId)
            ?? throw new InvalidDataException("Не знайдено активний аркуш.");
        return new() { SchemaVersion = SchemaVersion, ActivePageId = ActivePageId, Pages = Pages,
            Elements = active.Elements, WidthMm = active.WidthMm, HeightMm = active.HeightMm,
            CrossPageReferences = CrossPageReferences, Devices = Devices, Nets = Nets, TerminalStrips = TerminalStrips,
            Cables = Cables, CustomSymbols = CustomSymbols, ComponentLibraries = ComponentLibraries };
    }
}
