using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public sealed record CompanionRow(Companion Record, CompanionDetail? Detail = null)
{
    public string Role => Tr.Text("companion.role." + CompanionTypes.Shared.Characters.GetValueOrDefault(Record.Key));
    public string Level => Detail?.Level?.ToString() ?? "—";
    public string Name => CharacterText.Get(Record.Key).Name;
    public string Custom => string.IsNullOrEmpty(Record.Name) ? Tr.Text("(unnamed)") : Record.Name;
    public string Category => Tr.Text("companion.group." + Record.Group);
    public string Hint => Record.Editable ? CompanionTypes.Shared.Types.GetValueOrDefault(CompanionTypes.Shared.Characters.GetValueOrDefault(Record.Key), "") : Tr.Text("companion.internal_note");
    public ImageSource? Icon => ItemImages.GetFile(Path.Combine(AppContext.BaseDirectory, "icons_mercenary", $"{Record.Key}.webp"), 32);
}
public partial class CompanionPage : UserControl
{
    private sealed record Choice(string Key, string Label);
    private bool updating;
    private Dictionary<ulong, CompanionDetail> details = [];
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
        CompanionPanel.Visibility = RoleColumn.Visibility = LevelColumn.Visibility = GroupFilter.Visibility = Pets ? Visibility.Collapsed : Visibility.Visible;
        try { details = Pets || Model.Companions is null ? [] : Model.CompanionDetails(); }
        catch (InvalidDataException) { details = []; }
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
        Rows.ItemsSource = (Model.Companions ?? []).Where(c => InTab(c) && (group == "all" || c.Group == group)).Select(c => new CompanionRow(c, details.GetValueOrDefault(c.Number))).ToArray();
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
        Rename.IsEnabled = Clear.IsEnabled = Pets && row?.Record.Editable == true;
        Rename.ToolTip = Clear.ToolTip = row is { Record.Editable: false } ? Tr.Text("companion.internal_note") : null;
        bool orderable = Pets && Orderable.Contains((GroupBox.SelectedItem as Choice)?.Key);
        OrderButtons.Visibility = orderable ? Visibility.Visible : Visibility.Collapsed;
        int index = Rows.SelectedIndex;
        MoveUp.IsEnabled = orderable && row is not null && index > 0;
        MoveDown.IsEnabled = orderable && row is not null && index < Rows.Items.Count - 1;
        if (Pets) return;
        int robots = Model.Companions?.Count(c => c.Key == SaveDocument.RobotWorker) ?? 0;
        AddRobot.IsEnabled = robots > 0;
        RemoveRobot.IsEnabled = row?.Record.Key == SaveDocument.RobotWorker && robots > 1;
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
        return string.Join("\n", lines);
    }
    private async void OnAddRobot(object sender, RoutedEventArgs e) => await Model.EditRobotWorkersAsync(null);
    private async void OnRemoveRobot(object sender, RoutedEventArgs e)
    {
        if (Rows.SelectedItem is CompanionRow { Record.Key: SaveDocument.RobotWorker } row &&
            ThemedMessageBox.Show(Window.GetWindow(this), Tr.Text("companion.robot_remove_confirm"), Tr.Text("companion.robot_remove"), MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes)
            await Model.EditRobotWorkersAsync(row.Record);
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
