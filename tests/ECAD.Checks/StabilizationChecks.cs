using ECAD.Core;

namespace ECAD.Checks;

internal static class StabilizationChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("Page navigation and no-op document edits preserve revision and redo", () =>
        {
            var session = new EditorSession(); var firstPage = session.Document.ActivePageId;
            session.AddPage("Second");
            var secondPage = session.Document.ActivePageId;
            var savedRevision = session.ContentRevision;
            session.Add(new(Guid.NewGuid(), ElementKind.Line, new(0, 0), new(10, 0)));
            Require(session.ContentRevision != savedRevision);
            session.SwitchPage(firstPage); session.Undo();
            Require(session.ContentRevision == savedRevision && session.CanRedo);
            session.SwitchPage(secondPage); session.SwitchPage(firstPage);
            var before = session.Document;
            session.ApplyDocument(before with { Pages = before.Pages.Select(page => page with { Elements = page.Elements.ToArray() }).ToArray() });
            Require(ReferenceEquals(before, session.Document) && session.ContentRevision == savedRevision && session.CanRedo);
            session.Redo(); Require(session.ContentRevision != savedRevision);
            session.Undo(); Require(session.ContentRevision == savedRevision);
            session.Add(new(Guid.NewGuid(), ElementKind.Line, new(0, 5), new(10, 5)));
            Require(session.ContentRevision != savedRevision && !session.CanRedo);
        });
        check("Library changes protect inactive pages and remap physical representations atomically", () =>
        {
            var session = new EditorSession(); session.Load(ExampleElectricalProject.Create());
            session.AddPage("Empty active page");
            var library = session.Document.ComponentLibraries[0];
            var before = session.Document;
            Reject(() => session.DeleteComponentLibrary(library.Id));
            Reject(() => session.UpdateComponentLibrary(library with { DeviceTypes = [] }));
            Reject(() => session.UpdateComponentLibrary(library with { DeviceTypes = library.DeviceTypes.Select(type => type with
            { Families = type.Families.Select(family => family with
            { Variants = family.Variants.Select(variant => variant with
            { SymbolKey = variant.SymbolKey == "IEC_COIL" ? "IEC_NO_CONTACT" : "IEC_COIL" }).ToArray() }).ToArray() }).ToArray() }));
            Require(ReferenceEquals(before, session.Document));
            var changed = library with { DeviceTypes = library.DeviceTypes.Select(type => type with
            { Families = type.Families.Select(family => family with
            { Variants = family.Variants.Select(variant => variant with
            { PhysicalRepresentations = [variant.PhysicalRepresentations[0] with { Id = Guid.NewGuid() }] }).ToArray() }).ToArray() }).ToArray() };
            session.UpdateComponentLibrary(changed);
            var variants = ComponentCatalog.Variants(session.Document).ToDictionary(v => v.Variant.Id, v => v.Variant);
            foreach (var element in session.Document.Pages.SelectMany(page => page.Elements).Where(e => e.ComponentVariantId is not null))
                Require(variants[element.ComponentVariantId!.Value].PhysicalRepresentations.Any(p => p.Id == element.PhysicalRepresentationId));
            foreach (var device in session.Document.Devices.Where(d => d.ComponentVariantId is not null))
                Require(variants[device.ComponentVariantId!.Value].PhysicalRepresentations.Any(p => p.Id == device.PhysicalRepresentationId));
            session.Document.Validate(); session.Undo(); Require(ReferenceEquals(before, session.Document));
            session.Redo(); session.Document.Validate();
            var savedPath = Path.Combine(Path.GetTempPath(), "ecad-library-regression-" + Guid.NewGuid() + ".ecad");
            try
            {
                ProjectFile.Save(savedPath, session.Document);
                var reopened = ProjectFile.Open(savedPath);
                foreach (var device in reopened.Devices.Where(d => d.ComponentVariantId is not null))
                    Require(variants[device.ComponentVariantId!.Value].PhysicalRepresentations.Any(p => p.Id == device.PhysicalRepresentationId));
            }
            finally { if (File.Exists(savedPath)) File.Delete(savedPath); }
        });
        check("Unplaced project devices protect their library and physical representation", () =>
        {
            var library = ExampleComponentLibraries.Create()[0];
            var variant = library.DeviceTypes[0].Families[0].Variants[0];
            var device = new ProjectDevice(Guid.NewGuid(), "QF1", null, variant.Id, variant.PhysicalRepresentations[0].Id, []);
            var session = new EditorSession(); session.Load(new() { ComponentLibraries = [library], Devices = [device] });
            var before = session.Document;
            Reject(() => session.DeleteComponentLibrary(library.Id));
            Reject(() => session.UpdateComponentLibrary(library with { DeviceTypes = [] }));
            Reject(() => session.ApplyDocument(before with { Devices = [device with { PhysicalRepresentationId = Guid.NewGuid() }] }));
            Require(ReferenceEquals(before, session.Document) && !session.CanUndo);
        });
        check("Foreign device functions are rejected without inventing replacements", () =>
        {
            var session = new EditorSession(); session.Load(ExampleElectricalProject.Create());
            var before = session.Document;
            var symbol = before.Elements.First(e => e.Kind == ElementKind.Symbol && e.DeviceId is not null);
            var foreign = before.Devices.First(d => d.Id != symbol.DeviceId && d.Functions.Length > 0).Functions[0];
            var elements = before.Elements.Select(e => e.Id == symbol.Id ? e with { DeviceFunctionId = foreign.Id } : e).ToArray();
            Reject(() => session.Apply(elements));
            var invalid = before with { Elements = elements, Pages = before.Pages.Select(p => p.Id == before.ActivePageId ? p with { Elements = elements } : p).ToArray() };
            Reject(() => invalid.Validate());
            Require(ReferenceEquals(before, session.Document) && !session.CanUndo);
        });
        check("Net numbering respects step and reserves locked numbers case-insensitively", () =>
        {
            var session = new EditorSession();
            var locked = new ProjectNet(Guid.NewGuid(), "n15", NumberLocked: true);
            session.Load(new() { Nets = [new(Guid.NewGuid(), "old1"), locked,
                new(Guid.NewGuid(), "old2"), new(Guid.NewGuid(), "old3")] });
            var before = session.Document;
            session.RenumberNets("N", 10, 5);
            Require(session.Document.Nets.Select(n => n.Number).SequenceEqual(["N10", "n15", "N20", "N25"]));
            Require(session.Document.Nets[1] == locked);
            session.Undo(); Require(ReferenceEquals(session.Document, before));
            session.Redo(); Require(session.Document.Nets[3].Number == "N25");
        });
        check("Unchanged numbering preserves redo and document identity", () =>
        {
            var session = new EditorSession();
            session.Load(new() { Nets = [new(Guid.NewGuid(), "1")] });
            session.RenumberNets("X"); session.Undo();
            var before = session.Document;
            session.RenumberNets();
            Require(ReferenceEquals(before, session.Document) && session.CanRedo && !session.CanUndo);
        });
        check("Numbering overflow and invalid parameters are atomic", () =>
        {
            var session = new EditorSession();
            session.Load(new() { Nets = [new(Guid.NewGuid(), "a"), new(Guid.NewGuid(), "b")] });
            var before = session.Document;
            foreach (var action in new Action[] { () => session.RenumberNets("", int.MaxValue),
                () => session.RenumberNets("", 0, 0), () => session.RenumberNets(new string('N', 65)) })
            {
                try { action(); throw new Exception("Invalid numbering accepted"); }
                catch (InvalidDataException) { }
                Require(ReferenceEquals(before, session.Document) && !session.CanUndo);
            }
        });
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new Exception("Stabilization regression failed");
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new Exception("Invalid project mutation accepted");
    }
}
