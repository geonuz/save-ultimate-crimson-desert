using System.Windows;
using System.Windows.Controls;

namespace SaveUltimate.App;

/// <summary>The memory fragment tab of the map section: every fragment on its own, activated singly, by selection or all at once.</summary>
public partial class MapFragmentsPage : UserControl
{
    private MainViewModel Model => (MainViewModel)DataContext;
    private MapRow[] all = [];
    private TypingDelay? typing;
    public MapFragmentsPage()
    {
        InitializeComponent();
        Ui.Follow(this, nameof(MainViewModel.Map), Refresh);
    }

    private void Refresh()
    {
        var selected = Selected.Select(r => r.Entry).ToHashSet();
        all = Model.Map is { } state ? MapRow.Build(state, a => a.Mode == "knowledge").ToArray() : [];
        Filter();
        foreach (var row in Rows.Items.Cast<MapRow>().Where(r => selected.Contains(r.Entry))) Rows.SelectedItems.Add(row);
    }
    private void Filter()
    {
        string query = Search.Text.Trim();
        var shown = all.Where(r => r.Shown(StateFilter.SelectedIndex, query)).ToArray();
        Rows.ItemsSource = shown;
        Count.Text = Model.Map is null ? Tr.Text("map.load_prompt") : Tr.Count(all.Length, shown.Length, "map.count_active", shown.Count(r => r.Done));
        ActivateAll.IsEnabled = all.Any(r => !r.Done);
        Select();
    }
    private MapRow[] Selected => Rows.SelectedItems.Cast<MapRow>().ToArray();
    private void Select()
    {
        var rows = Selected;
        SelectedTitle.Text = rows.Length switch { 0 => Tr.Text("ui.select_item"), 1 => rows[0].Name, _ => Tr.Format("map.selected", ("count", rows.Length)) };
        Summary.Text = rows.Length == 1 && rows[0].Entry.Key is uint key ? KnowledgeText.Find(key)?.Description ?? "" : "";
        Summary.Visibility = Summary.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ActivateSelected.IsEnabled = rows.Any(r => !r.Done);
    }
    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) { if (DataContext is MainViewModel && IsInitialized) Filter(); }
    private void OnSearchChanged(object sender, TextChangedEventArgs e) => (typing ??= new(() => { if (DataContext is MainViewModel) Filter(); })).Restart(sender);
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => Select();
    private async void OnActivateSelected(object sender, RoutedEventArgs e) => await Model.ActivateMapEntriesAsync(Selected.Where(r => !r.Done).Select(r => r.Entry).ToArray());
    // Every fragment not active yet, whatever the filters show.
    private async void OnActivateAll(object sender, RoutedEventArgs e) => await Model.ActivateMapEntriesAsync(all.Where(r => !r.Done).Select(r => r.Entry).ToArray());
}
