using System.IO.Compression;
using System.Text.Json;
using ECAD.Core;

namespace ECAD.Checks;

internal static class MigrationFixtureChecks
{
    public static void Run(Action<string, Action> check)
    {
        for (var version = 6; version <= 11; version++)
        {
            var schema = version;
            check($"Historical writer schema {schema} migrates without losing geometry, metadata or references", () =>
            {
                var source = Path.Combine(AppContext.BaseDirectory, "fixtures", $"schema-{schema}.ecad");
                var original = File.ReadAllBytes(source);
                using (var archive = ZipFile.OpenRead(source))
                using (var stream = archive.GetEntry("project.json")!.Open())
                using (var json = JsonDocument.Parse(stream)) Require(json.RootElement.GetProperty("SchemaVersion").GetInt32() == schema);
                var document = ProjectFile.Open(source);
                Require(document.SchemaVersion == DocumentFormat.Current);
                DrawingElement Element(int id) => document.Pages.SelectMany(page => page.Elements).Single(e => e.Id == Id(id));
                Require(Element(1).A == new PointMm(10, 10) && Element(1).B == new PointMm(30, 10));
                Require(Element(2).Points!.SequenceEqual([new PointMm(30,50), new PointMm(42.5,50)]) && Element(2).NetId is not null);
                Require(Element(3).DeviceTag == "K1" && Element(3).DeviceId is not null && Element(3).DeviceFunctionId is not null);
                Require(Element(4).LinkedElementId == Id(3) && Element(4).Text == "K1" && Element(4).TextHeightMm == 3.5);
                Require(Element(5).StartReference?.ElementId == Id(1) && Element(5).EndReference?.ElementId == Id(1));
                if (schema >= 7) Require(Element(1).Name == "Контрольна лінія");
                if (schema >= 8) Require(Element(5).DimensionMode == DimensionMode.Driving && Element(5).DimensionTargetMm == 20);
                if (schema >= 9) Require(Element(6).ArcSweepDegrees == 90 && Element(7).Points!.Length == 3);
                if (schema >= 10)
                {
                    Require(Element(3).ComponentVariantId == Id(14) && Element(3).PhysicalRepresentationId == Id(15));
                    Require(document.Devices.Single().ComponentVariantId == Id(14));
                    Require(ComponentCatalog.FindVariant(document, Id(14))!.Variant.RatingText == "24 V DC");
                }
                if (schema >= 11)
                {
                    Require(document.Pages.Length == 2 && document.Pages[1].Id == Id(22) && document.Pages[1].WidthMm == 210);
                    Require(document.CrossPageReferences.Single().ToElementId == Id(8) && Element(8).Text == "Керування");
                }
                var destination = Path.Combine(Path.GetTempPath(), "ecad-migration-" + Guid.NewGuid() + ".ecad");
                try
                {
                    ProjectFile.Save(destination, document); var reopened = ProjectFile.Open(destination);
                    Require(DocumentContent.Equals(document, reopened));
                    var session = new EditorSession(); session.Load(reopened); var before = session.Document;
                    session.Add(new(Guid.NewGuid(), ElementKind.Line, new(0, 0), new(5, 0)));
                    session.Undo(); Require(ReferenceEquals(before, session.Document));
                }
                finally { if (File.Exists(destination)) File.Delete(destination); }
                Require(original.SequenceEqual(File.ReadAllBytes(source)));
            });
        }
    }
    private static Guid Id(int number) => Guid.Parse($"00000000-0000-0000-0000-{number:000000000000}");
    private static void Require(bool condition) { if (!condition) throw new Exception("Historical migration regression failed"); }
}
