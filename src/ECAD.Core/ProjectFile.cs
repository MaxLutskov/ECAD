using System.IO.Compression;
using System.Text.Json;

namespace ECAD.Core;

public static class ProjectFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static void Save(string path, DrawingDocument document)
    {
        if (document.SchemaVersion is < 1 or > 11) throw new InvalidDataException("Непідтримувана версія документа.");
        document = DrawingPages.Normalize(document with { Elements = DrivingDimensions.ApplyAll(document.Elements) });
        document.Validate();
        path = Path.GetFullPath(path);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                using (var stream = zip.CreateEntry("project.json").Open())
                    JsonSerializer.Serialize(stream, document, Options);
                file.Flush(flushToDisk: true);
            }
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static DrawingDocument Open(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count(e => e.FullName == "project.json") != 1)
            throw new InvalidDataException("Відсутній або неоднозначний project.json.");
        var entry = zip.GetEntry("project.json")!;
        if (entry.Length > 32 * 1024 * 1024) throw new InvalidDataException("Документ надто великий.");
        using var stream = entry.Open();
        var document = JsonSerializer.Deserialize<DrawingDocument>(stream, Options)
            ?? throw new InvalidDataException("Порожній документ.");
        if (document.SchemaVersion is < 1 or > 11) throw new InvalidDataException("Непідтримувана версія документа.");
        if (document.SchemaVersion <= 5) document = SymbolLabels.Ensure(document);
        document = DrawingPages.Normalize(document with { Elements = DrivingDimensions.ApplyAll(document.Elements) });
        document.Validate();
        return document;
    }
}
