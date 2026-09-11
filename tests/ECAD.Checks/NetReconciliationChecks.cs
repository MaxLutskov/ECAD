using ECAD.Core;

namespace ECAD.Checks;

internal static class NetReconciliationChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("Assignments can be cleared, split and reassigned with undo and valid core status", () =>
        {
            var net = new ProjectNet(Guid.NewGuid(), "24");
            var first = Wire(0, 10) with { NetId = net.Id }; var second = Wire(10, 20) with { NetId = net.Id };
            var terminal = new ProjectTerminal(Guid.NewGuid(), "1", 1, TerminalKind.FeedThrough, net.Id);
            var core = new CableCore(Guid.NewGuid(), "1", CableCoreStatus.Used, net.Id);
            var session = new EditorSession();
            session.Load(new() { Nets = [net], Elements = [first, second],
                TerminalStrips = [new(Guid.NewGuid(), "X1", null, [terminal])],
                Cables = [new(Guid.NewGuid(), "W1", null, 1, null, null, null, null, [core])] });
            var before = session.Document;
            Reject(() => session.SetTerminalNet(terminal.Id, Guid.NewGuid()));
            Require(ReferenceEquals(before, session.Document));
            session.SetTerminalNet(terminal.Id, null); session.SetCableCoreNet(core.Id, null);
            Require(session.Document.Cables[0].Cores[0].Status == CableCoreStatus.Spare);
            session.Apply(session.Document.Elements.Select(e => e.Id == second.Id ? e.Move(new(0, 10)) : e).ToArray());
            var branch = session.Document.Elements.Single(e => e.Id == second.Id).NetId;
            session.SetTerminalNet(terminal.Id, branch); session.SetCableCoreNet(core.Id, branch);
            Require(session.Document.Cables[0].Cores[0].Status == CableCoreStatus.Used);
            var assigned = session.Document;
            session.SetCableCoreNet(core.Id, branch); Require(ReferenceEquals(assigned, session.Document));
            for (var i = 0; i < 5; i++) session.Undo();
            Require(ReferenceEquals(before, session.Document));
            for (var i = 0; i < 5; i++) session.Redo();
            Require(session.Document.TerminalStrips[0].Terminals[0].NetId == branch);
        });
        check("Splitting a net preserves metadata, creates an unlocked number and supports undo", () =>
        {
            var session = new EditorSession();
            var first = Wire(0, 10); var second = Wire(10, 20);
            session.Load(new() { Elements = [first, second] });
            var net = session.Document.Nets.Single();
            session.UpdateNet(net.Id, "L+", "Control", "24V", "DC", "red", 1.5, true);
            var before = session.Document;
            session.Apply(before.Elements.Select(e => e.Id == second.Id ? e.Move(new(0, 10)) : e).ToArray());
            var after = session.Document;
            Require(after.Nets.Length == 2 && after.Nets.Count(n => n.NumberLocked) == 1);
            Require(after.Nets.All(n => n.Potential == "24V" && n.Color == "red" && n.CrossSectionMm2 == 1.5 && n.Description == "Control"));
            Require(after.Elements.Select(e => e.NetId).Distinct().Count() == 2);
            session.Undo(); Require(ReferenceEquals(before, session.Document)); session.Redo();
            var reopened = ElectricalProjectModel.Normalize(after);
            Require(reopened.Nets.SequenceEqual(after.Nets));
            Require(reopened.Elements.Select(e => e.NetId).SequenceEqual(after.Elements.Select(e => e.NetId)));
        });
        check("Merging compatible nets preserves the locked identity and remaps terminal and cable references", () =>
        {
            var a = new ProjectNet(Guid.NewGuid(), "1", Potential: "24V");
            var b = new ProjectNet(Guid.NewGuid(), "L+", Color: "red", NumberLocked: true);
            var first = Wire(0, 10) with { NetId = a.Id }; var second = Wire(20, 30) with { NetId = b.Id };
            var terminal = new ProjectTerminal(Guid.NewGuid(), "1", 1, TerminalKind.FeedThrough, a.Id);
            var core = new CableCore(Guid.NewGuid(), "1", CableCoreStatus.Used, a.Id);
            var session = new EditorSession();
            session.Load(new() { Nets = [a, b], Elements = [first, second],
                TerminalStrips = [new(Guid.NewGuid(), "X1", null, [terminal])],
                Cables = [new(Guid.NewGuid(), "W1", null, 1, null, null, null, null, [core])] });
            var before = session.Document;
            session.Add(Wire(10, 20));
            var result = session.Document;
            Require(result.Nets.Length == 1 && result.Nets[0].Id == b.Id && result.Nets[0].Potential == "24V" && result.Nets[0].Color == "red");
            Require(result.Elements.All(e => e.NetId == b.Id));
            Require(result.TerminalStrips[0].Terminals[0].NetId == b.Id && result.Cables[0].Cores[0].NetId == b.Id);
            session.Undo(); Require(ReferenceEquals(before, session.Document)); session.Redo(); result.Validate();
            var path = Path.Combine(Path.GetTempPath(), "ecad-net-merge-" + Guid.NewGuid() + ".ecad");
            try
            {
                ProjectFile.Save(path, result); var reopened = ProjectFile.Open(path);
                Require(reopened.Nets.Single() == result.Nets.Single());
                Require(reopened.Cables[0].Cores[0].NetId == b.Id && reopened.TerminalStrips[0].Terminals[0].NetId == b.Id);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
        check("Conflicting potentials and locked numbers reject a merge atomically", () =>
        {
            foreach (var locked in new[] { false, true })
            {
                var a = new ProjectNet(Guid.NewGuid(), "A", Potential: "24V", NumberLocked: locked);
                var b = new ProjectNet(Guid.NewGuid(), "B", Potential: locked ? "24V" : "0V", NumberLocked: locked);
                var session = new EditorSession();
                session.Load(new() { Nets = [a, b], Elements = [Wire(0, 10) with { NetId = a.Id }, Wire(20, 30) with { NetId = b.Id }] });
                var before = session.Document;
                Reject(() => session.Add(Wire(10, 20)));
                Require(ReferenceEquals(before, session.Document) && !session.CanUndo);
            }
        });
        check("Splitting a terminal-assigned net requires explicit reassignment", () =>
        {
            var net = new ProjectNet(Guid.NewGuid(), "1");
            var first = Wire(0, 10) with { NetId = net.Id }; var second = Wire(10, 20) with { NetId = net.Id };
            var session = new EditorSession();
            session.Load(new() { Nets = [net], Elements = [first, second], TerminalStrips =
                [new(Guid.NewGuid(), "X1", null, [new(Guid.NewGuid(), "1", 1, TerminalKind.FeedThrough, net.Id)])] });
            var before = session.Document;
            Reject(() => session.Apply(before.Elements.Select(e => e.Id == second.Id ? e.Move(new(0, 10)) : e).ToArray()));
            Require(ReferenceEquals(before, session.Document));
        });
        check("Duplicate functions stay visible to ERC; paste explicitly creates a separate function", () =>
        {
            var symbol = new DrawingElement(Guid.NewGuid(), ElementKind.Symbol, new(10, 10), new(10, 10))
                { SymbolKey = "IEC_COIL", DeviceTag = "K1" };
            var session = new EditorSession(); session.Load(new() { Elements = [symbol] });
            var original = session.Document.Elements.First(e => e.Kind == ElementKind.Symbol);
            session.Add(original.Move(new(20, 0)) with { Id = Guid.NewGuid() });
            Require(session.Document.Devices.Single().Functions.Length == 1);
            Require(ElectricalRuleChecker.Check(session.Document).Count(i => i.Kind == ElectricalIssueKind.FunctionUsedTwice) == 2);
            var reloaded = new EditorSession(); reloaded.Load(session.Document);
            Require(reloaded.Document.Devices.Single().Functions.Length == 1);
            session.Undo(); session.Select(original, false); session.Copy(); session.Paste();
            Require(session.Document.Devices.Single().Functions.Length == 2);
            Require(!ElectricalRuleChecker.Check(session.Document).Any(i => i.Kind == ElectricalIssueKind.FunctionUsedTwice));
        });
    }
    private static DrawingElement Wire(double from, double to) => new(Guid.NewGuid(), ElementKind.Wire, new(from, 0), new(to, 0))
        { Points = [new(from, 0), new(to, 0)] };
    private static void Require(bool condition) { if (!condition) throw new Exception("Net reconciliation regression failed"); }
    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception("Conflicting operation accepted");
    }
}
