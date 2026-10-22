using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public partial class ChallengeNode(string identity, string name, uint? root, uint[] roots, ChallengeNode[] children) : ObservableObject
{
    public string Identity { get; } = identity;
    public string Name { get; } = name;
    public uint? Root { get; } = root;
    public uint[] Roots { get; } = roots;
    public ChallengeNode[] Children { get; } = children;
    public bool Expanded { get; set; }
    public bool Selected { get; set; }
    [ObservableProperty] private string count = "";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private bool done;
}
public sealed record ChallengeRow(uint Key, string Role, string Name, string InternalName, string Status, string Progress, bool Done);

public partial class ChallengePage : UserControl
{
    private static ChallengeCatalog Catalog => ChallengeCatalog.Shared;
    private MainViewModel Model => (MainViewModel)DataContext;
    private ChallengeNode[] nodes = [];
    private ChallengeNode? selected;
    private ChallengeState? previous;
    private bool updating;
    private string language = Tr.Language;
    public ChallengePage()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.PropertyChanged -= Changed;
            if (e.NewValue is MainViewModel model) { model.PropertyChanged += Changed; Populate(); }
        };
    }
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.Challenges)) return;
        bool membership = Incomplete.IsChecked == true && ChallengeCatalog.Completion.Roots.Any(k =>
            (previous?.Mission.GetValueOrDefault(k)?.State == 5) != (Model.Challenges?.Mission.GetValueOrDefault(k)?.State == 5));
        if (previous is null || Model.Challenges is null || membership || language != Tr.Language) { language = Tr.Language; Populate(); }
        else { foreach (var node in All(nodes)) UpdateNode(node); ShowDetails(); }
        previous = Model.Challenges;
    }
    private static IEnumerable<ChallengeNode> All(IEnumerable<ChallengeNode> source)
        => source.SelectMany(n => new[] { n }.Concat(All(n.Children)));
    private static string DisplayName(string korean, string english, string fallback) => GameText.Display(Tr.Language == "ko" && korean.Length > 0 ? korean : english.Length > 0 ? english : fallback);
    // Translated name packs keep the game's raw {StaticInfo:...} references, so resolve after the lookup too.
    private static string DisplayName(ChallengeMission m) => GameText.Display(Tr.Name("missions", m.Key.ToString(), DisplayName(m.NameKo, m.Name, m.InternalName)));
    private bool Done(uint key) => Model.Challenges?.Mission.GetValueOrDefault(key)?.State == 5;
    private string State(uint key)
    {
        var record = Model.Challenges?.Mission.GetValueOrDefault(key);
        if (record?.State == 5) return Tr.Text("Completed");
        var rule = Catalog.Missions[key].ObjectiveEdit;
        if (record?.State == 2 && rule?.Goals is { } goals && goals.All(Done) &&
            Model.Challenges!.Mission.GetValueOrDefault(rule.Artifact)?.State == 2) return Tr.Text("challenge.await_artifact");
        return Tr.Text(record is null || record.State is <= 4 ? "challenge.unfinished" : "challenge.unknown_state");
    }
    private void UpdateNode(ChallengeNode node)
    {
        node.Count = node.Root.HasValue ? "" : $"{node.Roots.Count(Done)} / {node.Roots.Length}";
        node.Done = node.Root is uint key ? Done(key) : node.Roots.Length > 0 && node.Roots.All(Done);
        node.Status = node.Root is uint root ? State(root) : Tr.Text(node.Done ? "Completed" : "challenge.unfinished");
    }
    private void Populate()
    {
        if (Tree is null || updating) return;
        var old = All(nodes).ToDictionary(n => n.Identity);
        string? identity = selected?.Identity;
        string search = Search.Text.Trim();
        bool Matches(string value) => value.Contains(search, StringComparison.OrdinalIgnoreCase);
        var groups = new List<ChallengeNode>();
        if (Model.Challenges is not null)
            foreach (var group in Catalog.Groups)
            {
                var quests = new List<ChallengeNode>();
                foreach (uint key in group.Quests)
                {
                    var quest = Catalog.Quests[key];
                    var roots = new List<ChallengeNode>();
                    foreach (uint root in quest.Missions!)
                    {
                        if (Incomplete.IsChecked == true && Done(root)) continue;
                        string text = $"{group.Key} {group.Name} {group.NameKo} {group.InternalName} {quest.Key} {quest.Name} {quest.NameKo} {quest.InternalName} " +
                            string.Join(' ', Catalog.Descendants(root).Select(k => { var m = Catalog.Missions[k]; return $"{m.Key} {DisplayName(m)} {m.Name} {m.NameKo} {m.InternalName}"; }));
                        if (!Matches(text)) continue;
                        roots.Add(new($"m{root}", DisplayName(Catalog.Missions[root]), root, [root], []));
                    }
                    if (roots.Count > 0) quests.Add(new($"q{key}", Tr.Name("quests", quest.Key.ToString(), DisplayName(quest.NameKo, quest.Name, quest.InternalName)), null, quest.Missions!, roots.ToArray()) { Expanded = search.Length > 0 });
                }
                if (quests.Count > 0) groups.Add(new($"g{group.Key}", Tr.Name("quest_groups", group.Key.ToString(), DisplayName(group.NameKo, group.Name, group.InternalName)), null,
                    group.Quests.SelectMany(q => Catalog.Quests[q].Missions!).ToArray(), quests.ToArray()) { Expanded = true });
            }
        updating = true;
        try
        {
            nodes = groups.ToArray();
            foreach (var node in All(nodes))
            {
                UpdateNode(node);
                if (old.TryGetValue(node.Identity, out var prior)) node.Expanded |= prior.Expanded;
                node.Selected = node.Identity == identity;
            }
            selected = All(nodes).FirstOrDefault(n => n.Selected);
            Tree.ItemsSource = nodes;
        }
        finally { updating = false; }
        Count.Text = Model.Challenges is null ? Tr.Text("challenge.load_prompt") : Tr.Count(ChallengeCatalog.Completion.Roots.Length, All(nodes).Count(n => n.Root.HasValue));
        ShowDetails();
    }
    private void ShowDetails()
    {
        uint[] selection = Rows.SelectedItems.Cast<ChallengeRow>().Select(r => r.Key).ToArray();
        uint[] roots = selected is null ? [] : selected.Root is uint root ? Catalog.Descendants(root) : All(selected.Children).Where(n => n.Root.HasValue).Select(n => n.Root!.Value).ToArray();
        updating = true;
        try
        {
            Title.Text = selected?.Name ?? Tr.Text(Model.Challenges is null ? "challenge.load_prompt" : "challenge.select");
            DetailNote.Text = Tr.Text(selected?.Root is null ? "challenge.select_challenges" : "challenge.checkpoint_note");
            Rows.ItemsSource = roots.Select(key =>
            {
                var mission = Catalog.Missions[key];
                var record = Model.Challenges!.Mission.GetValueOrDefault(key);
                uint target = mission.ObjectiveEdit?.Target ?? 0;
                string progress = target > 0 && (record?.Count is not null || Done(key)) ? $"{(Done(key) ? target : record!.Count)} / {target}" : "—";
                return new ChallengeRow(key, Tr.Text(selected!.Root is null || selected.Root == key ? "challenge.parent" : "challenge.checkpoint"),
                    DisplayName(mission), mission.InternalName, State(key), progress, Done(key));
            }).ToArray();
            foreach (var row in Rows.Items.Cast<ChallengeRow>().Where(r => selection.Contains(r.Key))) Rows.SelectedItems.Add(row);
        }
        finally { updating = false; }
        UpdateActions();
    }
    private (uint[] Roots, uint[]? Checkpoints) Targets(bool checkpoints)
    {
        if (selected is null || Model.Challenges is null) return ([], null);
        uint[] keys = Rows.SelectedItems.Cast<ChallengeRow>().Select(r => r.Key).ToArray();
        if (checkpoints)
        {
            uint[] leaves = keys.Where(k => selected.Root is uint root && k != root && Catalog.Missions[k].Children.Length == 0 && !Done(k)).ToArray();
            return leaves.Length > 0 ? ([selected.Root!.Value], leaves) : ([], null);
        }
        return ((selected.Root is uint r ? new[] { r } : keys).Where(k => Catalog.Descendants(k).Any(child => !Done(child))).ToArray(), null);
    }
    private void UpdateActions()
    {
        if (updating) return;
        var all = Targets(false);
        var leaves = Targets(true);
        CompleteAll.IsEnabled = all.Roots.Length > 0;
        CompleteAll.Content = Tr.Text(selected?.Root is null ? "challenge.complete_challenges" : "challenge.complete_all") +
            (selected?.Root is null && all.Roots.Length > 0 ? $" ({all.Roots.Length})" : "");
        CompleteSelected.Visibility = selected?.Root is null ? Visibility.Collapsed : Visibility.Visible;
        CompleteSelected.IsEnabled = leaves.Checkpoints?.Length > 0;
        CompleteSelected.Content = Tr.Text("challenge.complete_selected") + (leaves.Checkpoints?.Length > 0 ? $" ({leaves.Checkpoints.Length})" : "");
        EditNote.Text = Tr.Text(selected is null ? "challenge.select" : "challenge.edit_scope");
    }
    private void OnSelected(object sender, RoutedPropertyChangedEventArgs<object> e) { if (!updating) { selected = e.NewValue as ChallengeNode; ShowDetails(); } }
    private void OnRowsSelected(object sender, SelectionChangedEventArgs e) => UpdateActions();
    private void OnFilter(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel) Populate(); }
    private void OnSearch(object sender, TextChangedEventArgs e) => OnFilter(sender, e);
    private async void OnCompleteAll(object sender, RoutedEventArgs e) { var targets = Targets(false); await Model.CompleteChallengesAsync(targets.Roots); }
    private async void OnCompleteSelected(object sender, RoutedEventArgs e) { var targets = Targets(true); await Model.CompleteChallengesAsync(targets.Roots, targets.Checkpoints); }
}
