using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using CommunityToolkit.Mvvm.ComponentModel;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public sealed class ManagementRow(ManagementState record) : ObservableObject
{
    public ManagementState Record { get; private set; } = record;
    public ushort Key => Record.Shop.Key;
    public string Region => Record.Shop.Region.Length == 0 ? "" : ManagementCatalog.Shared.RegionName(Record.Shop.Region, Tr.Language);
    public string Kind => Tr.Text("management.kind." + Record.Shop.Kind);
    public string Place => KnowledgeText.Find(Record.Shop.PlaceKnowledge)?.Name ?? "";
    // The player names a shop when opening it; until then it goes by its region and kind.
    public string Name => Record.CustomName is { Length: > 0 } name ? name : $"{Region} {Kind}".Trim();
    // The save holds a shop once the player opens it; its renown record comes later.
    public string Running => Tr.Text(Record.InSave ? "management.open" : "management.not_open");
    public string Editing => Tr.Text(Record.Editable ? "management.editable" : "management.read_only");
    // Open shops show green, the others grey.
    public bool Done => Record.InSave;
    public bool Dim => !Record.InSave;
    // Why the renown cannot be changed, or nothing when it can.
    public string Hint => Record.Editable ? "" : Tr.Text(!Record.InSave ? "management.open_first" : !Record.Operating ? "management.no_renown" : "management.no_levels");
    public string Level => Record.Operating ? Record.Shop.MaxLevel > 0 ? $"{Record.Level} / {Record.Shop.MaxLevel}" : $"{Record.Level}" : "—";
    internal void Update(ManagementState value) { Record = value; OnPropertyChanged(""); }
}

public partial class ManagementPage : UserControl
{
    private MainViewModel Model => (MainViewModel)DataContext;
    private ManagementRow? Selected => Rows.SelectedItem as ManagementRow;
    public ManagementPage()
    {
        InitializeComponent();
        Ui.Follow(this, nameof(MainViewModel.Management), Refresh);
    }
    private void Refresh()
    {
        var records = Model.Management ?? [];
        var old = Rows.Items.Cast<ManagementRow>().ToDictionary(r => r.Key);
        if (old.Keys.ToHashSet().SetEquals(records.Select(r => r.Shop.Key)))
            foreach (var record in records) old[record.Shop.Key].Update(record);
        else Rows.ItemsSource = records.Select(r => new ManagementRow(r)).ToArray();
        Count.Text = Model.Management is null ? Tr.Text("ui.load_prompt")
            : Tr.Count(records.Length, records.Length, "management.count", records.Count(r => r.InSave));
        Select();
    }
    private void Select()
    {
        var row = Selected;
        SelectedTitle.Text = row?.Name ?? Tr.Text("ui.select_item");
        Summary.Text = row is { Record.Operating: true }
            ? Tr.Format("management.summary", ("high", row.Record.HighLevel?.ToString() ?? "—"), ("rewarded", row.Record.RewardedLevel?.ToString() ?? "—"),
                ("customers", row.Record.Customers?.ToString("N0") ?? "0"))
            : "";
        Summary.Visibility = Summary.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Hint.Text = row?.Hint ?? "";
        Hint.Visibility = Hint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        LevelInput.ItemsSource = row is { Record.Editable: true } ? Enumerable.Range(1, (int)row.Record.Shop.MaxLevel).Select(i => (uint)i).ToArray() : null;
        LevelInput.SelectedItem = row?.Record.Level;
        LevelInput.IsEnabled = row is { Record.Editable: true };
        UpdateApply();
    }
    private void UpdateApply() { if (Apply is not null) Apply.IsEnabled = Selected is { Record.Editable: true } row && LevelInput.SelectedItem is uint level && level != row.Record.Level; }
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => Select();
    private void OnLevelChanged(object sender, SelectionChangedEventArgs e) => UpdateApply();
    private async void OnApply(object sender, RoutedEventArgs e)
    {
        if (Selected is { Record.Editable: true } row && LevelInput.SelectedItem is uint level) await Model.SetManagementLevelAsync(row.Key, level);
    }
}
