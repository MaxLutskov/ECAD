using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ECAD.Core;

namespace ECAD.Desktop;

public sealed class MainWindow : Window
{
    private readonly EditorSession session = new();
    private readonly DrawingCanvas canvas;
    private readonly TextBlock status = new() { Margin = new Thickness(12, 7) };
    private readonly ComboBox symbolPicker = new() { Width = 185, Margin = new Thickness(3) };
    private readonly ComboBox pagePicker = new() { Width = 210, Margin = new Thickness(3) };
    private readonly TextBlock pageInfo = new() { FontSize = 12, Foreground = Brushes.DimGray, Margin = new Thickness(12, 0, 12, 8) };
    private SymbolDefinition[] displayedCustomSymbols = [];
    private bool symbolRefreshPending;
    private bool pageRefreshPending;
    private bool refreshingPages;
    private (Guid PageId, Guid ElementId)? pendingPageLink;
    private DrawingDocument saved;
    private string? currentPath;
    private bool allowClose;
    private bool askingClose;
    private bool IsDirty => !ReferenceEquals(saved, session.Document);

    public MainWindow()
    {
        saved = session.Document;
        Title = "ECAD — прототип"; Width = 1280; Height = 850; MinWidth = 900; MinHeight = 600;
        canvas = new DrawingCanvas(session);
        var root = new DockPanel();
        var header = new StackPanel { Background = Brushes.White };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var title = new TextBlock { Text = "ECAD 0.16  /  Багатосторінковий проєкт", FontSize = 18, Margin = new Thickness(14, 8, 14, 3) };
        header.Children.Add(title);
        var commands = new WrapPanel { Margin = new Thickness(8, 0, 8, 2) };
        header.Children.Add(commands);
        var files = AddToolbarGroup(commands, "Файл");
        AddAsyncIconButton(files, "+", "Новий аркуш", async () => { if (await CanDiscard()) { canvas.Cancel(); session.Load(new()); saved = session.Document; currentPath = null; UpdateTitle(); } });
        AddAsyncIconButton(files, "↗", "Відкрити…", Open);
        AddAsyncIconButton(files, "↓", "Зберегти…", Save);
        AddAsyncIconButton(files, "▤", "Бібліотеки пристроїв", ShowLibraryEditor);
        var pages = AddToolbarGroup(commands, "Аркуші");
        pagePicker.ItemTemplate = new FuncDataTemplate<DrawingPage>((page, _) => new TextBlock
        {
            Text = page is null ? "" : $"{page.Number}. {page.Name} · {FormatName(page.Format)}"
        });
        pagePicker.SelectionChanged += (_, _) =>
        {
            if (refreshingPages || pagePicker.SelectedItem is not DrawingPage page || page.Id == session.Document.ActivePageId) return;
            var wasSaved = !IsDirty; canvas.Cancel(); session.SwitchPage(page.Id); if (wasSaved) saved = session.Document; canvas.Fit();
            status.Text = $"Відкрито аркуш {page.Number}: {page.Name}.";
        };
        pages.Children.Add(pagePicker);
        AddAsyncIconButton(pages, "+", "Додати аркуш", AddPage);
        AddAsyncIconButton(pages, "✎", "Параметри аркуша", EditPage);
        AddIconButton(pages, "⇄", "Зв’язати вибрані елементи на різних аркушах", CrossPageLink);
        AddIconButton(pages, "×", "Видалити поточний аркуш (можна скасувати)", DeletePage);
        RefreshPages();
        var edit = AddToolbarGroup(commands, "Редагування");
        AddIconButton(edit, "↶", "Скасувати (Ctrl+Z)", () => { canvas.Cancel(); session.Undo(); });
        AddIconButton(edit, "↷", "Повторити (Ctrl+Y)", () => { canvas.Cancel(); session.Redo(); });
        AddIconButton(edit, "⊞", "Групувати", () => { canvas.Cancel(); session.Group(); });
        AddIconButton(edit, "⊟", "Розгрупувати", () => { canvas.Cancel(); session.Ungroup(); });
        AddIconButton(edit, "⧉", "Копіювати (Ctrl+C)", canvas.CopySelection);
        AddIconButton(edit, "▣", "Вставити (Ctrl+V)", canvas.PasteSelection);
        AddIconButton(edit, "↻", "Повернути на 90° (Ctrl+R)", canvas.RotateSelection90);
        AddIconButton(edit, "⌇", "Розбити лінію або сегмент полілінії", canvas.BeginSplitSegment);
        AddIconButton(edit, "⌫", "Обрізати пряму лінію до межі", canvas.BeginTrim);
        AddIconButton(edit, "⇥", "Продовжити пряму лінію до межі", canvas.BeginExtend);
        AddIconButton(edit, "×", "Видалити (Delete)", () => { canvas.Cancel(); session.Delete(); });
        var checks = AddToolbarGroup(commands, "Перевірка і вигляд");
        AddAsyncIconButton(checks, "✓", "Перевірити схему", ShowElectricalIssues);
        AddAsyncIconButton(checks, "!", "Журнал помилок", ShowErrorLog);
        AddIconButton(checks, "□", "Вмістити аркуш у вікно", canvas.Fit);
        var workspace = new WrapPanel { Margin = new Thickness(8, 0, 8, 5) };
        header.Children.Add(workspace);
        var tools = AddToolbarGroup(workspace, "Інструменти");
        AddIconButton(tools, "↖", "Вибір / переміщення (Esc)", () => SetTool(null, "Потягни рамку з порожнього місця. Shift додає до вибору."));
        AddIconButton(tools, "╱", "Лінія / провідник (L)", () =>
        {
            canvas.ActivatePathTool();
            status.Text = canvas.ActivePathKind == ElementKind.Wire
                ? "Провідник: кліки задають трасу; число → довжина → Tab → кут → Enter."
                : "Графічна лінія: дві точки або початок → довжина → Tab → кут → Enter.";
        });
        AddIconButton(tools, "▭", "Прямокутник (R)", () => SetTool(ElementKind.Rectangle, "Прямокутник: два кути або початок → ширина → Tab → висота → Enter."));
        AddIconButton(tools, "○", "Коло (C)", () => SetTool(ElementKind.Circle, "Коло: центр і точка або центр → радіус; Tab перемикає на діаметр."));
        AddIconButton(tools, "⌁", "Полілінія (P)", () => SetTool(ElementKind.Polyline, "Полілінія: послідовні вершини або точні сегменти; Enter завершує."));
        AddIconButton(tools, "⌒", "Дуга (A)", () => SetTool(ElementKind.Arc, "Дуга: три точки або центр → радіус → Tab → кут → Enter."));
        AddIconButton(tools, "↔", "Розмір (D)", () => SetTool(ElementKind.Dimension, "Розмір: обери дві точки / паралельні лінії, потім клацни місце напису."));
        AddIconButton(tools, "●", "Точка з’єднання", () => SetTool(ElementKind.Junction, "Вузол: клацни кінець, сегмент або перетин ліній. Звичайний перетин без точки не є з’єднанням."));
        AddIconButton(tools, "T", "Текст", () => SetTool(ElementKind.Text, "Текст: клацни місце розташування. Подвійний клік редагує наявний напис."));
        symbolPicker.ItemTemplate = new FuncDataTemplate<SymbolDefinition>((item, _) => new TextBlock { Text = item?.Name ?? "" });
        symbolPicker.SelectionChanged += (_, _) => { if (symbolPicker.SelectedItem is SymbolDefinition item) canvas.ActiveSymbolKey = item.Key; };
        tools.Children.Add(symbolPicker); RefreshSymbolPicker();
        AddIconButton(tools, "◇", "Вставити вибраний символ", () => SetTool(ElementKind.Symbol, "Символ: обери тип у списку та клацай місця розташування; Ctrl+R повертає виділений символ."));
        AddAsyncIconButton(tools, "◇+", "Створити символ…", CreateCustomSymbol, 46);
        var settings = AddToolbarGroup(workspace, "Параметри побудови");
        var grid = new CheckBox { Content = "Сітка", IsChecked = true, Margin = new Thickness(0, 0, 8, 0) };
        grid.IsCheckedChanged += (_, _) => { canvas.GridVisible = grid.IsChecked == true; canvas.InvalidateVisual(); };
        settings.Children.Add(grid);
        var snap = new CheckBox { Content = "Прив’язка", IsChecked = true, Margin = new Thickness(0, 0, 8, 0) };
        snap.IsCheckedChanged += (_, _) => canvas.SnapEnabled = snap.IsChecked == true;
        settings.Children.Add(snap);
        settings.Children.Add(new TextBlock { Text = "Крок, мм", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
        var step = new NumericUpDown { Minimum = .1m, Maximum = 100, Increment = .5m, Value = 2.5m, Width = 105 };
        step.ValueChanged += (_, _) => { canvas.GridStep = (double)(step.Value ?? 2.5m); canvas.InvalidateVisual(); };
        settings.Children.Add(step);
        var pathTypeButton = new Button { Content = "Тип: провідник", Margin = new Thickness(5, 2, 2, 2), Padding = new Thickness(8, 5) };
        pathTypeButton.Click += (_, _) =>
        {
            var kind = canvas.ActivePathKind == ElementKind.Wire ? ElementKind.Line : ElementKind.Wire;
            canvas.SetPathKind(kind);
            pathTypeButton.Content = kind == ElementKind.Wire ? "Тип: провідник" : "Тип: графіка";
        };
        settings.Children.Add(pathTypeButton);
        var angleSteps = new[] { 0d, 15d, 30d, 45d };
        var angleIndex = 0;
        var angleButton = new Button { Content = "Кут: вільно", Margin = new Thickness(5, 2, 2, 2), Padding = new Thickness(8, 5) };
        angleButton.Click += (_, _) =>
        {
            angleIndex = (angleIndex + 1) % angleSteps.Length;
            var angleStep = angleSteps[angleIndex];
            canvas.SetAngleSnap(angleStep);
            angleButton.Content = angleStep == 0 ? "Кут: вільно" : $"Кут: {angleStep:0}°";
            canvas.Focus();
        };
        settings.Children.Add(angleButton);
        AddIconButton(settings, "✎", "Змінити параметри виділеної фігури", canvas.EditSelectedGeometry);
        var footer = new StackPanel { Background = Brushes.White };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(status);
        footer.Children.Add(pageInfo);
        UpdatePageInfo();
        var properties = new PropertyPanel(session, message => status.Text = message);
        DockPanel.SetDock(properties, Dock.Right); root.Children.Add(properties);
        root.Children.Add(canvas); Content = root;
        canvas.Status += message => status.Text = message;
        canvas.TextPlacementRequested += async request =>
        {
            try
            {
                var result = await ShowTextDialog(request);
                if (result is { } value) canvas.CommitText(request, value.Text, value.HeightMm);
            }
            catch (Exception ex) { ReportError(ex, "Редагування тексту"); }
        };
        session.Changed += HandleSessionChanged;
        Opened += (_, _) => { canvas.Fit(); canvas.Focus(); };
        Closing += async (_, e) =>
        {
            if (allowClose || !IsDirty) return;
            e.Cancel = true;
            if (askingClose) return;
            askingClose = true;
            try { if (await CanDiscard()) { allowClose = true; Close(); } }
            catch (Exception ex) { ReportError(ex, "Закриття програми"); }
            finally { askingClose = false; }
        };
        status.Text = "Обери інструмент. Escape завжди повертає до вибору / переміщення.";
    }

    private void SetTool(ElementKind? kind, string message) { canvas.SetTool(kind); status.Text = message; }
    private void HandleSessionChanged()
    {
        UpdateTitle();
        UpdatePageInfo();
        if (!pageRefreshPending)
        {
            pageRefreshPending = true;
            Dispatcher.UIThread.Post(() => { pageRefreshPending = false; RefreshPages(); });
        }
        if (ReferenceEquals(displayedCustomSymbols, session.Document.CustomSymbols) || symbolRefreshPending) return;
        symbolRefreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            symbolRefreshPending = false;
            RefreshSymbolPicker(canvas.ActiveSymbolKey);
        });
    }
    private void RefreshPages()
    {
        refreshingPages = true;
        try
        {
            pagePicker.ItemsSource = session.Document.Pages.OrderBy(page => page.Number).ToArray();
            pagePicker.SelectedItem = session.Document.Pages.Single(page => page.Id == session.Document.ActivePageId);
        }
        finally { refreshingPages = false; }
    }
    private void UpdatePageInfo()
    {
        var page = session.ActivePage;
        pageInfo.Text = $"{page.Number}/{session.Document.Pages.Length} · {page.Name} · {FormatName(page.Format)} · {page.WidthMm:0.#}×{page.HeightMm:0.#} мм   |   L: лінія · C: коло · R: прямокутник · P: полілінія · A: дуга · D: розмір · Esc: вибір";
    }
    private static string FormatName(PaperFormat format) => format switch
    {
        PaperFormat.A4Portrait => "A4 книжкова", PaperFormat.A4Landscape => "A4 альбомна",
        PaperFormat.A3Portrait => "A3 книжкова", PaperFormat.A3Landscape => "A3 альбомна", _ => "Довільний"
    };
    private void RefreshSymbolPicker(string? selectedKey = null)
    {
        var definitions = SymbolLibrary.Definitions(session.Document);
        displayedCustomSymbols = session.Document.CustomSymbols;
        symbolPicker.ItemsSource = definitions;
        symbolPicker.SelectedItem = definitions.FirstOrDefault(d => d.Key == selectedKey) ?? definitions[0];
    }
    private void UpdateTitle() => Title = $"{(IsDirty ? "* " : "")}{Path.GetFileName(currentPath) ?? "Новий аркуш"} — ECAD прототип";
    private static WrapPanel AddToolbarGroup(Panel parent, string title)
    {
        var controls = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        var card = new Border
        {
            Margin = new Thickness(3), Padding = new Thickness(6, 3), CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Color.Parse("#f8fafc")),
            BorderBrush = new SolidColorBrush(Color.Parse("#d8e0e8")), BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 11, Foreground = Brushes.SlateGray, Margin = new Thickness(3, 0) },
                    controls
                }
            }
        };
        parent.Children.Add(card);
        return controls;
    }

    private static void AddIconButton(Panel panel, string icon, string tip, Action action, double width = 38)
    {
        var button = new Button
        {
            Content = icon, Width = width, Height = 34, FontSize = icon.Length > 1 ? 15 : 19,
            Margin = new Thickness(2), Padding = new Thickness(5), HorizontalContentAlignment = HorizontalAlignment.Center
        };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => action();
        panel.Children.Add(button);
    }

    private void AddAsyncIconButton(Panel panel, string icon, string tip, Func<Task> action, double width = 38)
    {
        var button = new Button
        {
            Content = icon, Width = width, Height = 34, FontSize = icon.Length > 1 ? 15 : 19,
            Margin = new Thickness(2), Padding = new Thickness(5), HorizontalContentAlignment = HorizontalAlignment.Center
        };
        ToolTip.SetTip(button, tip);
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) { ReportError(ex, tip); }
        };
        panel.Children.Add(button);
    }

    private static void AddButton(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(3), Padding = new Thickness(10, 6) };
        button.Click += (_, _) => action(); panel.Children.Add(button);
    }
    private void AddAsyncButton(Panel panel, string text, Func<Task> action)
    {
        var button = new Button { Content = text, Margin = new Thickness(3), Padding = new Thickness(10, 6) };
        button.Click += async (_, _) =>
        {
            try { await action(); }
            catch (Exception ex) { ReportError(ex, text); }
        };
        panel.Children.Add(button);
    }

    public void ReportError(Exception exception, string context, bool writeToLog = true)
    {
        if (writeToLog) AppLog.Write(context, exception);
        status.Text = $"{context}: {exception.Message} Деталі записано в журнал.";
    }
    private static FilePickerFileType FileType => new("ECAD") { Patterns = ["*.ecad"] };

    private async Task<TextDialogResult?> ShowTextDialog(TextPlacementRequest request)
    {
        var dialog = new Window
        {
            Title = request.ElementId is null ? "Додати текст" : "Редагувати текст",
            Width = 460, Height = 260, MinWidth = 380, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "Текст" });
        var input = new TextBox { Text = request.Text, AcceptsReturn = true, Height = 90 };
        panel.Children.Add(input);
        var heightRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        heightRow.Children.Add(new TextBlock { Text = "Висота, мм", VerticalAlignment = VerticalAlignment.Center });
        var height = new NumericUpDown { Minimum = .5m, Maximum = 100, Increment = .5m, Value = (decimal)request.HeightMm, Width = 120 };
        heightRow.Children.Add(height); panel.Children.Add(heightRow);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        AddButton(buttons, "Скасувати", () => dialog.Close(null));
        AddButton(buttons, "Застосувати", () =>
        {
            if (!string.IsNullOrWhiteSpace(input.Text))
                dialog.Close(new TextDialogResult(input.Text, (double)(height.Value ?? 3.5m)));
        });
        panel.Children.Add(buttons); dialog.Content = panel;
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        return await dialog.ShowDialog<TextDialogResult?>(this);
    }

    private async Task CreateCustomSymbol()
    {
        var dialog = new Window
        {
            Title = "Створити власний символ", Width = 460, Height = 245, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 9 };
        panel.Children.Add(new TextBlock { Text = "Виділені лінії/прямокутники стануть графікою, а вузли — контактами." });
        panel.Children.Add(new TextBlock { Text = "Назва" }); var name = new TextBox(); panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "Префікс позначення" }); var prefix = new TextBox { MaxLength = 12 }; panel.Children.Add(prefix);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        AddButton(buttons, "Скасувати", () => dialog.Close(null));
        AddButton(buttons, "Створити", () =>
        {
            if (!string.IsNullOrWhiteSpace(name.Text) && !string.IsNullOrWhiteSpace(prefix.Text))
                dialog.Close(new CustomSymbolInput(name.Text.Trim(), prefix.Text.Trim()));
        });
        panel.Children.Add(buttons); dialog.Content = panel;
        dialog.Opened += (_, _) => name.Focus();
        var input = await dialog.ShowDialog<CustomSymbolInput?>(this);
        if (input is null) return;
        try
        {
            var definition = session.CreateCustomSymbol(input.Name, input.Prefix);
            canvas.ActiveSymbolKey = definition.Key;
            status.Text = $"Власний символ «{definition.Name}» створено і вибрано у бібліотеці.";
        }
        catch (InvalidDataException ex) { ReportError(ex, "Створення символу"); }
    }

    private async Task ShowErrorLog()
    {
        var dialog = new Window
        {
            Title = "Журнал помилок", Width = 820, Height = 560,
            MinWidth = 520, MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var root = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel { Spacing = 5 };
        header.Children.Add(new TextBlock { Text = "Файл журналу:" });
        header.Children.Add(new TextBox { Text = AppLog.LogPath, IsReadOnly = true });
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var close = new Button { Content = "Закрити", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        close.Click += (_, _) => dialog.Close();
        DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
        root.Children.Add(new TextBox
        {
            Text = AppLog.ReadTail(), IsReadOnly = true, AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap, Margin = new Thickness(0, 10, 0, 0),
            FontFamily = FontFamily.Parse("Consolas, monospace")
        });
        dialog.Content = root;
        await dialog.ShowDialog(this);
    }

    private async Task ShowLibraryEditor()
    {
        canvas.Cancel();
        var dialog = new LibraryEditorWindow(session);
        await dialog.ShowDialog(this);
        status.Text = "Редактор бібліотек закрито.";
    }

    private async Task AddPage()
    {
        canvas.Cancel();
        var next = session.Document.Pages.Max(page => page.Number) + 1;
        var input = await ShowPageDialog(new PageDialogResult($"Аркуш {next}", PaperFormat.A3Landscape, true, 8, 6, new()));
        if (input is null) return;
        session.AddPage(input.Name, input.Format, input.ShowFrame, input.HorizontalZones, input.VerticalZones, input.TitleBlock);
        canvas.Fit(); status.Text = $"Додано аркуш {session.ActivePage.Number}: {session.ActivePage.Name}.";
    }

    private async Task EditPage()
    {
        canvas.Cancel(); var page = session.ActivePage;
        var input = await ShowPageDialog(new(page.Name, page.Format, page.ShowFrame, page.HorizontalZones, page.VerticalZones, page.TitleBlock));
        if (input is null) return;
        session.UpdatePage(page.Id, input.Name, input.Format, input.ShowFrame, input.HorizontalZones, input.VerticalZones, input.TitleBlock);
        canvas.Fit(); status.Text = "Параметри аркуша оновлено.";
    }

    private void DeletePage()
    {
        try
        {
            var removed = session.ActivePage.Name; canvas.Cancel(); session.DeletePage(session.Document.ActivePageId); canvas.Fit();
            status.Text = $"Аркуш «{removed}» видалено. Ctrl+Z відновить його.";
        }
        catch (InvalidDataException ex) { ReportError(ex, "Видалення аркуша"); }
    }

    private void CrossPageLink()
    {
        if (session.Selection.Count != 1)
        {
            status.Text = "Для міжсторінкового зв’язку виділи один елемент."; return;
        }
        var elementId = session.Selection.Single(); var pageId = session.Document.ActivePageId;
        if (session.Document.CrossPageReferences.Any(reference =>
            reference.FromPageId == pageId && reference.FromElementId == elementId ||
            reference.ToPageId == pageId && reference.ToElementId == elementId))
        {
            session.RemoveCrossPageReferences(pageId, elementId); pendingPageLink = null;
            status.Text = "Міжсторінковий зв’язок видалено. Ctrl+Z відновить його."; return;
        }
        if (pendingPageLink is not { } source)
        {
            pendingPageLink = (pageId, elementId);
            status.Text = "Початок зв’язку вибрано. Перейди на інший аркуш, виділи цільовий елемент і натисни ⇄."; return;
        }
        if (source.PageId == pageId)
        {
            status.Text = "Ціль має бути на іншому аркуші. Початковий елемент збережено."; return;
        }
        try
        {
            session.AddCrossPageReference(source.PageId, source.ElementId, pageId, elementId);
            pendingPageLink = null; status.Text = "Міжсторінковий зв’язок створено.";
        }
        catch (InvalidDataException ex) { ReportError(ex, "Міжсторінковий зв’язок"); }
    }

    private async Task<PageDialogResult?> ShowPageDialog(PageDialogResult value)
    {
        var dialog = new Window
        {
            Title = "Параметри аркуша", Width = 490, Height = 550, MinWidth = 420, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Назва" }); var name = new TextBox { Text = value.Name }; panel.Children.Add(name);
        panel.Children.Add(new TextBlock { Text = "Формат" });
        var formats = Enum.GetValues<PaperFormat>().Where(format => format != PaperFormat.Custom).ToArray();
        var formatPicker = new ComboBox { ItemsSource = formats, SelectedItem = value.Format == PaperFormat.Custom ? PaperFormat.A3Landscape : value.Format };
        formatPicker.ItemTemplate = new FuncDataTemplate<PaperFormat>((format, _) => new TextBlock { Text = FormatName(format) });
        panel.Children.Add(formatPicker);
        var frame = new CheckBox { Content = "Показувати рамку, зони та штамп", IsChecked = value.ShowFrame }; panel.Children.Add(frame);
        panel.Children.Add(new TextBlock
        {
            Text = "Зони ділять рамку для пошуку місця на кресленні, наприклад C4. Горизонталь позначається літерами, вертикаль — числами.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.DimGray
        });
        var horizontalRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        horizontalRow.Children.Add(new TextBlock { Text = "Горизонтальні зони (A, B, C…)", Width = 245, VerticalAlignment = VerticalAlignment.Center });
        var horizontal = new NumericUpDown
        {
            Name = "HorizontalZones", Minimum = 1, Maximum = 100, Increment = 1,
            Value = value.HorizontalZones, Width = 130, HorizontalContentAlignment = HorizontalAlignment.Left
        };
        horizontalRow.Children.Add(horizontal); panel.Children.Add(horizontalRow);
        var verticalRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        verticalRow.Children.Add(new TextBlock { Text = "Вертикальні зони (1, 2, 3…)", Width = 245, VerticalAlignment = VerticalAlignment.Center });
        var vertical = new NumericUpDown
        {
            Name = "VerticalZones", Minimum = 1, Maximum = 100, Increment = 1,
            Value = value.VerticalZones, Width = 130, HorizontalContentAlignment = HorizontalAlignment.Left
        };
        verticalRow.Children.Add(vertical); panel.Children.Add(verticalRow);
        panel.Children.Add(new TextBlock { Text = "Штамп", FontWeight = FontWeight.Bold, Margin = new Thickness(0, 5, 0, 0) });
        panel.Children.Add(new TextBlock { Text = "Проєкт" }); var project = new TextBox { Text = value.TitleBlock.Project }; panel.Children.Add(project);
        panel.Children.Add(new TextBlock { Text = "Назва креслення" }); var drawing = new TextBox { Text = value.TitleBlock.Drawing }; panel.Children.Add(drawing);
        var details = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var author = new TextBox { Text = value.TitleBlock.Author, PlaceholderText = "Автор", Width = 210 };
        var revision = new TextBox { Text = value.TitleBlock.Revision, PlaceholderText = "Ревізія", Width = 180 };
        details.Children.Add(author); details.Children.Add(revision); panel.Children.Add(details);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        AddButton(buttons, "Скасувати", () => dialog.Close(null));
        AddButton(buttons, "Застосувати", () =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || formatPicker.SelectedItem is not PaperFormat format) return;
            dialog.Close(new PageDialogResult(name.Text.Trim(), format, frame.IsChecked == true,
                (int)(horizontal.Value ?? 8), (int)(vertical.Value ?? 6), new PageTitleBlock
                {
                    Project = project.Text?.Trim() ?? "", Drawing = drawing.Text?.Trim() ?? "",
                    Author = author.Text?.Trim() ?? "", Revision = revision.Text?.Trim() ?? ""
                }));
        });
        panel.Children.Add(buttons); dialog.Content = panel;
        dialog.Opened += (_, _) => { name.Focus(); name.SelectAll(); };
        return await dialog.ShowDialog<PageDialogResult?>(this);
    }

    private async Task ShowElectricalIssues()
    {
        canvas.Cancel(); var issues = ElectricalRuleChecker.Check(session.Document.Elements);
        if (issues.Length == 0) { status.Text = "Перевірка завершена: обривів і дублів не знайдено."; return; }
        var dialog = new Window
        {
            Title = $"Перевірка схеми — проблем: {issues.Length}", Width = 620, Height = 430,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var root = new DockPanel { Margin = new Thickness(16) };
        var close = new Button { Content = "Закрити", Margin = new Thickness(0, 10, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        close.Click += (_, _) => dialog.Close(); DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
        root.Children.Add(new ListBox { ItemsSource = issues.Select(i => i.Message).ToArray() });
        dialog.Content = root; await dialog.ShowDialog(this);
        status.Text = $"Перевірка схеми: знайдено проблем — {issues.Length}.";
    }

    private async Task<bool> CanDiscard()
    {
        if (!IsDirty) return true;
        var dialog = new Window { Title = "Незбережені зміни", Width = 440, Height = 180, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 15 };
        panel.Children.Add(new TextBlock { Text = "Продовжити без збереження змін?" });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        AddButton(buttons, "Повернутися", () => dialog.Close(false));
        AddButton(buttons, "Без збереження", () => dialog.Close(true));
        panel.Children.Add(buttons); dialog.Content = panel;
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task Save()
    {
        canvas.Cancel();
        try
        {
            var projects = await WorkspaceFolders.Projects(StorageProvider);
            var file = await StorageProvider.SaveFilePickerAsync(new()
            {
                Title = "Зберегти проєкт", SuggestedFileName = Path.GetFileName(currentPath) ?? "drawing.ecad",
                DefaultExtension = "ecad", FileTypeChoices = [FileType], ShowOverwritePrompt = true,
                SuggestedStartLocation = projects
            });
            if (file is null) return;
            var path = file.TryGetLocalPath() ?? throw new IOException("Потрібен локальний файл.");
            ProjectFile.Save(path, session.Document);
            currentPath = path; saved = session.Document; UpdateTitle(); status.Text = "Документ збережено.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { ReportError(ex, "Не вдалося зберегти"); }
    }

    private async Task Open()
    {
        canvas.Cancel();
        if (!await CanDiscard()) return;
        try
        {
            var projects = await WorkspaceFolders.Projects(StorageProvider);
            var files = await StorageProvider.OpenFilePickerAsync(new()
            {
                Title = "Відкрити проєкт", AllowMultiple = false, FileTypeFilter = [FileType], SuggestedStartLocation = projects
            });
            if (files.Count == 0) return;
            var path = files[0].TryGetLocalPath() ?? throw new IOException("Потрібен локальний файл.");
            var document = ProjectFile.Open(path);
            session.Load(document); currentPath = path; saved = session.Document;
            canvas.Fit(); UpdateTitle(); status.Text = "Документ відкрито.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException)
        { ReportError(ex, "Не вдалося відкрити"); }
    }
}

public sealed record TextDialogResult(string Text, double HeightMm);
public sealed record CustomSymbolInput(string Name, string Prefix);
public sealed record PageDialogResult(string Name, PaperFormat Format, bool ShowFrame,
    int HorizontalZones, int VerticalZones, PageTitleBlock TitleBlock);
