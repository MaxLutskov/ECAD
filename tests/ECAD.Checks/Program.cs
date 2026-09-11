using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ECAD.Core;
using ECAD.Desktop;

if (args.Contains("--profile")) { ECAD.Checks.PerformanceProbe.Run(); return; }
var passed = 0;
ECAD.Checks.StabilizationChecks.Run(Check);
ECAD.Checks.NetReconciliationChecks.Run(Check);
ECAD.Checks.FileValidationChecks.Run(Check);
ECAD.Checks.MigrationFixtureChecks.Run(Check);
ECAD.Checks.ReleaseReadinessChecks.Run(Check);
void Check(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
void Assert(bool value) { if (!value) throw new Exception("Assertion failed"); }
void Near(double actual, double expected) => Assert(Math.Abs(actual - expected) < 1e-9);
void Reject(Action action)
{
    try { action(); }
    catch (Exception ex) when (ex is InvalidDataException or ArgumentOutOfRangeException) { return; }
    throw new Exception("Invalid input was accepted");
}
DrawingElement Line(double y = 0) => new(Guid.NewGuid(), ElementKind.Line, new(0, y), new(10, y));
DrawingElement Wire(params PointMm[] points) => new(Guid.NewGuid(), ElementKind.Wire, points[0], points[^1]) { Points = points };

Check("Exact length overrides grid, including diagonal directions", () =>
{
    var a = Geometry.Snap(new(3.2, 7.1), 2.5);
    var b = Geometry.AtLength(a, new(20, 20), 12.3);
    Near((b - a).Length, 12.3);
    Near(Geometry.AtLength(a, a, 12.3).X - a.X, 12.3);
});
Check("Snap is symmetric around zero", () =>
{
    Assert(Geometry.Snap(new(1.25, -1.25), 2.5) == new PointMm(2.5, -2.5));
});
Check("Reject invalid numeric constraints", () =>
{
    Reject(() => Geometry.Snap(default, 0));
    Reject(() => Geometry.AtLength(default, new(1, 0), double.NaN));
    Reject(() => Geometry.AtLength(default, new(1, 0), -1));
});
Check("Hit testing segments does not include infinite line extensions", () =>
{
    Assert(Line().Hit(new(5, .2), .5)); Assert(!Line().Hit(new(20, 0), .5));
    Assert(new DrawingElement(Guid.NewGuid(), ElementKind.Circle, new(0, 0), new(5, 0)).Hit(new(0, 5), .1));
});
Check("Three-point arcs preserve the through point and support hit testing", () =>
{
    var arc = ArcGeometry.FromThreePoints(Guid.NewGuid(), new(0, 0), new(5, -5), new(10, 0));
    Assert(arc.Kind == ElementKind.Arc); Near(arc.A.X, 5); Near(arc.A.Y, 0); Near(arc.LengthMm, 5);
    Near(arc.ArcSweepDegrees, -180);
    var middle = ArcGeometry.PointAt(arc, .5); Near(middle.X, 5); Near(middle.Y, -5);
    Assert(arc.Hit(new(5, -5.1), .2) && !arc.Hit(new(5, 5), .2));
    Assert(new SelectionBox(-1, -6, 11, 1).Matches(arc, false));
    Reject(() => ArcGeometry.FromThreePoints(Guid.NewGuid(), new(0, 0), new(5, 0), new(10, 0)));
});
Check("Polylines validate, move, rotate and expose segment references", () =>
{
    var polyline = new DrawingElement(Guid.NewGuid(), ElementKind.Polyline, new(0, 0), new(10, 10))
        { Points = [new(0, 0), new(10, 0), new(10, 10)] };
    new DrawingDocument { Elements = [polyline] }.Validate();
    Assert(AssociativeDimensions.Edges(polyline).Length == 2 && polyline.Hit(new(10, 5), .1));
    var s = new EditorSession(); s.Add(polyline); s.Select(polyline, false); s.Move(new(5, 5));
    Assert(s.Document.Elements[0].Points!.SequenceEqual([new PointMm(5, 5), new(15, 5), new(15, 15)]));
    s.RotateSelection90(); Assert(s.Document.Elements[0].Points!.Length == 3);
    Reject(() => new DrawingDocument { Elements = [polyline with { Points = [new(0, 0), new(0, 0)] }] }.Validate());
});
Check("Splitting lines and polyline segments preserves associative references", () =>
{
    var line = Line();
    var dimension = new DrawingElement(Guid.NewGuid(), ElementKind.Dimension, line.A, line.B)
    {
        StartReference = new(line.Id, ReferenceKind.Vertex, 0), EndReference = new(line.Id, ReferenceKind.Vertex, 1)
    };
    var splitLine = SegmentEditing.Split([line, dimension], new(line.Id, ReferenceKind.Edge, 0, .4));
    Assert(splitLine.Count(e => e.Kind == ElementKind.Line) == 2);
    var resolved = AssociativeDimensions.ResolveAll(splitLine);
    Near(resolved.Single(e => e.Kind == ElementKind.Dimension).LengthMm, 10);

    var polyline = new DrawingElement(Guid.NewGuid(), ElementKind.Polyline, new(0, 0), new(10, 10))
        { Points = [new(0, 0), new(10, 0), new(10, 10)] };
    var polyDimension = dimension with { Id = Guid.NewGuid(), StartReference = new(polyline.Id, ReferenceKind.Vertex, 0), EndReference = new(polyline.Id, ReferenceKind.Vertex, 2) };
    var splitPolyline = SegmentEditing.Split([polyline, polyDimension], new(polyline.Id, ReferenceKind.Edge, 0, .5));
    Assert(splitPolyline[0].Points!.Length == 4 && splitPolyline[1].EndReference?.Index == 3);
    Reject(() => SegmentEditing.Split([line], new(line.Id, ReferenceKind.Edge, 0, 0)));
});
Check("Trim and extend change the selected line end at a straight boundary", () =>
{
    var target = Line();
    var boundary = new DrawingElement(Guid.NewGuid(), ElementKind.Line, new(4, -5), new(4, 5));
    var trimmed = SegmentEditing.Trim([target, boundary], new(target.Id, ReferenceKind.Edge, 0, .2),
        new(boundary.Id, ReferenceKind.Edge, 0, .5));
    Assert(trimmed[0].A == new PointMm(4, 0) && trimmed[0].B == new PointMm(10, 0));

    var shortLine = target with { B = new(3, 0) };
    var extended = SegmentEditing.Extend([shortLine, boundary], new(shortLine.Id, ReferenceKind.Edge, 0, .9),
        new(boundary.Id, ReferenceKind.Edge, 0, .5));
    Assert(extended[0].A == new PointMm(0, 0) && extended[0].B == new PointMm(4, 0));
    Reject(() => SegmentEditing.Extend([shortLine, boundary], new(shortLine.Id, ReferenceKind.Edge, 0, .1),
        new(boundary.Id, ReferenceKind.Edge, 0, .5)));
});
Check("Connection nodes snap to exact line intersections", () =>
{
    var a = new DrawingElement(Guid.NewGuid(), ElementKind.Line, new(0, 0), new(10, 10));
    var b = new DrawingElement(Guid.NewGuid(), ElementKind.Line, new(0, 10), new(10, 0));
    var picked = ConnectionSnap.Pick([a, b], new(5.2, 5.1), 1, new(5, 6));
    Assert(picked == new PointMm(5, 5));
    Assert(Geometry.SegmentIntersection(a.A, a.B, b.A, b.B, out var crossing) && crossing == picked);
});
Check("Junction and text point elements validate, hit and rotate", () =>
{
    var node = new DrawingElement(Guid.NewGuid(), ElementKind.Junction, new(3, 4), new(3, 4));
    var label = new DrawingElement(Guid.NewGuid(), ElementKind.Text, new(10, 10), new(10, 10))
        { Text = "KM1", TextHeightMm = 3.5 };
    new DrawingDocument { Elements = [node, label] }.Validate();
    Assert(node.Hit(new(3.2, 4), .3) && label.Hit(new(11, 9), .1));
    var s = new EditorSession(); s.Apply([node, label]); s.Select(label, false); s.RotateSelection90();
    Near(s.Document.Elements[1].RotationDegrees, 90);
    Reject(() => new DrawingDocument { Elements = [label with { Text = "" }] }.Validate());
});
Check("Arbitrary-angle wires validate, move and rotate with all vertices", () =>
{
    var wire = Wire(new(0, 0), new(10, 0), new(10, 20));
    new DrawingDocument { Elements = [wire] }.Validate();
    new DrawingDocument { Elements = [Wire(new(0, 0), new(10, 10))] }.Validate();
    var moved = wire.Move(new(5, 7)); Assert(moved.Points![1] == new PointMm(15, 7));
    var s = new EditorSession(); s.Add(wire); s.Select(wire, false); s.RotateSelection90();
    Assert(s.Document.Elements[0].Points!.SequenceEqual([new PointMm(15, 5), new(15, 15), new(-5, 15)]));
    Reject(() => new DrawingDocument { Elements = [Wire(new(0, 0), new(0, 0))] }.Validate());
});
Check("Wire crossings require a junction and shared endpoints connect directly", () =>
{
    var horizontal = Wire(new(0, 5), new(10, 5));
    var vertical = Wire(new(5, 0), new(5, 10));
    Assert(ElectricalConnectivity.Build([horizontal, vertical]).Length == 2);
    var node = new DrawingElement(Guid.NewGuid(), ElementKind.Junction, new(5, 5), new(5, 5));
    Assert(ElectricalConnectivity.Build([horizontal, vertical, node]).Single().WireIds.Length == 2);
    var continued = Wire(new(10, 5), new(20, 5));
    Assert(ElectricalConnectivity.Build([horizontal, continued]).Length == 1);
});
Check("IEC symbol pins join a net at wire endpoints", () =>
{
    var wire = Wire(new(0, 5), new(10, 5));
    var contact = new DrawingElement(Guid.NewGuid(), ElementKind.Symbol, new(17.5, 5), new(17.5, 5))
        { SymbolKey = "IEC_NO_CONTACT", DeviceTag = "K1" };
    var net = ElectricalConnectivity.Build([wire, contact]).Single();
    Assert(net.Pins.SequenceEqual([new PinAddress(contact.Id, 0)]));
    new DrawingDocument { Elements = [wire, contact] }.Validate();
});
Check("Electrical rule check reports open pins and dangling wire ends", () =>
{
    var contact = new DrawingElement(Guid.NewGuid(), ElementKind.Symbol, new(20, 10), new(20, 10))
        { SymbolKey = "IEC_NO_CONTACT", DeviceTag = "K1" };
    var wire = Wire(new(0, 10), new(12.5, 10));
    var issues = ElectricalRuleChecker.Check([contact, wire]);
    Assert(issues.Count(i => i.Kind == ElectricalIssueKind.UnconnectedPin) == 1);
    Assert(issues.Count(i => i.Kind == ElectricalIssueKind.DanglingWireEnd) == 1);
    var closed = Wire(new(12.5, 10), new(27.5, 10));
    Assert(ElectricalRuleChecker.Check([contact, closed]).Length == 0);
});
Check("Exact duplicate symbols are reported without rejecting shared device tags", () =>
{
    var a = new DrawingElement(Guid.NewGuid(), ElementKind.Symbol, new(10, 10), new(10, 10))
        { SymbolKey = "IEC_COIL", DeviceTag = "K1" };
    var legitimateContact = a with { Id = Guid.NewGuid(), A = new(30, 10), B = new(30, 10), SymbolKey = "IEC_NO_CONTACT" };
    Assert(!ElectricalRuleChecker.Check([a, legitimateContact]).Any(i => i.Kind == ElectricalIssueKind.DuplicateDeviceTag));
    var duplicate = a with { Id = Guid.NewGuid() };
    Assert(ElectricalRuleChecker.Check([a, duplicate]).Count(i => i.Kind == ElectricalIssueKind.DuplicateDeviceTag) == 2);
});
Check("Selected geometry creates an undoable custom symbol definition", () =>
{
    var s = new EditorSession();
    var body = new DrawingElement(Guid.NewGuid(), ElementKind.Rectangle, new(10, 10), new(30, 20));
    var left = new DrawingElement(Guid.NewGuid(), ElementKind.Junction, new(10, 15), new(10, 15));
    var right = new DrawingElement(Guid.NewGuid(), ElementKind.Junction, new(30, 15), new(30, 15));
    s.Apply([body, left, right]); s.Selection.UnionWith([body.Id, left.Id, right.Id]);
    var definition = s.CreateCustomSymbol("Мій блок", "Y");
    Assert(s.Document.CustomSymbols.Single() == definition && definition.Pins.Length == 2 && definition.Strokes!.Length == 4);
    var instance = new DrawingElement(Guid.NewGuid(), ElementKind.Symbol, new(50, 50), new(50, 50))
    { SymbolKey = definition.Key, DeviceTag = "Y1", PinOffsets = definition.Pins, SymbolStrokes = definition.Strokes };
    s.Add(instance); Assert(SymbolLibrary.PinPositions(instance).Length == 2);
    s.Undo(); s.Undo(); Assert(s.Document.CustomSymbols.Length == 0); s.Redo();
    Assert(s.Document.CustomSymbols.Length == 1);
});
Check("Symbol labels copy with owners and copy independently as ordinary text", () =>
{
    var symbol = new DrawingElement(Guid.NewGuid(), ElementKind.Symbol, new(20, 20), new(20, 20))
        { SymbolKey = "IEC_NO_CONTACT", DeviceTag = "K1" };
    var label = SymbolLabels.Create(symbol); var s = new EditorSession(); s.Apply([symbol, label]);
    s.Select(symbol, false); s.Copy(); s.Paste();
    var copiedSymbol = s.Document.Elements.Last(e => e.Kind == ElementKind.Symbol);
    var copiedLabel = s.Document.Elements.Single(e => e.LinkedElementId == copiedSymbol.Id);
    Assert(copiedLabel.Text == "K1");
    s.Undo(); s.Select(label, false); s.Copy(); s.Paste();
    var ordinaryCopy = s.Document.Elements.Last();
    Assert(ordinaryCopy.Kind == ElementKind.Text && ordinaryCopy.LinkedElementId is null);
});
Check("Dimension is selectable at its displayed offset", () =>
{
    Assert(new DrawingElement(Guid.NewGuid(), ElementKind.Dimension, new(0, 0), new(10, 0)).Hit(new(5, 7), .1));
});
Check("Group move is one undo operation and preserves exact lengths", () =>
{
    var s = new EditorSession(); var a = Line(); var b = Line(10);
    s.Add(a); s.Add(b); s.Selection.UnionWith([a.Id, b.Id]); s.Group();
    var grouped = s.Document;
    s.Move(new(2.5, 5)); Near(s.Document.Elements[0].LengthMm, 10);
    s.Undo(); Assert(s.Document.Elements.SequenceEqual(grouped.Elements));
    s.Redo(); Near(s.Document.Elements[1].A.X, 2.5);
    s.Select(s.Document.Elements[0], false); Assert(s.Selection.Count == 2);
    s.Ungroup(); Assert(s.Document.Elements.All(e => e.GroupId is null));
    s.Undo(); Assert(s.Document.Elements.All(e => e.GroupId is not null));
});
Check("Editing after undo discards redo, no-op move preserves history", () =>
{
    var s = new EditorSession(); s.Add(Line()); s.Add(Line(5)); s.Undo();
    s.Move(default); Assert(s.CanRedo);
    s.Add(Line(7)); Assert(!s.CanRedo);
});
Check("Invalid document does not replace editor state", () =>
{
    var s = new EditorSession(); s.Add(Line()); var before = s.Document;
    Reject(() => s.Load(new() { SchemaVersion = 99 })); Assert(ReferenceEquals(before, s.Document));
    Reject(() => s.Apply([Line() with { A = new(double.NaN, 0) }])); Assert(ReferenceEquals(before, s.Document));
});
Check("Reject duplicate IDs and degenerate rectangles", () =>
{
    var line = Line(); Reject(() => new DrawingDocument { Elements = [line, line] }.Validate());
    Reject(() => new DrawingDocument { Elements = [line with { Kind = ElementKind.Rectangle }] }.Validate());
});

var output = Path.GetFullPath(args.FirstOrDefault() ?? "tmp/checks");
Directory.CreateDirectory(output);
Environment.SetEnvironmentVariable("ECAD_LOG_PATH", Path.Combine(output, $"ecad-{Guid.NewGuid():N}.log"));
Environment.SetEnvironmentVariable("ECAD_WORKSPACE_ROOT", Path.Combine(output, "workspace"));
Check("Workspace creates dedicated project and library folders", () =>
{
    Assert(WorkspaceFolders.RootDirectory == Path.Combine(output, "workspace"));
    Assert(Directory.Exists(WorkspaceFolders.ProjectsDirectory) && Directory.Exists(WorkspaceFolders.LibrariesDirectory));
});
Check("Application errors are persisted with context and exception details", () =>
{
    AppLog.Write("Створення тестового символу", new InvalidOperationException("Тестова помилка"));
    var content = File.ReadAllText(AppLog.LogPath);
    Assert(content.Contains("Створення тестового символу", StringComparison.Ordinal));
    Assert(content.Contains("InvalidOperationException", StringComparison.Ordinal));
    Assert(content.Contains("Тестова помилка", StringComparison.Ordinal));
    Assert(content.Contains($"ECAD {AppInfo.BuildVersion}", StringComparison.Ordinal));
});
Check("ZIP round-trip preserves geometry and groups, overwrite remains readable", () =>
{
    var group = Guid.NewGuid();
    var doc = new DrawingDocument { Elements = [Line() with { GroupId = group, Name = "Живлення" }, Line(5) with { GroupId = group }] };
    var path = Path.Combine(output, "roundtrip.ecad");
    ProjectFile.Save(path, doc); var read = ProjectFile.Open(path);
    Assert(read.Elements.SequenceEqual(doc.Elements)); Near(read.WidthMm, 420);
    ProjectFile.Save(path, doc with { Elements = [Line(10)] });
    Assert(ProjectFile.Open(path).Elements.Length == 1);
    Assert(!Directory.GetFiles(output, "*.tmp").Any());
});
Check("ZIP round-trip preserves custom symbol definitions and instances", () =>
{
    var definition = new SymbolDefinition("CUSTOM_TEST", "Тест", "Y", [new(-5, 0), new(5, 0)],
        [new(new(-5, 0), new(5, 0))]);
    var symbol = new DrawingElement(Guid.NewGuid(), ElementKind.Symbol, new(20, 20), new(20, 20))
    { SymbolKey = definition.Key, DeviceTag = "Y1", PinOffsets = definition.Pins, SymbolStrokes = definition.Strokes };
    var path = Path.Combine(output, "custom-symbol.ecad");
    ProjectFile.Save(path, new() { Elements = [symbol, SymbolLabels.Create(symbol)], CustomSymbols = [definition] });
    var read = ProjectFile.Open(path);
    Assert(read.CustomSymbols.Single().Key == definition.Key);
    var readSymbol = read.Elements.Single(e => e.Kind == ElementKind.Symbol);
    Assert(readSymbol.PinOffsets!.SequenceEqual(definition.Pins));
    Assert(readSymbol.SymbolStrokes!.Single() == definition.Strokes!.Single());
    var label = read.Elements.Single(e => e.Kind == ElementKind.Text);
    Assert(label.LinkedElementId == readSymbol.Id && label.Text == "Y1");
});
Check("Version 5 symbol tags migrate to independent text labels", () =>
{
    var symbol = new DrawingElement(Guid.NewGuid(), ElementKind.Symbol, new(20, 20), new(20, 20))
        { SymbolKey = "IEC_MOTOR_3P", DeviceTag = "M1" };
    var path = Path.Combine(output, "legacy-symbol-label.ecad");
    using (var file = File.Create(path))
    using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(zip.CreateEntry("project.json").Open()))
        writer.Write(System.Text.Json.JsonSerializer.Serialize(new DrawingDocument { SchemaVersion = 5, Elements = [symbol] }));
    var read = ProjectFile.Open(path);
    var label = read.Elements.Single(e => e.LinkedElementId == symbol.Id);
    Assert(read.SchemaVersion == DocumentFormat.Current && label.Text == "M1" && label.Kind == ElementKind.Text);
});
Check("Pages keep independent geometry, formats and title blocks in one project", () =>
{
    var s = new EditorSession(); var first = s.ActivePage.Id;
    s.Add(Line());
    s.AddPage("Керування", PaperFormat.A4Portrait, true, 6, 8,
        new PageTitleBlock { Project = "Шафа 1", Drawing = "Керування", Author = "MX", Revision = "A" });
    var second = s.ActivePage.Id; s.Add(Line(20));
    Assert(s.Document.Pages.Length == 2 && s.ActivePage.Format == PaperFormat.A4Portrait);
    Assert(s.ActivePage.Elements.Length == 1 && s.ActivePage.TitleBlock.Project == "Шафа 1");
    s.SwitchPage(first); Assert(s.Document.Elements.Single().A.Y == 0);
    s.SwitchPage(second); Assert(s.Document.Elements.Single().A.Y == 20);
    var path = Path.Combine(output, "multipage.ecad"); ProjectFile.Save(path, s.Document);
    var read = ProjectFile.Open(path);
    Assert(read.SchemaVersion == DocumentFormat.Current && read.Pages.Length == 2 && read.CrossPageReferences.Length == 0);
    Assert(read.Pages.Single(page => page.Id == first).Elements.Single().A.Y == 0);
    Assert(read.Pages.Single(page => page.Id == second).Elements.Single().A.Y == 20);
    s.DeletePage(second); Assert(s.Document.Pages.Length == 1); s.Undo(); Assert(s.Document.Pages.Length == 2);
});
Check("Cross-page references require existing elements on different pages", () =>
{
    var s = new EditorSession(); var first = s.ActivePage.Id; var from = Line(); s.Add(from);
    s.AddPage("Силова", PaperFormat.A3Landscape); var second = s.ActivePage.Id; var to = Line(5); s.Add(to);
    s.AddCrossPageReference(first, from.Id, second, to.Id, "X1");
    var linked = s.Document; var link = linked.CrossPageReferences.Single(); linked.Validate();
    Reject(() => DrawingPages.Normalize(linked with { CrossPageReferences = [link with { ToElementId = Guid.NewGuid() }] }).Validate());
    s.RemoveCrossPageReferences(second, to.Id); Assert(s.Document.CrossPageReferences.Length == 0);
    s.Undo(); Assert(s.Document.CrossPageReferences.Single().Label == "X1");
});
Check("Repeated symbol representations share a device and keep distinct functions", () =>
{
    var coil = new DrawingElement(Guid.NewGuid(), ElementKind.Symbol, new(20, 20), new(20, 20))
        { SymbolKey = "IEC_COIL", DeviceTag = "KM1" };
    var contact = new DrawingElement(Guid.NewGuid(), ElementKind.Symbol, new(50, 20), new(50, 20))
        { SymbolKey = "IEC_NO_CONTACT", DeviceTag = "KM1" };
    var s = new EditorSession(); s.Apply([coil, contact]);
    var symbols = s.Document.Elements.Where(item => item.Kind == ElementKind.Symbol).ToArray();
    Assert(s.Document.Devices.Length == 1 && symbols.Select(item => item.DeviceId).Distinct().Count() == 1);
    Assert(symbols.Select(item => item.DeviceFunctionId).Distinct().Count() == 2);
    Assert(s.Document.Devices.Single().Functions.Any(function => function.Kind == DeviceFunctionKind.Coil));
    Assert(s.Document.Devices.Single().Functions.Any(function => function.Kind == DeviceFunctionKind.NoContact));
});
Check("Connected wire segments receive a stable editable net and automatic number", () =>
{
    var s = new EditorSession(); var first = Wire(new(0, 0), new(10, 0)); var second = Wire(new(10, 0), new(20, 0));
    s.Apply([first, second]); var netId = s.Document.Elements[0].NetId ?? throw new Exception("Net ID missing");
    Assert(s.Document.Elements[1].NetId == netId && s.Document.Nets.Length == 1);
    s.UpdateNet(netId, "L+", "Живлення ПЛК", "+24VDC", "DC control", "RD", .75, true);
    s.RenumberNets("N", 100); Assert(s.Document.Nets.Single().Number == "L+");
    var path = Path.Combine(output, "stable-net.ecad"); ProjectFile.Save(path, s.Document);
    var read = ProjectFile.Open(path); Assert(read.Elements.All(item => item.NetId == netId));
    Assert(read.Nets.Single().Potential == "+24VDC" && read.Nets.Single().CrossSectionMm2 == .75);
});
Check("Terminal strips and cable cores validate and participate in ERC", () =>
{
    var s = new EditorSession(); s.Add(Wire(new(0, 0), new(10, 0))); var net = s.Document.Nets.Single();
    var strip = s.CreateTerminalStrip("X1", "Польові клеми", 2);
    var cable = s.CreateCable("W1", "4G1.5", 2, 1.5, 12, "X1", "M1");
    var cores = cable.Cores.Select((core, index) => index == 0
        ? core with { Status = CableCoreStatus.Used, NetId = net.Id } : core).ToArray();
    s.UpdateCable(cable with { Cores = cores });
    Assert(s.Document.TerminalStrips.Single().Id == strip.Id && s.Document.Cables.Single().Cores[0].NetId == net.Id);
    Assert(!ElectricalRuleChecker.Check(s.Document).Any(issue => issue.Kind == ElectricalIssueKind.InvalidCableCore));
    s.UpdateCable(s.Document.Cables.Single() with { Cores = cores.Select((core, index) => index == 1 ? core with { NetId = net.Id } : core).ToArray() });
    Assert(ElectricalRuleChecker.Check(s.Document).Any(issue => issue.Kind == ElectricalIssueKind.InvalidCableCore));
});
Check("Complete electrical example includes every 0.17 model and round-trips", () =>
{
    var example = ExampleElectricalProject.Create();
    Assert(example.Pages.Length == 2 && example.Devices.Length >= 4 && example.Nets.Length >= 7);
    Assert(example.Devices.Single(device => device.Tag == "KM1").Functions.Length == 2);
    Assert(example.TerminalStrips.Single().Terminals.Length == 4 && example.Cables.Single().Cores.Length == 4);
    Assert(example.CrossPageReferences.Length == 1 && example.ComponentLibraries.Length == 2);
    var path = Path.Combine(output, "complete-electrical-demo.ecad"); ProjectFile.Save(path, example);
    var read = ProjectFile.Open(path); Assert(read.SchemaVersion == DocumentFormat.Current && read.Cables.Single().Tag == "W1");
    var distributed = ProjectFile.Open(Path.Combine("examples", "projects", "complete-electrical-demo.ecad"));
    Assert(distributed.Pages.Length == 2 && distributed.Devices.Any(device => device.Tag == "KM1"));
});
Check("Unsupported archive version rejected", () =>
{
    var path = Path.Combine(output, "future.ecad");
    using (var file = File.Create(path))
    using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(zip.CreateEntry("project.json").Open())) writer.Write("{\"SchemaVersion\":999}");
    Reject(() => ProjectFile.Open(path));
});
Check("Device catalog supports arbitrary configuration axes and physical variants", () =>
{
    var s = new EditorSession();
    var library = s.CreateComponentLibrary("Промислова автоматика", "Власна бібліотека підприємства");
    var type = s.CreateDeviceType(library.Id, "Автоматичний вимикач", "Модульні та силові виконання",
    [
        new("poles", "Кількість полюсів", true, ["1P", "2P", "3P", "4P"]),
        new("contact-arrangement", "Виконання контактів", true),
        new("installation", "Спосіб встановлення")
    ]);
    var family = s.CreateDeviceFamily(library.Id, type.Id, "System pro M compact", "ABB", "S200",
        [new("standard", "Стандарт", "IEC 60898-1")]);
    var panel = new PhysicalRepresentation(Guid.NewGuid(), "DIN-виконання", 72, 85, 75, "DIN-рейка", 4);
    var layout = new PhysicalRepresentation(Guid.NewGuid(), "Монтажна плата", 78, 92, 76, "Монтажна плата");
    var variant = s.CreateDeviceVariant(library.Id, type.Id, family.Id,
        new(Guid.Empty, "S204 C63", "2CDS254001R0634", "C63, 400 V", "IEC_BREAKER",
            [new("poles", "4P"), new("contact-arrangement", "2NO+2NC"), new("installation", "знімне виконання")],
            [new("rated-current", "Номінальний струм", "63", "A"), new("breaking-capacity", "Вимикальна здатність", "6", "kA")],
            [new("1", "1", "Силовий вхід L1", "Power"), new("2", "2", "Силовий вихід T1", "Power")],
            [panel, layout]));
    var symbol = new DrawingElement(Guid.NewGuid(), ElementKind.Symbol, new(20, 20), new(20, 20))
        { SymbolKey = "IEC_BREAKER", DeviceTag = "QF1" };
    s.Add(symbol); s.AssignDeviceVariant(symbol.Id, variant.Id);
    var linked = ComponentCatalog.FindVariant(s.Document, variant.Id)!;
    Assert(linked.Library.Name == "Промислова автоматика" && linked.Family.Manufacturer == "ABB");
    Assert(linked.Variant.Configuration.Any(value => value.Key == "poles" && value.Value == "4P"));
    Assert(linked.Variant.Configuration.Any(value => value.Key == "contact-arrangement" && value.Value == "2NO+2NC"));
    Assert(s.Document.Elements.Single().PhysicalRepresentationId == panel.Id);

    var path = Path.Combine(output, "device-catalog.ecad"); ProjectFile.Save(path, s.Document);
    var reopened = ProjectFile.Open(path); var restored = ComponentCatalog.FindVariant(reopened, variant.Id)!;
    Assert(reopened.SchemaVersion == DocumentFormat.Current && restored.Variant.PhysicalRepresentations.Length == 2);
    Assert(reopened.Elements.Single().ComponentVariantId == variant.Id);
    s.Undo(); Assert(s.Document.Elements.Single().ComponentVariantId is null);
    s.Redo(); Assert(s.Document.Elements.Single().ComponentVariantId == variant.Id);
});
Check("Device catalog rejects invalid variants and incompatible symbols", () =>
{
    var s = new EditorSession();
    var library = s.CreateComponentLibrary("Тестова");
    var type = s.CreateDeviceType(library.Id, "Пристрій", null,
        [new("channels", "Кількість каналів", true, ["1", "2", "8", "16"]), new("execution", "Виконання")]);
    var family = s.CreateDeviceFamily(library.Id, type.Id, "Серія X", null, null);
    var physical = new PhysicalRepresentation(Guid.NewGuid(), "Корпус", 20, 30, 40, "Панель");
    var bad = new DeviceVariant(Guid.Empty, "X5", null, null, "IEC_COIL",
        [new("channels", "5")], [], [], [physical]);
    var before = s.Document;
    Reject(() => s.CreateDeviceVariant(library.Id, type.Id, family.Id, bad));
    Assert(ReferenceEquals(before, s.Document));

    var valid = s.CreateDeviceVariant(library.Id, type.Id, family.Id, bad with
    {
        Name = "X16", Configuration = [new("channels", "16"), new("execution", "двоканальне резервування")]
    });
    var breaker = new DrawingElement(Guid.NewGuid(), ElementKind.Symbol, new(0, 0), new(0, 0))
        { SymbolKey = "IEC_BREAKER", DeviceTag = "QF1" };
    s.Add(breaker); before = s.Document;
    Reject(() => s.AssignDeviceVariant(breaker.Id, valid.Id));
    Assert(ReferenceEquals(before, s.Document));
});
Check("Catalog updates preserve links, remap removed cases and reject breaking edits", () =>
{
    var s = new EditorSession(); s.Load(ProjectFile.Open(Path.Combine(output, "device-catalog.ecad")));
    var library = s.Document.ComponentLibraries.Single();
    var type = library.DeviceTypes.Single(); var family = type.Families.Single(); var variant = family.Variants.Single();
    var remainingPhysical = variant.PhysicalRepresentations[1];
    var updatedVariant = variant with { RatingText = "C63, 415 V", PhysicalRepresentations = [remainingPhysical] };
    var updatedFamily = family with { Variants = [updatedVariant] };
    var updatedType = type with { Families = [updatedFamily] };
    s.UpdateComponentLibrary(library with { DeviceTypes = [updatedType] });
    Assert(s.Document.ComponentLibraries.Single().Version == library.Version + 1);
    Assert(s.Document.Elements.Single().PhysicalRepresentationId == remainingPhysical.Id);

    var before = s.Document;
    Reject(() => s.UpdateComponentLibrary(s.Document.ComponentLibraries.Single() with
    {
        DeviceTypes = [updatedType with { Families = [updatedFamily with { Variants = [updatedVariant with { SymbolKey = "IEC_COIL" }] }] }]
    }));
    Assert(ReferenceEquals(before, s.Document));
    Reject(() => s.DeleteComponentLibrary(library.Id)); Assert(ReferenceEquals(before, s.Document));
});
Check("Catalog libraries copy and round-trip through ecadlib files", () =>
{
    var s = new EditorSession(); s.Load(ProjectFile.Open(Path.Combine(output, "device-catalog.ecad")));
    var source = s.Document.ComponentLibraries.Single(); var copy = s.DuplicateComponentLibrary(source.Id);
    Assert(copy.Name != source.Name && copy.Id != source.Id && copy.Version == 1);
    var sourceIds = source.DeviceTypes.SelectMany(type => new[] { type.Id }.Concat(type.Families.SelectMany(family =>
        new[] { family.Id }.Concat(family.Variants.SelectMany(variant => new[] { variant.Id }.Concat(variant.PhysicalRepresentations.Select(item => item.Id))))))).ToHashSet();
    var copyIds = copy.DeviceTypes.SelectMany(type => new[] { type.Id }.Concat(type.Families.SelectMany(family =>
        new[] { family.Id }.Concat(family.Variants.SelectMany(variant => new[] { variant.Id }.Concat(variant.PhysicalRepresentations.Select(item => item.Id))))))).ToHashSet();
    Assert(!sourceIds.Overlaps(copyIds));
    var path = Path.Combine(output, "industrial.ecadlib"); ComponentLibraryFile.Save(path, copy);
    var reopened = ComponentLibraryFile.Open(path, SymbolLibrary.Definitions(s.Document).Select(item => item.Key));
    Assert(reopened.Id == copy.Id && reopened.Name == copy.Name && reopened.DeviceTypes.Single().Families.Single().Variants.Single().Name == "S204 C63");
    var imported = new EditorSession(); imported.ImportComponentLibrary(reopened);
    Assert(imported.Document.ComponentLibraries.Single().Id == copy.Id); imported.Undo(); Assert(imported.Document.ComponentLibraries.Length == 0);
});
Check("Example libraries exercise universal configurations, contacts and physical outlines", () =>
{
    var examples = ExampleComponentLibraries.Create();
    Assert(examples.Length == 2 && examples.Sum(item => item.DeviceTypes.Length) == 5);
    var document = new DrawingDocument { ComponentLibraries = examples }; document.Validate();
    var all = ComponentCatalog.Variants(document);
    Assert(all.Length == 9 && all.Sum(item => item.Variant.PhysicalRepresentations.Length) == 14);
    Assert(all.Any(item => item.Variant.Configuration.Any(value => value.Key == "poles" && value.Value == "4P")));
    Assert(all.Any(item => item.Variant.Configuration.Any(value => value.Key == "power" && value.Value == "7.5")));
    Assert(all.All(item => item.Variant.Contacts.Length >= 2));
    Assert(all.SelectMany(item => item.Variant.PhysicalRepresentations).All(item => item.Outline?.Length == 4));
    foreach (var file in new[] { "iec-automation-demo.ecadlib", "drives-and-motors-demo.ecadlib" })
    {
        var imported = ComponentLibraryFile.Open(Path.GetFullPath(Path.Combine("examples", "libraries", file)), SymbolLibrary.All.Select(item => item.Key));
        Assert(examples.Any(item => item.Id == imported.Id && item.Name == imported.Name));
    }
    var s = new EditorSession(); s.ImportComponentLibraries(examples); Assert(s.Document.ComponentLibraries.Length == 2);
    s.Undo(); Assert(s.Document.ComponentLibraries.Length == 0);
});

AppBuilder.Configure<App>().UseSkia().WithInterFont()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
var editor = new EditorSession();
var canvas = new DrawingCanvas(editor) { ExactLength = 12.3 };
var window = new Window { Width = 1000, Height = 650, Content = canvas };
window.Show();
void Click(Point p) { window.MouseDown(p, MouseButton.Left); window.MouseUp(p, MouseButton.Left); }
Check("Pointer input creates exact line with grid enabled", () =>
{
    canvas.SetTool(ElementKind.Line);
    Click(new(60, 60)); Click(new(180, 60));
    Assert(editor.Document.Elements.Length == 1); Near(editor.Document.Elements[0].LengthMm, 12.3);
});
Check("Pointer drag moves element; undo restores coordinates", () =>
{
    var before = editor.Document.Elements[0];
    canvas.SetTool(null);
    window.MouseDown(new(72, 60), MouseButton.Left);
    window.MouseMove(new(96, 84));
    window.MouseUp(new(96, 84), MouseButton.Left);
    Near(editor.Document.Elements[0].A.X, before.A.X + 10);
    Near(editor.Document.Elements[0].A.Y, before.A.Y + 10);
    editor.Undo(); Assert(editor.Document.Elements[0] == before);
});
Check("Rectangle, circle and dimension pointer tools", () =>
{
    canvas.ExactLength = 0;
    foreach (var kind in new[] { ElementKind.Rectangle, ElementKind.Circle, ElementKind.Dimension })
    {
        canvas.SetTool(kind); Click(new(240, 200)); Click(new(420, 320));
        if (kind == ElementKind.Dimension) { window.MouseMove(new(450, 370)); Click(new(450, 370)); }
        Assert(editor.Document.Elements[^1].Kind == kind);
    }
    using var frame = window.CaptureRenderedFrame() ?? throw new Exception("No rendered canvas");
    frame.Save(Path.Combine(output, "canvas.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
});
window.Close();
Check("Window versus crossing selection uses actual geometry", () =>
{
    var s = new EditorSession(); var a = Line(); var b = Line(20);
    var diagonal = new DrawingElement(Guid.NewGuid(), ElementKind.Line, new(0, 0), new(100, 100));
    s.Add(a); s.Add(b); s.Add(diagonal);
    s.SelectBox(new(-1, -1, 11, 1), false, false); Assert(s.Selection.SetEquals([a.Id]));
    s.SelectBox(new(4, -1, 6, 1), true, false); Assert(s.Selection.SetEquals([a.Id]));
    s.SelectBox(new(40, 0, 50, 10), true, false); Assert(s.Selection.Count == 0);
    s.SelectBox(new(-1, 19, 11, 21), false, true); Assert(s.Selection.SetEquals([b.Id]));
});
Check("A selection frame preserves group boundaries", () =>
{
    var s = new EditorSession(); var group = Guid.NewGuid();
    var a = Line() with { GroupId = group }; var b = Line(20) with { GroupId = group };
    s.Add(a); s.Add(b);
    s.SelectBox(new(-1, -1, 11, 1), false, false); Assert(s.Selection.Count == 0);
    s.SelectBox(new(-1, -1, 11, 1), true, false); Assert(s.Selection.Count == 2);
});
Check("Circle crossing excludes rectangles entirely inside the empty circle", () =>
{
    var circle = new DrawingElement(Guid.NewGuid(), ElementKind.Circle, new(10, 10), new(20, 10));
    Assert(!new SelectionBox(9, 9, 11, 11).Matches(circle, true));
    Assert(new SelectionBox(19, 9, 21, 11).Matches(circle, true));
});
Check("Endpoint dimensions follow resizing, delete and undo atomically", () =>
{
    var s = new EditorSession(); var line = Line();
    var dim = new DrawingElement(Guid.NewGuid(), ElementKind.Dimension, line.A, line.B)
    { StartReference = new(line.Id, ReferenceKind.Vertex, 0), EndReference = new(line.Id, ReferenceKind.Vertex, 1) };
    s.Add(line); s.Add(dim);
    s.Apply(s.Document.Elements.Select(e => e.Id == line.Id ? e with { B = new(23.7, 0) } : e).ToArray());
    Near(s.Document.Elements[1].LengthMm, 23.7);
    s.Undo(); Near(s.Document.Elements[1].LengthMm, 10); s.Redo();
    s.Select(s.Document.Elements[0], false); s.Delete(); Assert(s.Document.Elements.Length == 0);
    s.Undo(); Near(s.Document.Elements[1].LengthMm, 23.7);
    var path = Path.Combine(output, "associative.ecad"); ProjectFile.Save(path, s.Document);
    var read = ProjectFile.Open(path); Assert(read.SchemaVersion == DocumentFormat.Current);
    Assert(read.Elements[1].StartReference == dim.StartReference);
});
Check("Parallel edge and point-to-edge distances remain perpendicular", () =>
{
    var a = Line(); var b = Line(20); var p = Line(35);
    var dim = new DrawingElement(Guid.NewGuid(), ElementKind.Dimension, default, new(1, 1))
    { StartReference = new(a.Id, ReferenceKind.Edge, 0, .5), EndReference = new(b.Id, ReferenceKind.Edge, 0, .9) };
    var s = new EditorSession(); s.Apply([a, b, p, dim]); Near(s.Document.Elements[3].LengthMm, 20);
    s.Select(b, false); s.Move(new(5, 7)); Near(s.Document.Elements[3].LengthMm, 27);
    s.Undo(); Near(s.Document.Elements[3].LengthMm, 20);
    var pointDimension = dim with { Id = Guid.NewGuid(), StartReference = new(p.Id, ReferenceKind.Vertex, 1) };
    s.Add(pointDimension); Near(s.Document.Elements[^1].LengthMm, 15);
    s.Select(b, false); s.Move(new(0, 5)); Near(s.Document.Elements[^1].LengthMm, 10);
});
Check("Driving aligned dimension resizes a line and connected geometry atomically", () =>
{
    var line = Line();
    var wire = Wire(new PointMm(10, 0), new PointMm(10, 20));
    var node = new DrawingElement(Guid.NewGuid(), ElementKind.Junction, line.B, line.B);
    var dimension = new DrawingElement(Guid.NewGuid(), ElementKind.Dimension, line.A, line.B)
    {
        StartReference = new(line.Id, ReferenceKind.Vertex, 0),
        EndReference = new(line.Id, ReferenceKind.Vertex, 1),
        DimensionMode = DimensionMode.Driving, DimensionTargetMm = 24.75
    };
    var s = new EditorSession(); s.Apply([line, wire, node, dimension]);
    Near(s.Document.Elements[0].LengthMm, 24.75);
    Assert(s.Document.Elements[1].A == new PointMm(24.75, 0));
    Assert(s.Document.Elements[2].A == new PointMm(24.75, 0));
    Near(s.Document.Elements[3].DimensionValueMm, 24.75);
    var path = Path.Combine(output, "driving-dimension.ecad"); ProjectFile.Save(path, s.Document);
    var reopened = ProjectFile.Open(path);
    Assert(reopened.SchemaVersion == DocumentFormat.Current && reopened.Elements[3].DimensionMode == DimensionMode.Driving);
    Near(reopened.Elements[3].DimensionTargetMm!.Value, 24.75);
    s.Undo(); Assert(s.Document.Elements.Length == 0); s.Redo(); Near(s.Document.Elements[0].LengthMm, 24.75);
});
Check("Driving horizontal and vertical dimensions resize a rectangle independently", () =>
{
    var rectangle = new DrawingElement(Guid.NewGuid(), ElementKind.Rectangle, new(0, 0), new(10, 20));
    DrawingElement Dimension(int a, int b, DimensionType type, double target) =>
        new(Guid.NewGuid(), ElementKind.Dimension, default, default)
        {
            StartReference = new(rectangle.Id, ReferenceKind.Vertex, a),
            EndReference = new(rectangle.Id, ReferenceKind.Vertex, b),
            DimensionType = type, DimensionMode = DimensionMode.Driving, DimensionTargetMm = target
        };
    var s = new EditorSession(); s.Apply([rectangle,
        Dimension(0, 1, DimensionType.Horizontal, 32.5),
        Dimension(1, 2, DimensionType.Vertical, 17.25)]);
    var resized = s.Document.Elements[0]; Near(resized.B.X, 32.5); Near(resized.B.Y, 17.25);
    Near(s.Document.Elements[1].DimensionValueMm, 32.5); Near(s.Document.Elements[2].DimensionValueMm, 17.25);
});
Check("Driving diameter resizes a circle and curve references follow", () =>
{
    var circle = new DrawingElement(Guid.NewGuid(), ElementKind.Circle, new(10, 10), new(15, 10));
    var dimension = new DrawingElement(Guid.NewGuid(), ElementKind.Dimension, circle.A, new(10, 15))
    {
        StartReference = new(circle.Id, ReferenceKind.Vertex, 0),
        EndReference = new(circle.Id, ReferenceKind.Curve, 0, .25),
        DimensionType = DimensionType.Diameter, DimensionMode = DimensionMode.Driving, DimensionTargetMm = 30
    };
    var s = new EditorSession(); s.Apply([circle, dimension]);
    Near(s.Document.Elements[0].LengthMm, 15); Near(s.Document.Elements[1].DimensionValueMm, 30);
    Near(s.Document.Elements[1].B.X, 10); Near(s.Document.Elements[1].B.Y, 25);
    var picked = AssociativeDimensions.Pick(s.Document.Elements, new(10.1, 25.1), .5);
    Assert(picked.Reference?.Kind == ReferenceKind.Curve);
});
Check("Conflicting and cross-object driving dimensions are rejected", () =>
{
    var line = Line();
    DrawingElement Driver(double target) => new(Guid.NewGuid(), ElementKind.Dimension, line.A, line.B)
    {
        StartReference = new(line.Id, ReferenceKind.Vertex, 0), EndReference = new(line.Id, ReferenceKind.Vertex, 1),
        DimensionMode = DimensionMode.Driving, DimensionTargetMm = target
    };
    Reject(() => new EditorSession().Apply([line, Driver(10), Driver(20)]));
    var other = Line(10);
    Reject(() => new EditorSession().Apply([line, other, Driver(10) with
    { EndReference = new(other.Id, ReferenceKind.Vertex, 1) }]));
});
Check("Moving geometry and its dimension together does not double the offset", () =>
{
    var s = new EditorSession(); var line = Line();
    var dim = new DrawingElement(Guid.NewGuid(), ElementKind.Dimension, line.A, line.B)
    { StartReference = new(line.Id, ReferenceKind.Vertex, 0), EndReference = new(line.Id, ReferenceKind.Vertex, 1), DimensionOffset = 12 };
    s.Apply([line, dim]); s.Selection.UnionWith([line.Id, dim.Id]); s.Move(new(5, 8));
    Assert(s.Document.Elements[1].A == new PointMm(5, 8)); Near(s.Document.Elements[1].DimensionOffset, 12);
    s.Select(s.Document.Elements[1], false); s.Move(new(0, 3)); Near(s.Document.Elements[1].DimensionOffset, 15);
    Assert(s.Document.Elements[0].A == new PointMm(5, 8));
});
Check("Copy/paste remaps IDs, groups and internal dimension references", () =>
{
    var s = new EditorSession(); var group = Guid.NewGuid();
    var line = Line() with { GroupId = group };
    var dim = new DrawingElement(Guid.NewGuid(), ElementKind.Dimension, line.A, line.B, group)
    { StartReference = new(line.Id, ReferenceKind.Vertex, 0), EndReference = new(line.Id, ReferenceKind.Vertex, 1) };
    s.Apply([line, dim]); s.Selection.UnionWith([line.Id, dim.Id]); s.Copy(); s.Paste();
    Assert(s.Document.Elements.Length == 4 && s.Selection.Count == 2);
    var copyLine = s.Document.Elements[2]; var copyDim = s.Document.Elements[3];
    Assert(copyLine.Id != line.Id && copyLine.GroupId != group);
    Assert(copyDim.StartReference?.ElementId == copyLine.Id && copyDim.EndReference?.ElementId == copyLine.Id);
    Near(copyLine.A.X, line.A.X + 5); Near(copyDim.LengthMm, line.LengthMm);
    s.Undo(); Assert(s.Document.Elements.Length == 2); s.Redo(); Assert(s.Document.Elements.Length == 4);
});
Check("Rotate 90 preserves geometry and associative rectangle references", () =>
{
    var s = new EditorSession();
    var rectangle = new DrawingElement(Guid.NewGuid(), ElementKind.Rectangle, new(0, 0), new(10, 20));
    var dim = new DrawingElement(Guid.NewGuid(), ElementKind.Dimension, rectangle.A, new(10, 0))
    { StartReference = new(rectangle.Id, ReferenceKind.Vertex, 0), EndReference = new(rectangle.Id, ReferenceKind.Vertex, 1) };
    s.Apply([rectangle, dim]); s.Select(rectangle, false); s.RotateSelection90();
    var rotated = s.Document.Elements[0];
    Assert(rotated.A == new PointMm(-5, 5) && rotated.B == new PointMm(15, 15));
    Assert(s.Document.Elements[1].StartReference?.Index == 1 && s.Document.Elements[1].EndReference?.Index == 2);
    Near(s.Document.Elements[1].LengthMm, 10); Assert(s.Document.Elements[1].A == new PointMm(15, 5));
    s.Undo(); Assert(s.Document.Elements[0] == rectangle); Near(s.Document.Elements[1].LengthMm, 10);
});
Check("Rotation swaps horizontal and vertical driving constraints", () =>
{
    var rectangle = new DrawingElement(Guid.NewGuid(), ElementKind.Rectangle, new(0, 0), new(30, 20));
    var dimension = new DrawingElement(Guid.NewGuid(), ElementKind.Dimension, rectangle.A, new(30, 0))
    {
        StartReference = new(rectangle.Id, ReferenceKind.Vertex, 0),
        EndReference = new(rectangle.Id, ReferenceKind.Vertex, 1),
        DimensionType = DimensionType.Horizontal, DimensionMode = DimensionMode.Driving, DimensionTargetMm = 30
    };
    var s = new EditorSession(); s.Apply([rectangle, dimension]); s.Select(rectangle, false); s.RotateSelection90();
    var rotatedDimension = s.Document.Elements[1];
    Assert(rotatedDimension.DimensionType == DimensionType.Vertical); Near(rotatedDimension.DimensionValueMm, 30);
});
Check("Invalid dimension references and nonparallel edges are rejected", () =>
{
    var a = Line(); var b = Line(20) with { B = new(5, 25) };
    var dim = new DrawingElement(Guid.NewGuid(), ElementKind.Dimension, default, new(1, 1))
    { StartReference = new(a.Id, ReferenceKind.Edge, 0), EndReference = new(b.Id, ReferenceKind.Edge, 0) };
    Reject(() => new DrawingDocument { Elements = [a, b, dim] }.Validate());
    Reject(() => new DrawingDocument { Elements = [dim] }.Validate());
});
Check("Version 1 documents are loaded and upgraded without losing free dimensions", () =>
{
    var path = Path.Combine(output, "legacy.ecad");
    using (var file = File.Create(path))
    using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(zip.CreateEntry("project.json").Open()))
        writer.Write(System.Text.Json.JsonSerializer.Serialize(new DrawingDocument { SchemaVersion = 1, Elements = [Line()] }));
    var read = ProjectFile.Open(path); Assert(read.SchemaVersion == DocumentFormat.Current); Near(read.Elements[0].LengthMm, 10);
});

var interactive = new EditorSession();
var liveCanvas = new DrawingCanvas(interactive);
var liveWindow = new Window { Width = 1000, Height = 650, Content = liveCanvas };
liveWindow.Show();
void Tap(double x, double y) { liveWindow.MouseDown(new(x, y), MouseButton.Left); liveWindow.MouseUp(new(x, y), MouseButton.Left); }
Check("Line supports start point to end point with a second click", () =>
{
    interactive.Load(new()); liveCanvas.SetTool(ElementKind.Line);
    Tap(60, 60);
    liveWindow.MouseMove(new(500, 300)); Tap(500, 300);
    Assert(interactive.Document.Elements.Length == 1 && !liveCanvas.LineInput.IsVisible);
    Assert(interactive.Document.Elements[0].A == new PointMm(10, 10));
    Near(interactive.Document.Elements[0].B.X, 192.5);
    Near(interactive.Document.Elements[0].B.Y, 110);
});
Check("Angle step modes snap pointer lines and wires to selected increments", () =>
{
    interactive.Load(new()); liveCanvas.SetAngleSnap(30); liveCanvas.SetTool(ElementKind.Line);
    Tap(180, 180); liveWindow.MouseMove(new(300, 110)); Tap(300, 110);
    Near(Geometry.Angle(interactive.Document.Elements.Single().B - interactive.Document.Elements.Single().A), 30);

    interactive.Load(new()); liveCanvas.SetAngleSnap(45); liveCanvas.SetTool(ElementKind.Wire);
    Tap(180, 180); liveWindow.MouseMove(new(300, 60)); Tap(300, 60);
    liveWindow.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    var wire = interactive.Document.Elements.Single();
    Assert(wire.Points!.Length == 2); Near(Geometry.Angle(wire.B - wire.A), 45);
    liveCanvas.SetAngleSnap(0);
});
Check("One path tool switches between electrical and graphical lines", () =>
{
    liveCanvas.SetPathKind(ElementKind.Wire); liveCanvas.ActivatePathTool();
    Assert(liveCanvas.Tool == ElementKind.Wire && liveCanvas.ActivePathKind == ElementKind.Wire);
    liveCanvas.SetPathKind(ElementKind.Line);
    Assert(liveCanvas.Tool == ElementKind.Line && liveCanvas.ActivePathKind == ElementKind.Line);
    liveCanvas.EscapeToSelection();
    Assert(liveCanvas.Tool is null && liveCanvas.ActivePathKind == ElementKind.Line);
});
Check("L C R D P A keyboard shortcuts activate drawing tools", () =>
{
    void Press(PhysicalKey key)
    {
        liveWindow.KeyPressQwerty(key, RawInputModifiers.None);
        liveWindow.KeyReleaseQwerty(key, RawInputModifiers.None);
    }
    liveCanvas.SetPathKind(ElementKind.Wire); liveCanvas.Focus();
    Press(PhysicalKey.L); Assert(liveCanvas.Tool == ElementKind.Wire);
    Press(PhysicalKey.C); Assert(liveCanvas.Tool == ElementKind.Circle);
    Press(PhysicalKey.R); Assert(liveCanvas.Tool == ElementKind.Rectangle);
    Press(PhysicalKey.D); Assert(liveCanvas.Tool == ElementKind.Dimension);
    Press(PhysicalKey.P); Assert(liveCanvas.Tool == ElementKind.Polyline);
    Press(PhysicalKey.A); Assert(liveCanvas.Tool == ElementKind.Arc);
    liveCanvas.EscapeToSelection();
});
Check("Floating input: type length, Tab, angle and Enter", () =>
{
    interactive.Load(new()); liveCanvas.SetTool(ElementKind.Line); Tap(60, 60); liveWindow.MouseMove(new(240, 160));
    liveWindow.KeyTextInput("12,3");
    liveWindow.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
    Assert(liveCanvas.LineInput.AngleBox.IsFocused);
    liveWindow.KeyTextInput("30");
    using (var frame = liveWindow.CaptureRenderedFrame()!)
        frame.Save(Path.Combine(output, "dynamic-input.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    liveWindow.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    Assert(interactive.Document.Elements.Length == 1);
    Near(interactive.Document.Elements[0].LengthMm, 12.3);
    Near(Geometry.Angle(interactive.Document.Elements[0].B - interactive.Document.Elements[0].A), 30);
});
Check("Floating input rejects zero and Escape cancels without adding a command", () =>
{
    Tap(150, 150); liveWindow.MouseMove(new(300, 200)); liveWindow.KeyTextInput("0");
    liveWindow.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    Assert(interactive.Document.Elements.Length == 1);
    liveWindow.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
    Assert(!liveCanvas.LineInput.IsVisible); Assert(interactive.Document.Elements.Length == 1);
    Assert(liveCanvas.Tool is null);
});
Check("Rectangle supports exact width and height through the shared input", () =>
{
    interactive.Load(new()); liveCanvas.SetTool(ElementKind.Rectangle); Tap(60, 60); liveWindow.MouseMove(new(240, 180));
    liveWindow.KeyTextInput("12,3");
    liveWindow.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
    liveWindow.KeyTextInput("7.4");
    liveWindow.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    var rectangle = interactive.Document.Elements.Single();
    Assert(rectangle.Kind == ElementKind.Rectangle && rectangle.A == new PointMm(10, 10));
    Near(Math.Abs(rectangle.B.X - rectangle.A.X), 12.3); Near(Math.Abs(rectangle.B.Y - rectangle.A.Y), 7.4);
});
Check("Circle supports exact radius or diameter through the shared input", () =>
{
    interactive.Load(new()); liveCanvas.SetTool(ElementKind.Circle); Tap(60, 60); liveWindow.MouseMove(new(240, 60));
    liveWindow.KeyTextInput("8.75");
    liveWindow.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    Near(interactive.Document.Elements.Single().LengthMm, 8.75);

    Tap(180, 180); liveWindow.MouseMove(new(300, 180)); liveWindow.KeyTextInput("1");
    liveWindow.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
    liveWindow.KeyTextInput("22.5");
    liveWindow.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    Near(interactive.Document.Elements.Last().LengthMm, 11.25);
});
Check("Rectangle parameter editing keeps its anchor and is undoable", () =>
{
    var rectangle = new DrawingElement(Guid.NewGuid(), ElementKind.Rectangle, new(20, 30), new(35, 40));
    interactive.Load(new() { Elements = [rectangle] }); interactive.Select(rectangle, false);
    liveCanvas.EditSelectedGeometry();
    liveCanvas.LineInput.LengthBox.Text = "24.6"; liveCanvas.LineInput.AngleBox.Text = "13.2";
    liveCanvas.LineInput.AngleBox.Focus();
    liveWindow.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    var edited = interactive.Document.Elements.Single();
    Assert(edited.A == rectangle.A); Near(edited.B.X - edited.A.X, 24.6); Near(edited.B.Y - edited.A.Y, 13.2);
    interactive.Undo(); Assert(interactive.Document.Elements.Single() == rectangle);
});
Check("Circle diameter editing keeps its centre and is undoable", () =>
{
    var circle = new DrawingElement(Guid.NewGuid(), ElementKind.Circle, new(40, 50), new(46, 50));
    interactive.Load(new() { Elements = [circle] }); interactive.Select(circle, false);
    liveCanvas.EditSelectedGeometry();
    liveCanvas.LineInput.FocusField(true); liveCanvas.LineInput.AngleBox.Text = "31.4";
    liveWindow.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    var edited = interactive.Document.Elements.Single();
    Assert(edited.A == circle.A); Near(edited.LengthMm, 15.7);
    interactive.Undo(); Assert(interactive.Document.Elements.Single() == circle);
});
Check("Escape returns every tool to selection and preserves selection", () =>
{
    interactive.Load(new() { Elements = [Line()] });
    interactive.Select(interactive.Document.Elements[0], false); liveCanvas.SetTool(ElementKind.Circle);
    liveWindow.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
    liveWindow.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
    Assert(liveCanvas.Tool is null && interactive.Selection.Count == 1);
});
Check("Junction tool places one persistent node at a crossing", () =>
{
    var a = new DrawingElement(Guid.NewGuid(), ElementKind.Line, new(20, 20), new(100, 100));
    var b = new DrawingElement(Guid.NewGuid(), ElementKind.Line, new(20, 100), new(100, 20));
    interactive.Load(new() { Elements = [a, b] }); liveCanvas.SetTool(ElementKind.Junction);
    Tap(180, 180); Tap(181, 181);
    Assert(interactive.Document.Elements.Count(e => e.Kind == ElementKind.Junction) == 1);
    Assert(interactive.Document.Elements[^1].A == new PointMm(60, 60));
});
Check("Canvas commits and edits text as undoable document operations", () =>
{
    interactive.Load(new()); var request = new TextPlacementRequest(null, new(20, 30), "", 3.5);
    liveCanvas.CommitText(request, "QF1", 4);
    var label = interactive.Document.Elements.Single();
    Assert(label.Kind == ElementKind.Text && label.Text == "QF1"); Near(label.TextHeightMm, 4);
    liveCanvas.CommitText(new(label.Id, label.A, label.Text!, label.TextHeightMm), "QF2", 5);
    Assert(interactive.Document.Elements.Single().Text == "QF2"); interactive.Undo();
    Assert(interactive.Document.Elements.Single().Text == "QF1");
    interactive.Select(interactive.Document.Elements.Single(), false); interactive.RotateSelection90();
    using var frame = liveWindow.CaptureRenderedFrame() ?? throw new Exception("No text render");
    frame.Save(Path.Combine(output, "text-tool.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
});
Check("Pointer tool builds an orthogonal multi-segment wire", () =>
{
    interactive.Load(new()); liveCanvas.SetTool(ElementKind.Wire);
    Tap(60, 60); liveWindow.MouseMove(new(156, 108)); Tap(156, 108);
    liveWindow.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    liveWindow.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    var wire = interactive.Document.Elements.Single();
    Assert(wire.Kind == ElementKind.Wire);
    Assert(wire.Points!.SequenceEqual([new PointMm(10, 10), new(50, 10), new(50, 30)]));
});
Check("Pointer tool creates a free and exact-segment polyline", () =>
{
    interactive.Load(new()); liveCanvas.SetTool(ElementKind.Polyline);
    Tap(60, 60); liveWindow.MouseMove(new(120, 60)); Tap(120, 60);
    liveWindow.MouseMove(new(120, 120)); Tap(120, 120);
    liveWindow.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    var free = interactive.Document.Elements.Single();
    Assert(free.Kind == ElementKind.Polyline && free.Points!.SequenceEqual([new PointMm(10, 10), new(35, 10), new(35, 35)]));

    interactive.Load(new()); liveCanvas.SetTool(ElementKind.Polyline); Tap(60, 60); liveWindow.MouseMove(new(200, 100));
    liveWindow.KeyTextInput("12.5");
    liveWindow.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
    liveWindow.KeyTextInput("25");
    liveWindow.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    liveWindow.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    var exact = interactive.Document.Elements.Single();
    Near((exact.Points![1] - exact.Points[0]).Length, 12.5); Near(Geometry.Angle(exact.Points[1] - exact.Points[0]), 25);
});
Check("Selected polyline vertices drag as one undoable edit", () =>
{
    var polyline = new DrawingElement(Guid.NewGuid(), ElementKind.Polyline, new(10, 10), new(30, 30))
        { Points = [new(10, 10), new(30, 10), new(30, 30)] };
    interactive.Load(new() { Elements = [polyline] }); interactive.Select(polyline, false); liveCanvas.SetTool(null);
    liveWindow.MouseDown(new(60, 60), MouseButton.Left); liveWindow.MouseMove(new(84, 84)); liveWindow.MouseUp(new(84, 84), MouseButton.Left);
    Assert(interactive.Document.Elements[0].Points![0] == new PointMm(20, 20));
    interactive.Undo(); Assert(interactive.Document.Elements[0].Points![0] == new PointMm(10, 10));
});
Check("Split command divides a line at the clicked point", () =>
{
    var line = new DrawingElement(Guid.NewGuid(), ElementKind.Line, new(10, 10), new(50, 10));
    interactive.Load(new() { Elements = [line] }); liveCanvas.BeginSplitSegment(); Tap(108, 60);
    var pieces = interactive.Document.Elements.Where(e => e.Kind == ElementKind.Line).ToArray();
    Assert(pieces.Length == 2); Near(pieces[0].LengthMm, 20); Near(pieces[1].LengthMm, 20);
    interactive.Undo(); Assert(interactive.Document.Elements.Single() == line);
});
Check("Trim and extend commands use two clicked straight segments", () =>
{
    var target = new DrawingElement(Guid.NewGuid(), ElementKind.Line, new(10, 10), new(50, 10));
    var boundary = new DrawingElement(Guid.NewGuid(), ElementKind.Line, new(30, 0), new(30, 20));
    interactive.Load(new() { Elements = [target, boundary] }); liveCanvas.BeginTrim();
    Tap(72, 60); Tap(108, 72);
    Assert(interactive.Document.Elements[0].A == new PointMm(30, 10));

    var shortLine = target with { B = new(20, 10) };
    interactive.Load(new() { Elements = [shortLine, boundary] }); liveCanvas.BeginExtend();
    Tap(82, 60); Tap(108, 72);
    Assert(interactive.Document.Elements[0].B == new PointMm(30, 10));
});
Check("Pointer tool creates a three-point arc and stores it in project format", () =>
{
    interactive.Load(new()); liveCanvas.SetTool(ElementKind.Arc);
    Tap(60, 108); Tap(108, 60); Tap(156, 108);
    var arc = interactive.Document.Elements.Single();
    Assert(arc.Kind == ElementKind.Arc); Near(arc.LengthMm, 20); Near(Math.Abs(arc.ArcSweepDegrees), 180);
    var path = Path.Combine(output, "arc-polyline.ecad"); ProjectFile.Save(path, interactive.Document);
    var reopened = ProjectFile.Open(path); Assert(reopened.SchemaVersion == DocumentFormat.Current && reopened.Elements.Single().Kind == ElementKind.Arc);
    using var frame = liveWindow.CaptureRenderedFrame() ?? throw new Exception("No arc render");
    frame.Save(Path.Combine(output, "arc-tool.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
});
Check("Arc supports exact radius and sweep through the shared input", () =>
{
    interactive.Load(new()); liveCanvas.SetTool(ElementKind.Arc); Tap(60, 60); liveWindow.MouseMove(new(140, 60));
    liveWindow.KeyTextInput("12.5");
    liveWindow.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
    liveWindow.KeyTextInput("-135");
    liveWindow.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    var arc = interactive.Document.Elements.Single();
    Assert(arc.Kind == ElementKind.Arc && arc.A == new PointMm(10, 10));
    Near(arc.LengthMm, 12.5); Near(arc.ArcSweepDegrees, -135);
});
Check("Wire supports consecutive exact segments at arbitrary angles", () =>
{
    interactive.Load(new()); liveCanvas.SetAngleSnap(15); liveCanvas.SetTool(ElementKind.Wire); Tap(60, 60); liveWindow.MouseMove(new(240, 60));
    liveWindow.KeyTextInput("12.3");
    liveWindow.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
    liveWindow.KeyTextInput("0");
    liveWindow.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    Assert(interactive.Document.Elements.Length == 0 && liveCanvas.IsDrawing);
    liveWindow.KeyTextInput("7.2");
    liveWindow.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
    liveWindow.KeyTextInput("37");
    liveWindow.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    liveWindow.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    var exactWire = interactive.Document.Elements.Single();
    var exactPoints = exactWire.Points ?? throw new Exception("Wire points missing");
    Assert(exactWire.Kind == ElementKind.Wire && exactPoints.Length == 3);
    Assert(exactPoints[0] == new PointMm(10, 10)); Near(exactPoints[1].X, 22.3); Near(exactPoints[1].Y, 10);
    var expected = Geometry.Polar(exactPoints[1], 7.2, 37);
    Near(exactPoints[2].X, expected.X); Near(exactPoints[2].Y, expected.Y);
    liveCanvas.SetAngleSnap(0);
});
Check("Pointer places a selected IEC symbol and renders its contacts", () =>
{
    liveCanvas.ActiveSymbolKey = "IEC_NO_CONTACT"; liveCanvas.SetTool(ElementKind.Symbol); Tap(228, 156);
    var symbol = interactive.Document.Elements.Single(e => e.Kind == ElementKind.Symbol);
    var label = interactive.Document.Elements.Single(e => e.LinkedElementId == symbol.Id);
    Assert(symbol.Kind == ElementKind.Symbol && symbol.SymbolKey == "IEC_NO_CONTACT" && symbol.DeviceTag == "K1");
    liveCanvas.CommitText(new(label.Id, label.A, label.Text!, label.TextHeightMm), "K17", 5);
    Assert(interactive.Document.Elements.Single(e => e.Id == symbol.Id).DeviceTag == "K17");
    Near(interactive.Document.Elements.Single(e => e.Id == label.Id).TextHeightMm, 5);
    interactive.Select(interactive.Document.Elements.Single(e => e.Id == label.Id), false);
    interactive.Move(new(8, 3)); Assert(interactive.Document.Elements.Single(e => e.Id == symbol.Id).A == symbol.A);
    interactive.Undo(); interactive.Select(interactive.Document.Elements.Single(e => e.Id == symbol.Id), false);
    interactive.Move(new(4, 6));
    Assert(interactive.Document.Elements.Single(e => e.Id == label.Id).A == label.A + new PointMm(4, 6));
    interactive.Undo();
    using var frame = liveWindow.CaptureRenderedFrame() ?? throw new Exception("No symbol render");
    frame.Save(Path.Combine(output, "wire-and-symbol.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
});
Check("Built-in IEC and ANSI symbol set renders", () =>
{
    Assert(SymbolLibrary.Get("IEC_NO_CONTACT").Name.Contains("NO", StringComparison.Ordinal));
    Assert(SymbolLibrary.Get("IEC_NC_CONTACT").Name.Contains("NC", StringComparison.Ordinal));
    Assert(!SymbolLibrary.Get("IEC_NO_CONTACT").Name.Contains("НВ", StringComparison.Ordinal));
    var motorPins = SymbolLibrary.Get("IEC_MOTOR_3P").Pins;
    Assert(motorPins.All(pin => pin.X == -12.5) && motorPins.Select(pin => pin.Y).SequenceEqual([-5d, 0d, 5d]));
    var elements = SymbolLibrary.All.Select((definition, index) =>
    {
        var p = new PointMm(25 + index % 4 * 45, 55 + index / 4 * 35);
        return new DrawingElement(Guid.NewGuid(), ElementKind.Symbol, p, p)
        { SymbolKey = definition.Key, DeviceTag = definition.Prefix + (index + 1) };
    }).ToArray();
    interactive.Load(new() { Elements = elements });
    using var frame = liveWindow.CaptureRenderedFrame() ?? throw new Exception("No library render");
    frame.Save(Path.Combine(output, "symbol-library.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
});
Check("Pointer frame selects multiple objects for grouping", () =>
{
    interactive.Load(new() { Elements = [Line(10) with { A = new(10, 10), B = new(20, 10) }, Line(20) with { A = new(10, 20), B = new(20, 20) }] });
    liveCanvas.SetTool(null);
    liveWindow.MouseDown(new(50, 50), MouseButton.Left); liveWindow.MouseMove(new(95, 95));
    using (var frame = liveWindow.CaptureRenderedFrame()!)
        frame.Save(Path.Combine(output, "selection-frame.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    liveWindow.MouseUp(new(95, 95), MouseButton.Left);
    Assert(interactive.Selection.Count == 2); interactive.Group();
    Assert(interactive.Document.Elements[0].GroupId == interactive.Document.Elements[1].GroupId);
});
Check("Pointer picks endpoints and places an associative dimension", () =>
{
    var source = new DrawingElement(Guid.NewGuid(), ElementKind.Line, new(30, 30), new(100, 30));
    interactive.Load(new() { Elements = [source] }); liveCanvas.SetTool(ElementKind.Dimension);
    Tap(108, 108); Tap(276, 108); liveWindow.MouseMove(new(200, 180)); Tap(200, 180);
    Assert(interactive.Document.Elements.Length == 2);
    var dim = interactive.Document.Elements[1]; Assert(dim.StartReference?.ElementId == source.Id); Near(dim.LengthMm, 70);
    interactive.Select(source, false); liveCanvas.EditSelectedLine();
    liveWindow.KeyTextInput("90"); liveWindow.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    Near(interactive.Document.Elements[1].LengthMm, 90);
    using (var frame = liveWindow.CaptureRenderedFrame()!)
        frame.Save(Path.Combine(output, "associative-dimension.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
});
Check("Pointer dimensions between edges use perpendicular distance", () =>
{
    var a = new DrawingElement(Guid.NewGuid(), ElementKind.Line, new(20, 20), new(100, 20));
    var b = a with { Id = Guid.NewGuid(), A = new(20, 50), B = new(100, 50) };
    interactive.Load(new() { Elements = [a, b] }); liveCanvas.SetTool(ElementKind.Dimension);
    Tap(180, 84); Tap(220, 156); liveWindow.MouseMove(new(110, 130)); Tap(110, 130);
    var dim = interactive.Document.Elements[2]; Near(dim.LengthMm, 30);
    Assert(dim.StartReference?.Kind == ReferenceKind.Edge && dim.EndReference?.Kind == ReferenceKind.Edge);
    interactive.Select(b, false); interactive.Move(new(15, 5)); Near(interactive.Document.Elements[2].LengthMm, 35);
});
Check("Single line dimension via Enter and cancelled frame preserve selection", () =>
{
    var source = new DrawingElement(Guid.NewGuid(), ElementKind.Line, new(20, 20), new(100, 20));
    interactive.Load(new() { Elements = [source] }); liveCanvas.SetTool(ElementKind.Dimension);
    Tap(180, 84); liveWindow.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    liveWindow.MouseMove(new(180, 150)); Tap(180, 150);
    Near(interactive.Document.Elements[1].LengthMm, 80);
    liveCanvas.SetTool(null); interactive.Select(source, false);
    liveWindow.MouseDown(new(45, 45), MouseButton.Left); liveWindow.MouseMove(new(350, 200));
    liveWindow.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Escape, RawInputModifiers.None);
    liveWindow.MouseUp(new(350, 200), MouseButton.Left); Assert(interactive.Selection.SetEquals([source.Id]));
});
Check("Geometry snap uses exact off-grid endpoints", () =>
{
    var source = Line() with { B = new(12.3, 0) };
    var pick = AssociativeDimensions.Pick([source], new(12.4, .1), .5);
    Assert(pick.Reference?.Index == 1); Near(pick.Point.X, 12.3);
});
Check("Property panel edits geometry, name and path type as one undoable action", () =>
{
    var source = new DrawingElement(Guid.NewGuid(), ElementKind.Line, new(10, 20), new(20, 20));
    interactive.Load(new() { Elements = [source] }); interactive.Select(source, false);
    var properties = new PropertyPanel(interactive, _ => { });
    var propertyWindow = new Window { Width = 320, Height = 700, Content = properties };
    propertyWindow.Show(); Dispatcher.UIThread.RunJobs();
    TextBox Box(string name) => properties.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == name);
    Box("PropertyName").Text = "Живлення двигуна";
    Box("PropertyX").Text = "25"; Box("PropertyY").Text = "30";
    Box("PropertyLength").Text = "40"; Box("PropertyAngle").Text = "30";
    var kind = properties.GetVisualDescendants().OfType<ComboBox>().Single(x => x.Name == "PropertyPathKind");
    kind.SelectedIndex = 0;
    var apply = properties.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "ApplyProperties");
    apply.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    var edited = interactive.Document.Elements.Single();
    Assert(edited.Kind == ElementKind.Wire && edited.Name == "Живлення двигуна" && edited.A == new PointMm(25, 30));
    Near(edited.LengthMm, 40); Near(Geometry.Angle(edited.B - edited.A), 30);
    interactive.Undo(); Assert(interactive.Document.Elements.Single() == source);
    propertyWindow.Close();
});
Check("Inspector converts a wire to graphics without changing its former net and supports undo", () =>
{
    interactive.Load(new() { Elements = [Wire(new(10, 20), new(30, 20))] });
    var source = interactive.Document.Elements.Single();
    var net = interactive.Document.Nets.Single();
    interactive.Select(source, false);
    var errors = new List<string>();
    var properties = new PropertyPanel(interactive, errors.Add);
    var propertyWindow = new Window { Width = 320, Height = 900, Content = properties };
    propertyWindow.Show(); Dispatcher.UIThread.RunJobs();
    properties.GetVisualDescendants().OfType<ComboBox>().Single(x => x.Name == "PropertyPathKind").SelectedIndex = 1;
    // Net fields no longer apply when changing this wire into graphics.
    properties.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "PropertyNetNumber").Text = "";
    properties.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "ApplyProperties")
        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    var result = interactive.Document.Elements.Single();
    Assert(result.Kind == ElementKind.Line && result.NetId is null && result.Points is null);
    Assert(result.Id == source.Id && result.A == source.A && result.B == source.B);
    Assert(interactive.Document.Nets.Single() == net);
    interactive.Undo(); Assert(interactive.Document.Elements.Single() == source);
    interactive.Redo(); Assert(interactive.Document.Elements.Single().Kind == ElementKind.Line);
    propertyWindow.Close();
});
Check("Property panel turns an associative dimension into a driving constraint", () =>
{
    var source = Line();
    var dimension = new DrawingElement(Guid.NewGuid(), ElementKind.Dimension, source.A, source.B)
    {
        StartReference = new(source.Id, ReferenceKind.Vertex, 0), EndReference = new(source.Id, ReferenceKind.Vertex, 1)
    };
    interactive.Load(new() { Elements = [source, dimension] }); interactive.Select(dimension, false);
    var properties = new PropertyPanel(interactive, _ => { });
    var propertyWindow = new Window { Width = 320, Height = 700, Content = properties };
    propertyWindow.Show(); Dispatcher.UIThread.RunJobs();
    var boxes = properties.GetVisualDescendants().OfType<TextBox>().ToArray();
    boxes.Single(x => x.Name == "PropertyTarget").Text = "27,5";
    properties.GetVisualDescendants().OfType<ComboBox>().Single(x => x.Name == "PropertyDimensionMode").SelectedIndex = 1;
    properties.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "ApplyProperties")
        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Near(interactive.Document.Elements[0].LengthMm, 27.5);
    Assert(interactive.Document.Elements[1].DimensionMode == DimensionMode.Driving);
    propertyWindow.Close();
});
Check("Property panel edits polyline vertices and arc parameters", () =>
{
    var polyline = new DrawingElement(Guid.NewGuid(), ElementKind.Polyline, new(0, 0), new(10, 10))
        { Points = [new(0, 0), new(10, 0), new(10, 10)] };
    interactive.Load(new() { Elements = [polyline] }); interactive.Select(polyline, false);
    var properties = new PropertyPanel(interactive, _ => { });
    var propertyWindow = new Window { Width = 320, Height = 700, Content = properties };
    propertyWindow.Show(); Dispatcher.UIThread.RunJobs();
    properties.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "PropertyVertices").Text = "0; 0\n15,5; 0\n15,5; 8";
    properties.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "ApplyProperties")
        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Near(interactive.Document.Elements.Single().Points![1].X, 15.5);
    propertyWindow.Close();

    var arc = ArcGeometry.FromThreePoints(Guid.NewGuid(), new(0, 0), new(5, -5), new(10, 0));
    interactive.Load(new() { Elements = [arc] }); interactive.Select(arc, false);
    properties = new PropertyPanel(interactive, _ => { }); propertyWindow = new Window { Width = 320, Height = 700, Content = properties };
    propertyWindow.Show(); Dispatcher.UIThread.RunJobs();
    properties.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "PropertyRadius").Text = "8";
    properties.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "PropertySweep").Text = "90";
    properties.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "ApplyProperties")
        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Near(interactive.Document.Elements.Single().LengthMm, 8); Near(interactive.Document.Elements.Single().ArcSweepDegrees, 90);
    propertyWindow.Close();
});
Check("Property panel assigns and displays a device variant", () =>
{
    interactive.Load(ProjectFile.Open(Path.Combine(output, "device-catalog.ecad")));
    var symbol = interactive.Document.Elements.Single(e => e.Kind == ElementKind.Symbol);
    interactive.Select(symbol, false);
    var properties = new PropertyPanel(interactive, _ => { });
    var propertyWindow = new Window { Width = 360, Height = 800, Content = properties };
    propertyWindow.Show(); Dispatcher.UIThread.RunJobs();
    var variant = properties.GetVisualDescendants().OfType<ComboBox>().Single(x => x.Name == "PropertyComponentVariant");
    var physical = properties.GetVisualDescendants().OfType<ComboBox>().Single(x => x.Name == "PropertyPhysicalRepresentation");
    Assert(variant.SelectedIndex == 1 && physical.ItemCount == 2);
    var details = string.Join("\n", properties.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text));
    Assert(details.Contains("Виробник: ABB") && details.Contains("Кількість полюсів: 4P") && details.Contains("2NO+2NC"));
    physical.SelectedIndex = 1;
    properties.GetVisualDescendants().OfType<Button>().Single(x => x.Name == "ApplyProperties")
        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Assert(interactive.Document.Elements.Single(e => e.Id == symbol.Id).PhysicalRepresentationId != symbol.PhysicalRepresentationId);
    propertyWindow.Close();
});
Check("Library editor creates every hierarchy level and searches variants", () =>
{
    var catalogSession = new EditorSession();
    var dialog = new LibraryEditorWindow(catalogSession); dialog.Show(); Dispatcher.UIThread.RunJobs();
    Button NamedButton(string name) => dialog.GetVisualDescendants().OfType<Button>().Single(item => item.Name == name);
    NamedButton("NewLibrary").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    NamedButton("AddLevel1").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    dialog.GetVisualDescendants().OfType<TextBox>().Single(item => item.Name == "TypeConfigurationFields").Text =
        "channels | Кількість каналів | так | 1,2,16 |";
    NamedButton("SaveLibraryItem").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    NamedButton("AddLevel2").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    NamedButton("AddLevel3").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    dialog.GetVisualDescendants().OfType<TextBox>().Single(item => item.Name == "VariantConfiguration").Text = "channels | 16";
    dialog.GetVisualDescendants().OfType<TextBox>().Single(item => item.Name == "VariantParameters").Text = "voltage | Напруга живлення | 24 | V DC";
    dialog.GetVisualDescendants().OfType<TextBox>().Single(item => item.Name == "VariantContacts").Text = "A1 | A1 | Живлення + | Power\nA2 | A2 | Живлення - | Power";
    dialog.GetVisualDescendants().OfType<TextBox>().Single(item => item.Name == "VariantPhysical").Text =
        "DIN-корпус | 22.5 | 100 | 115 | DIN-рейка | 1.25\nПанельний корпус | 30 | 110 | 120 | Панель |";
    NamedButton("SaveLibraryItem").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    var library = catalogSession.Document.ComponentLibraries.Single();
    var created = library.DeviceTypes.Single().Families.Single().Variants.Single();
    Assert(created.Configuration.Single().Value == "16" && created.Contacts.Length == 2 && created.PhysicalRepresentations.Length == 2);
    var search = dialog.GetVisualDescendants().OfType<TextBox>().Single(item => item.Name == "LibrarySearch");
    search.Text = "16"; Dispatcher.UIThread.RunJobs();
    Assert(dialog.GetVisualDescendants().OfType<ListBox>().Single(item => item.Name == "DeviceVariantList").ItemCount == 1);
    using (var frame = dialog.CaptureRenderedFrame() ?? throw new Exception("No rendered library editor"))
        frame.Save(Path.Combine(output, "library-editor.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    NamedButton("CopyLibrary").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Assert(catalogSession.Document.ComponentLibraries.Length == 2);
    search.Text = ""; Dispatcher.UIThread.RunJobs();
    NamedButton("AddExampleLibraries").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    Assert(catalogSession.Document.ComponentLibraries.Length == 4);
    search.Text = "SINAMICS"; Dispatcher.UIThread.RunJobs();
    Assert(dialog.GetVisualDescendants().OfType<ListBox>().Single(item => item.Name == "LibraryList").ItemCount == 1);
    Assert(dialog.GetVisualDescendants().OfType<ListBox>().Single(item => item.Name == "DeviceVariantList").ItemCount == 2);
    using (var frame = dialog.CaptureRenderedFrame() ?? throw new Exception("No rendered example library"))
        frame.Save(Path.Combine(output, "library-examples.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    dialog.Close();
});
liveWindow.Close();
Check("Assignment tab edits a core, preserves tab and refreshes on undo", () =>
{
    var session = new EditorSession();
    var net = new ProjectNet(Guid.NewGuid(), "24V");
    var core = new CableCore(Guid.NewGuid(), "1", CableCoreStatus.Spare);
    session.Load(new() { Nets = [net], Cables = [new(Guid.NewGuid(), "W1", null, 1, null, null, null, null, [core])] });
    var window = new ElectricalProjectWindow(session); window.Show(); Dispatcher.UIThread.RunJobs();
    var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(); tabs.SelectedIndex = 4;
    Dispatcher.UIThread.RunJobs();
    ComboBox NetBox() => window.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "AssignmentNet");
    void ApplyAssignment() => window.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ApplyNetAssignment")
        .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    NetBox().SelectedIndex = 1; ApplyAssignment(); Dispatcher.UIThread.RunJobs();
    Assert(session.Document.Cables[0].Cores[0].NetId == net.Id && tabs.SelectedIndex == 4);
    session.Undo(); Dispatcher.UIThread.RunJobs(); Assert(NetBox().SelectedIndex == 0);
    session.Redo(); Dispatcher.UIThread.RunJobs();
    NetBox().SelectedIndex = 0; ApplyAssignment(); Dispatcher.UIThread.RunJobs();
    Assert(session.Document.Cables[0].Cores[0].NetId is null && session.Document.Cables[0].Cores[0].Status == CableCoreStatus.Spare);
    window.Close();
});
Check("Electrical project navigator renders devices, nets, terminals and cables", () =>
{
    var projectSession = new EditorSession(); projectSession.Load(ExampleElectricalProject.Create());
    var window = new ElectricalProjectWindow(projectSession); window.Show(); Dispatcher.UIThread.RunJobs();
    var text = string.Join("\n", window.GetVisualDescendants().OfType<TextBlock>().Select(item => item.Text));
    Assert(text.Contains("Пристроїв:") && text.Contains("Кіл:") && text.Contains("Клемників:") && text.Contains("Кабелів:"));
    Assert(window.GetVisualDescendants().OfType<TabItem>().Count() == 5);
    using (var frame = window.CaptureRenderedFrame() ?? throw new Exception("No rendered electrical navigator"))
        frame.Save(Path.Combine(output, "electrical-project.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    window.Close();
});
Check("Main window refreshes and selects a newly created custom symbol", () =>
{
    var main = new MainWindow(); main.Show();
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
    var mainSession = (EditorSession)(typeof(MainWindow).GetField("session", flags)?.GetValue(main)
        ?? throw new Exception("Main session field unavailable"));
    var picker = (ComboBox)(typeof(MainWindow).GetField("symbolPicker", flags)?.GetValue(main)
        ?? throw new Exception("Symbol picker field unavailable"));
    var mainCanvas = (DrawingCanvas)(typeof(MainWindow).GetField("canvas", flags)?.GetValue(main)
        ?? throw new Exception("Main canvas field unavailable"));
    var body = new DrawingElement(Guid.NewGuid(), ElementKind.Rectangle, new(10, 10), new(30, 20));
    var pin = new DrawingElement(Guid.NewGuid(), ElementKind.Junction, new(10, 15), new(10, 15));
    mainSession.Apply([body, pin]); mainSession.Selection.UnionWith([body.Id, pin.Id]);
    var definition = mainSession.CreateCustomSymbol("Тестовий символ", "TS");
    mainCanvas.ActiveSymbolKey = definition.Key;
    Dispatcher.UIThread.RunJobs();
    picker.IsDropDownOpen = true; Dispatcher.UIThread.RunJobs();
    Assert(picker.ItemsSource!.Cast<SymbolDefinition>().Any(item => item.Key == definition.Key));
    Assert((picker.SelectedItem as SymbolDefinition)?.Key == definition.Key);
    typeof(MainWindow).GetField("allowClose", flags)?.SetValue(main, true);
    main.Close();
});
Check("Main window dirty marker follows saved content across navigation and undo", () =>
{
    var window = new MainWindow(); window.Show();
    var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
    var session = (EditorSession)typeof(MainWindow).GetField("session", flags)!.GetValue(window)!;
    var first = session.Document.ActivePageId; session.AddPage("Second");
    typeof(MainWindow).GetField("savedRevision", flags)!.SetValue(window, session.ContentRevision);
    session.SwitchPage(first); Dispatcher.UIThread.RunJobs(); Assert(!window.Title!.StartsWith("*"));
    session.Add(Line()); Dispatcher.UIThread.RunJobs(); Assert(window.Title!.StartsWith("*"));
    session.Undo(); Dispatcher.UIThread.RunJobs(); Assert(!window.Title!.StartsWith("*"));
    typeof(MainWindow).GetField("allowClose", flags)!.SetValue(window, true); window.Close();
});
Check("Main window renders with Ukrainian controls", () =>
{
    var main = new MainWindow(); main.Show();
    Assert(main.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "ApplicationVersion").Text!.Contains($"ECAD {AppInfo.Version}"));
    Assert(AppInfo.Version == typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().Single().InformationalVersion.Split('+')[0]);
    var labels = main.GetVisualDescendants().OfType<TextBlock>().Select(x => x.Text).ToHashSet();
    Assert(labels.Contains("Файл") && labels.Contains("Аркуші") && labels.Contains("Редагування") && labels.Contains("Інструменти") && labels.Contains("Параметри побудови"));
    Assert(main.GetVisualDescendants().OfType<Button>().Count(button => ToolTip.GetTip(button) is string) >= 15);
    main.MouseDown(new(500, 450), MouseButton.Left);
    main.MouseUp(new(500, 450), MouseButton.Left);
    using var frame = main.CaptureRenderedFrame() ?? throw new Exception("No rendered main window");
    frame.Save(Path.Combine(output, "main-window.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    main.Close();
});
Check("Page settings show readable horizontal and vertical zone values", () =>
{
    var main = new MainWindow(); main.Show(); Dispatcher.UIThread.RunJobs();
    var edit = main.GetVisualDescendants().OfType<Button>()
        .Single(button => Equals(ToolTip.GetTip(button), "Параметри аркуша"));
    edit.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Dispatcher.UIThread.RunJobs();
    var dialog = main.OwnedWindows.Single(window => window.Title == "Параметри аркуша");
    var horizontal = dialog.GetVisualDescendants().OfType<NumericUpDown>().Single(control => control.Name == "HorizontalZones");
    var vertical = dialog.GetVisualDescendants().OfType<NumericUpDown>().Single(control => control.Name == "VerticalZones");
    Assert(horizontal.Value == 8 && vertical.Value == 6 && horizontal.Width >= 120 && vertical.Width >= 120);
    using (var frame = dialog.CaptureRenderedFrame() ?? throw new Exception("No rendered page settings"))
        frame.Save(Path.Combine(output, "page-settings.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    dialog.Close(null); Dispatcher.UIThread.RunJobs();
    typeof(MainWindow).GetField("allowClose", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.SetValue(main, true);
    main.Close();
});
Console.WriteLine($"{passed} checks passed. Rendered previews: {output}");
