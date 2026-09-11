using System.Text.Json;

namespace ECAD.Core;

internal static class FileJson
{
    public static T Read<T>(Stream stream, JsonSerializerOptions options, bool project = false)
    {
        try
        {
            using var json = JsonDocument.Parse(stream);
            Inspect(json.RootElement, "$");
            if (project)
            {
                var root = json.RootElement;
                foreach (var name in new[] { "SchemaVersion" })
                    if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(name, out _))
                        throw new InvalidDataException($"У файлі відсутнє обов’язкове поле {name}.");
                var version = root.GetProperty("SchemaVersion");
                if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var schema) || schema is < 1 or > DocumentFormat.Current)
                    throw new InvalidDataException("Непідтримувана версія документа.");
                var required = new List<string>();
                if (schema < 13) required.AddRange(["WidthMm", "HeightMm", "Elements"]);
                else
                {
                    required.Add("CustomSymbols");
                    foreach (var name in new[] { "WidthMm", "HeightMm", "Elements" })
                        if (root.TryGetProperty(name, out _)) throw new InvalidDataException($"Schema 13 зберігає {name} лише в аркушах.");
                }
                if (schema >= 10) required.Add("ComponentLibraries");
                if (schema >= 11) required.AddRange(["Pages", "ActivePageId", "CrossPageReferences"]);
                if (schema >= 12) required.AddRange(["Devices", "Nets", "TerminalStrips", "Cables"]);
                foreach (var name in required)
                    if (!root.TryGetProperty(name, out _)) throw new InvalidDataException($"У файлі відсутнє обов’язкове поле {name}.");
                if (schema >= 11 && root.TryGetProperty("Pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
                {
                    if (pages.GetArrayLength() == 0) throw new InvalidDataException("Файл не містить аркушів.");
                    foreach (var page in pages.EnumerateArray())
                    {
                        if (page.ValueKind != JsonValueKind.Object || !page.TryGetProperty("Id", out _))
                            throw new InvalidDataException("У сторінки відсутній ID.");
                        foreach (var name in new[] { "Elements", "WidthMm", "HeightMm" })
                            if (!page.TryGetProperty(name, out _)) throw new InvalidDataException($"В аркуші відсутнє поле {name}.");
                        if (schema < 13 && page.GetProperty("Id").ToString() == root.GetProperty("ActivePageId").ToString())
                            foreach (var name in new[] { "Elements", "WidthMm", "HeightMm" })
                                if (!System.Text.Json.Nodes.JsonNode.DeepEquals(
                                    System.Text.Json.Nodes.JsonNode.Parse(page.GetProperty(name).GetRawText()),
                                    System.Text.Json.Nodes.JsonNode.Parse(root.GetProperty(name).GetRawText())))
                                    throw new InvalidDataException($"Дані активного аркуша не збігаються з {name} документа.");
                    }
                }
                if (schema == 13 && typeof(T) == typeof(DrawingDocument))
                {
                    var snapshot = root.Deserialize<ProjectSnapshot>(options) ?? throw new InvalidDataException("Порожній документ.");
                    return (T)(object)snapshot.ToDocument();
                }
            }
            return json.RootElement.Deserialize<T>(options) ?? throw new InvalidDataException("Порожній файл даних.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Пошкоджений JSON: перевір поле {ex.Path ?? "$"}, рядок {(ex.LineNumber ?? 0) + 1}.", ex);
        }
    }

    private static void Inspect(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException($"Повторне поле {path}.{property.Name}.");
                Inspect(property.Value, path + "." + property.Name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Null) throw new InvalidDataException($"Порожній запис {path}[{index}].");
                Inspect(item, $"{path}[{index++}]");
            }
        }
    }
}
