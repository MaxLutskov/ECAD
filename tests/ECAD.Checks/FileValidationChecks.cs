using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using ECAD.Core;

namespace ECAD.Checks;

internal static class FileValidationChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("Malformed project structures reject before migration without changing session or history", () =>
        {
            var session = new EditorSession();
            session.Add(new(Guid.NewGuid(), ElementKind.Line, new(0, 0), new(10, 0))); session.Undo();
            var before = session.Document; var revision = session.ContentRevision;
            var baseline = CanonicalJson();
            WithProject(baseline.ToJsonString(), path => ProjectFile.Open(path));
            Action<JsonObject>[] corruptions =
            [
                root => root["Pages"] = new JsonArray((JsonNode?)null),
                root => root["Devices"] = new JsonArray((JsonNode?)null),
                root => root["Pages"]![0]!["Elements"] = null,
                root => root["Nets"]!.AsArray().Add(root["Nets"]![0]!.DeepClone()),
                root => root["Devices"]![0]!["Functions"] = null,
                root => root["ActivePageId"] = Guid.NewGuid().ToString(),
                root => root.Remove("Cables"),
                root => root["Pages"]![0]!.AsObject().Remove("Id"),
                root => root["Elements"] = new JsonArray(),
                root => root["SchemaVersion"] = "12"
            ];
            foreach (var corrupt in corruptions)
            {
                var json = (JsonObject)baseline.DeepClone(); corrupt(json);
                WithProject(json.ToJsonString(), path => Reject(() => session.Load(ProjectFile.Open(path))));
                Require(ReferenceEquals(before, session.Document) && revision == session.ContentRevision && session.CanRedo && !session.CanUndo);
            }
        });
        check("Invalid JSON and duplicate properties produce InvalidDataException diagnostics", () =>
        {
            foreach (var json in new[] { "{", "{\"SchemaVersion\":12,\"SchemaVersion\":1}", "null", "[]" })
                WithProject(json, path => Reject(() => ProjectFile.Open(path)));
            var path = Path.Combine(Path.GetTempPath(), "ecad-invalid-library-" + Guid.NewGuid() + ".ecadlib");
            try
            {
                File.WriteAllText(path, "{");
                Reject(() => ComponentLibraryFile.Open(path, SymbolLibrary.All.Select(symbol => symbol.Key)));
            }
            finally { File.Delete(path); }
        });
        check("Rejected save leaves the existing project file unchanged", () =>
        {
            var path = Path.Combine(Path.GetTempPath(), "ecad-save-validation-" + Guid.NewGuid() + ".ecad");
            try
            {
                var session = new EditorSession(); ProjectFile.Save(path, session.Document);
                var bytes = File.ReadAllBytes(path);
                Reject(() => ProjectFile.Save(path, session.Document with { Pages = [null!] }));
                Require(bytes.SequenceEqual(File.ReadAllBytes(path))); ProjectFile.Open(path);
                var text = new string('A', 4096);
                var oversized = session.Document with { Elements = Enumerable.Range(0, 9000).Select(i =>
                    new DrawingElement(Guid.NewGuid(), ElementKind.Text, new(1, 1), new(1, 1)) { Text = text }).ToArray() };
                Reject(() => ProjectFile.Save(path, oversized));
                Require(bytes.SequenceEqual(File.ReadAllBytes(path))); ProjectFile.Open(path);
            }
            finally { File.Delete(path); }
        });
    }
    private static void WithProject(string json, Action<string> action)
    {
        var path = Path.Combine(Path.GetTempPath(), "ecad-file-validation-" + Guid.NewGuid() + ".ecad");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            using (var writer = new StreamWriter(zip.CreateEntry("project.json").Open())) writer.Write(json);
            action(path);
        }
        finally { File.Delete(path); }
    }

    private static JsonObject CanonicalJson()
    {
        var path = Path.Combine(Path.GetTempPath(), "ecad-valid-seed-" + Guid.NewGuid() + ".ecad");
        try
        {
            ProjectFile.Save(path, ExampleElectricalProject.Create());
            using var archive = ZipFile.OpenRead(path);
            using var stream = archive.GetEntry("project.json")!.Open();
            return JsonNode.Parse(stream)!.AsObject();
        }
        finally { File.Delete(path); }
    }
    private static void Require(bool value) { if (!value) throw new Exception("File validation regression failed"); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException ex) { Require(!string.IsNullOrWhiteSpace(ex.Message)); return; }
        throw new Exception("Malformed file accepted");
    }
}
