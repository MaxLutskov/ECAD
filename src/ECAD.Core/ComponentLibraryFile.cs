using System.Text.Encodings.Web;
using System.Text.Json;

namespace ECAD.Core;

public static class ComponentLibraryFile
{
    private sealed record Envelope(int SchemaVersion, ComponentLibrary Library);
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static void Save(string path, ComponentLibrary library)
    {
        path = Path.GetFullPath(path);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllText(temp, JsonSerializer.Serialize(new Envelope(1, library), Options));
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static ComponentLibrary Open(string path, IEnumerable<string> symbolKeys)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(Path.GetFullPath(path)), Options)
            ?? throw new InvalidDataException("Порожній файл бібліотеки.");
        if (envelope.SchemaVersion != 1 || envelope.Library is null)
            throw new InvalidDataException("Непідтримуваний формат бібліотеки.");
        ComponentCatalog.Validate([envelope.Library], symbolKeys.ToHashSet(StringComparer.Ordinal));
        return envelope.Library;
    }
}
