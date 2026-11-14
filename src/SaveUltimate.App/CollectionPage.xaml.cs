using System.Windows;
using System.Windows.Controls;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public sealed class CollectionRow(CollectionState state)
{
    public CollectionState Record { get; } = state;
    public uint Key => Record.Entry.Key;
    public string Name { get; } = ItemCatalog.Shared.Name(state.Entry.Key);
    public string Category { get; } = ItemCatalog.Shared.Category(state.Entry.Key);
    public string State => Tr.Text(Record.Important ? "collection.state.important" : "collection.state." + Record.Status.ToString().ToLowerInvariant());
    // Items held in another bag stay grey; collected ones show green.
    public bool Dim => Record.Status == CollectionStatus.Elsewhere;
    public bool Done => Record.Status == CollectionStatus.Collected;
    public string Knowledge => Record.Entry.Knowledge.Length == 0 ? "—" : Tr.Text(Record.Learned ? "collection.learned" : "collection.not_learned");
    // What filling would change: the row, an important mark it clears, or with the knowledge option its knowledge. Items held
    // elsewhere are never filled.
    public bool Fillable(bool learn) => Record.Status == CollectionStatus.Missing || Record.Important || learn && Record.Status == CollectionStatus.Collected && !Record.Learned;
}

public partial class CollectionPage : UserControl
{
    private MainViewModel Model => (MainViewModel)DataContext;
    public CollectionPage()
    {
        InitializeComponent();
        Ui.Follow(this, nameof(MainViewModel.Collection), Refresh);
    }
    private CollectionRow[] all = [];
    private TypingDelay? typing;
    private void Refresh()
    {
        all = (Model.Collection ?? []).Select(s => new CollectionRow(s)).ToArray();
        Filter();
    }
    // The state filter's order: all, collected, missing, held elsewhere.
    private static readonly CollectionStatus?[] States = [null, CollectionStatus.Collected, CollectionStatus.Missing, CollectionStatus.Elsewhere];
    private void Filter()
    {
        // Keep the selection across a fill or a filter change; the rows are rebuilt every time.
        var categories = SelectedCategories.Select(c => c.Name).ToHashSet();
        var keys = Selected.Select(r => r.Key).ToHashSet();
        var state = States[Math.Max(0, StateFilter.SelectedIndex)];
        string query = Search.Text.Trim();
        var shown = all.Where(r => (state is null || r.Record.Status == state) &&
            (query.Length == 0 || $"{r.Name} {r.Category} {r.Key}".Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        Categories.ItemsSource = shown.GroupBy(r => r.Category)
            .Select(g => new CategoryRow<CollectionRow>(g.Key, g.ToArray(), r => r.Done, ("collection.category.all", "collection.category.none", "collection.category.partial"))).ToArray();
        foreach (var category in Categories.Items.Cast<CategoryRow<CollectionRow>>().Where(c => categories.Contains(c.Name))) Categories.SelectedItems.Add(category);
        if (Categories.SelectedItems.Count == 0 && Categories.Items.Count > 0) Categories.SelectedIndex = 0;
        Count.Text = Model.Collection is null ? Tr.Text("ui.load_prompt") : Tr.Count(all.Length, shown.Length, "collection.count", all.Count(r => r.Done));
        ShowRows();
        foreach (var row in Rows.Items.Cast<CollectionRow>().Where(r => keys.Contains(r.Key))) Rows.SelectedItems.Add(row);
    }
    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) { if (DataContext is MainViewModel && IsInitialized) Filter(); }
    private void OnSearchChanged(object sender, TextChangedEventArgs e) => (typing ??= new(() => { if (DataContext is MainViewModel) Filter(); })).Restart(sender);
    private CategoryRow<CollectionRow>[] SelectedCategories => Categories.SelectedItems.Cast<CategoryRow<CollectionRow>>().ToArray();
    private CollectionRow[] Selected => Rows.SelectedItems.Cast<CollectionRow>().ToArray();
    private bool LearnChecked => Learn.IsChecked == true;
    private void ShowRows()
    {
        var categories = SelectedCategories;
        Title.Text = categories.Length switch { 0 => Tr.Text("map.select_category"), 1 => categories[0].Name, _ => Tr.Format("map.selected", ("count", categories.Length)) };
        Rows.ItemsSource = categories.SelectMany(c => c.Rows).ToArray();
        Select();
    }
    private void Select()
    {
        FillCategories.IsEnabled = SelectedCategories.SelectMany(c => c.Rows).Any(r => r.Fillable(LearnChecked));
        FillSelected.IsEnabled = Selected.Any(r => r.Fillable(LearnChecked));
        Hint.Text = Selected.Any(r => r.Dim) ? Tr.Text("collection.elsewhere_note") : "";
        Hint.Visibility = Hint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }
    private void OnCategoriesSelected(object sender, SelectionChangedEventArgs e) => ShowRows();
    private void OnRowsSelected(object sender, SelectionChangedEventArgs e) => Select();
    private void OnLearnChanged(object sender, RoutedEventArgs e) => Select();
    private async void OnFillCategories(object sender, RoutedEventArgs e) =>
        await Model.FillCollectionAsync(SelectedCategories.SelectMany(c => c.Rows).Select(r => r.Key).ToArray(), LearnChecked);
    private async void OnFillSelected(object sender, RoutedEventArgs e) => await Model.FillCollectionAsync(Selected.Select(r => r.Key).ToArray(), LearnChecked);
}
