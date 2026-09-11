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
        var content = JsonSerializer.SerializeToUtf8Bytes(new Envelope(1, library), Options);
        if (content.Length > 32 * 1024 * 1024) throw new InvalidDataException("Файл бібліотеки надто великий для збереження.");
        path = Path.GetFullPath(path);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
            File.WriteAllBytes(temp, content);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static ComponentLibrary Open(string path, IEnumerable<string> symbolKeys)
    {
        using var stream = File.OpenRead(Path.GetFullPath(path));
        if (stream.Length > 32 * 1024 * 1024) throw new InvalidDataException("Файл бібліотеки надто великий.");
        var envelope = FileJson.Read<Envelope>(stream, Options);
        if (envelope.SchemaVersion != 1 || envelope.Library is null)
            throw new InvalidDataException("Непідтримуваний формат бібліотеки.");
        ComponentCatalog.Validate([envelope.Library], symbolKeys.ToHashSet(StringComparer.Ordinal));
        return envelope.Library;
    }
}
