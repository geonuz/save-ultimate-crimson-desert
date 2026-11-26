using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using CDSE.Core.Catalogs;
using CDSE.Core.Saves;

namespace CDSE.App;

public sealed class ItemDatabasePage : UserControl
{
    public sealed record Row(ItemSearchWindow.Choice Item, ulong Owned)
    {
        public uint Key => Item.Key;
        public string Name => Item.Name;
        public string Category => Item.Category;
        public string InternalName => Item.InternalName;
        public ulong MaxStack => ItemCatalog.Shared.MaxStack(Key);
        public System.Windows.Media.ImageSource? Icon => ItemImages.Get(Key, 32);
    }
    private readonly TextBox search = new();
    private readonly CheckBox addable = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 12, 0) };
    private readonly ComboBox category = new();
    public DataGrid Rows { get; } = new() { AutoGenerateColumns = false, IsReadOnly = true, SelectionMode = DataGridSelectionMode.Extended };
    private readonly TextBlock count = Ui.Text("");
    private readonly TextBlock title = Ui.Text("ui.select_item"), description = Ui.Text("");
    private readonly Image picture = new() { MaxHeight = 180 };
    public TextBox Quantity { get; } = new() { Text = "1" };
    public Button Add { get; }
    public TextBlock AddHint { get; } = Ui.Text("");
    private MainViewModel? Model => DataContext as MainViewModel;
    private Dictionary<uint, ulong> owned = [];
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private ItemCatalog catalog = ItemCatalog.Shared;
    public ItemDatabasePage()
    {
        var root = new DockPanel(); Content = root;
        var header = new PageHeader { HelpTopic = "tab.database" }; header.LoadButton.Visibility = Visibility.Collapsed;
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var categoryLabel = Ui.Text("Category"); categoryLabel.SetResourceReference(StyleProperty, "FilterLabel"); header.Filters.Add(categoryLabel);
        category.SetResourceReference(StyleProperty, "FilterBox"); Tr.Bind(category, AutomationProperties.NameProperty, "Category"); header.Filters.Add(category);
        Tr.Bind(addable, ContentControl.ContentProperty, "items.addable_only"); addable.Click += (_, _) => Filter(); header.Filters.Add(addable);
        var searchLabel = new Label { Target = search, Margin = new(0, 0, 12, 0) }; Tr.Bind(searchLabel, ContentControl.ContentProperty, "Search:"); header.Filters.Add(searchLabel); header.Filters.Add(search);
        Tr.Bind(search, TagProperty, "Search name, key or category"); Tr.Bind(search, AutomationProperties.NameProperty, "Search name, key or category");
        var counts = new DockPanel(); counts.SetResourceReference(StyleProperty, "CountRow"); count.SetResourceReference(StyleProperty, "CountText");
        counts.Children.Add(count); DockPanel.SetDock(counts, Dock.Top); root.Children.Add(counts);
        category.ItemsSource = new[] { Tr.Text("All") }.Concat(ItemCatalog.Shared.Choices.Select(c => c.Category).Distinct().Order()).ToArray(); category.SelectedIndex = 0;
        foreach (var (label, path, width) in new[] { ("ItemKey", "Key", 115d), ("Name", "Name", -1.5), ("Internal Name", "InternalName", -1d), ("Category", "Category", 200d), ("Max Stack", "MaxStack", 110d), ("items.owned", "Owned", 100d) })
        {
            // A negative width is a share of the free space, so the names take the room left over instead of an empty filler column.
            var column = new DataGridTextColumn { Binding = new Binding(path), Width = width < 0 ? new(-width, DataGridLengthUnitType.Star) : new(width), MinWidth = width < 0 ? 180 : 20 }; Tr.Bind(column, DataGridColumn.HeaderProperty, label); Rows.Columns.Add(column);
        }
        var image = new FrameworkElementFactory(typeof(Image)); image.SetBinding(Image.SourceProperty, new Binding("Icon")); image.SetValue(Image.WidthProperty, 32d); image.SetValue(Image.HeightProperty, 32d);
        Rows.Columns.Insert(0, new DataGridTemplateColumn { Width = 40, CellTemplate = new DataTemplate { VisualTree = image } });
        Rows.SelectionChanged += (_, _) =>
        {
            var row = Rows.SelectedItems.Count == 1 ? Rows.SelectedItem as Row : null;
            title.Text = row?.Name ?? (Rows.SelectedItems.Count > 1 ? Tr.Format("port.selected", ("count", Rows.SelectedItems.Count.ToString("N0"))) : Tr.Text("ui.select_item")); picture.Source = row is null ? null : ItemImages.Get(row.Key, 180);
            description.Text = row is null ? "" : ItemCatalog.Shared.Description(row.Key);
            UpdateAdd();
        };
        title.FontWeight = FontWeights.SemiBold; description.Margin = new(0, 0, 0, 12);
        var quantityLabel = new Label { Target = Quantity, Padding = new(0, 0, 0, 6) }; Tr.Bind(quantityLabel, ContentControl.ContentProperty, "items.add_quantity");
        Tr.Bind(Quantity, AutomationProperties.NameProperty, "items.add_quantity");
        Quantity.TextChanged += (_, _) => UpdateAdd();
        Quantity.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (System.Windows.Input.Key.Up or System.Windows.Input.Key.Down)) return;
            Quantity.Text = Math.Clamp((long)(MainViewModel.Quantity(Quantity.Text) ?? 1) + (e.Key == System.Windows.Input.Key.Up ? 1 : -1), 1, (long)SaveDocument.MaxQuantity).ToString();
            e.Handled = true;
        };
        Add = Ui.Button("items.add", async (_, _) => { if (MainViewModel.Quantity(Quantity.Text) is ulong value) await Model!.AddItemsAsync(Addable(), value); }, "AccentButton");
        Add.Margin = new(0, 8, 0, 16); AddHint.SetResourceReference(StyleProperty, "SecondaryText"); AddHint.Margin = new(0, 8, 0, 0);
        var copyKey = Ui.Button("Copy Key", (_, _) => Copy()); copyKey.Margin = default;
        var detail = new StackPanel { Margin = new(12) };
        foreach (UIElement part in new UIElement[] { title, picture, quantityLabel, Quantity, AddHint, Add, description, copyKey }) detail.Children.Add(part);
        var menu = new ContextMenu(); var copy = new MenuItem(); Tr.Bind(copy, HeaderedItemsControl.HeaderProperty, "Copy Key"); copy.Click += (_, _) => Copy(); menu.Items.Add(copy); Rows.ContextMenu = menu;
        var split = new DetailSplit(); split.Children.Add(Rows); split.Children.Add(Ui.Scroll(detail)); root.Children.Add(split);
        timer.Tick += (_, _) => Filter(); search.TextChanged += (_, _) => { timer.Stop(); timer.Start(); }; category.SelectionChanged += (_, _) => Filter();
        IsVisibleChanged += (_, _) => { if (IsVisible) { Filter(); Rows.Columns[0].Visibility = SettingsStore.Current.ShowIcons ? Visibility.Visible : Visibility.Collapsed; } };
        Unloaded += (_, _) => timer.Stop();
        // Edits and opened saves change what is owned and how many slots are free.
        Ui.Follow(this, nameof(MainViewModel.ItemsView), () => { if (IsVisible) Filter(); });
        Ui.Follow(this, nameof(MainViewModel.Busy), UpdateAdd);
    }
    /// <summary>Lists what can be added, ready for typing a name.</summary>
    public void ShowAddable() { addable.IsChecked = true; Filter(); search.Focus(); }
    private uint[] Addable() => Rows.SelectedItems.Cast<Row>().Select(r => r.Key).Where(ItemRules.CanAdd).ToArray();
    private void UpdateAdd()
    {
        uint[] selected = Rows.SelectedItems.Cast<Row>().Select(r => r.Key).ToArray(), keys = Addable();
        bool addable = Model?.Busy == false && Model.HasDocument && keys.Length > 0;
        Quantity.IsEnabled = addable;
        var quantity = MainViewModel.Quantity(Quantity.Text);
        var space = quantity is ulong value && keys.Length > 0 ? Model?.SpaceToAdd(keys, value) : null;
        Add.IsEnabled = addable && space is { } s && s.Space.Used + s.More <= s.Space.Total;
        AddHint.Text = selected.Length == 0 ? "" : Model?.HasDocument != true ? Tr.Text("Open a save file. Ctrl+O")
            : keys.Length == 0 ? Tr.Text(selected.Length == 1 && CollectionCatalog.Shared.Items.Any(i => i.Key == selected[0]) ? "items.block_collection" : "items.block")
            : quantity is null ? Tr.Text("Enter a whole quantity from 1 to 999,999,999.")
            : (keys.Length < selected.Length ? Tr.Format("items.addable", ("count", keys.Length.ToString("N0")), ("total", selected.Length.ToString("N0"))) + "\n" : "")
                + (space is { } t ? MainViewModel.SpaceText(t.Space, t.More) : "");
    }
    private void Copy() { if (Rows.SelectedItem is Row row) Clipboard.SetText(row.Key.ToString()); }
    private void Filter()
    {
        timer.Stop(); owned = Model?.OwnedCounts() ?? []; uint[] selected = Rows.SelectedItems.Cast<Row>().Select(r => r.Key).ToArray(); string text = search.Text.Trim();
        if (!ReferenceEquals(catalog, ItemCatalog.Shared))
        {
            var key = catalog.Choices.FirstOrDefault(c => c.Category == category.SelectedItem as string)?.Key;
            catalog = ItemCatalog.Shared;
            string chosen = key is uint k ? catalog.Category(k) : Tr.Text("All");
            category.ItemsSource = new[] { Tr.Text("All") }.Concat(catalog.Choices.Select(c => c.Category).Distinct().Order()).ToArray();
            category.SelectedItem = chosen;
        }
        var rows = ItemCatalog.Shared.Choices.Where(c => (category.SelectedIndex <= 0 || c.Category == (string)category.SelectedItem) &&
            (addable.IsChecked != true || ItemRules.CanAdd(c.Key)) && (c.Name.Contains(text, StringComparison.OrdinalIgnoreCase) || c.InternalName.Contains(text, StringComparison.OrdinalIgnoreCase) || c.Key.ToString().Contains(text))).Select(c => new Row(c, owned.GetValueOrDefault(c.Key))).ToArray();
        Ui.SetRows(Rows, rows); foreach (var row in rows.Where(r => selected.Contains(r.Key))) Rows.SelectedItems.Add(row);
        count.Text = Tr.Count(ItemCatalog.Shared.Choices.Length, rows.Length);
        UpdateAdd();
    }
}
