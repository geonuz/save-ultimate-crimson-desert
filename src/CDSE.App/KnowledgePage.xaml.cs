using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using CDSE.Core.Saves;

namespace CDSE.App;

public sealed record KnowledgeRow(uint Key, string Name, string InternalName, string Category, uint Level, uint Limit, uint? Record = null)
{
    public bool Learned => Level > 0;
    public bool Done => Learned;
    public string Status => Tr.Text(Learned ? "Learned" : "Unlearned");
    public string LevelText => $"{Level} / {Limit}";
    /// <summary>The save's learn-from record as the game names it, with what it means ("Shop · Bought at a shop"). A learned
    /// entry without one says so (the game writes none for some, such as the main characters); an unlearned one is blank.</summary>
    public string RecordText => Record is not uint value ? Learned ? Tr.Text("knowledge.from.missing") : "" : Enum.IsDefined((LearnFrom)value)
        ? $"{(LearnFrom)value} · {Tr.Text($"knowledge.from.{(LearnFrom)value}")}" : $"{value} · {Tr.Text("knowledge.from.unknown")}";
}

/// <summary>A journal category in the left tree: the entries under it (after the filters) and how many are learned.
/// Key 0 is the "All" node at the top.</summary>
public partial class KnowledgeNode(uint key, string name, KnowledgeRow[] rows, KnowledgeNode[] children) : ObservableObject
{
    public uint Key { get; } = key;
    public string Name { get; } = name;
    public KnowledgeRow[] Rows { get; } = rows;
    public KnowledgeNode[] Children { get; } = children;
    public bool Expanded { get; set; }
    public bool Selected { get; set; }
    public string Count => $"{Rows.Count(r => r.Learned):N0} / {Rows.Length:N0}";
    public bool Done => Rows.Length > 0 && Rows.All(r => r.Learned);
    public bool Dim => !Done && !Rows.Any(r => r.Learned);
    public string Status => Tr.Text(Done ? "knowledge.group.all" : Dim ? "knowledge.group.none" : "knowledge.group.partial");
}

public partial class KnowledgePage : UserControl
{
    private KnowledgeRow[] entries = [];
    private KnowledgeNode[] nodes = [];
    private uint? selectedGroup = 0;
    private bool updating;
    private MainViewModel Model => (MainViewModel)DataContext;
    private static KnowledgeCatalog Catalog => KnowledgeCatalog.Shared;
    public KnowledgePage()
    {
        InitializeComponent();
        Ui.Follow(this, nameof(MainViewModel.Knowledge), Refresh);
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) Ui.FitAutoColumns(Rows); };
    }
    private static bool Visible(uint key) => !DlcContent.Hidden || !DlcContent.Shared.Knowledge.Contains(key);
    internal static string GroupName(KnowledgeGroup group) => Tr.Name("knowledge_groups", group.Key.ToString(), Tr.Language == "ko" ? group.NameKo : group.Name);
    private static IEnumerable<KnowledgeNode> All(IEnumerable<KnowledgeNode> source) => source.SelectMany(n => new[] { n }.Concat(All(n.Children)));
    private void Refresh()
    {
        entries = Model.Knowledge is null ? [] : Catalog.ItemPaths.Where(p => Visible(p.Key)).Select(p =>
        {
            var text = KnowledgeText.Get(p.Key);
            return new KnowledgeRow(p.Key, text.Name, text.InternalName,
                string.Join(" › ", p.Value.Select(k => GroupName(Catalog.Groups.Single(g => g.Key == k)))),
                Model.Knowledge.GetValueOrDefault(p.Key), Catalog.LevelCounts[p.Key],
                Model.LearnRecords.TryGetValue(p.Key, out uint record) ? record : null);
        }).ToArray();
        // The whole-journal action ignores the filters, as its help text says.
        AllMaximum.IsEnabled = entries.Any(r => r.Level < r.Limit);
        Filter();
    }
    private void Filter()
    {
        if (updating || Rows is null || DataContext is not MainViewModel) return;
        var selectedKeys = SelectedKeys().ToHashSet();
        var expanded = All(nodes).Where(n => n.Expanded).Select(n => n.Key).ToHashSet();
        string search = Search.Text.Trim();
        var shown = entries.Where(r => (State.SelectedIndex != 1 || r.Learned) && (State.SelectedIndex != 2 || !r.Learned) &&
            (search.Length == 0 || $"{r.Name} {r.InternalName} {r.Key} {r.Category} {r.RecordText}".Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
        // Each entry belongs to every category on its path, so a category lists its subcategories' entries too.
        var byGroup = shown.SelectMany(r => Catalog.ItemPaths[r.Key].Select(g => (g, r))).ToLookup(p => p.g, p => p.r);
        KnowledgeNode? Build(uint key)
        {
            var group = Catalog.Groups.FirstOrDefault(g => g.Key == key);
            if (group is null || !byGroup.Contains(key)) return null;
            var children = group.Children.Select(Build).OfType<KnowledgeNode>().ToArray();
            return new(key, GroupName(group), byGroup[key].ToArray(), children) { Expanded = search.Length > 0 || expanded.Contains(key) };
        }
        updating = true;
        try
        {
            var roots = Model.Knowledge is null ? [] : Catalog.Roots.Select(Build).OfType<KnowledgeNode>().ToArray();
            nodes = Model.Knowledge is null ? [] : [new(0, Tr.Text("All"), shown, []), .. roots];
            var target = All(nodes).FirstOrDefault(n => n.Key == selectedGroup) ?? nodes.FirstOrDefault();
            if (target is not null) target.Selected = true;
            selectedGroup = target?.Key;
            Tree.ItemsSource = nodes;
        }
        finally { updating = false; }
        Count.Text = Model.Knowledge is null ? Tr.Text("knowledge.load_prompt") : Tr.Count(entries.Length, shown.Length, "ui.count_learned", entries.Count(r => r.Learned));
        ShowRows(selectedKeys);
    }
    private KnowledgeNode? SelectedNode => All(nodes).FirstOrDefault(n => n.Key == selectedGroup);
    private void ShowRows(HashSet<uint>? keep = null)
    {
        var node = SelectedNode;
        Title.Text = node?.Name ?? Tr.Text(Model.Knowledge is null ? "knowledge.load_prompt" : "map.select_category");
        Ui.SetRows(Rows, node?.Rows ?? []);
        if (keep is not null) foreach (var row in Rows.Items.Cast<KnowledgeRow>().Where(r => keep.Contains(r.Key))) Rows.SelectedItems.Add(row);
        Select();
    }
    private uint[] SelectedKeys() => Rows.SelectedItems.Cast<KnowledgeRow>().Select(r => r.Key).ToArray();
    private void Select()
    {
        var selected = Rows.SelectedItems.Cast<KnowledgeRow>().ToArray();
        Learn.IsEnabled = selected.Any(r => !r.Learned);
        Unlearn.IsEnabled = selected.Any(r => r.Learned);
        Maximum.IsEnabled = selected.Any(r => r.Learned && r.Level < r.Limit);
        var row = selected.Length == 1 ? selected[0] : null;
        Level.ItemsSource = row is null ? Array.Empty<uint>() : Enumerable.Range(1, (int)row.Limit).Select(i => (uint)i).ToArray();
        bool settable = row is { Learned: true } && row.Level <= row.Limit;
        Level.IsEnabled = Apply.IsEnabled = settable;
        Level.SelectedItem = settable ? row!.Level : null;
        Hint.Text = selected.Length > 1 ? Tr.Format("knowledge.level_multiple", ("count", selected.Length))
            : row is null ? Tr.Text("knowledge.level_select") : !row.Learned ? ""
            : !settable ? Tr.Text("knowledge.level_unsupported") : "";
        Hint.Visibility = Hint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void OnGroupSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        // A rebuilt tree selects its node again once the container exists; the table already shows that node's rows.
        if (updating || e.NewValue is not KnowledgeNode node || node == SelectedNode) return;
        selectedGroup = node.Key;
        ShowRows();
    }
    private void OnFilterChanged(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel && IsInitialized) Filter(); }
    private TypingDelay? typing;
    private void OnSearchChanged(object sender, TextChangedEventArgs e) => (typing ??= new(() => OnFilterChanged(this, e))).Restart(sender);
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) { if (Level is not null) Select(); }
    private async void OnLearn(object sender, RoutedEventArgs e) => await Model.EditKnowledgeAsync(SelectedKeys(), KnowledgeAction.Learn);
    private async void OnUnlearn(object sender, RoutedEventArgs e) => await Model.EditKnowledgeAsync(SelectedKeys(), KnowledgeAction.Unlearn);
    private async void OnMaximum(object sender, RoutedEventArgs e) => await Model.EditKnowledgeAsync(SelectedKeys(), KnowledgeAction.Maximum);
    private async void OnAllMaximum(object sender, RoutedEventArgs e) => await Model.EditKnowledgeAsync([], KnowledgeAction.AllMaximum);
    private async void OnApply(object sender, RoutedEventArgs e) { if (Level.SelectedItem is uint value) await Model.EditKnowledgeAsync(SelectedKeys(), KnowledgeAction.Level, value); }
    private void OnCopy(object sender, RoutedEventArgs e) { if (SelectedKeys() is { Length: > 0 } keys) Clipboard.SetText(string.Join(", ", keys)); }
}
