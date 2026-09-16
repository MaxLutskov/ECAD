using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ECAD.Core;

namespace ECAD.Desktop;

public sealed partial class MainWindow
{
    public WorkspaceCommands Commands { get; } = new();
    public WorkspacePanels Panels { get; private set; } = null!;
    private readonly ListBox projectPages = new() { Name = "ProjectPages" };
    private readonly ListBox pageTabs = new() { Name = "PageTabs", MinWidth = 0,
        ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel { Orientation = Orientation.Horizontal }) };
    private readonly ListBox librarySymbols = new() { Name = "LibrarySymbols", Focusable = true };
    private readonly ListBox electricalObjects = new() { Name = "ElectricalObjects" };
    private readonly ListBox ercIssues = new() { Name = "ErcIssues" };
    private readonly TextBox librarySearch = new() { Name = "LibrarySearch", PlaceholderText = "Пошук символу / NO / NC" };
    private readonly TextBlock ercSummary = new() { Text = "Запусти перевірку схеми.", TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock contextInfo = new() { FontSize = 12, Margin = new(8, 4) };
    private readonly TextBlock notificationText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border notification = new() { IsVisible = false, BorderBrush = Brushes.DarkOrange, BorderThickness = new(1), Margin = new(6), Padding = new(8) };
    private void ShowNotice(string text) { notificationText.Text = text; notification.IsVisible = true; }
    private readonly TextBlock libraryEmpty = new() { Text = "Нічого не знайдено. Зміни пошук або відкрий редактор бібліотек.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8) };
    private bool refreshingWorkspace;
    private bool fileOperationBusy;
    private CancellationTokenSource? backgroundCancellation;
    private async Task<T> RunBackground<T>(Func<CancellationToken, T> work, bool discardResultOnCancel = true)
    {
        using var cancellation = new CancellationTokenSource();
        backgroundCancellation = cancellation; Commands.Refresh();
        try
        {
            var result = await Task.Run(() => work(cancellation.Token), cancellation.Token);
            if (discardResultOnCancel) cancellation.Token.ThrowIfCancellationRequested();
            return result;
        }
        finally { backgroundCancellation = null; Commands.Refresh(); }
    }
    private Guid workspaceRevision;
    private sealed record NavigationItem(string Text, Guid PageId, Guid ElementId);
    public sealed record LibraryEntry(string Label, string SymbolKey, Guid? VariantId);

    private void BuildWorkspace()
    {
        WorkspaceTheme.Apply(this);
        RegisterCommands();
        canvas.Commands = Commands;
        var root = new DockPanel { LastChildFill = true };
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var menu = new Menu { Name = "WorkspaceMenu" };
        foreach (var category in new[] { "Файл", "Редагування", "Вигляд", "Креслення", "Електрика", "Бібліотеки", "Довідка" })
        {
            var section = new MenuItem { Header = category };
            foreach (var command in Commands.All.Where(c => c.Category == category)) section.Items.Add(CommandMenu(command));
            menu.Items.Add(section);
        }
        header.Children.Add(menu);
        var toolbar = new WrapPanel { Margin = new(8, 2), Name = "MainToolbar" };
        foreach (var id in new[] { "file.new", "file.open", "file.save", "edit.undo", "edit.redo", "edit.copy", "edit.paste", "view.fit", "electrical.check", "view.palette", "work.cancel" }) toolbar.Children.Add(CommandButton(id));
        toolbar.Children.Add(new TextBlock { Name = "ApplicationVersion", Text = $"ECAD {AppInfo.Version}", Margin = new(16, 0), VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(toolbar);
        var toolbox = new StackPanel { Width = 44, Spacing = 3, Margin = new(3), Name = "DrawingToolbox" };
        foreach (var id in new[] { "tool.select", "tool.path", "tool.rectangle", "tool.circle", "tool.polyline", "tool.arc", "tool.dimension", "tool.junction", "tool.text", "tool.symbol" }) toolbox.Children.Add(CommandButton(id));
        var toolScroll = new ScrollViewer { Content = toolbox, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        DockPanel.SetDock(toolScroll, Dock.Left); root.Children.Add(toolScroll);
        var footer = new StackPanel { Spacing = 0 };
        var noticeContent = new DockPanel();
        var dismiss = new Button { Content = "Закрити", Margin = new(8, 0) }; dismiss.Click += (_, _) => notification.IsVisible = false;
        DockPanel.SetDock(dismiss, Dock.Right); noticeContent.Children.Add(dismiss);
        var log = CommandButton("help.log", true); DockPanel.SetDock(log, Dock.Right); noticeContent.Children.Add(log);
        noticeContent.Children.Add(notificationText); notification.Child = noticeContent; footer.Children.Add(notification);
        status.TextWrapping = TextWrapping.Wrap; status.MaxHeight = 48; status.FontSize = 12;
        footer.Children.Add(status); footer.Children.Add(contextInfo);
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);

        var drawing = new DockPanel();
        var pageBar = new DockPanel { Margin = new(4) };
        pagePicker.Width = 190;
        pagePicker.ItemTemplate = new FuncDataTemplate<DrawingPage>((page, _) => new TextBlock { Text = page is null ? "" : $"{page.Number}. {page.Name}" });
        pagePicker.SelectionChanged += (_, _) => { if (!refreshingPages && pagePicker.SelectedItem is DrawingPage page) NavigatePage(page.Id); };
        var pageActions = new StackPanel { Orientation = Orientation.Horizontal };
        pageActions.Children.Add(CommandButton("page.add")); pageActions.Children.Add(CommandButton("page.edit"));
        DockPanel.SetDock(pageActions, Dock.Right); pageBar.Children.Add(pageActions);
        pageTabs.ItemTemplate = new FuncDataTemplate<DrawingPage>((page, _) => new TextBlock { Text = page is null ? "" : $"{page.Number} · {page.Name}", FontSize = 13, MaxWidth = 180, TextTrimming = TextTrimming.CharacterEllipsis });
        pageTabs.SelectionChanged += (_, _) => { if (!refreshingWorkspace && pageTabs.SelectedItem is DrawingPage page) NavigatePage(page.Id); };
        ScrollViewer.SetHorizontalScrollBarVisibility(pageTabs, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(pageTabs, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        pageBar.Children.Add(pageTabs);
        DockPanel.SetDock(pageBar, Dock.Top); drawing.Children.Add(pageBar);
        var parameters = new WrapPanel { Margin = new(4), Name = "DrawingParameters" };
        var grid = new CheckBox { Content = "Сітка", IsChecked = true, Margin = new(4) };
        grid.IsCheckedChanged += (_, _) => { canvas.GridVisible = grid.IsChecked == true; canvas.InvalidateVisual(); };
        var snap = new CheckBox { Content = "Прив’язка", IsChecked = true, Margin = new(4) };
        snap.IsCheckedChanged += (_, _) => canvas.SnapEnabled = snap.IsChecked == true;
        parameters.Children.Add(grid); parameters.Children.Add(snap);
        var step = new NumericUpDown { Value = 2.5m, Minimum = .1m, Maximum = 100, Increment = .5m, Width = 140, Name = "GridStep" };
        ToolTip.SetTip(step, "Крок сітки, мм"); AutomationProperties.SetName(step, "Крок сітки, мм");
        step.ValueChanged += (_, _) => { canvas.GridStep = (double)(step.Value ?? 2.5m); canvas.InvalidateVisual(); };
        parameters.Children.Add(step);
        foreach (var id in new[] { "drawing.path-kind", "drawing.angle" }) parameters.Children.Add(CommandButton(id, true));
        header.Children.Add(parameters); drawing.Children.Add(canvas);
        Panels = new WorkspacePanels(drawing, text => status.Text = text);
        Panels.Register("project", "Аркуші", BuildProjectPanel(), "left");
        Panels.Register("library", "Бібліотеки", BuildLibraryPanel(), "left");
        var inspector = new PropertyPanel(session, text => status.Text = text) { Width = double.NaN };
        Panels.Register("properties", "Властивості", inspector, "right");
        Closed += (_, _) => inspector.Dispose();
        Panels.Register("electrical", "Пристрої / кола", BuildElectricalPanel(), "left");
        Panels.Register("erc", "ERC", BuildErcPanel(), "bottom");
        Panels.Restore(); root.Children.Add(Panels); Content = root;
        var context = new ContextMenu();
        foreach (var id in new[] { "edit.copy", "edit.paste", "edit.delete", "edit.group", "edit.ungroup", "edit.rotate", "drawing.properties" }) context.Items.Add(CommandMenu(Commands[id]));
        canvas.ContextMenu = context;
        AddHandler(KeyDownEvent, WorkspaceKeyDown, RoutingStrategies.Tunnel);
        canvas.ToolChanged += () => { Commands.Refresh(); UpdateWorkspaceStatus(); };
        Closed += (_, _) => { backgroundCancellation?.Cancel(); Panels.Save(); session.Changed -= HandleSessionChanged; };
        RefreshPages(); RefreshSymbolPicker(); RefreshWorkspace();
    }

    private void RegisterCommands()
    {
        bool Selected() => session.Selection.Count > 0;
        void Add(string id, string title, string category, string icon, Action run, Func<bool>? can = null, string? key = null, Func<bool>? active = null) =>
            Async(id, title, category, icon, () => { run(); return Task.CompletedTask; }, can, key, active);
        void Async(string id, string title, string category, string icon, Func<Task> run, Func<bool>? can = null, string? key = null, Func<bool>? active = null) =>
            Commands.Add(new(id, WorkspaceText.Get(id, title), category, icon, async () =>
            {
                var fileOperation = id.StartsWith("file.", StringComparison.Ordinal);
                if (fileOperation) { fileOperationBusy = true; Commands.Refresh(); }
                try { await run(); }
                finally { if (fileOperation) fileOperationBusy = false; Commands.Refresh(); UpdateWorkspaceStatus(); }
            }, () => (!id.StartsWith("file.", StringComparison.Ordinal) || !fileOperationBusy) &&
                (!(id.StartsWith("file.", StringComparison.Ordinal) || id == "electrical.check") || backgroundCancellation is null) && (can?.Invoke() ?? true),
                ex => { if (ex is OperationCanceledException) status.Text = "Операцію скасовано."; else if (ex is InvalidDataException or ArgumentException) ShowNotice(ex.Message); else ReportError(ex, title); }, key is null ? null : KeyGesture.Parse(key), active));
        Async("file.new", "Новий проєкт", "Файл", "new", async () => { if (await CanDiscard()) { canvas.Cancel(); session.Load(new()); savedRevision = session.ContentRevision; currentPath = null; UpdateTitle(); } }, key: "Ctrl+N");
        Async("file.open", "Відкрити…", "Файл", "open", Open, key: "Ctrl+O");
        Async("file.save", "Зберегти…", "Файл", "save", Save, key: "Ctrl+S");
        Add("edit.undo", "Скасувати", "Редагування", "undo", () => { canvas.Cancel(); session.Undo(); }, () => session.CanUndo, "Ctrl+Z");
        Add("edit.redo", "Повторити", "Редагування", "redo", () => { canvas.Cancel(); session.Redo(); }, () => session.CanRedo, "Ctrl+Y");
        Add("edit.copy", "Копіювати", "Редагування", "copy", canvas.CopySelection, Selected, "Ctrl+C");
        Add("edit.paste", "Вставити", "Редагування", "copy", canvas.PasteSelection, () => session.CanPaste, "Ctrl+V");
        Add("edit.delete", "Видалити", "Редагування", "delete", () => { canvas.Cancel(); session.Delete(); }, Selected, "Delete");
        Add("edit.group", "Групувати", "Редагування", "group", () => { canvas.Cancel(); session.Group(); }, () => session.Selection.Count > 1, "Ctrl+G");
        Add("edit.ungroup", "Розгрупувати", "Редагування", "group", () => { canvas.Cancel(); session.Ungroup(); }, () => session.Document.Elements.Any(e => session.Selection.Contains(e.Id) && e.GroupId is not null), "Ctrl+Shift+G");
        Add("edit.rotate", "Повернути на 90°", "Редагування", "redo", canvas.RotateSelection90, Selected, "Ctrl+R");
        Add("edit.select-all", "Вибрати все", "Редагування", "select", () => session.SelectBox(new(-100000, -100000, 100000, 100000), true, false), key: "Ctrl+A");
        Add("drawing.split", "Розбити сегмент", "Креслення", "line", canvas.BeginSplitSegment);
        Add("drawing.trim", "Обрізати лінію", "Креслення", "line", canvas.BeginTrim);
        Add("drawing.extend", "Продовжити лінію", "Креслення", "line", canvas.BeginExtend);
        Add("drawing.properties", "Параметри фігури", "Креслення", "settings", canvas.EditSelectedGeometry, () => session.Selection.Count == 1);
        Add("tool.select", "Вибір / переміщення", "Креслення", "select", canvas.EscapeToSelection, key: "Escape", active: () => canvas.Tool is null);
        Add("tool.path", "Лінія / провідник", "Креслення", "line", canvas.ActivatePathTool, key: "L", active: () => canvas.Tool is ElementKind.Line or ElementKind.Wire);
        foreach (var (kind, key, icon, title) in new[] { (ElementKind.Rectangle, "R", "rectangle", "Прямокутник"), (ElementKind.Circle, "C", "circle", "Коло"), (ElementKind.Polyline, "P", "polyline", "Полілінія"), (ElementKind.Arc, "A", "arc", "Дуга"), (ElementKind.Dimension, "D", "dimension", "Розмір"), (ElementKind.Junction, "", "junction", "Точка з’єднання"), (ElementKind.Text, "", "text", "Текст"), (ElementKind.Symbol, "", "symbol", "Вставити символ") })
            Add("tool." + (kind == ElementKind.Dimension ? "dimension" : kind.ToString().ToLowerInvariant()), title, "Креслення", icon, () => SetTool(kind, title + ": задай початкову точку."), key: key.Length == 0 ? null : key, active: () => canvas.Tool == kind);
        Add("drawing.path-kind", "Тип лінії", "Креслення", "line", () => { canvas.SetPathKind(canvas.ActivePathKind == ElementKind.Wire ? ElementKind.Line : ElementKind.Wire); Commands.Refresh(); });
        Add("drawing.angle", "Крок кута", "Креслення", "arc", () => { var steps = new[] { 0d, 15d, 30d, 45d }; canvas.SetAngleSnap(steps[(Array.IndexOf(steps, canvas.AngleSnapStep) + 1) % steps.Length]); Commands.Refresh(); });
        Async("page.add", "Додати аркуш", "Файл", "new", AddPage);
        Async("page.edit", "Параметри аркуша", "Файл", "settings", EditPage);
        Add("page.delete", "Видалити аркуш", "Файл", "delete", DeletePage, () => session.Document.Pages.Length > 1);
        Add("page.link", "Міжсторінковий зв’язок", "Електрика", "line", CrossPageLink, () => session.Selection.Count == 1);
        Add("view.fit", "Аркуш у вікно", "Вигляд", "fit", canvas.Fit, key: "F");
        Async("view.palette", "Пошук команд…", "Вигляд", "search", ShowCommandPalette, key: "Ctrl+Shift+P");
        Add("view.reset", "Відновити розкладку", "Вигляд", "settings", () => Panels.Reset());
        Add("work.cancel", "Скасувати фонову операцію", "Вигляд", "delete", () => backgroundCancellation?.Cancel(), () => backgroundCancellation is not null);
        foreach (var (id, title) in new[] { ("project", "Аркуші"), ("library", "Бібліотеки"), ("properties", "Властивості"), ("electrical", "Пристрої / кола"), ("erc", "ERC") })
            Add("view.panel." + id, "Панель: " + title, "Вигляд", "settings", () => Panels.Toggle(id));
        Add("view.theme", "Світла / темна тема", "Вигляд", "settings", () => RequestedThemeVariant = ActualThemeVariant == Avalonia.Styling.ThemeVariant.Dark ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark);
        Async("electrical.check", "Перевірити схему", "Електрика", "check", ShowElectricalIssues, key: "F7");
        Async("electrical.edit", "Редактор електричної моделі…", "Електрика", "symbol", ShowElectricalProject);
        Add("electrical.clean", "Очистити невикористані кола", "Електрика", "delete", session.RemoveUnusedNets);
        Async("library.edit", "Редактор бібліотек…", "Бібліотеки", "symbol", ShowLibraryEditor);
        Async("library.create-symbol", "Створити символ…", "Бібліотеки", "symbol", CreateCustomSymbol, Selected);
        Async("help.log", "Журнал помилок", "Довідка", "settings", ShowErrorLog);
    }

    private Button CommandButton(string id, bool text = false)
    {
        var command = Commands[id];
        var button = new Button { Name = "Command_" + id.Replace('.', '_'), Command = command, MinHeight = 32, MinWidth = 32, Padding = new(6), Margin = new(2), Content = text ? command.Title : new WorkspaceIcon(command.Icon) };
        ToolTip.SetTip(button, command.Hint); ToolTip.SetShowOnDisabled(button, true); AutomationProperties.SetName(button, command.Hint);
        void Refresh()
        {
            button.Opacity = command.CanExecute(null) ? 1 : .4;
            ToolTip.SetTip(button, command.Hint + (command.CanExecute(null) ? "" : " — недоступно для поточного стану або вибору"));
            button.BorderThickness = new(command.IsActive ? 2 : 0);
            button.BorderBrush = Brushes.DodgerBlue;
            if (id == "drawing.path-kind") button.Content = canvas.ActivePathKind == ElementKind.Wire ? "Провідник" : "Графіка";
            if (id == "drawing.angle") button.Content = canvas.AngleSnapStep == 0 ? "Кут: вільно" : $"Кут: {canvas.AngleSnapStep:0}°";
        }
        command.CanExecuteChanged += (_, _) => Refresh(); Refresh(); return button;
    }
    private static MenuItem CommandMenu(WorkspaceCommand command) => new() { Header = command.Title, Command = command, InputGesture = command.Shortcut, Icon = new WorkspaceIcon(command.Icon) };
    private void WorkspaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.Source is Control source && (source is TextBox || source.GetVisualAncestors().Any(x => x is TextBox or NumericUpDown)) &&
            !(e.Key == Key.P && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))) return;
        Commands.Handle(e);
    }
    private Control BuildProjectPanel()
    {
        projectPages.ItemTemplate = new FuncDataTemplate<DrawingPage>((page, _) => new TextBlock { Text = page is null ? "" : $"{page.Number} · {page.Name}", TextWrapping = TextWrapping.Wrap });
        projectPages.SelectionChanged += (_, _) => { if (!refreshingWorkspace && projectPages.SelectedItem is DrawingPage page) NavigatePage(page.Id); };
        return projectPages;
    }
    private Control BuildLibraryPanel()
    {
        var panel = new DockPanel { Margin = new(8) }; DockPanel.SetDock(librarySearch, Dock.Top); panel.Children.Add(librarySearch);
        DockPanel.SetDock(libraryEmpty, Dock.Top); panel.Children.Add(libraryEmpty);
        var edit = CommandButton("library.edit", true); DockPanel.SetDock(edit, Dock.Bottom); panel.Children.Add(edit);
        var create = CommandButton("library.create-symbol", true); DockPanel.SetDock(create, Dock.Bottom); panel.Children.Add(create);
        librarySymbols.ItemTemplate = new FuncDataTemplate<LibraryEntry>((symbol, _) => new TextBlock { Text = symbol?.Label, TextWrapping = TextWrapping.Wrap });
        librarySearch.TextChanged += (_, _) => RefreshLibrarySearch();
        void Insert() { if (librarySymbols.SelectedItem is LibraryEntry item) { canvas.ActiveSymbolKey = item.SymbolKey; canvas.ActiveVariantId = item.VariantId; Commands["tool.symbol"].Execute(null); } }
        librarySymbols.DoubleTapped += (_, _) => Insert();
        librarySymbols.AddHandler(KeyDownEvent, (_, e) => { if (e.Key == Key.Enter) { Insert(); e.Handled = true; } }, RoutingStrategies.Tunnel);
        panel.Children.Add(librarySymbols); return panel;
    }
    private void RefreshLibrarySearch()
    {
        var entries = SymbolLibrary.Definitions(session.Document).Select(s => new LibraryEntry(s.Name, s.Key, null))
            .Concat(ComponentCatalog.Variants(session.Document).Select(v => new LibraryEntry($"{v.Library.Name} / {v.Family.Manufacturer} / {v.Variant.Name} · {v.Variant.RatingText}", v.Variant.SymbolKey, v.Variant.Id)));
        var found = entries.Where(s => s.Label.Contains(librarySearch.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray();
        librarySymbols.ItemsSource = found; libraryEmpty.IsVisible = found.Length == 0;
    }
    private Control BuildElectricalPanel()
    {
        var panel = new DockPanel(); var edit = CommandButton("electrical.edit", true); DockPanel.SetDock(edit, Dock.Top); panel.Children.Add(edit);
        electricalObjects.ItemTemplate = new FuncDataTemplate<NavigationItem>((item, _) => new TextBlock { Text = item?.Text, TextWrapping = TextWrapping.Wrap });
        electricalObjects.SelectionChanged += (_, _) => { if (!refreshingWorkspace && electricalObjects.SelectedItem is NavigationItem item) NavigateElement(item.PageId, item.ElementId); };
        panel.Children.Add(electricalObjects); return panel;
    }
    private Control BuildErcPanel()
    {
        var panel = new DockPanel { Margin = new(8) }; DockPanel.SetDock(ercSummary, Dock.Top); panel.Children.Add(ercSummary);
        ercIssues.ItemTemplate = new FuncDataTemplate<ElectricalIssue>((issue, _) => new TextBlock { Text = issue?.Message, TextWrapping = TextWrapping.Wrap });
        ercIssues.SelectionChanged += (_, _) => { if (ercIssues.SelectedItem is ElectricalIssue issue) NavigateElement(issue.PageId, issue.ElementId); };
        panel.Children.Add(ercIssues); return panel;
    }
    private void NavigatePage(Guid pageId)
    {
        if (session.Document.ActivePageId == pageId) return;
        canvas.Cancel(); session.SwitchPage(pageId); canvas.Fit();
    }
    public void NavigateElement(Guid? pageId, Guid elementId)
    {
        var page = session.Document.Pages.FirstOrDefault(p => (pageId is null || p.Id == pageId) && p.Elements.Any(e => e.Id == elementId));
        if (page is null) { status.Text = "Повідомлення стосується моделі без розміщеного символу."; return; }
        NavigatePage(page.Id); canvas.EscapeToSelection(); session.Select(page.Elements.Single(e => e.Id == elementId), false); canvas.CenterOn(page.Elements.Single(e => e.Id == elementId).A);
    }
    private void RefreshWorkspace()
    {
        if (Panels is null) return;
        Commands.Refresh(); UpdateWorkspaceStatus();
        refreshingWorkspace = true;
        try
        {
            projectPages.ItemsSource = session.Document.Pages; projectPages.SelectedItem = session.ActivePage;
            pageTabs.ItemsSource = session.Document.Pages; pageTabs.SelectedItem = session.ActivePage;
            if (workspaceRevision == session.ContentRevision) return;
            workspaceRevision = session.ContentRevision;
            RefreshLibrarySearch();
            electricalObjects.ItemsSource = session.Document.Pages.SelectMany(p => p.Elements.Where(e => e.Kind is ElementKind.Symbol or ElementKind.Wire).Select(e =>
                new NavigationItem(e.Kind == ElementKind.Symbol ? $"{e.DeviceTag} · {p.Name}" : $"Коло {session.Document.Nets.FirstOrDefault(n => n.Id == e.NetId)?.Number} · {p.Name}", p.Id, e.Id))).ToArray();
            if (ercIssues.ItemCount > 0) ercSummary.Text = "Документ змінено. Натисни F7 для повторної перевірки.";
        }
        finally { refreshingWorkspace = false; }
    }
    private void UpdateWorkspaceStatus() => contextInfo.Text = $"Аркуш {session.ActivePage.Number} · {session.Selection.Count} вибрано · {canvas.ActivePathKind switch { ElementKind.Wire => "Провідник", _ => "Графіка" }} · L/C/R/D/P/A · Esc: вибір · Ctrl+Shift+P: команди";
    private async Task ShowCommandPalette()
    {
        var window = new Window { Title = "Пошук команд", Width = 540, Height = 460, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new DockPanel { Margin = new(12) };
        var search = new TextBox { PlaceholderText = "Назва, категорія або ID команди…", Name = "CommandSearch" };
        DockPanel.SetDock(search, Dock.Top); panel.Children.Add(search);
        var list = new ListBox { Name = "CommandResults", ItemTemplate = new FuncDataTemplate<WorkspaceCommand>((c, _) => new TextBlock { Text = c is null ? "" : $"{c.Category} · {c.Hint}{(c.CanExecute(null) ? "" : " — недоступно для поточного вибору")}", TextWrapping = TextWrapping.Wrap }) };
        void Filter() { list.ItemsSource = Commands.All.Where(c => (c.Title + c.Category + c.Id).Contains(search.Text ?? "", StringComparison.OrdinalIgnoreCase)).ToArray(); list.SelectedIndex = 0; }
        void Execute() { if (list.SelectedItem is WorkspaceCommand c && c.CanExecute(null)) window.Close(c.Id); }
        search.TextChanged += (_, _) => Filter(); list.DoubleTapped += (_, _) => Execute();
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) window.Close(); if (e.Key == Key.Enter) Execute(); if (e.Key == Key.Down && search.IsFocused) { list.Focus(); e.Handled = true; } };
        panel.Children.Add(list); window.Content = panel; Filter(); window.Opened += (_, _) => search.Focus();
        var selected = await window.ShowDialog<string?>(this); if (selected is not null) Commands[selected].Execute(null);
    }
}
