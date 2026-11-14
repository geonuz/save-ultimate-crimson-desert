using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public sealed record KnowledgeRow(uint Key, string Name, string InternalName, string Category, uint Level, uint Limit)
{
    public bool Learned => Level > 0;
    public bool Done => Learned;
    public string Status => Tr.Text(Learned ? "Learned" : "Unlearned");
    public string LevelText => $"{Level} / {Limit}";
    public string InternalLabel => InternalName.Replace("Knowledge_", "").Replace('_', ' ');
}

public partial class KnowledgePage : UserControl
{
    private sealed record Choice(uint Key, string Label);
    private KnowledgeRow[] entries = [];
    private bool updating;
    private MainViewModel Model => (MainViewModel)DataContext;
    private static KnowledgeCatalog Catalog => KnowledgeCatalog.Shared;
    public KnowledgePage()
    {
        InitializeComponent();
        Ui.Follow(this, nameof(MainViewModel.Knowledge), Refresh);
        IsVisibleChanged += (_, e) => { if ((bool)e.NewValue) FitColumns(); };
    }
    // Auto columns keep their first measurement, taken while the page was still hidden; remeasure once shown. The collapsed
    // layout must run before widths are restored, or Auto columns keep shrinking and star columns stop sharing the space.
    private void FitColumns() => Dispatcher.InvokeAsync(() =>
    {
        var widths = Rows.Columns.Select(c => c.Width).ToArray();
        foreach (var column in Rows.Columns.Where(c => c.Width.IsAuto)) column.Width = 0;
        Rows.UpdateLayout();
        for (int i = 0; i < widths.Length; i++) Rows.Columns[i].Width = widths[i];
    }, System.Windows.Threading.DispatcherPriority.Loaded);
    private static bool Visible(uint key) => !DlcContent.Hidden || !DlcContent.Shared.Knowledge.Contains(key);
    internal static string GroupName(KnowledgeGroup group) => Tr.Name("knowledge_groups", group.Key.ToString(), Tr.Language == "ko" ? group.NameKo : group.Name);
    private Choice Group(uint key, string prefix = "")
    {
        var group = Catalog.Groups.Single(g => g.Key == key);
        var members = group.Members.Where(Visible).ToArray();
        return new(key, $"{prefix}{GroupName(group)} ({members.Count(k => Model.Knowledge?.GetValueOrDefault(k) > 0)}/{members.Length})");
    }
    private void Refresh()
    {
        var selected = SelectedKeys().ToHashSet();
        uint root = (Root.SelectedItem as Choice)?.Key ?? 0;
        uint sub = (Subcategory.SelectedItem as Choice)?.Key ?? 0;
        updating = true;
        try
        {
            entries = Model.Knowledge is null ? [] : Catalog.ItemPaths.Where(p => Visible(p.Key)).Select(p =>
            {
                var text = KnowledgeText.Get(p.Key);
                return new KnowledgeRow(p.Key, text.Name, text.InternalName,
                    string.Join(" › ", p.Value.Select(k => GroupName(Catalog.Groups.Single(g => g.Key == k)))),
                    Model.Knowledge.GetValueOrDefault(p.Key), Catalog.LevelCounts[p.Key]);
            }).ToArray();
            Root.ItemsSource = new[] { new Choice(0, Tr.Text("All")) }.Concat(Model.Knowledge is null ? [] : Catalog.Roots.Select(k => Group(k))).ToArray();
            Root.SelectedIndex = Math.Max(0, Root.Items.Cast<Choice>().ToList().FindIndex(c => c.Key == root));
            FillSubcategories(sub);
            AllMaximum.IsEnabled = entries.Any(r => r.Level < r.Limit);
        }
        finally { updating = false; }
        Filter();
        foreach (var row in Rows.Items.Cast<KnowledgeRow>().Where(r => selected.Contains(r.Key))) Rows.SelectedItems.Add(row);
        Select();
    }
    private void FillSubcategories(uint selected = 0)
    {
        var choices = new List<Choice> { new(0, Tr.Text("map.all")) };
        void Add(uint parent, string prefix)
        {
            foreach (uint child in Catalog.Groups.Single(g => g.Key == parent).Children)
                if (Catalog.Groups.FirstOrDefault(g => g.Key == child) is { } group)
                { choices.Add(Group(child, prefix)); Add(child, prefix + GroupName(group) + " › "); }
        }
        if (Root.SelectedItem is Choice { Key: > 0 } root) Add(root.Key, "");
        Subcategory.ItemsSource = choices;
        Subcategory.SelectedIndex = Math.Max(0, choices.FindIndex(c => c.Key == selected));
        Subcategory.IsEnabled = choices.Count > 1;
    }
    private void OnRootChanged(object sender, SelectionChangedEventArgs e)
    {
        if (updating || Subcategory is null) return;
        updating = true;
        try { FillSubcategories(); } finally { updating = false; }
        Filter();
    }
    private void Filter()
    {
        if (updating || Rows is null) return;
        uint group = (Subcategory.SelectedItem as Choice)?.Key ?? 0;
        if (group == 0) group = (Root.SelectedItem as Choice)?.Key ?? 0;
        var members = group == 0 ? null : Catalog.Groups.Single(g => g.Key == group).Members.ToHashSet();
        string search = Search.Text.Trim();
        Ui.SetRows(Rows, entries.Where(r => (members is null || members.Contains(r.Key)) &&
            (State.SelectedIndex != 1 || r.Learned) && (State.SelectedIndex != 2 || !r.Learned) &&
            $"{r.Name} {r.InternalName} {r.Key} {r.Category}".Contains(search, StringComparison.OrdinalIgnoreCase)).ToArray());
        Count.Text = Model.Knowledge is null ? Tr.Text("knowledge.load_prompt") : Tr.Count(entries.Length, Rows.Items.Count, "ui.count_learned", entries.Count(r => r.Learned));
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
        Level.IsEnabled = Apply.IsEnabled = row is { Learned: true } && row.Level <= row.Limit;
        Level.SelectedItem = Apply.IsEnabled ? row!.Level : null;
        Hint.Text = selected.Length > 1 ? Tr.Format("knowledge.level_multiple", ("count", selected.Length))
            : row is null ? Tr.Text("knowledge.level_select") : !row.Learned ? ""
            : !Apply.IsEnabled ? Tr.Text("knowledge.level_unsupported") : Tr.Format("knowledge.level_range", ("current", row.Level), ("maximum", row.Limit));
        Hint.Visibility = Hint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Description.Text = row is null ? "" : KnowledgeText.Get(row.Key).Description is { Length: > 0 } text ? text : Tr.Text("No description available");
        Description.Visibility = SelectedTitle.Visibility = row is null ? Visibility.Collapsed : Visibility.Visible;
        SelectedTitle.Text = row?.Name ?? "";
    }
    private void OnFilterChanged(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel) Filter(); }
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

