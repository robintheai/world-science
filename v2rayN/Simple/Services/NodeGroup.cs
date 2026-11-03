namespace WorldScience.Services;
public sealed class NodeGroup(string id, string name, List<NodeRow> nodes, bool expanded) : INotifyPropertyChanged
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public List<NodeRow> Nodes { get; } = nodes;
    public bool Expanded { get; private set; } = expanded;
    public string Detail => $"{Nodes.Count} 个节点";
    public string Chevron => Expanded ? "⌄" : "›";
    public event PropertyChangedEventHandler? PropertyChanged;
    public void Toggle() { Expanded = !Expanded; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null)); }
}
