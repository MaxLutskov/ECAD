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

public sealed partial class MainWindow : Window
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
    private Guid savedRevision;
    private string? currentPath;
    private bool allowClose;
    private bool askingClose;
    private bool IsDirty => savedRevision != session.ContentRevision;

    public MainWindow()
    {
        savedRevision = session.ContentRevision;
        Title = "ECAD — прототип"; Width = 1280; Height = 850; MinWidth = 900; MinHeight = 600;
        canvas = new DrawingCanvas(session);
        BuildWorkspace();
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
        RefreshWorkspace();
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
    private static void AddButton(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(3), Padding = new Thickness(10, 6) };
        button.Click += (_, _) => action(); panel.Children.Add(button);
    }
    public void ReportError(Exception exception, string context, bool writeToLog = true)
    {
        var reference = writeToLog ? Guid.NewGuid().ToString("N")[..8] : "";
        if (writeToLog) AppLog.Write($"{context} [{reference}]", exception);
        status.Text = $"{context}: {exception.Message} Деталі записано в журнал. {reference}";
        ShowNotice(status.Text);
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
        canvas.Cancel(); ercSummary.Text = "Перевірка…"; Panels.Show("erc");
        var revision = session.ContentRevision;
        var document = session.Document;
        var issues = await RunBackground(_ => ElectricalRuleChecker.Check(document));
        if (revision != session.ContentRevision) { ercSummary.Text = "Документ змінено під час перевірки. Запусти F7 повторно."; return; }
        ercIssues.ItemsSource = issues;
        ercSummary.Text = issues.Length == 0 ? "Перевірка завершена: повідомлень немає." : $"Повідомлень: {issues.Length}. Обери рядок для переходу до об’єкта.";
        status.Text = ercSummary.Text;
    }

    private async Task ShowElectricalProject()
    {
        canvas.Cancel(); await new ElectricalProjectWindow(session).ShowDialog(this);
        status.Text = "Електричну модель проєкту оновлено.";
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
            var document = session.Document; var revision = session.ContentRevision;
            status.Text = "Збереження документа…";
            await RunBackground(token => { ProjectFile.Save(path, document, token); return true; }, discardResultOnCancel: false);
            currentPath = path; savedRevision = revision; UpdateTitle(); status.Text = IsDirty ? "Файл збережено; нові зміни ще не записані." : "Документ збережено.";
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
            var revision = session.ContentRevision;
            status.Text = "Завантаження документа…";
            var document = await RunBackground(token => ProjectFile.Open(path, token));
            if (revision != session.ContentRevision && !await CanDiscard()) return;
            session.Load(document); currentPath = path; savedRevision = session.ContentRevision;
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
