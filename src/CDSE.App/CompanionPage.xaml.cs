using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using CDSE.Core.Saves;

namespace CDSE.App;

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
    public ImageSource? Icon => CharacterText.Picture(Record.Key, 96);
}
/// <summary>One gear slot in the detail panel; CanChange is off when nothing can go in it, and Full is on when what is worn there
/// would go to an inventory without room.</summary>
public sealed record GearRow(GearSlot Slot, bool CanChange)
{
    // A slot that takes several types (the A.T.A.G. arms) names them all.
    public string Label => string.Join(" / ", Slot.Types.Select(TypeName).Where(n => n.Length > 0).Distinct());
    public static string TypeName(uint type) => CDSE.Core.Catalogs.GearCatalog.Shared.EquipTypes.GetValueOrDefault(type)?.GetValueOrDefault(Tr.Language) ?? "";
    public string Name => Slot.Item is { } item ? ItemCatalog.Shared.Name(item.Key) : Tr.Text("companion.gear_none");
    public ImageSource? Icon => Slot.Item is { } item ? ItemImages.Get(item.Key, 96) : null;
    public bool CanTakeOff => Slot is { Item: not null, Required: false };
    public string OffHint => Tr.Text(Slot is { Item: not null, Required: true } ? "companion.gear_required" : "companion.gear_off_hint");
    public string ChangeHint => Tr.Text(CanChange ? "companion.gear_change" : "companion.gear_nothing");
    public string ChangeLabel => $"{Tr.Text("companion.gear_change")} · {Label}";
    public string OffLabel => $"{Tr.Text("companion.gear_off")} · {Label}";
}
public partial class CompanionPage : UserControl
{
    // A tab reads its ToString as its automation name.
    private sealed record Choice(string Key, string Label) { public override string ToString() => Label; }
    private bool updating;
    private Dictionary<ulong, CompanionDetail> details = [];
    private HashSet<ulong> factionWorkers = [];
    private HashSet<ulong>? questLinked;
    private MainViewModel Model => (MainViewModel)DataContext;
    /// <summary>Pets and special mounts can be renamed; the companion tab shows mercenaries read-only, except Grey-0 robots.</summary>
    public bool Pets { get; set; }
    // The companion tab lists mercenaries only; playable companions (Damiane, Oongka) have nothing to edit here and show in neither tab.
    private static readonly string[] CompanionGroups = ["mercenary"];
    // The kinds the game keeps apart. What is left (cattle, caught fish, internal records) has nothing to edit here.
    private static readonly string[] PetGroups = ["pet", "horse", "special", "dedicated", "wagon", "ship"];
    private string Tab => (GroupTabs.SelectedItem as Choice)?.Key ?? (Pets ? "pet" : "all");
    private static bool InGroup(Companion c, string group) => group == "all" || c.Group == group;
    private bool InTab(Companion c) => (Pets ? PetGroups : CompanionGroups).Contains(c.Group);
    public CompanionPage()
    {
        InitializeComponent();
        // Character pictures follow the same icon setting as item tables.
        IsVisibleChanged += (_, _) => { if (IsVisible) { Rows.Columns[0].Visibility = SettingsStore.Current.ShowIcons ? Visibility.Visible : Visibility.Collapsed; SelectPet(); } };
        Ui.Follow(this, nameof(MainViewModel.Companions), Refresh);
        // What can be worn and removed depends on the inventory.
        Ui.Follow(this, nameof(MainViewModel.ItemsView), SelectPet);
    }
    private void Refresh()
    {
        string selected = Tab;
        updating = true; Header.HelpTopic = Pets ? "companion.pets" : "companion.title";
        NameButtons.Visibility = More.Visibility = AddPet.Visibility = CustomColumn.Visibility = GroupTabs.Visibility = Pets ? Visibility.Visible : Visibility.Collapsed;
        // Only pets and mounts need a large picture to tell them apart.
        Rows.SetResourceReference(DataGrid.RowHeightProperty, Pets ? "PictureRowHeight" : "TableRowHeight");
        if (!Pets) Rows.Resources["PictureSize"] = 32d;
        // Pets and mounts show the game's order, which Move Up and Move Down change, so only the companion table sorts by header.
        Rows.CanUserSortColumns = !Pets;
        CompanionPanel.Visibility = AddRobot.Visibility = RoleColumn.Visibility = SkillsColumn.Visibility = SearchLabel.Visibility = Search.Visibility =
            Pets ? Visibility.Collapsed : Visibility.Visible;
        details = []; factionWorkers = []; questLinked = null;
        try { if (!Pets && Model.Companions is not null) { details = Model.CompanionDetails(); factionWorkers = Model.FactionWorkers(); } }
        catch (InvalidDataException) { }
        int Count(string g) => Model.Companions?.Count(c => InTab(c) && InGroup(c, g)) ?? 0;
        if (Pets)
        {
            GroupTabs.ItemsSource = PetGroups.Select(g => new Choice(g, $"{Tr.Text("companion.group." + g)} ({Count(g)})")).ToArray();
            GroupTabs.SelectedItem = GroupTabs.Items.Cast<Choice>().FirstOrDefault(c => c.Key == selected) ?? GroupTabs.Items[0];
        }
        updating = false; Filter();
    }
    private void Filter()
    {
        ulong? selected = (Rows.SelectedItem as CompanionRow)?.Record.Number;
        string group = Tab;
        string query = Pets ? "" : Search.Text.Trim();
        Ui.SetRows(Rows, (Model.Companions ?? []).Where(c => InTab(c) && InGroup(c, group)).Select(c => new CompanionRow(c, details.GetValueOrDefault(c.Number)))
            .Where(r => $"{r.Record.Key} {r.Record.Number} {r.Name} {r.Skills} {r.Role} {r.Category}".Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray());
        Rows.SelectedItem = Rows.Items.Cast<CompanionRow>().FirstOrDefault(r => r.Record.Number == selected);
        Count.Text = Model.Companions is null ? Tr.Text("ui.load_prompt") : Tr.Count(Model.Companions.Count(c => InTab(c) && InGroup(c, group)), Rows.Items.Count);
        int named = Named().Length;
        ClearAll.IsEnabled = Pets && named > 0;
        ClearAll.Header = Tr.Text("Reset All to Default") + (named > 0 ? $" ({named})" : "");
        // The button keeps its place on the tabs that cannot add, disabled, as the move buttons do.
        string? block = !SaveDocument.AddableGroups.Contains(group) ? "companion.add_tab" : Model.Companions is null ? "ui.load_prompt" : !Model.CanAddTo(group) ? "companion.add_first"
            : SaveDocument.CompanionChoices(group, Model.Companions).Length == 0 ? "companion.add_none" : null;
        AddPet.IsEnabled = Pets && block is null;
        AddPet.ToolTip = Tr.Text(block ?? "companion.add_hint");
        Select();
    }
    private void SelectPet() { if (Pets && IsVisible && DataContext is MainViewModel) Select(); }
    private void Select()
    {
        if (Rename is null) return;
        var row = Rows.SelectedItem as CompanionRow;
        SelectedTitle.Text = row?.Name ?? Tr.Text("ui.select_item");
        (Profile.Text, Picture.Source) = row is null ? ("", null) : CharacterText.Profile(row.Record.Key);
        Profile.Visibility = Profile.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Rename.IsEnabled = Pets && row is not null;
        Clear.IsEnabled = Pets && row?.Record.Name is not null;
        bool orderable = Pets && CompanionTypes.Orderable.Contains(Tab);
        // Groups the game orders elsewhere (horses, wagons) keep the buttons, disabled, as every tab does.
        OrderButtons.Visibility = Pets ? Visibility.Visible : Visibility.Collapsed;
        int index = Rows.SelectedIndex;
        MoveUp.IsEnabled = orderable && row is not null && index > 0;
        MoveDown.IsEnabled = orderable && row is not null && index < Rows.Items.Count - 1;
        if (Pets)
        {
            string? reason = row is null ? null : Model.ReleaseBlock(row.Record, questLinked ??= Model.QuestLinked());
            Release.IsEnabled = row is not null && reason is null;
            ReleaseReason.Text = reason is null ? "" : Tr.Text("companion.release_block." + reason);
            ReleaseReason.Visibility = reason is null ? Visibility.Collapsed : Visibility.Visible;
            Gear.ItemsSource = row is null ? [] : Model.GearOf(row.Record).Select(s => new GearRow(s, Model.GearChoices(row.Record, s.Number).Length > 0)).ToArray();
            GearPanel.Visibility = Gear.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            return;
        }
        AddRobot.IsEnabled = Model.Companions is not null && Model.CanAddWorkers;
        string? block = row is null ? null : SaveDocument.RemoveBlock(row.Record, row.Detail, factionWorkers);
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
        return new ItemSearchWindow.Choice(k, text.Name, skills, $"{text.Title} {text.Area} {skills}", key => CharacterText.Picture(key, 96));
    }).ToArray(), Tr.Text("companion.worker_pick"), Tr.Text("companion.worker_pick_hint"), Tr.Text("companion.skills"));
    // Many breeds share one game name, so the variant column tells them apart, as on the Knowledge page.
    public static ItemSearchWindow PetPicker(Companion[] roster, string group = "pet") => new(SaveDocument.CompanionChoices(group, roster).Select(k =>
    {
        var text = CharacterText.Get(k);
        string variant = text.InternalName.Replace("Animal_", "").Replace('_', ' ');
        return new ItemSearchWindow.Choice(k, text.Name, variant, text.InternalName, key => CharacterText.Picture(key, 96));
    }).OrderBy(c => c.Name, StringComparer.CurrentCulture).ToArray(), Tr.Text("companion.pick"), Tr.Text("companion.pick_hint"), Tr.Text("Internal Name"));
    private ItemSearchWindow GearPicker(CompanionRow row, GearRow gear) => new(Model.GearChoices(row.Record, gear.Slot.Number)
        .OrderBy(ItemCatalog.Shared.Name, StringComparer.CurrentCulture).Select(k => new ItemSearchWindow.Choice(k, ItemCatalog.Shared.Name(k),
            GearRow.TypeName(CDSE.Core.Catalogs.GearCatalog.Shared.Wearables[k][0]), ItemCatalog.Shared.InternalName(k), key => ItemImages.Get(key, 96))).ToArray(),
        gear.ChangeLabel, Tr.Text("companion.gear_hint"), Tr.Text("management.kind"), "ItemKey");
    private async void OnGear(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is not CompanionRow row || (sender as FrameworkElement)?.DataContext is not GearRow gear) return;
        var dialog = GearPicker(row, gear); dialog.Owner = Window.GetWindow(this);
        if (dialog.ShowDialog() == true && dialog.SelectedKey is uint key) await Model.SetGearAsync(row.Record, gear.Slot.Number, key);
    }
    private async void OnGearOff(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is CompanionRow row && (sender as FrameworkElement)?.DataContext is GearRow { CanTakeOff: true } gear) await Model.SetGearAsync(row.Record, gear.Slot.Number, 0);
    }
    private async void OnRelease(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is not CompanionRow row) return;
        string[] worn = Model.GearOf(row.Record).Select(s => s.Item).OfType<InventoryItem>().Select(i => "• " + ItemCatalog.Shared.Name(i.Key)).ToArray();
        string prompt = Tr.Format("companion.remove_confirm", ("name", row.Name)) + (worn.Length == 0 ? "" : "\n\n" + Tr.Text("companion.remove_gear") + "\n" + string.Join("\n", worn));
        if (ThemedMessageBox.Show(Window.GetWindow(this), prompt, Tr.Text("companion.remove"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await Model.RemoveCompanionAsync(row.Record);
    }
    private void OnMore(object sender, RoutedEventArgs e) { More.ContextMenu.PlacementTarget = More; More.ContextMenu.Placement = PlacementMode.Bottom; More.ContextMenu.IsOpen = true; }
    private async void OnAddPet(object sender, RoutedEventArgs e)
    {
        var dialog = PetPicker(Model.Companions ?? [], Tab); dialog.Owner = Window.GetWindow(this);
        if (dialog.ShowDialog() != true || dialog.SelectedKey is not uint key) return;
        await Model.AddCompanionAsync(key);
        // Show the new one, which the game lists last.
        Rows.SelectedItem = Rows.Items.Cast<CompanionRow>().LastOrDefault(r => r.Record.Key == key);
        if (Rows.SelectedItem is { } row) Rows.ScrollIntoView(row);
    }
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
    private TypingDelay? typing;
    private void OnSearch(object sender, TextChangedEventArgs e) => (typing ??= new(() => { if (DataContext is MainViewModel && !updating) Filter(); })).Restart(sender);
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
        if (Rows.SelectedItem is not CompanionRow row) return;
        var dialog = new NameDialog(Tr.Format("merc.rename_prompt", ("index", Array.FindIndex(Model.Companions!, c => c.Number == row.Record.Number)), ("key", row.Record.Key), ("current", row.Record.Name ?? Tr.Text("merc.unnamed"))), row.Record.Name ?? "") { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) await Model.RenameCompanionsAsync([row.Record], dialog.Value);
    }
    private async void OnClear(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is not CompanionRow { Record.Name: not null } row) return;
        if (ThemedMessageBox.Show(Window.GetWindow(this), Tr.Format("merc.clear_confirm", ("name", row.Record.Name)), Tr.Text("merc.clear_name"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await Model.RenameCompanionsAsync([row.Record], null);
    }
    private Companion[] Named() => Model.Companions?.Where(c => InTab(c) && InGroup(c, Tab) && c.Name is not null).ToArray() ?? [];
    private async void OnClearAll(object sender, RoutedEventArgs e)
    {
        var rows = Named();
        if (rows.Length > 0 && ThemedMessageBox.Show(Window.GetWindow(this), Tr.Format("merc.clear_all_confirm", ("count", rows.Length)), Tr.Text("merc.reset_all"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await Model.RenameCompanionsAsync(rows, null);
    }
}
