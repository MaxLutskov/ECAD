using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
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
    private readonly ComboBox symbolPicker = new() { Width = 235, Margin = new Thickness(3) };
    private SymbolDefinition[] displayedCustomSymbols = [];
    private bool symbolRefreshPending;
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
        var title = new TextBlock { Text = "ECAD 0.8.1  /  Креслення в міліметрах", FontSize = 20, Margin = new Thickness(14, 12) };
        header.Children.Add(title);
        var files = new WrapPanel { Margin = new Thickness(8, 0) };
        header.Children.Add(files);
        AddAsyncButton(files, "Новий", async () => { if (await CanDiscard()) { canvas.Cancel(); session.Load(new()); saved = session.Document; currentPath = null; UpdateTitle(); } });
        AddAsyncButton(files, "Відкрити…", Open);
        AddAsyncButton(files, "Зберегти…", Save);
        AddButton(files, "↶ Скасувати", () => { canvas.Cancel(); session.Undo(); });
        AddButton(files, "↷ Повторити", () => { canvas.Cancel(); session.Redo(); });
        AddButton(files, "Групувати", () => { canvas.Cancel(); session.Group(); });
        AddButton(files, "Розгрупувати", () => { canvas.Cancel(); session.Ungroup(); });
        AddButton(files, "Копіювати", canvas.CopySelection);
        AddButton(files, "Вставити", canvas.PasteSelection);
        AddButton(files, "Повернути 90°", canvas.RotateSelection90);
        AddAsyncButton(files, "Перевірити схему", ShowElectricalIssues);
        AddAsyncButton(files, "Журнал помилок", ShowErrorLog);
        AddButton(files, "Видалити", () => { canvas.Cancel(); session.Delete(); });
        AddButton(files, "Аркуш у вікно", canvas.Fit);
        var tools = new WrapPanel { Margin = new Thickness(8, 4) };
        header.Children.Add(tools);
        AddButton(tools, "Вибір / переміщення", () => SetTool(null, "Потягни рамку з порожнього місця. Shift додає до вибору."));
        AddButton(tools, "Лінія", () => SetTool(ElementKind.Line, "Лінія: початок → кінцева точка (клік), або початок → довжина → Tab → кут → Enter."));
        AddButton(tools, "Прямокутник", () => SetTool(ElementKind.Rectangle, "Прямокутник: два кути або початок → ширина → Tab → висота → Enter."));
        AddButton(tools, "Коло", () => SetTool(ElementKind.Circle, "Коло: центр і точка або центр → радіус; Tab перемикає на діаметр."));
        AddButton(tools, "Розмір", () => SetTool(ElementKind.Dimension, "Розмір: обери дві точки / паралельні лінії, потім клацни місце напису."));
        AddButton(tools, "Вузол", () => SetTool(ElementKind.Junction, "Вузол: клацни кінець, сегмент або перетин ліній. Звичайний перетин без точки не є з’єднанням."));
        AddButton(tools, "Текст", () => SetTool(ElementKind.Text, "Текст: клацни місце розташування. Подвійний клік редагує наявний напис."));
        AddButton(tools, "Провідник", () => SetTool(ElementKind.Wire, "Провідник: кліки задають трасу; число → довжина → Tab → кут (кратно 90°) → Enter. Повторний Enter завершує."));
        symbolPicker.ItemTemplate = new FuncDataTemplate<SymbolDefinition>((item, _) => new TextBlock { Text = item?.Name ?? "" });
        symbolPicker.SelectionChanged += (_, _) => { if (symbolPicker.SelectedItem is SymbolDefinition item) canvas.ActiveSymbolKey = item.Key; };
        tools.Children.Add(symbolPicker); RefreshSymbolPicker();
        AddButton(tools, "Символ", () => SetTool(ElementKind.Symbol, "Символ: обери тип у списку та клацай місця розташування; Ctrl+R повертає виділений символ."));
        AddAsyncButton(tools, "Створити символ…", CreateCustomSymbol);
        var settings = new WrapPanel { Margin = new Thickness(12, 4, 12, 10) };
        header.Children.Add(settings);
        var grid = new CheckBox { Content = "Сітка", IsChecked = true, Margin = new Thickness(0, 0, 15, 0) };
        grid.IsCheckedChanged += (_, _) => { canvas.GridVisible = grid.IsChecked == true; canvas.InvalidateVisual(); };
        settings.Children.Add(grid);
        var snap = new CheckBox { Content = "Прив’язка", IsChecked = true, Margin = new Thickness(0, 0, 15, 0) };
        snap.IsCheckedChanged += (_, _) => canvas.SnapEnabled = snap.IsChecked == true;
        settings.Children.Add(snap);
        settings.Children.Add(new TextBlock { Text = "Крок, мм", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0) });
        var step = new NumericUpDown { Minimum = .1m, Maximum = 100, Increment = .5m, Value = 2.5m, Width = 140 };
        step.ValueChanged += (_, _) => { canvas.GridStep = (double)(step.Value ?? 2.5m); canvas.InvalidateVisual(); };
        settings.Children.Add(step);
        AddButton(settings, "Змінити параметри", canvas.EditSelectedGeometry);
        var footer = new StackPanel { Background = Brushes.White };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        footer.Children.Add(status);
        footer.Children.Add(new TextBlock
        {
            Text = "A3 · мм   |   Esc: вибір/переміщення · Колесо: масштаб · Середня кнопка: панорама · Shift: додати · Ctrl+C/V: копія · Ctrl+R: поворот",
            FontSize = 12, Foreground = Brushes.DimGray, Margin = new Thickness(12, 0, 12, 8)
        });
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
        if (ReferenceEquals(displayedCustomSymbols, session.Document.CustomSymbols) || symbolRefreshPending) return;
        symbolRefreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            symbolRefreshPending = false;
            RefreshSymbolPicker(canvas.ActiveSymbolKey);
        });
    }
    private void RefreshSymbolPicker(string? selectedKey = null)
    {
        var definitions = SymbolLibrary.Definitions(session.Document);
        displayedCustomSymbols = session.Document.CustomSymbols;
        symbolPicker.ItemsSource = definitions;
        symbolPicker.SelectedItem = definitions.FirstOrDefault(d => d.Key == selectedKey) ?? definitions[0];
    }
    private void UpdateTitle() => Title = $"{(IsDirty ? "* " : "")}{Path.GetFileName(currentPath) ?? "Новий аркуш"} — ECAD прототип";
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
            var file = await StorageProvider.SaveFilePickerAsync(new()
            {
                Title = "Зберегти проєкт", SuggestedFileName = Path.GetFileName(currentPath) ?? "drawing.ecad",
                DefaultExtension = "ecad", FileTypeChoices = [FileType], ShowOverwritePrompt = true
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
            var files = await StorageProvider.OpenFilePickerAsync(new() { Title = "Відкрити проєкт", AllowMultiple = false, FileTypeFilter = [FileType] });
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
