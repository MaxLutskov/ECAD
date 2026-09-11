using System.IO.Compression;
using System.Text.Json;
using ECAD.Core;

namespace ECAD.Checks;

internal static class ReleaseReadinessChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("Canonical pages own active geometry and schema 13 stores no root mirror", () =>
        {
            var session = new EditorSession(); session.Load(ExampleElectricalProject.Create());
            var before = session.Document;
            var line = new DrawingElement(Guid.NewGuid(), ElementKind.Line, new(1, 1), new(2, 2));
            var edited = before with { Elements = [.. before.Elements, line] };
            Require(ReferenceEquals(edited.Elements, edited.Pages.Single(p => p.Id == edited.ActivePageId).Elements));
            Require(!before.Elements.Any(e => e.Id == line.Id));
            session.ApplyDocument(edited);
            var path = Path.Combine(Path.GetTempPath(), "ecad-canonical-" + Guid.NewGuid() + ".ecad");
            try
            {
                ProjectFile.Save(path, session.Document);
                using (var zip = ZipFile.OpenRead(path))
                using (var stream = zip.GetEntry("project.json")!.Open())
                using (var json = JsonDocument.Parse(stream))
                {
                    Require(json.RootElement.GetProperty("SchemaVersion").GetInt32() == 13);
                    foreach (var name in new[] { "Elements", "WidthMm", "HeightMm" }) Require(!json.RootElement.TryGetProperty(name, out _));
                }
                Require(DocumentContent.Equals(session.Document, ProjectFile.Open(path)));
            }
            finally { File.Delete(path); }
        });
        check("Undo snapshot survives changes through an old mutable document reference", () =>
        {
            var session = new EditorSession(); session.Load(ExampleElectricalProject.Create());
            var before = session.Document;
            var expectedTag = before.Devices[0].Tag;
            var firstPoint = before.Elements.First(e => e.Kind == ElementKind.Wire).Points![0];
            session.Add(new(Guid.NewGuid(), ElementKind.Line, new(1, 1), new(2, 2)));
            before.Devices[0] = before.Devices[0] with { Tag = "EXTERNAL_CHANGE" };
            before.Elements.First(e => e.Kind == ElementKind.Wire).Points![0] = new(double.NaN, 999);
            session.Undo();
            Require(session.Document.Devices[0].Tag == expectedTag);
            Require(session.Document.Elements.First(e => e.Kind == ElementKind.Wire).Points![0] == firstPoint);
            session.Document.Validate();
            session.Redo();
            Require(session.Document.Devices[0].Tag == expectedTag);
            Require(session.Document.Elements.First(e => e.Kind == ElementKind.Wire).Points![0] == firstPoint);
            session.Document.Validate();
        });
        check("Explicit cleanup keeps referenced nets and unplaced device definitions", () =>
        {
            var session = new EditorSession(); session.Load(ExampleElectricalProject.Create());
            var deviceCount = session.Document.Devices.Length;
            var spare = new ProjectNet(Guid.NewGuid(), "unused-audit-net");
            session.ApplyDocument(session.Document with { Nets = [.. session.Document.Nets, spare] });
            var before = session.Document;
            session.RemoveUnusedNets();
            Require(!session.Document.Nets.Any(net => net.Id == spare.Id) && session.Document.Devices.Length == deviceCount);
            session.Document.Validate(); session.Undo(); Require(ReferenceEquals(before, session.Document));
        });
        check("Deleting a referenced element removes cross links and clears cable endpoints atomically", () =>
        {
            var session = new EditorSession(); session.Load(ExampleElectricalProject.Create());
            var reference = session.Document.CrossPageReferences[0];
            session.SwitchPage(reference.FromPageId);
            var target = session.Document.Elements.Single(e => e.Id == reference.FromElementId);
            var cable = session.Document.Cables[0];
            session.UpdateCable(cable with { Cores = cable.Cores.Select((core, i) => i == 0 ? core with { FromElementId = target.Id } : core).ToArray() });
            var before = session.Document;
            session.Select(target, false); session.Delete();
            Require(!session.Document.CrossPageReferences.Any(r => r.FromElementId == target.Id || r.ToElementId == target.Id));
            Require(session.Document.Cables[0].Cores[0].FromElementId is null);
            session.Document.Validate(); session.Undo(); Require(ReferenceEquals(before, session.Document));
        });
    }
    private static void Require(bool value) { if (!value) throw new Exception("Release readiness check failed"); }
}
