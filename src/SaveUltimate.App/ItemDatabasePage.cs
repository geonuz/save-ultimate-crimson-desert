using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;

namespace SaveUltimate.App;

public sealed class ItemDatabasePage : UserControl
{
    public sealed record Row(ItemSearchWindow.Choice Item)
    {
        public uint Key => Item.Key;
        public string Name => Item.Name;
        public string Category => Item.Category;
        public string InternalName => Item.InternalName;
        public ulong MaxStack => ItemCatalog.Shared.MaxStack(Key);
        public System.Windows.Media.ImageSource? Icon => ItemImages.Get(Key, 32);
    }
    private readonly TextBox search = new();
    private readonly ComboBox category = new() { MinWidth = 180 };
    public DataGrid Rows { get; } = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended };
    private readonly TextBlock count = Ui.Text("");
    private readonly TextBlock title = Ui.Text("ui.select_item"), description = Ui.Text("");
    private readonly Image picture = new() { MaxHeight = 180 };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private ItemCatalog catalog = ItemCatalog.Shared;
    public ItemDatabasePage()
    {
        var root = new DockPanel(); Content = root;
        var header = new PageHeader { Title = Tr.Text("tab.database"), HelpTopic = "tab.database" }; header.LoadButton.Visibility = Visibility.Collapsed;
        var top = new StackPanel { Margin = new(0, 0, 0, 12) }; DockPanel.SetDock(top, Dock.Top); root.Children.Add(top); top.Children.Add(header);
        top.Children.Add(Ui.Text("ui.readonly"));
        var filters = new DockPanel { Margin = new(0, 12, 0, 12) }; top.Children.Add(filters);
        DockPanel.SetDock(category, Dock.Right); filters.Children.Add(category); filters.Children.Add(search); top.Children.Add(count);
        category.ItemsSource = new[] { Tr.Text("All") }.Concat(ItemCatalog.Shared.Choices.Select(c => c.Category).Distinct().Order()).ToArray(); category.SelectedIndex = 0;
        foreach (var (label, path, width) in new[] { ("ItemKey", "Key", 90d), ("Name", "Name", 230d), ("Internal Name", "InternalName", 230d), ("Category", "Category", 200d), ("Max Stack", "MaxStack", 110d) })
            Rows.Columns.Add(new DataGridTextColumn { Header = Tr.Text(label), Binding = new Binding(path), Width = new(width) });
        var image = new FrameworkElementFactory(typeof(Image)); image.SetBinding(Image.SourceProperty, new Binding("Icon")); image.SetValue(Image.WidthProperty, 32d); image.SetValue(Image.HeightProperty, 32d);
        Rows.Columns.Insert(0, new DataGridTemplateColumn { Width = 40, CellTemplate = new DataTemplate { VisualTree = image } });
        Rows.SelectionChanged += (_, _) =>
        {
            var row = Rows.SelectedItems.Count == 1 ? Rows.SelectedItem as Row : null;
            title.Text = row?.Name ?? Tr.Text("ui.select_item"); picture.Source = row is null ? null : ItemImages.Get(row.Key, 180);
            description.Text = row is null ? "" : ItemCatalog.Shared.Description(row.Key);
        };
        var detail = new StackPanel { Margin = new(12) }; detail.Children.Add(title); detail.Children.Add(picture); detail.Children.Add(description);
        detail.Children.Add(Ui.Button("Copy Key", (_, _) => Copy()));
        var menu = new ContextMenu(); var copy = new MenuItem { Header = Tr.Text("Copy Key") }; copy.Click += (_, _) => Copy(); menu.Items.Add(copy); Rows.ContextMenu = menu;
        var split = new DetailSplit(); split.Children.Add(Rows); split.Children.Add(Ui.Scroll(detail)); root.Children.Add(split);
        timer.Tick += (_, _) => Filter(); search.TextChanged += (_, _) => { timer.Stop(); timer.Start(); }; category.SelectionChanged += (_, _) => Filter();
        IsVisibleChanged += (_, _) => { if (IsVisible) { Filter(); Rows.Columns[0].Visibility = SettingsStore.Current.ShowIcons ? Visibility.Visible : Visibility.Collapsed; } };
        Unloaded += (_, _) => timer.Stop();
    }
    private void Copy() { if (Rows.SelectedItem is Row row) Clipboard.SetText(row.Key.ToString()); }
    private void Filter()
    {
        timer.Stop(); uint[] selected = Rows.SelectedItems.Cast<Row>().Select(r => r.Key).ToArray(); string text = search.Text.Trim();
        if (!ReferenceEquals(catalog, ItemCatalog.Shared))
        {
            var key = catalog.Choices.FirstOrDefault(c => c.Category == category.SelectedItem as string)?.Key;
            catalog = ItemCatalog.Shared;
            string chosen = key is uint k ? catalog.Category(k) : Tr.Text("All");
            category.ItemsSource = new[] { Tr.Text("All") }.Concat(catalog.Choices.Select(c => c.Category).Distinct().Order()).ToArray();
            category.SelectedItem = chosen;
        }
        var rows = ItemCatalog.Shared.Choices.Where(c => (category.SelectedIndex <= 0 || c.Category == (string)category.SelectedItem) &&
            (c.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || c.InternalName.Contains(text, StringComparison.OrdinalIgnoreCase) || c.Key.ToString().Contains(text))).Select(c => new Row(c)).ToArray();
        Rows.ItemsSource = rows; foreach (var row in rows.Where(r => selected.Contains(r.Key))) Rows.SelectedItems.Add(row);
        count.Text = Tr.Count(ItemCatalog.Shared.Choices.Length, rows.Length);
    }
}
