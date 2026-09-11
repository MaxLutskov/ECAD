using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using ECAD.Core;

namespace ECAD.Desktop;

public sealed class NetAssignmentsPanel : StackPanel, IDisposable
{
    private readonly EditorSession session;
    private readonly ListBox assignments = new() { Name = "NetAssignments", Height = 240 };
    private readonly ComboBox nets = new() { Name = "AssignmentNet", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Button apply = new() { Name = "ApplyNetAssignment", Content = "Застосувати" };
    private readonly TextBlock message = new() { TextWrapping = TextWrapping.Wrap };
    private sealed record Entry(Guid Id, bool IsCore, string Label, Guid? NetId)
    { public override string ToString() => Label; }
    private sealed record NetOption(Guid? Id, string Label)
    { public override string ToString() => Label; }

    public NetAssignmentsPanel(EditorSession session)
    {
        this.session = session; Spacing = 8; Margin = new Thickness(4, 10);
        Children.Add(new TextBlock { Text = "Клеми та жили: призначення кіл", FontSize = 18 });
        Children.Add(new TextBlock { Text = "Щоб розділити коло, зніми його призначення, зміни провідники та признач потрібну гілку повторно. Кожна зміна підтримує Undo.", TextWrapping = TextWrapping.Wrap });
        Children.Add(assignments); Children.Add(new TextBlock { Text = "Електричне коло" });
        Children.Add(nets); Children.Add(apply); Children.Add(message);
        assignments.SelectionChanged += (_, _) => SelectNet();
        apply.Click += (_, _) =>
        {
            if (assignments.SelectedItem is not Entry entry || nets.SelectedItem is not NetOption net) return;
            try
            {
                if (entry.IsCore) session.SetCableCoreNet(entry.Id, net.Id); else session.SetTerminalNet(entry.Id, net.Id);
                message.Text = "Призначення збережено.";
            }
            catch (InvalidDataException ex) { message.Text = ex.Message; }
        };
        session.Changed += Refresh; Refresh();
    }

    private void Refresh()
    {
        var selected = assignments.SelectedItem as Entry;
        var options = new[] { new NetOption(null, "Без призначення") }.Concat(session.Document.Nets
            .OrderBy(net => net.Number).Select(net => new NetOption(net.Id, $"{net.Number} · {net.Potential ?? "—"}"))).ToArray();
        nets.ItemsSource = options;
        string Number(Guid? id) => session.Document.Nets.FirstOrDefault(net => net.Id == id)?.Number ?? "без кола";
        var entries = session.Document.TerminalStrips.SelectMany(strip => strip.Terminals.Select(terminal =>
            new Entry(terminal.Id, false, $"Клема {strip.Tag}:{terminal.Number}, рівень {terminal.Level} → {Number(terminal.NetId)}", terminal.NetId)))
            .Concat(session.Document.Cables.SelectMany(cable => cable.Cores.Select(core =>
                new Entry(core.Id, true, $"Жила {cable.Tag}:{core.Designation} → {Number(core.NetId)}", core.NetId)))).ToArray();
        assignments.ItemsSource = entries;
        assignments.SelectedItem = entries.FirstOrDefault(entry => selected is not null && entry.Id == selected.Id && entry.IsCore == selected.IsCore) ?? entries.FirstOrDefault();
        message.Text = entries.Length == 0 ? "Додай клемник або кабель, щоб призначити кола." : "";
        SelectNet();
    }

    private void SelectNet()
    {
        var entry = assignments.SelectedItem as Entry;
        nets.SelectedItem = (nets.ItemsSource as NetOption[])?.FirstOrDefault(net => net.Id == entry?.NetId);
        apply.IsEnabled = entry is not null; nets.IsEnabled = entry is not null;
    }

    public void Dispose() => session.Changed -= Refresh;
}
