using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ECAD.Core;

namespace ECAD.Desktop;

public sealed class ElectricalProjectWindow : Window
{
    private readonly EditorSession session;
    private readonly TabControl tabs = new();
    private readonly NetAssignmentsPanel assignments;
    private readonly TabItem assignmentsTab;
    private readonly TextBlock summary = new() { Margin = new Thickness(4), Foreground = Brushes.DimGray };

    public ElectricalProjectWindow(EditorSession session)
    {
        this.session = session;
        assignments = new NetAssignmentsPanel(session);
        assignmentsTab = new TabItem { Header = "Призначення", Content = new ScrollViewer { Content = assignments } };
        Closed += (_, _) => assignments.Dispose();
        Title = "Електрична модель проєкту"; Width = 850; Height = 620; MinWidth = 650; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(14) };
        var top = new StackPanel { Spacing = 8 };
        top.Children.Add(new TextBlock { Text = "Електрична модель", FontSize = 21, FontWeight = FontWeight.SemiBold });
        top.Children.Add(summary);
        var actions = new WrapPanel();
        Add(actions, "Автонумерація кіл", () => RunCommand(() => session.RenumberNets()));
        Add(actions, "Очистити невикористані кола", () => RunCommand(session.RemoveUnusedNets));
        Add(actions, "+ Клемник", AddTerminalStrip);
        Add(actions, "+ Кабель", AddCable);
        Add(actions, "Закрити", Close);
        top.Children.Add(actions); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        root.Children.Add(tabs); Content = root; session.Changed += Refresh; Closed += (_, _) => session.Changed -= Refresh;
        Refresh();
    }

    private void Refresh()
    {
        var selectedTab = tabs.SelectedIndex;
        var doc = session.Document;
        summary.Text = $"Пристроїв: {doc.Devices.Length} · Кіл: {doc.Nets.Length} · Клемників: {doc.TerminalStrips.Length} · Кабелів: {doc.Cables.Length}";
        tabs.ItemsSource = new[]
        {
            Tab("Пристрої", doc.Devices.OrderBy(item => item.Tag).Select(device =>
                $"{device.Tag}  ·  {device.Description ?? "без опису"}  ·  функцій: {device.Functions.Length}")),
            Tab("Кола", doc.Nets.OrderBy(item => item.Number).Select(net =>
                $"{net.Number}  ·  {net.Potential ?? "потенціал не задано"}  ·  {net.SignalClass ?? "клас не задано"}  ·  {net.CrossSectionMm2?.ToString("0.###") ?? "—"} мм²")),
            Tab("Клемники", doc.TerminalStrips.OrderBy(item => item.Tag).Select(strip =>
                $"{strip.Tag}  ·  {strip.Description ?? "без опису"}  ·  клем: {strip.Terminals.Length}")),
            Tab("Кабелі", doc.Cables.OrderBy(item => item.Tag).Select(cable =>
                $"{cable.Tag}  ·  {cable.Type ?? "тип не задано"}  ·  {cable.CoreCount} жил  ·  {cable.From ?? "—"} → {cable.To ?? "—"}")),
            assignmentsTab
        };
        tabs.SelectedIndex = Math.Max(0, selectedTab);
    }

    private static TabItem Tab(string header, IEnumerable<string> rows) => new()
    {
        Header = header,
        Content = new ListBox { ItemsSource = rows.DefaultIfEmpty("Немає записів").ToArray(), Margin = new Thickness(0, 10, 0, 0) }
    };

    private async void AddTerminalStrip()
    {
        var input = await ShowCreateDialog("Новий клемник", "X1", "10", "Опис клемника");
        if (input is null) return;
        try { session.CreateTerminalStrip(input.Tag, input.Description, input.Count); }
        catch (Exception ex) when (ex is InvalidDataException or FormatException) { await ShowMessage(ex.Message); }
    }

    private async void AddCable()
    {
        var input = await ShowCreateDialog("Новий кабель", "W1", "4", "Марка кабелю");
        if (input is null) return;
        try { session.CreateCable(input.Tag, input.Description, input.Count, null, null, null, null); }
        catch (Exception ex) when (ex is InvalidDataException or FormatException) { await ShowMessage(ex.Message); }
    }

    private async Task<CreateElectricalItem?> ShowCreateDialog(string title, string tagValue, string countValue, string descriptionHint)
    {
        var dialog = new Window { Title = title, Width = 400, Height = 285, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Позначення" }); var tag = new TextBox { Text = tagValue }; panel.Children.Add(tag);
        panel.Children.Add(new TextBlock { Text = "Кількість" }); var count = new NumericUpDown { Minimum = 1, Maximum = 10000, Value = decimal.Parse(countValue), Width = 140, HorizontalAlignment = HorizontalAlignment.Left }; panel.Children.Add(count);
        panel.Children.Add(new TextBlock { Text = descriptionHint }); var description = new TextBox(); panel.Children.Add(description);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Add(buttons, "Скасувати", () => dialog.Close(null));
        Add(buttons, "Створити", () => dialog.Close(new CreateElectricalItem(tag.Text?.Trim() ?? "", (int)(count.Value ?? 1), description.Text)));
        panel.Children.Add(buttons); dialog.Content = panel;
        return await dialog.ShowDialog<CreateElectricalItem?>(this);
    }

    private async Task ShowMessage(string message)
    {
        var dialog = new Window { Title = "Помилка", Width = 420, Height = 160, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var panel = new StackPanel { Margin = new Thickness(18), Spacing = 12, Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap } } };
        Add(panel, "Закрити", dialog.Close); dialog.Content = panel; await dialog.ShowDialog(this);
    }

    private async void RunCommand(Action command)
    {
        try { command(); }
        catch (InvalidDataException ex) { await ShowMessage(ex.Message); }
    }

    private static void Add(Panel panel, string text, Action action)
    {
        var button = new Button { Content = text, Margin = new Thickness(3), Padding = new Thickness(10, 6) };
        button.Click += (_, _) => action(); panel.Children.Add(button);
    }

    private sealed record CreateElectricalItem(string Tag, int Count, string? Description);
}
