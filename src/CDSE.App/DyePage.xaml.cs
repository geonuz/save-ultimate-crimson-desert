using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using CDSE.Core.Catalogs;
using CDSE.Core.Saves;

namespace CDSE.App;

internal static class DyeText
{
    // The color groups in the order of the ten dye materials (PartPrefabDyeTexturePalleteInfo 1 to 10). Three Demeniss groups share
    // one name, so they are numbered.
    private static readonly uint[] Regions = [3363967477, 3693560950, 110038222, 1081114260, 2817281435, 1586656, 755308489, 713422964, 1329654226, 1196838804];
    private static string Plain(uint group) => GearCatalog.Shared.ColorGroups.GetValueOrDefault(group)?.GetValueOrDefault(Tr.Language) ?? group.ToString();
    public static string Group(uint group)
    {
        string name = Plain(group);
        int same = Regions.TakeWhile(g => g != group).Count(g => Plain(g) == name);
        return same == 0 ? name : $"{name} {same + 1}";
    }
    private static string Material(uint item, DyePart part)
    {
        string kind = GearCatalog.Shared.MaterialOf(item, part.Part) is var m and ('c' or 'l' or 'm') ? Tr.Text("dye.material." + m) : "";
        string from = part.Texture == 0 ? Tr.Text("dye.material.own") : part.Texture <= Regions.Length ? Group(Regions[part.Texture - 1]) : part.Texture.ToString();
        return Tr.Format("dye.material", ("name", kind.Length == 0 ? from : $"{kind} ({from})"));
    }
    public static string Detail(uint item, DyePart part)
    {
        var lines = new List<string>();
        if (part.Group != 0) lines.Add(Tr.Format("dye.group", ("name", Group(part.Group))));
        lines.Add(Material(item, part));
        if (part.Grime is byte grime) lines.Add(grime == 0 ? Tr.Text("dye.clean") : Tr.Format("dye.grime", ("percent", grime * 100 / 127)));
        if (part.HideSymbol) lines.Add(Tr.Text("dye.emblem_hidden"));
        return string.Join("\n", lines);
    }
    public static Brush? Brush(DyePart? part)
    {
        if (part is not { Colored: true }) return null;
        var brush = new SolidColorBrush(Color.FromRgb(part.R, part.G, part.B)); brush.Freeze(); return brush;
    }
    public static Brush[] Swatches(IEnumerable<DyePart> parts) => parts.OrderBy(p => p.Part).Select(Brush).OfType<Brush>().ToArray();
    public static string Where(InventoryItem item) => new ItemRow(item, "") is var row && item.Source == ItemSource.Inventory ? row.BagName : row.OwnerText;
}

/// <summary>One part of the selected item: its color and what else it was given.</summary>
public sealed record DyePartRow(uint Item, int Index, DyePart? Part)
{
    public string Title => Tr.Format("dye.part", ("index", Index + 1)) + (Part is { Colored: true } p ? $" · #{p.R:X2}{p.G:X2}{p.B:X2}" : "");
    public string Detail => Part is null ? Tr.Text("dye.none") : DyeText.Detail(Item, Part);
    public Brush? Color => DyeText.Brush(Part);
}

public sealed class DyeRow(DyedItem dyed)
{
    public DyedItem Dyed { get; } = dyed;
    public (ulong, uint) Key => (Dyed.Item.Number, Dyed.Item.Key);
    public bool Checked { get; set; }
    public string Name => ItemCatalog.Shared.Name(Dyed.Item.Key);
    public string Category => ItemCatalog.Shared.Category(Dyed.Item.Key);
    public string Where => DyeText.Where(Dyed.Item);
    public string Progress => $"{Dyed.Parts.Length} / {Dyed.PartCount}";
    public Brush[] Swatches => DyeText.Swatches(Dyed.Parts);
    public ImageSource? Icon => ItemImages.Get(Dyed.Item.Key, 64);
    public DyeEntry Entry => new(Dyed.Item.Key, Name, Dyed.Parts);
    // 1 Kliff, 2 Oongka, 3 Damiane (worn), 4 what pets and mounts wear, 5 bags.
    public int Owner => Dyed.Item.Source switch
    {
        ItemSource.Equipment => 1, ItemSource.Inventory => 5,
        _ when Dyed.Item.OwnerSlot != "equipped" => 0,
        _ => Dyed.Item.OwnerCharacter switch { 6 => 2, 4 => 3, _ => 4 }
    };
}

/// <summary>What every owned item that takes dye was dyed, exported to a file and imported from one.</summary>
public partial class DyePage : UserControl
{
    private MainViewModel Model => (MainViewModel)DataContext;
    private DyeRow[] rows = [];
    private string? path;
    private bool stale = true;
    private DyeRow[] Checked => rows.Where(r => r.Checked).ToArray();
    public DyePage()
    {
        InitializeComponent();
        // Every edit, undo and language switch gives the model new item rows.
        Ui.Follow(this, nameof(MainViewModel.ItemsView), () => { stale = true; Refresh(); });
        IsVisibleChanged += (_, _) => { if (IsVisible) { PictureColumn.Visibility = SettingsStore.Current.ShowIcons ? Visibility.Visible : Visibility.Collapsed; Refresh(); } };
        // Each export goes to a file or, as a code, to the clipboard; an import comes from either. The wear-set menu lists each
        // character with its two actions in one popup: a submenu would open over the list.
        foreach (int owner in new[] { 1, 2, 3 })
        {
            if (owner > 1) ExportSet.ContextMenu.Items.Add(new Separator());
            var group = new MenuItem { Tag = owner, Style = (Style)FindResource("MenuGroup") };
            Tr.Bind(group, HeaderedItemsControl.HeaderProperty, $"skills.character.{owner - 1}");
            ExportSet.ContextMenu.Items.Add(group);
            IEnumerable<DyeRow> Worn() => rows.Where(r => r.Owner == owner);
            var file = Choice("dye.to_file", () => Export(Worn(), Tr.Text($"skills.character.{owner - 1}")));
            var code = Choice("dye.to_code", () => Model.CopyDyeCode(Worn().Select(r => r.Entry).ToArray()));
            file.Tag = code.Tag = owner; file.Padding = code.Padding = new Thickness(20, 6, 12, 6);
            ExportSet.ContextMenu.Items.Add(file); ExportSet.ContextMenu.Items.Add(code);
        }
        ExportChecked.ContextMenu = new ContextMenu();
        ExportChecked.ContextMenu.Items.Add(Choice("dye.to_file", () => Export(Checked, "dyes")));
        ExportChecked.ContextMenu.Items.Add(Choice("dye.to_code", () => Model.CopyDyeCode(Checked.Select(r => r.Entry).ToArray())));
        Import.ContextMenu = new ContextMenu();
        Import.ContextMenu.Items.Add(Choice("dye.from_file", ImportFile));
        Import.ContextMenu.Items.Add(Choice("dye.from_code", ImportCode));
    }
    private static MenuItem Choice(string text, Action action)
    {
        var item = new MenuItem(); Tr.Bind(item, HeaderedItemsControl.HeaderProperty, text);
        item.Click += (_, _) => action();
        return item;
    }
    private static void Open(Button button) { button.ContextMenu.PlacementTarget = button; button.ContextMenu.Placement = PlacementMode.Bottom; button.ContextMenu.IsOpen = true; }
    private void Refresh()
    {
        if (!stale || !IsVisible) return;
        stale = false;
        var marked = path == Model.LoadedPath ? Checked.Select(r => r.Key).ToHashSet() : [];
        path = Model.LoadedPath;
        // A worn item may also have a row in a bag; it is one item, shown where it is worn.
        rows = Model.ReadDyes().OrderBy(d => d.Item.Source == ItemSource.Inventory).DistinctBy(d => (d.Item.Number, d.Item.Key)).Select(d => new DyeRow(d)).ToArray();
        foreach (var row in rows) row.Checked = marked.Contains(row.Key);
        Import.IsEnabled = Model.HasDocument && Model.CanDye;
        ExportSet.IsEnabled = rows.Any(r => r.Owner is 1 or 2 or 3);
        foreach (var item in ExportSet.ContextMenu.Items.OfType<MenuItem>()) item.IsEnabled = rows.Any(r => r.Owner == (int)item.Tag);
        Hint.Text = !Model.HasDocument || Model.CanDye ? "" : Tr.Text("dye.first");
        Hint.Visibility = Hint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        Import.ToolTip = Hint.Text.Length == 0 ? Tr.Text("dye.import_hint") : Hint.Text;
        Filter(); Ui.FitAutoColumns(Rows);
    }
    private void Filter()
    {
        var selected = (Rows.SelectedItem as DyeRow)?.Key;
        string query = Search.Text.Trim();
        int owner = OwnerFilter.SelectedIndex;
        Ui.SetRows(Rows, rows.Where(r => (owner <= 0 || r.Owner == owner) && (DyedOnly.IsChecked != true || r.Dyed.Parts.Length > 0) &&
            $"{r.Dyed.Item.Key} {r.Name} {r.Category} {r.Where}".Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray());
        Rows.SelectedItem = Rows.Items.Cast<DyeRow>().FirstOrDefault(r => r.Key == selected);
        Count.Text = !Model.HasDocument ? "" : Tr.Count(rows.Length, Rows.Items.Count, "dye.count", Rows.Items.Cast<DyeRow>().Count(r => r.Dyed.Parts.Length > 0));
        Select(); CountChecked();
    }
    private void Select()
    {
        var row = Rows.SelectedItem as DyeRow;
        SelectedTitle.Text = row?.Name ?? Tr.Text("ui.select_item");
        Picture.Source = row is null ? null : ItemImages.Get(row.Dyed.Item.Key, 180);
        Parts.ItemsSource = row is null ? null : GearCatalog.Shared.PartsOf(row.Dyed.Item.Key)
            .Select(i => new DyePartRow(row.Dyed.Item.Key, i, row.Dyed.Parts.FirstOrDefault(p => p.Part == i))).ToArray();
    }
    private void CountChecked()
    {
        int count = Checked.Length;
        ExportChecked.IsEnabled = count > 0;
        ExportChecked.Content = Tr.Text("dye.export_checked") + (count > 0 ? $" ({count:N0})" : "");
        CheckAll.IsChecked = Rows.Items.Count > 0 && Rows.Items.Cast<DyeRow>().All(r => r.Checked);
    }
    private void OnFilter(object sender, RoutedEventArgs e) { if (DataContext is MainViewModel && IsLoaded) Filter(); }
    private TypingDelay? typing;
    private void OnSearch(object sender, TextChangedEventArgs e) => (typing ??= new(() => { if (DataContext is MainViewModel) Filter(); })).Restart(sender);
    private void OnSelect(object sender, SelectionChangedEventArgs e) => Select();
    private void OnCheck(object sender, RoutedEventArgs e) => CountChecked();
    private void OnCheckAll(object sender, RoutedEventArgs e)
    {
        foreach (DyeRow row in Rows.Items) row.Checked = CheckAll.IsChecked == true;
        Rows.Items.Refresh(); CountChecked();
    }
    // The check boxes take no focus of their own, so Space checks the selected row.
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Space || Rows.SelectedItem is not DyeRow row) return;
        row.Checked = !row.Checked; Rows.Items.Refresh(); CountChecked(); e.Handled = true;
    }
    private void OnExportSet(object sender, RoutedEventArgs e) => Open(ExportSet);
    private void OnExportChecked(object sender, RoutedEventArgs e) => Open(ExportChecked);
    private void Export(IEnumerable<DyeRow> items, string name)
    {
        var entries = items.Select(r => r.Entry).ToArray();
        var dialog = new SaveFileDialog { Filter = Tr.Text("dye.filter"), DefaultExt = DyeFiles.Extension, AddExtension = true, FileName = name + DyeFiles.Extension, OverwritePrompt = true };
        if (entries.Length > 0 && dialog.ShowDialog() == true) Model.ExportDyes(dialog.FileName, entries);
    }
    private void OnImport(object sender, RoutedEventArgs e) => Open(Import);
    private void ImportFile()
    {
        var dialog = new OpenFileDialog { Filter = Tr.Text("dye.open_filter"), CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        ImportEntries(MainViewModel.ReadDyeFile(dialog.FileName, out string problem), problem);
    }
    private void ImportCode()
    {
        string text = "";
        // Another program may hold the clipboard for a moment; the box then starts empty.
        try { if (Clipboard.ContainsText()) text = Clipboard.GetText(); }
        catch (System.Runtime.InteropServices.COMException) { }
        var dialog = new DyeCodeDialog(text) { Owner = Window.GetWindow(this) };
        if (dialog.ShowDialog() == true) ImportEntries(dialog.Entries, "");
    }
    private async void ImportEntries(DyeEntry[]? entries, string problem)
    {
        if (entries is null) { ThemedMessageBox.Show(problem, Tr.Text("dye.title"), MessageBoxButton.OK, MessageBoxImage.Error); return; }
        var window = new DyeImportWindow(Model, entries) { Owner = Window.GetWindow(this) };
        if (window.ShowDialog() == true) await Model.ApplyDyesAsync(window.Targets);
    }
}
