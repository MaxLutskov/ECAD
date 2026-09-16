using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace ECAD.Desktop;

public sealed record PanelPlacement(string Id, string Side, bool Visible = true);
public sealed record WorkspaceLayout(int Version, double Left, double Right, double Bottom, PanelPlacement[] Panels);

public sealed class WorkspacePanels : Grid
{
    public static string? SettingsDirectoryOverride { get; set; }
    private readonly string settingsPath;
    private readonly Dictionary<string, (string Title, Control View)> panels = new();
    private readonly Dictionary<string, PanelPlacement> placements = new();
    private readonly Dictionary<string, TabControl> hosts = new();
    private readonly Dictionary<string, string> defaults = new();
    private readonly Action<string> report;
    public IReadOnlyCollection<PanelPlacement> Placements => placements.Values;

    public WorkspacePanels(Control center, Action<string> report)
    {
        this.report = report;
        settingsPath = Path.Combine(SettingsDirectoryOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ECAD"), "workspace.json");
        ColumnDefinitions = new("230,5,*,5,300"); RowDefinitions = new("*,5,180");
        foreach (var side in new[] { "left", "right", "bottom" })
        {
            var tabs = new TabControl { Name = "Dock" + side, MinWidth = 0, MinHeight = 0 };
            hosts[side] = tabs; Children.Add(tabs);
        }
        SetColumn(hosts["right"], 4); SetRow(hosts["bottom"], 2); SetColumnSpan(hosts["bottom"], 5);
        SetColumn(center, 2); Children.Add(center);
        AddSplitter(1, 0, false); AddSplitter(3, 0, false); AddSplitter(0, 1, true);
        SizeChanged += (_, _) => UpdateLimits();
    }
    private void AddSplitter(int column, int row, bool horizontal)
    {
        var splitter = new GridSplitter { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = horizontal ? GridResizeDirection.Rows : GridResizeDirection.Columns, ResizeBehavior = GridResizeBehavior.PreviousAndNext };
        SetColumn(splitter, column); SetRow(splitter, row); if (horizontal) SetColumnSpan(splitter, 5); Children.Add(splitter);
    }
    public void Register(string id, string title, Control view, string side)
    { panels.Add(id, (title, view)); defaults[id] = side; placements[id] = new(id, side); }
    public void Move(string id, string side)
    {
        if (!hosts.ContainsKey(side)) throw new ArgumentException("Unknown dock side");
        placements[id] = new(id, side); Rebuild(); Save();
    }
    public void Toggle(string id) { placements[id] = placements[id] with { Visible = !placements[id].Visible }; Rebuild(); Save(); }
    public void Show(string id)
    {
        if (!placements[id].Visible) { placements[id] = placements[id] with { Visible = true }; Rebuild(); }
        var host = hosts[placements[id].Side];
        host.SelectedItem = host.Items.OfType<TabItem>().Single(item => Equals(item.Tag, id));
    }
    public void Reset()
    {
        foreach (var id in panels.Keys) placements[id] = new(id, defaults[id]);
        ColumnDefinitions[0].Width = new(230); ColumnDefinitions[4].Width = new(300); RowDefinitions[2].Height = new(180);
        Rebuild(); Save(useRequestedSize: true);
    }
    public void Restore()
    {
        try
        {
            if (File.Exists(settingsPath))
            {
                if (new FileInfo(settingsPath).Length > 65536) throw new InvalidDataException("Розкладка надто велика.");
                var saved = JsonSerializer.Deserialize<WorkspaceLayout>(File.ReadAllText(settingsPath));
                if (saved is { Version: 1, Panels: not null } && double.IsFinite(saved.Left) && double.IsFinite(saved.Right) && double.IsFinite(saved.Bottom))
                {
                    ColumnDefinitions[0].Width = new(Math.Clamp(saved.Left, 160, 360)); ColumnDefinitions[4].Width = new(Math.Clamp(saved.Right, 200, 440));
                    RowDefinitions[2].Height = new(Math.Clamp(saved.Bottom, 100, 320));
                    foreach (var p in saved.Panels.Where(p => p is not null && p.Id is not null && p.Side is not null && panels.ContainsKey(p.Id) && hosts.ContainsKey(p.Side))) placements[p.Id] = p;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { report("Не вдалося прочитати розкладку. Використано стандартну."); }
        Rebuild();
    }
    public void Save(bool useRequestedSize = false)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
            double Width(int index) => useRequestedSize || ColumnDefinitions[index].ActualWidth <= 0 ? ColumnDefinitions[index].Width.Value : ColumnDefinitions[index].ActualWidth;
            var bottom = useRequestedSize || RowDefinitions[2].ActualHeight <= 0 ? RowDefinitions[2].Height.Value : RowDefinitions[2].ActualHeight;
            var value = new WorkspaceLayout(1, Math.Max(160, Width(0)), Math.Max(200, Width(4)), Math.Max(100, bottom), placements.Values.ToArray());
            var temp = settingsPath + ".tmp"; File.WriteAllText(temp, JsonSerializer.Serialize(value)); File.Move(temp, settingsPath, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { report("Не вдалося зберегти розкладку панелей."); }
    }
    private void Rebuild()
    {
        var active = hosts.ToDictionary(pair => pair.Key, pair => (pair.Value.SelectedItem as TabItem)?.Tag);
        foreach (var host in hosts.Values)
        { foreach (var item in host.Items.OfType<TabItem>()) item.Content = null; host.Items.Clear(); }
        foreach (var p in placements.Values.Where(p => p.Visible))
        {
            var entry = panels[p.Id]; var item = new TabItem { Header = entry.Title, Content = entry.View, Tag = p.Id };
            var menu = new ContextMenu();
            foreach (var (side, title) in new[] { ("left", "Ліворуч"), ("right", "Праворуч"), ("bottom", "Унизу") })
            { var move = new MenuItem { Header = title }; move.Click += (_, _) => Move(p.Id, side); menu.Items.Add(move); }
            var hide = new MenuItem { Header = "Приховати" }; hide.Click += (_, _) => Toggle(p.Id); menu.Items.Add(hide); item.ContextMenu = menu;
            hosts[p.Side].Items.Add(item);
        }
        foreach (var (side, host) in hosts)
        {
            host.IsVisible = host.ItemCount > 0;
            host.SelectedItem = host.Items.OfType<TabItem>().FirstOrDefault(item => Equals(item.Tag, active[side])) ?? host.Items.OfType<TabItem>().FirstOrDefault();
        }
        UpdateLimits();
    }
    private void UpdateLimits()
    {
        ColumnDefinitions[0].MinWidth = hosts["left"].IsVisible ? 160 : 0;
        ColumnDefinitions[0].MaxWidth = hosts["left"].IsVisible ? Math.Clamp(Bounds.Width * .27, 160, 420) : 0;
        ColumnDefinitions[4].MinWidth = hosts["right"].IsVisible ? 200 : 0;
        ColumnDefinitions[4].MaxWidth = hosts["right"].IsVisible ? Math.Clamp(Bounds.Width * .32, 200, 480) : 0;
        RowDefinitions[2].MinHeight = hosts["bottom"].IsVisible ? 100 : 0;
        RowDefinitions[2].MaxHeight = hosts["bottom"].IsVisible ? Math.Clamp(Bounds.Height * .35, 100, 360) : 0;
    }
}
