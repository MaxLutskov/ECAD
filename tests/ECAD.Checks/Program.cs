using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using ECAD.Core;
using ECAD.Desktop;

var passed = 0;
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
Check("Orthogonal wires validate, move and rotate with all vertices", () =>
{
    var wire = Wire(new(0, 0), new(10, 0), new(10, 20));
    new DrawingDocument { Elements = [wire] }.Validate();
    var moved = wire.Move(new(5, 7)); Assert(moved.Points![1] == new PointMm(15, 7));
    var s = new EditorSession(); s.Add(wire); s.Select(wire, false); s.RotateSelection90();
    Assert(s.Document.Elements[0].Points!.SequenceEqual([new PointMm(15, 5), new(15, 15), new(-5, 15)]));
    Reject(() => new DrawingDocument { Elements = [Wire(new(0, 0), new(10, 10))] }.Validate());
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
Check("Application errors are persisted with context and exception details", () =>
{
    AppLog.Write("Створення тестового символу", new InvalidOperationException("Тестова помилка"));
    var content = File.ReadAllText(AppLog.LogPath);
    Assert(content.Contains("Створення тестового символу", StringComparison.Ordinal));
    Assert(content.Contains("InvalidOperationException", StringComparison.Ordinal));
    Assert(content.Contains("Тестова помилка", StringComparison.Ordinal));
});
Check("ZIP round-trip preserves geometry and groups, overwrite remains readable", () =>
{
    var group = Guid.NewGuid();
    var doc = new DrawingDocument { Elements = [Line() with { GroupId = group }, Line(5) with { GroupId = group }] };
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
    Assert(read.SchemaVersion == 6 && label.Text == "M1" && label.Kind == ElementKind.Text);
});
Check("Unsupported archive version rejected", () =>
{
    var path = Path.Combine(output, "future.ecad");
    using (var file = File.Create(path))
    using (var zip = new ZipArchive(file, ZipArchiveMode.Create))
    using (var writer = new StreamWriter(zip.CreateEntry("project.json").Open())) writer.Write("{\"SchemaVersion\":999}");
    Reject(() => ProjectFile.Open(path));
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
    var read = ProjectFile.Open(path); Assert(read.SchemaVersion == 6);
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
    var read = ProjectFile.Open(path); Assert(read.SchemaVersion == 6); Near(read.Elements[0].LengthMm, 10);
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
Check("Wire supports consecutive exact length and orthogonal angle segments", () =>
{
    interactive.Load(new()); liveCanvas.SetTool(ElementKind.Wire); Tap(60, 60); liveWindow.MouseMove(new(240, 60));
    liveWindow.KeyTextInput("12.3");
    liveWindow.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
    liveWindow.KeyTextInput("0");
    liveWindow.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    Assert(interactive.Document.Elements.Length == 0 && liveCanvas.IsDrawing);
    liveWindow.KeyTextInput("7.2");
    liveWindow.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Tab, RawInputModifiers.None);
    liveWindow.KeyTextInput("90");
    liveWindow.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    liveWindow.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None); liveWindow.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
    var exactWire = interactive.Document.Elements.Single();
    var exactPoints = exactWire.Points ?? throw new Exception("Wire points missing");
    Assert(exactWire.Kind == ElementKind.Wire && exactPoints.Length == 3);
    Assert(exactPoints[0] == new PointMm(10, 10)); Near(exactPoints[1].X, 22.3); Near(exactPoints[1].Y, 10);
    Near(exactPoints[2].X, 22.3); Near(exactPoints[2].Y, 2.8);
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
liveWindow.Close();
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
Check("Main window renders with Ukrainian controls", () =>
{
    var main = new MainWindow(); main.Show();
    main.MouseDown(new(500, 450), MouseButton.Left);
    main.MouseUp(new(500, 450), MouseButton.Left);
    using var frame = main.CaptureRenderedFrame() ?? throw new Exception("No rendered main window");
    frame.Save(Path.Combine(output, "main-window.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    main.Close();
});
Console.WriteLine($"{passed} checks passed. Rendered previews: {output}");
