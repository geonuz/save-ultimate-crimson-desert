using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public sealed record CompanionRow(Companion Record, CompanionDetail? Detail = null)
{
    public string Role => Tr.Text("companion.role." + CompanionTypes.Shared.Characters.GetValueOrDefault(Record.Key));
    public string Skills => SkillText(Record.Key);
    // A higher tier implies the lower ones (Farming_III after _I and _II), so each skill family shows its last tier only.
    public static string SkillText(uint key) => string.Join(" · ", CompanionTypes.Shared.Skills.GetValueOrDefault(key, [])
        .Select(k => KnowledgeText.Find(k)).OfType<KnowledgeText>().GroupBy(t => Regex.Replace(t.InternalName, "_I+$", "")).Select(g => SkillLabel(g.Last())));
    // The game names a tier by a word (초급 낚시, Expert Fisherman, Рыбалка: эксперт); the label is the family and the tier in Roman numerals: 낚시(I).
    private static readonly Regex TierWord = new(@"^(초급|숙련|전문|初級|熟練|専門|Novice|Skilled|Expert|Базовое|Мастерское|Экспертное)\s*|:\s*(основы|мастер|эксперт|эсперт)$|\s+I+$");
    internal static string SkillLabel(KnowledgeText text)
    {
        var tier = Regex.Match(text.InternalName, "_(I+)$");
        string family = TierWord.Replace(text.Name, "");
        return !tier.Success || family.Length == 0 ? text.Name : $"{char.ToUpperInvariant(family[0])}{family[1..]}({tier.Groups[1].Value})";
    }
    public string Name => CharacterText.Get(Record.Key).Name;
    public string Custom => string.IsNullOrEmpty(Record.Name) ? Tr.Text("(unnamed)") : Record.Name;
    public string Category => Tr.Text("companion.group." + Record.Group);
    public string Hint => Record.Editable ? CompanionTypes.Shared.Types.GetValueOrDefault(CompanionTypes.Shared.Characters.GetValueOrDefault(Record.Key), "") : Tr.Text("companion.internal_note");
    public ImageSource? Icon => CharacterText.Picture(Record.Key, 32);
}
public partial class CompanionPage : UserControl
{
    private sealed record Choice(string Key, string Label);
    private bool updating;
    private Dictionary<ulong, CompanionDetail> details = [];
    private HashSet<ulong> factionWorkers = [];
    private MainViewModel Model => (MainViewModel)DataContext;
    /// <summary>Pets and special mounts can be renamed; the companion tab shows mercenaries read-only, except Grey-0 robots.</summary>
    public bool Pets { get; set; }
    private static readonly string[] CompanionGroups = ["mercenary", "playable"];
    private string[] Groups => Pets ? ["pet", "special", .. CompanionTypes.Groups.Except(["all", "pet", "special", .. CompanionGroups])] : ["all", .. CompanionGroups];
    private static readonly string[] Orderable = ["pet", "special"];
    private Selector GroupBox => Pets ? GroupTabs : Group;
    private bool InTab(Companion c) => CompanionGroups.Contains(c.Group) != Pets;
    public CompanionPage()
    {
        InitializeComponent();
        // Character pictures follow the same icon setting as item tables.
        IsVisibleChanged += (_, _) => { if (IsVisible) Rows.Columns[0].Visibility = SettingsStore.Current.ShowIcons ? Visibility.Visible : Visibility.Collapsed; };
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.PropertyChanged -= Changed;
            if (e.NewValue is MainViewModel model) { model.PropertyChanged += Changed; Refresh(); }
        };
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MainViewModel.Companions)) Refresh(); }
    private void Refresh()
    {
        string selected = (GroupBox.SelectedItem as Choice)?.Key ?? (Pets ? "pet" : "all");
        updating = true;
        Header.Title = Tr.Text(Pets ? "companion.pets" : "companion.title");
        NameButtons.Visibility = ClearAll.Visibility = CustomColumn.Visibility = GroupTabs.Visibility = Pets ? Visibility.Visible : Visibility.Collapsed;
        // Pets and mounts show the game's order, which Move Up and Move Down change, so only the companion table sorts by header.
        Rows.CanUserSortColumns = !Pets;
        CompanionPanel.Visibility = AddRobot.Visibility = RoleColumn.Visibility = SkillsColumn.Visibility = GroupFilter.Visibility = Pets ? Visibility.Collapsed : Visibility.Visible;
        details = []; factionWorkers = [];
        try { if (!Pets && Model.Companions is not null) { details = Model.CompanionDetails(); factionWorkers = Model.FactionWorkers(); } }
        catch (InvalidDataException) { }
        int Count(string g) => Model.Companions?.Count(c => InTab(c) && (g == "all" || c.Group == g)) ?? 0;
        // Tabs list only the categories this save has; the filter keeps every category.
        GroupBox.ItemsSource = Groups.Where(g => !Pets || Orderable.Contains(g) || Count(g) > 0).Select(g => new Choice(g, $"{Tr.Text("companion.group." + g)} ({Count(g)})")).ToArray();
        GroupBox.SelectedItem = GroupBox.Items.Cast<Choice>().FirstOrDefault(c => c.Key == selected) ?? GroupBox.Items.Cast<Choice>().First();
        updating = false; Filter();
    }
    private void Filter()
    {
        ulong? selected = (Rows.SelectedItem as CompanionRow)?.Record.Number;
        string group = (GroupBox.SelectedItem as Choice)?.Key ?? "all";
        Ui.SetRows(Rows, (Model.Companions ?? []).Where(c => InTab(c) && (group == "all" || c.Group == group)).Select(c => new CompanionRow(c, details.GetValueOrDefault(c.Number))).ToArray());
        Rows.SelectedItem = Rows.Items.Cast<CompanionRow>().FirstOrDefault(r => r.Record.Number == selected);
        Count.Text = Model.Companions is null ? Tr.Text("ui.load_prompt") : Tr.Count(Model.Companions.Count(InTab), Rows.Items.Count);
        int named = Model.Companions?.Count(c => InTab(c) && c.Editable && c.Name is not null) ?? 0;
        ClearAll.IsEnabled = Pets && named > 0;
        ClearAll.Content = Tr.Text("Reset All to Default") + (named > 0 ? $" ({named})" : "");
        Select();
    }
    private void Select()
    {
        if (Rename is null) return;
        var row = Rows.SelectedItem as CompanionRow;
        SelectedTitle.Text = row?.Name ?? Tr.Text("ui.select_item");
        (Profile.Text, Picture.Source) = row is null ? ("", null) : CharacterText.Profile(row.Record.Key);
        Profile.Visibility = Profile.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Rename.IsEnabled = Clear.IsEnabled = Pets && row?.Record.Editable == true;
        Rename.ToolTip = Clear.ToolTip = row is { Record.Editable: false } ? Tr.Text("companion.internal_note") : null;
        bool orderable = Pets && Orderable.Contains((GroupBox.SelectedItem as Choice)?.Key);
        OrderButtons.Visibility = orderable ? Visibility.Visible : Visibility.Collapsed;
        int index = Rows.SelectedIndex;
        MoveUp.IsEnabled = orderable && row is not null && index > 0;
        MoveDown.IsEnabled = orderable && row is not null && index < Rows.Items.Count - 1;
        if (Pets) return;
        AddRobot.IsEnabled = Model.Companions?.Any(c => SaveDocument.IsWorker(c.Key)) == true;
        string? block = row is null ? null : SaveDocument.RemoveBlock(row.Record, Model.Companions ?? [], row.Detail, factionWorkers);
        RemoveRobot.IsEnabled = row is not null && block is null;
        RemoveReason.Text = block is null ? "" : Tr.Text("companion.remove_block." + block);
        RemoveReason.Visibility = block is null ? Visibility.Collapsed : Visibility.Visible;
        Details.Text = row is null ? "" : Describe(row);
    }
    private static string Describe(CompanionRow row)
    {
        if (row.Detail is not { } detail) return "";
        var lines = new List<string> { row.Category + " · " + row.Role };
        if (detail.Level is uint level) lines.Add(Tr.Format("companion.level", ("level", level), ("exp", detail.Experience ?? 0)));
        if (detail.SkillPoints is ulong points) lines.Add(Tr.Format("companion.skill_points", ("count", points)));
        if (detail.Health is ulong hp) lines.Add($"HP {hp:N0}" + (detail.Mana is ulong mp ? $" · MP {mp:N0}" : ""));
        if (detail.Main) lines.Add(Tr.Text("companion.main"));
        if (detail.Dead) lines.Add(Tr.Text("companion.dead"));
        foreach (uint skill in CompanionTypes.Shared.Skills.GetValueOrDefault(row.Record.Key, []))
            if (KnowledgeText.Find(skill) is { } text) lines.Add(text.Description.Length > 0 ? $"• {CompanionRow.SkillLabel(text)}: {text.Description}" : "• " + CompanionRow.SkillLabel(text));
        return string.Join("\n", lines);
    }
    public static ItemSearchWindow WorkerPicker(Companion[] roster) => new(SaveDocument.WorkerChoices(roster).Select(k =>
    {
        var text = CharacterText.Get(k);
        string skills = CompanionRow.SkillText(k);
        return new ItemSearchWindow.Choice(k, text.Name, skills, $"{text.Title} {text.Area} {skills}");
    }).ToArray(), Tr.Text("companion.worker_pick"), Tr.Text("companion.worker_pick_hint"), Tr.Text("companion.skills"));
    private async void OnAddRobot(object sender, RoutedEventArgs e)
    {
        var dialog = WorkerPicker(Model.Companions ?? []); dialog.Owner = Window.GetWindow(this);
        if (dialog.ShowDialog() == true && dialog.SelectedKey is uint key) await Model.EditWorkersAsync(key, null);
    }
    private async void OnRemoveRobot(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is CompanionRow row && SaveDocument.IsWorker(row.Record.Key) &&
            ThemedMessageBox.Show(Window.GetWindow(this), Tr.Text("companion.robot_remove_confirm"), Tr.Text("companion.robot_remove"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await Model.EditWorkersAsync(0, row.Record);
    }
    private void OnGroup(object sender, SelectionChangedEventArgs e) { if (e.OriginalSource == sender && !updating && DataContext is MainViewModel) Filter(); }
    private async void OnMove(object sender, RoutedEventArgs e)
    {
        int index = Rows.SelectedIndex, other = index + (sender == MoveUp ? -1 : 1);
        if (index < 0 || other < 0 || other >= Rows.Items.Count) return;
        var (row, neighbor) = ((CompanionRow)Rows.Items[index], (CompanionRow)Rows.Items[other]);
        await Model.SwapCompanionsAsync(row.Record, neighbor.Record);
        // The selected pet now sits in its neighbour's slot.
        Rows.SelectedItem = Rows.Items.Cast<CompanionRow>().FirstOrDefault(r => r.Record.Number == neighbor.Record.Number);
    }
    private void OnSelect(object sender, SelectionChangedEventArgs e) => Select();
    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is not CompanionRow { Record.Editable: true } row) return;
        var dialog = new NameDialog(Tr.Format("merc.rename_prompt", ("index", Array.FindIndex(Model.Companions!, c => c.Number == row.Record.Number)), ("key", row.Record.Key), ("current", row.Record.Name ?? Tr.Text("merc.unnamed"))), row.Record.Name ?? "") { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) await Model.RenameCompanionsAsync([row.Record], dialog.Value);
    }
    private async void OnClear(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is not CompanionRow { Record.Editable: true, Record.Name: not null } row) return;
        if (ThemedMessageBox.Show(Window.GetWindow(this), Tr.Format("merc.clear_confirm", ("name", row.Record.Name)), Tr.Text("merc.clear_name"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await Model.RenameCompanionsAsync([row.Record], null);
    }
    private async void OnClearAll(object sender, RoutedEventArgs e)
    {
        var rows = Model.Companions?.Where(r => InTab(r) && r.Editable && r.Name is not null).ToArray() ?? [];
        if (rows.Length > 0 && ThemedMessageBox.Show(Window.GetWindow(this), Tr.Format("merc.clear_all_confirm", ("count", rows.Length)), Tr.Text("merc.reset_all"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await Model.RenameCompanionsAsync(rows, null);
    }
}
