using System.IO.Compression;
using System.Text.Json;

namespace ECAD.Core;

public static class ProjectFile
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public static void Save(string path, DrawingDocument document)
    {
        DocumentStructure.Validate(document);
        if (document.SchemaVersion is < 1 or > DocumentFormat.Current) throw new InvalidDataException("Непідтримувана версія документа.");
        document = ElectricalProjectModel.Normalize(document with { Elements = DrivingDimensions.ApplyAll(document.Elements) });
        document.Validate();
        var content = JsonSerializer.SerializeToUtf8Bytes(ProjectSnapshot.From(document), Options);
        if (content.Length > 32 * 1024 * 1024) throw new InvalidDataException("Документ надто великий для збереження.");
        path = Path.GetFullPath(path);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                using (var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
                using (var stream = zip.CreateEntry("project.json").Open())
                    stream.Write(content);
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
        var document = FileJson.Read<DrawingDocument>(stream, Options, project: true);
        DocumentStructure.Validate(document);
        if (document.SchemaVersion is < 1 or > DocumentFormat.Current) throw new InvalidDataException("Непідтримувана версія документа.");
        if (document.SchemaVersion <= 5) document = SymbolLabels.Ensure(document);
        document = ElectricalProjectModel.Normalize(document with { Elements = DrivingDimensions.ApplyAll(document.Elements) });
        document.Validate();
        return document;
    }
}
