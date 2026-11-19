using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public partial class ItemSearchWindow : Window
{
    /// <summary>One row; <paramref name="Picture"/> draws a character instead of the item image.</summary>
    public sealed record Choice(uint Key, string Name, string Category, string InternalName, Func<uint, ImageSource?>? Picture = null)
    {
        public ImageSource? Icon => Picture is null ? ItemImages.Get(Key, 32) : Picture(Key);
    }
    private readonly Choice[] choices;
    private Choice[] filtered = [];
    private int page, pageSize = 100;
    public uint? SelectedKey => (Results.SelectedItem as Choice)?.Key;

    public ItemSearchWindow(InventoryItem item) : this(ItemCatalog.Shared.Choices.Where(c => item.CanBecome(c.Key)).ToArray()) { }

    private ItemSearchWindow(Choice[] choices)
    {
        this.choices = choices;
        InitializeComponent();
        PictureColumn.Visibility = SettingsStore.Current.ShowIcons ? Visibility.Visible : Visibility.Collapsed;
        Filter();
    }

    /// <summary>
    /// A short list of characters, such as the mercenaries a roster can add: every choice on one page, laid out like the
    /// mercenary table (picture, name, <paramref name="category"/>, character key).
    /// </summary>
    public ItemSearchWindow(Choice[] choices, string title, string hint, string category) : this(choices)
    {
        Title = title; Hint.Text = hint; Width = 900; Height = 640;
        pageSize = int.MaxValue; Paging.Visibility = Visibility.Collapsed;
        CategoryColumn.Header = category; CategoryColumn.Width = new DataGridLength(2, DataGridLengthUnitType.Star);
        KeyColumn.Header = Tr.Text("companion.key"); KeyColumn.DisplayIndex = Results.Columns.Count - 1;
        ShowPage();
    }

    private void OnFilter(object sender, RoutedEventArgs e) { if (Results is not null) Filter(); }
    private void Filter()
    {
        string query = SearchBox.Text.Trim();
        filtered = choices.Where(c => c.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || c.Key.ToString().Contains(query) || c.InternalName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        page = 0; ShowPage();
    }
    private void ShowPage()
    {
        Results.SelectedItem = null;
        if (pageSize == int.MaxValue) { Results.ItemsSource = filtered; Count.Text = Tr.Count(choices.Length, filtered.Length); }
        else
        {
            Results.ItemsSource = filtered.Skip(page * pageSize).Take(pageSize).ToArray();
            Count.Text = $"{(filtered.Length == 0 ? 0 : page * pageSize + 1)}–{Math.Min((page + 1) * pageSize, filtered.Length)} / {filtered.Length:N0}";
            Previous.IsEnabled = page > 0; Next.IsEnabled = (page + 1) * pageSize < filtered.Length;
        }
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
