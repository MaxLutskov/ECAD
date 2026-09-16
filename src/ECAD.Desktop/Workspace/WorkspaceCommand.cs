using System.Windows.Input;
using Avalonia.Input;

namespace ECAD.Desktop;

public sealed class WorkspaceCommand(string id, string title, string category, string icon,
    Func<Task> action, Func<bool>? available, Action<Exception> report, KeyGesture? shortcut = null, Func<bool>? active = null) : ICommand
{
    private bool running;
    public string Id { get; } = id;
    public string Title { get; } = title;
    public string Category { get; } = category;
    public string Icon { get; } = icon;
    public KeyGesture? Shortcut { get; } = shortcut;
    public bool IsActive => active?.Invoke() == true;
    public string Hint => Title + (Shortcut is null ? "" : $" ({Shortcut})");
    public bool CanExecute(object? parameter) => !running && (available?.Invoke() ?? true);
    public event EventHandler? CanExecuteChanged;
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        running = true; Refresh();
        try { await action(); }
        catch (Exception ex) { report(ex); }
        finally { running = false; Refresh(); }
    }
}

public sealed class WorkspaceCommands
{
    private readonly Dictionary<string, WorkspaceCommand> commands = new(StringComparer.Ordinal);
    public IEnumerable<WorkspaceCommand> All => commands.Values;
    public WorkspaceCommand this[string id] => commands[id];
    public void Add(WorkspaceCommand command) => commands.Add(command.Id, command);
    public void Refresh() { foreach (var command in All) command.Refresh(); }
    public bool Handle(KeyEventArgs e)
    {
        var command = All.FirstOrDefault(c => c.Shortcut?.Matches(e) == true);
        if (command is null) return false;
        command.Execute(null); e.Handled = true; return true;
    }
}
