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
    public string Place => Record.Shop.Place.Length > 0 ? Record.Shop.Place : Record.Shop.Name;
    // The player names a shop when opening it; until then it goes by its region and kind.
    public string Name => Record.CustomName is { Length: > 0 } name ? name : $"{Region} {Kind}".Trim();
    public string State => Tr.Text(Record.Operating ? "management.operating" : Record.InSave ? "management.not_operating" : "management.not_started");
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
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is MainViewModel old) old.PropertyChanged -= Changed;
            if (e.NewValue is MainViewModel model) { model.PropertyChanged += Changed; Refresh(); }
        };
    }
    private void Changed(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName == nameof(MainViewModel.Management)) Refresh(); }
    private void Refresh()
    {
        var records = Model.Management ?? [];
        var old = Rows.Items.Cast<ManagementRow>().ToDictionary(r => r.Key);
        if (old.Keys.ToHashSet().SetEquals(records.Select(r => r.Shop.Key)))
            foreach (var record in records) old[record.Shop.Key].Update(record);
        else Rows.ItemsSource = records.Select(r => new ManagementRow(r)).ToArray();
        Count.Text = Model.Management is null ? Tr.Text("ui.load_prompt")
            : Tr.Format("management.count", ("operating", records.Count(r => r.Operating)), ("total", records.Length));
        Select();
    }
    private void Select()
    {
        var row = Selected;
        SelectedTitle.Text = row?.Name ?? Tr.Text("ui.select_item");
        Summary.Text = row is null ? "" : string.Join("\n", new[]
        {
            row.Record.Shop.Name,
            row.Record.Operating ? Tr.Format("management.summary", ("high", row.Record.HighLevel?.ToString() ?? "—"), ("rewarded", row.Record.RewardedLevel?.ToString() ?? "—"),
                ("customers", row.Record.Customers?.ToString("N0") ?? "0")) : Tr.Text(row.Record.Shop.MaxLevel == 0 ? "management.no_levels" : "management.open_first")
        });
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
