using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.App;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class ItemAreaTests
{
    [TestMethod]
    public void CatalogAndDetailsMatchOriginalPythonWidgets()
    {
        string? reference = Environment.GetEnvironmentVariable("SAVEULTIMATE_ITEM_REFERENCE");
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        if (reference is null || fixtures is null) Assert.Inconclusive("Set SAVEULTIMATE_ITEM_REFERENCE and SAVEULTIMATE_FIXTURES.");
        using var json = JsonDocument.Parse(File.ReadAllBytes(reference));
        var catalog = ItemCatalog.Shared;
        foreach (var expected in json.RootElement.GetProperty("catalog").EnumerateArray())
        {
            uint key = expected.GetProperty("key").GetUInt32();
            Assert.AreEqual(expected.GetProperty("name").GetString(), catalog.Name(key), $"Name {key}");
            Assert.AreEqual(expected.GetProperty("category").GetString(), catalog.Category(key), $"Category {key}");
            Assert.AreEqual(expected.GetProperty("hint").GetString(), catalog.CategoryHint(key), $"Hint {key}");
            Assert.AreEqual(expected.GetProperty("maxStack").GetUInt64(), catalog.MaxStack(key), $"Stack {key}");
        }
        var document = SaveDocument.Open(Path.Combine(fixtures, "save_v2.03.02.save"));
        foreach (var expected in json.RootElement.GetProperty("details").EnumerateArray())
        {
            var item = document.Items.First(i => i.Number == expected.GetProperty("number").GetUInt64() && i.Key == expected.GetProperty("key").GetUInt32() && i.Source.ToString() == expected.GetProperty("source").GetString());
            Assert.AreEqual(expected.GetProperty("stats").GetString(), catalog.Stats(item), $"Stats {item.Key}");
            Assert.AreEqual(expected.GetProperty("description").GetString(), catalog.Description(item.Key), $"Description {item.Key}");
            Assert.AreEqual(expected.GetProperty("owner").GetString(), catalog.Owner(item), $"Owner {item.Key}");
        }
    }

    [STATestMethod]
    public void GemFiltersKeepEmptyAndSelectedAndImagesAreShared()
    {
        var catalog = ItemCatalog.Shared;
        Assert.AreEqual(190, catalog.Gems.Length);
        var selected = catalog.Gems[1];
        var row = new SocketRow(0, selected.Key, true, false);
        row.Search = "unlikely_missing_gem";
        CollectionAssert.AreEqual(new uint[] { 0, selected.Key }, row.Choices.Select(c => c.Key).ToArray());
        Assert.IsTrue(row.Choices[1].Name.EndsWith("(선택됨)"));
        row.Search = selected.CanonicalCategory;
        Assert.IsTrue(row.Choices.Length > 2, "All-category search accepts the original category name too.");
        row.Search = "";
        row.Category = catalog.GemCategories.Last();
        Assert.IsTrue(row.Choices.All(g => g.Key == 0 || g.Key == selected.Key || g.Category == row.Category));
        var unknown = new SocketRow(1, uint.MaxValue, false, true, row);
        Assert.AreEqual(row.Category, unknown.Category);
        Assert.IsFalse(unknown.IsEditable);
        Assert.IsTrue(unknown.Label.Contains("잠김"));
        Assert.IsTrue(unknown.Choices.Any(g => g.Key == uint.MaxValue));
        Assert.IsNull(ItemImages.Get(uint.MaxValue, 32), "Items without a game image show no placeholder.");
        var skillIcon = ItemImages.GetFile(Path.Combine(AppContext.BaseDirectory, "resources", "assets", "skill-generic.svg"), 36);
        Assert.IsNotNull(skillIcon);
        Assert.IsTrue(skillIcon.IsFrozen);
        Assert.IsTrue(skillIcon.PixelWidth <= 36 && skillIcon.PixelHeight <= 36);
        var rgba = new System.Windows.Media.Imaging.FormatConvertedBitmap(skillIcon, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        byte[] corner = new byte[4];
        rgba.CopyPixels(new Int32Rect(0, 0, 1, 1), corner, 4, 0);
        Assert.AreEqual((byte)0, corner[3], "SVG skill icons must preserve transparent corners over tree nodes.");
        string corrupt = Path.Combine(AppContext.BaseDirectory, "icons_local", $"{uint.MaxValue - 2}.webp");
        Directory.CreateDirectory(Path.GetDirectoryName(corrupt)!);
        try
        {
            File.WriteAllText(corrupt, "invalid image");
            Assert.IsNull(ItemImages.Get(uint.MaxValue - 2, 32), "Corrupt local images must fall back without breaking selection.");
        }
        finally { File.Delete(corrupt); }
        Assert.AreEqual("화폐", catalog.Category(1));
        Assert.AreEqual("Hernandel General Store", catalog.Store(3101));
        Assert.AreEqual("65535", catalog.Store(65535));
    }

    [STATestMethod]
    public void ItemPanelsMatchSelectionStorageAndWindowWidth()
    {
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        if (fixtures is null) Assert.Inconclusive("Set SAVEULTIMATE_FIXTURES.");
        if (Application.Current is null) { var app = new App.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; }
        var document = SaveDocument.Open(Path.Combine(fixtures, "save_v2.03.02.save"));
        var model = new MainViewModel(document);
        var window = new MainWindow(model) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Show(); window.UpdateLayout();
        try
        {
            var grid = (DataGrid)window.FindName("InventoryGrid");
            var editor = (ScrollViewer)window.FindName("EditorScroll");
            Assert.AreEqual(2, Grid.GetRow(editor));
            grid.SelectedItem = grid.Items[0];
            Assert.IsNotNull(model.Detail);
            WindowTests.Capture(window, "detail");
            grid.SelectedItems.Add(grid.Items[1]);
            Assert.IsNull(model.Detail);
            Assert.AreEqual(3, Grid.GetColumnSpan((StackPanel)window.FindName("QuantityActions")));
            model.Storage = model.StorageFilters.Single(f => f.Bag == 2);
            Assert.IsNull(model.Detail);
            Assert.AreEqual(document.Items.Count(i => i.Source == ItemSource.Inventory && i.Bag == 2), model.Storage.Count);
            decimal slots = document.Items.Where(i => i.Source == ItemSource.Inventory && i.Bag == 2).Sum(i => decimal.Ceiling((decimal)i.Quantity / ItemCatalog.Shared.MaxStack(i.Key)));
            Assert.IsTrue(model.CountText.EndsWith($"약 {slots:N0}슬롯"));
            model.Search = "unlikely_missing_item";
            Assert.IsTrue(model.CountText.EndsWith($"약 {slots:N0}슬롯"), "Search must not change slot totals.");
            model.PageIndex = 2;
            var target = grid.Items.Cast<ItemRow>().First(r => r.Item.Source == ItemSource.Inventory && r.Item.Sockets.Unlocked < SocketState.Limit(r.Item.Key));
            grid.SelectedItem = target;
            Assert.IsNull(model.Detail);
            Assert.AreEqual(SocketState.Limit(target.Item.Key), model.SocketRows.Length);
            Assert.IsTrue(model.SocketRows.Skip(target.Item.Sockets.Unlocked).All(r => !r.IsEditable && r.Label.Contains("잠김")));
            Assert.AreEqual(model.SocketRows.Length + 1, model.SocketOptions.Count());
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            var scroll = Scroll(grid)!;
            scroll.ScrollToVerticalOffset(40); scroll.ScrollToHorizontalOffset(80);
            grid.Columns[2].Width = 150;
            window.UpdateLayout();
            double vertical = scroll.VerticalOffset, horizontal = scroll.HorizontalOffset;
            model.PageIndex = 1;
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            model.PageIndex = 2;
            window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            Assert.AreEqual(vertical, scroll.VerticalOffset);
            Assert.AreEqual(horizontal, scroll.HorizontalOffset, 0.01);
            Assert.AreEqual(150, grid.Columns[2].Width.Value);
            WindowTests.Capture(window, "locked-sockets");
            window.Width = 2300; window.UpdateLayout();
            Assert.AreEqual(2, Grid.GetColumn(editor));
            Assert.AreEqual(0, Grid.GetRow(editor));
            WindowTests.Capture(window, "wide");
            window.Width = 800; window.UpdateLayout();
            Assert.AreEqual(2, Grid.GetRow(editor));
            WindowTests.Capture(window, "small");
        }
        finally { window.Close(); }
    }

    private static ScrollViewer? Scroll(DependencyObject parent)
    {
        if (parent is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (Scroll(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }
}
