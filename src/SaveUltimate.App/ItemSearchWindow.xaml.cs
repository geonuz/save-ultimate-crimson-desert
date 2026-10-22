using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SaveUltimate.App;

public partial class ItemSearchWindow : Window
{
    public sealed record Choice(uint Key, string Name, string Category, string InternalName, bool HasTemplate);
    private readonly Choice[] choices;
    private Choice[] filtered = [];
    private int page;
    public uint? SelectedKey => (Results.SelectedItem as Choice)?.Key;

    public ItemSearchWindow()
    {
        choices = ItemCatalog.Shared.Choices;
        InitializeComponent();
        Filter();
    }

    private void OnFilter(object sender, RoutedEventArgs e) { if (Results is not null) Filter(); }
    private void Filter()
    {
        string query = SearchBox.Text.Trim();
        filtered = choices.Where(c => (TemplatesOnly.IsChecked != true || c.HasTemplate) &&
            (c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || c.Key.ToString().Contains(query) || c.InternalName.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        page = 0; ShowPage();
    }
    private void ShowPage()
    {
        Results.SelectedItem = null;
        Results.ItemsSource = filtered.Skip(page * 100).Take(100).ToArray();
        Count.Text = $"{(filtered.Length == 0 ? 0 : page * 100 + 1)}–{Math.Min((page + 1) * 100, filtered.Length)} / {filtered.Length:N0}";
        Previous.IsEnabled = page > 0; Next.IsEnabled = (page + 1) * 100 < filtered.Length;
        Accept.IsEnabled = false;
    }
    private void OnPrevious(object sender, RoutedEventArgs e) { page--; ShowPage(); }
    private void OnNext(object sender, RoutedEventArgs e) { page++; ShowPage(); }
    private void OnSelection(object sender, SelectionChangedEventArgs e) { if (Accept is not null) Accept.IsEnabled = SelectedKey.HasValue; }
    private void OnAccept(object sender, RoutedEventArgs e) { if (SelectedKey.HasValue) DialogResult = true; }
    private void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(Results, e.OriginalSource as DependencyObject) is DataGridRow) OnAccept(sender, e);
    }
}
