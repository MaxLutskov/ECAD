using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ECAD.Core;
using ECAD.Desktop;

namespace ECAD.Checks;

internal static class WorkspaceChecks
{
    public static void Run(Action<string, Action> check, string output)
    {
        check("Session separates content, selection and navigation notifications", () =>
        {
            var session = new EditorSession(); var content = 0; var selection = 0; var navigation = 0;
            session.ContentChanged += () => content++; session.SelectionChanged += () => selection++; session.ActivePageChanged += () => navigation++;
            var line = new DrawingElement(Guid.NewGuid(), ElementKind.Line, new(0, 0), new(10, 0));
            session.Add(line); Require(content == 1 && selection == 0 && navigation == 0);
            session.Select(line, false); Require(content == 1 && selection == 1);
            session.SelectBox(new(-1, -1, 20, 20), true, false); Require(content == 1 && selection == 1);
            var first = session.Document.ActivePageId; session.AddPage(); var before = content;
            session.SwitchPage(first); Require(content == before && navigation == 2);
        });
        check("Cancelled file operations preserve the saved document", () =>
        {
            var path = Path.Combine(output, "cancelled-save.ecad"); var session = new EditorSession(); ProjectFile.Save(path, session.Document);
            var original = File.ReadAllBytes(path);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { ProjectFile.Save(path, session.Document, cancellation.Token); throw new Exception("Save was not cancelled"); } catch (OperationCanceledException) { }
            try { ProjectFile.Open(path, cancellation.Token); throw new Exception("Open was not cancelled"); } catch (OperationCanceledException) { }
            Require(original.SequenceEqual(File.ReadAllBytes(path))); ProjectFile.Open(path);
        });
        check("Shared inspector movement preserves associative dimension anchors", () =>
        {
            var session = new EditorSession();
            var line = new DrawingElement(Guid.NewGuid(), ElementKind.Line, new(0, 0), new(20, 0)); session.Add(line);
            var dim = new DrawingElement(Guid.NewGuid(), ElementKind.Dimension, line.A, line.B)
            { StartReference = new(line.Id, ReferenceKind.Vertex, 0), EndReference = new(line.Id, ReferenceKind.Vertex, 1), DimensionOffset = 7 };
            session.Add(dim); session.Select(dim, false); var before = session.Document;
            session.ApplyDocument(SelectionProperties.Apply(before, session.Selection, false, null, new(0, 5), null));
            var updated = session.Document.Elements.Single(e => e.Id == dim.Id);
            Require(updated.DimensionOffset == 12 && updated.A == line.A && updated.B == line.B);
            session.Undo(); Require(ReferenceEquals(before, session.Document));
        });
        check("Page tabs navigate and library variant insertion preserves catalog identity", () =>
        {
            var window = new MainWindow(); window.Show(); var session = Session(window); session.Load(ExampleElectricalProject.Create()); Dispatcher.UIThread.RunJobs();
            var tabs = window.GetVisualDescendants().OfType<ListBox>().Single(x => x.Name == "PageTabs");
            var page = session.Document.Pages.First(p => p.Id != session.Document.ActivePageId);
            var revision = session.ContentRevision; tabs.SelectedItem = page; Require(session.Document.ActivePageId == page.Id && session.ContentRevision == revision);
            window.Panels.Show("library"); Dispatcher.UIThread.RunJobs();
            var entries = window.GetVisualDescendants().OfType<ListBox>().Single(x => x.Name == "LibrarySymbols");
            var entry = entries.Items.OfType<MainWindow.LibraryEntry>().First(x => x.VariantId is not null); entries.SelectedItem = entry; entries.Focus(); Press(window, PhysicalKey.Enter);
            Require(Canvas(window).ActiveVariantId == entry.VariantId && Canvas(window).Tool == ElementKind.Symbol);
            var before = session.Document.Elements.Select(e => e.Id).ToHashSet();
            var deviceIds = session.Document.Devices.Select(device => device.Id).ToHashSet();
            typeof(DrawingCanvas).GetMethod("AddSymbol", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Canvas(window), [new PointMm(100, 100)]);
            var symbol = session.Document.Elements.Single(e => !before.Contains(e.Id) && e.Kind == ElementKind.Symbol);
            Require(symbol.ComponentVariantId == entry.VariantId && symbol.PhysicalRepresentationId is not null && !deviceIds.Contains(symbol.DeviceId!.Value)); session.Undo(); Require(session.ContentRevision == revision); Close(window);
        });
        check("Workspace commands share availability and shortcuts preserve text editing", () =>
        {
            var window = new MainWindow(); window.Show(); Dispatcher.UIThread.RunJobs();
            var session = Session(window); var canvas = Canvas(window);
            Require(!window.Commands["edit.undo"].CanExecute(null));
            window.Commands["edit.undo"].Execute(null); Require(!session.CanUndo);
            canvas.Focus(); Press(window, PhysicalKey.C); Require(canvas.Tool == ElementKind.Circle);
            Press(window, PhysicalKey.Escape); Require(canvas.Tool is null);
            var line = new DrawingElement(Guid.NewGuid(), ElementKind.Line, new(10, 10), new(20, 10)); session.Add(line); session.Select(line, false);
            Require(window.Commands["edit.copy"].CanExecute(null));
            window.Commands["edit.copy"].Execute(null); Require(window.Commands["edit.paste"].CanExecute(null));
            Dispatcher.UIThread.RunJobs();
            var input = window.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "PropertyName");
            input.Focus(); input.Text = "LCRDPA"; Press(window, PhysicalKey.L); Require(canvas.Tool is null && input.IsFocused);
            window.Commands["edit.undo"].Execute(null); Require(!session.Document.Elements.Any()); Close(window);
        });
        check("Multiple properties apply atomically, reject invalid values and keep inspector focus on no-op", () =>
        {
            var session = new EditorSession();
            var a = new DrawingElement(Guid.NewGuid(), ElementKind.Text, new(10, 10), new(10, 10)) { Text = "A", Name = "first", TextHeightMm = 3 };
            var b = new DrawingElement(Guid.NewGuid(), ElementKind.Text, new(20, 20), new(20, 20)) { Text = "B", Name = "second", TextHeightMm = 5 };
            session.Add(a); session.Add(b); session.Select(a, false); session.Select(b, true);
            using var panel = new PropertyPanel(session, _ => { });
            var window = new Window { Width = 360, Height = 600, Content = panel }; window.Show(); Dispatcher.UIThread.RunJobs();
            TextBox Field(string id) => panel.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == id);
            var name = Field("MultipleName"); Require(name.PlaceholderText == "Різні значення"); name.Focus();
            session.SelectBox(new(0, 0, 100, 100), true, false); Dispatcher.UIThread.RunJobs(); Require(ReferenceEquals(name, Field("MultipleName")) && name.IsFocused);
            var before = session.Document; name.Text = "Shared"; Field("MultipleX").Text = "2,5"; Field("MultipleHeight").Text = "-1"; Dispatcher.UIThread.RunJobs();
            void Apply() => panel.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ApplyMultipleProperties").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Apply(); Require(ReferenceEquals(before, session.Document));
            Field("MultipleHeight").Text = "4"; Apply();
            Require(session.Document.Elements.All(e => e.Name == "Shared" && e.TextHeightMm == 4)); Require(session.Document.Elements[0].A.X == 12.5);
            session.Undo(); Require(ReferenceEquals(before, session.Document)); window.Close();
        });
        check("Dock panels move, hide, restore and reset without duplicating controls", () =>
        {
            WorkspacePanels.SettingsDirectoryOverride = Path.Combine(output, "dock-persistence");
            var first = new MainWindow(); first.Show(); Dispatcher.UIThread.RunJobs();
            first.Panels.Move("library", "right"); first.Panels.Toggle("erc"); first.Panels.Save(); Close(first);
            var second = new MainWindow(); second.Show(); Dispatcher.UIThread.RunJobs();
            Require(second.Panels.Placements.Single(p => p.Id == "library").Side == "right");
            Require(!second.Panels.Placements.Single(p => p.Id == "erc").Visible);
            second.Panels.Show("library"); Require(second.GetVisualDescendants().OfType<TextBox>().Count(box => box.Name == "LibrarySearch") == 1);
            second.Panels.Reset(); Require(second.Panels.Placements.Single(p => p.Id == "library").Side == "left"); Close(second);
            File.WriteAllText(Path.Combine(WorkspacePanels.SettingsDirectoryOverride!, "workspace.json"), "{\"Version\":1,\"Left\":200,\"Right\":300,\"Bottom\":180,\"Panels\":[{\"Id\":null,\"Side\":null}]}");
            var malformed = new MainWindow(); malformed.Show(); Dispatcher.UIThread.RunJobs(); Require(malformed.Panels.Placements.Count == 5); Close(malformed);
            WorkspacePanels.SettingsDirectoryOverride = Path.Combine(output, "workspace-other");
        });
        check("ERC panel selects an element on its own page without changing the document", () =>
        {
            var window = new MainWindow(); window.Show(); var session = Session(window); session.Load(ExampleElectricalProject.Create());
            var revision = session.ContentRevision;
            window.Commands["electrical.check"].Execute(null);
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(x => x.Name == "ErcIssues");
            var timer = System.Diagnostics.Stopwatch.StartNew();
            while (list.ItemCount == 0 && timer.Elapsed < TimeSpan.FromSeconds(10)) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
            Require(list.ItemCount > 0);
            var issue = list.Items.OfType<ElectricalIssue>().First(i => session.Document.Pages.Any(p => p.Elements.Any(e => e.Id == i.ElementId)));
            list.SelectedItem = issue; Dispatcher.UIThread.RunJobs();
            Require(session.Selection.Contains(issue.ElementId) && session.Document.Elements.Any(e => e.Id == issue.ElementId));
            Require(revision == session.ContentRevision); Close(window);
        });
        check("Command palette filters by stable ID and Escape dismisses without an edit", () =>
        {
            var window = new MainWindow(); window.Show(); var session = Session(window); var revision = session.ContentRevision;
            window.Commands["view.palette"].Execute(null); Dispatcher.UIThread.RunJobs();
            var palette = window.OwnedWindows.Single();
            palette.GetVisualDescendants().OfType<TextBox>().Single(x => x.Name == "CommandSearch").Text = "tool.circle";
            Dispatcher.UIThread.RunJobs(); var list = palette.GetVisualDescendants().OfType<ListBox>().Single(x => x.Name == "CommandResults");
            Require(list.ItemCount == 1 && ((WorkspaceCommand)list.Items[0]!).Id == "tool.circle");
            palette.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None); Dispatcher.UIThread.RunJobs(); Require(session.ContentRevision == revision && !window.OwnedWindows.Any()); Close(window);
        });
        check("Workspace stays usable at narrow width and light/dark themes", () =>
        {
            var window = new MainWindow { Width = 900, Height = 650 }; window.Show(); Dispatcher.UIThread.RunJobs();
            using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "workspace-narrow.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            if (Canvas(window).Bounds.Width < 280 || Canvas(window).Bounds.Height <= 150) throw new Exception($"Narrow canvas: {Canvas(window).Bounds}");
            foreach (var scaling in new[] { 1d, 1.5d, 2d })
            {
                window.SetRenderScaling(scaling); Dispatcher.UIThread.RunJobs();
                Require(Canvas(window).Bounds.Width >= 280);
                var step = window.GetVisualDescendants().OfType<NumericUpDown>().Single(x => x.Name == "GridStep");
                Require(step.Bounds.Width >= 140 && step.Value == 2.5m);
                using var scaled = window.CaptureRenderedFrame()!;
                scaled.Save(Path.Combine(output, $"workspace-scale-{scaling * 100:0}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            window.SetRenderScaling(1); Dispatcher.UIThread.RunJobs();
            window.Commands["view.theme"].Execute(null); Dispatcher.UIThread.RunJobs();
            using (var frame = window.CaptureRenderedFrame()!) frame.Save(Path.Combine(output, "workspace-dark.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            Close(window);
        });
    }
    private static EditorSession Session(MainWindow window) => (EditorSession)typeof(MainWindow).GetField("session", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
    private static DrawingCanvas Canvas(MainWindow window) => window.GetVisualDescendants().OfType<DrawingCanvas>().Single();
    private static void Press(Window window, PhysicalKey key) { window.KeyPressQwerty(key, RawInputModifiers.None); window.KeyReleaseQwerty(key, RawInputModifiers.None); Dispatcher.UIThread.RunJobs(); }
    private static void Close(MainWindow window) { typeof(MainWindow).GetField("allowClose", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(window, true); window.Close(); }
    private static void Require(bool value) { if (!value) throw new Exception("Workspace regression failed"); }
}
