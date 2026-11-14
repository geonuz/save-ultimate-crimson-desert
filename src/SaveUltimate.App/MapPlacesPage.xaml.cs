using System.Windows;
using System.Windows.Controls;

namespace SaveUltimate.App;

/// <summary>
/// One row of the left table on a two-table page (map places, collection): the rows it holds on the right and how many
/// of them are done. <paramref name="states"/> are the keys for all, none and some done.
/// </summary>
public sealed class CategoryRow<T>(string name, T[] rows, Func<T, bool> done, (string All, string None, string Some) states)
{
    public string Name { get; } = name;
    public T[] Rows { get; } = rows;
    public int Active => Rows.Count(done);
    public bool Done => Active == Rows.Length;
    public string Progress => $"{Active:N0} / {Rows.Length:N0}";
    public string State => Tr.Text(Done ? states.All : Active == 0 ? states.None : states.Some);
}

/// <summary>
/// The places tab of the map section, laid out like the challenges page: categories on the left, the places of the
/// selected categories on the right. Places and bells only; memory fragments and the Abyss have tabs of their own.
/// </summary>
public partial class MapPlacesPage : UserControl
{
    private MainViewModel Model => (MainViewModel)DataContext;
    private MapRow[] all = [];
    private TypingDelay? typing;
    public MapPlacesPage()
    {
        InitializeComponent();
        Ui.Follow(this, nameof(MainViewModel.Map), Refresh);
    }

    private void Refresh()
    {
        all = Model.Map is { } state ? MapRow.Build(state, a => a.Group == "places" || a.SceneNames is not null).ToArray() : [];
        Filter();
    }
    private void Filter()
    {
        // Keep the selection across an activation or a filter change; categories are rebuilt every time.
        var categories = SelectedCategories.Select(c => c.Name).ToHashSet();
        var rows = SelectedRows.Select(r => r.Entry).ToHashSet();
        string query = Search.Text.Trim();
        var shown = all.Where(r => r.Shown(StateFilter.SelectedIndex, query, byCategory: true)).ToArray();
        Categories.ItemsSource = shown.GroupBy(r => r.Category).Select(g => new CategoryRow<MapRow>(g.Key, g.ToArray(), r => r.Done, ("map.state_active", "map.state_inactive", "map.state_partial"))).ToArray();
        foreach (var category in Categories.Items.Cast<CategoryRow<MapRow>>().Where(c => categories.Contains(c.Name))) Categories.SelectedItems.Add(category);
        if (Categories.SelectedItems.Count == 0 && Categories.Items.Count > 0) Categories.SelectedIndex = 0;
        Count.Text = Model.Map is null ? Tr.Text("map.load_prompt") : Tr.Count(all.Length, shown.Length, "map.count_active", shown.Count(r => r.Done));
        ShowRows();
        foreach (var row in Rows.Items.Cast<MapRow>().Where(r => rows.Contains(r.Entry))) Rows.SelectedItems.Add(row);
    }
    private CategoryRow<MapRow>[] SelectedCategories => Categories.SelectedItems.Cast<CategoryRow<MapRow>>().ToArray();
    private MapRow[] SelectedRows => Rows.SelectedItems.Cast<MapRow>().ToArray();
    private void ShowRows()
    {
        var categories = SelectedCategories;
        Title.Text = categories.Length switch { 0 => Tr.Text("map.select_category"), 1 => categories[0].Name, _ => Tr.Format("map.selected", ("count", categories.Length)) };
        Rows.ItemsSource = categories.SelectMany(c => c.Rows).ToArray();
        ActivateCategories.IsEnabled = categories.Any(c => !c.Done);
        ActivateSelected.IsEnabled = false;
    }
    private void OnCategoriesSelected(object sender, SelectionChangedEventArgs e) => ShowRows();
    private void OnRowsSelected(object sender, SelectionChangedEventArgs e) => ActivateSelected.IsEnabled = SelectedRows.Any(r => !r.Done);
    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) { if (DataContext is MainViewModel && IsInitialized) Filter(); }
    private void OnSearchChanged(object sender, TextChangedEventArgs e) => (typing ??= new(() => { if (DataContext is MainViewModel) Filter(); })).Restart(sender);
    private async void OnActivateCategories(object sender, RoutedEventArgs e) =>
        await Model.ActivateMapEntriesAsync(SelectedCategories.SelectMany(c => c.Rows).Where(r => !r.Done).Select(r => r.Entry).ToArray());
    private async void OnActivateSelected(object sender, RoutedEventArgs e) => await Model.ActivateMapEntriesAsync(SelectedRows.Where(r => !r.Done).Select(r => r.Entry).ToArray());
}
