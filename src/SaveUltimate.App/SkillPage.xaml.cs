using System.IO;
using System.Windows;
using System.Windows.Controls;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public sealed class SkillRow(SkillDefinition definition, SkillState state)
{
    public uint Key => Definition.Key;
    public SkillDefinition Definition { get; } = definition;
    public string Name { get; } = ItemCatalog.Shared.KnowledgeName(definition.Key);
    public string Owner { get; } = Tr.Text($"skills.character.{definition.Owner}");
    // Where the game shows it: a demonstration in a region, or a monster or boss in combat.
    public string Where { get; } = Tr.Text(definition.Observe == (uint)LearnFrom.FollowManualLockOn ? "skills.where.region" : "skills.where.combat");
    public bool Done { get; } = state.Observed(definition.Key);
    // Learned another way (invested, granted): watching only adds the record.
    public bool Learned { get; } = state.Level(definition.Key) > 0;
    public string LearnedText => Tr.Text(Learned ? "Learned" : "Unlearned");
    public string ObservedText => Tr.Text(Done ? "skills.observed_done" : "skills.observed_missing");
    public string Description => Tr.Name("skill_descriptions", Key.ToString(), Definition.Description![Tr.Language == "ko" ? 1 : 0]);
}

public partial class SkillPage : UserControl
{
    private MainViewModel Model => (MainViewModel)DataContext;
    private SkillRow[] all = [];
    public SkillPage()
    {
        InitializeComponent();
        Owners();
        Ui.Follow(this, nameof(MainViewModel.Skills), Refresh);
    }
    // Rebuilt on refresh so the labels follow a language change.
    private void Owners()
    {
        int index = Math.Max(0, OwnerFilter.SelectedIndex);
        OwnerFilter.ItemsSource = new[] { Tr.Text("map.all") }.Concat(Enumerable.Range(0, 3).Select(i => Tr.Text($"skills.character.{i}"))).ToArray();
        OwnerFilter.SelectedIndex = index;
    }
    // Watchable skills in the game's key order; DLC ones only while the DLC is shown.
    private void Refresh()
    {
        Owners();
        uint[] selected = Selected.Select(r => r.Key).ToArray();
        all = Model.Skills is not { } state ? [] : SkillCatalog.Shared.Skills.Values
            .Where(d => d.Observe is not null && (!DlcContent.Hidden || !DlcContent.Shared.Knowledge.Contains(d.Key)))
            .OrderBy(d => d.Key).Select(d => new SkillRow(d, state)).ToArray();
        Filter();
        foreach (var row in Shown.Where(r => selected.Contains(r.Key))) Rows.SelectedItems.Add(row);
    }
    private SkillRow[] Shown => OwnerFilter.SelectedIndex <= 0 ? all : all.Where(r => r.Definition.Owner == OwnerFilter.SelectedIndex - 1).ToArray();
    private void Filter()
    {
        var shown = Shown;
        Rows.ItemsSource = shown;
        Count.Text = Model.Skills is null ? Tr.Text("ui.load_prompt") : Tr.Count(all.Length, shown.Length, "skills.count", shown.Count(r => r.Done));
        Select();
    }
    private SkillRow[] Selected => Rows.SelectedItems.Cast<SkillRow>().ToArray();
    private void Select()
    {
        var rows = Selected;
        SelectedTitle.Text = rows.Length switch { 0 => Tr.Text("skills.select"), 1 => rows[0].Name, _ => Tr.Format("skills.selected", ("count", rows.Length)) };
        Description.Text = rows.Length == 1 ? rows[0].Description : "";
        Icon.Source = rows.Length == 1 ? ItemImages.GetFile(Path.Combine(AppContext.BaseDirectory, "game_data", "images", "skills", $"{rows[0].Key}.png"), 64, trim: true) : null;
        ObserveSelected.IsEnabled = rows.Any(r => !r.Done);
        ObserveAll.IsEnabled = all.Any(r => !r.Done);
    }
    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) { if (DataContext is MainViewModel) Filter(); }
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => Select();
    private async void OnObserveSelected(object sender, RoutedEventArgs e) => await Model.ObserveSkillsAsync(Selected.Where(r => !r.Done).Select(r => r.Key).ToArray());
    // Every skill not watched yet, whatever the character filter shows.
    private async void OnObserveAll(object sender, RoutedEventArgs e) => await Model.ObserveSkillsAsync(all.Where(r => !r.Done).Select(r => r.Key).ToArray());
}
