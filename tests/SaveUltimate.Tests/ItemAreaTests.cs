using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.App;
using SaveUltimate.Core.Catalogs;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class ItemAreaTests
{
    [STATestMethod]
    [TestCategory("UI")]
    [DoNotParallelize]
    public void GemFiltersKeepEmptyAndSelectedAndImagesAreShared()
    {
        var catalog = ItemCatalog.Shared;
        Assert.AreEqual(210, catalog.Gems.Length, "Clear and every item the game groups as an Abyss gear.");
        Assert.AreEqual(Tr.Text("Special Effects"), catalog.Gems.Single(g => g.Key == 1006108).Category, "A DLC gear is listed.");
        DlcContent.Hidden = true; ItemCatalog.Reset();
        try { Assert.IsFalse(ItemCatalog.Shared.Gems.Any(g => DlcContent.Shared.Items.Contains(g.Key)), "Hidden DLC hides its gears."); }
        finally { DlcContent.Hidden = false; ItemCatalog.Reset(); }
        catalog = ItemCatalog.Shared;
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
        string corrupt = Path.Combine(AppContext.BaseDirectory, "icons_local", $"{uint.MaxValue - 2}.webp");
        Directory.CreateDirectory(Path.GetDirectoryName(corrupt)!);
        try
        {
            File.WriteAllText(corrupt, "invalid image");
            Assert.IsNull(ItemImages.Get(uint.MaxValue - 2, 32), "Corrupt local images must fall back without breaking selection.");
        }
        finally { File.Delete(corrupt); }
        Assert.AreEqual("화폐", catalog.Category(1));
        Assert.AreEqual("에르난드 장비상점", catalog.Store(1));
        Assert.AreEqual("캠프 의상점", catalog.Store(6700));
        Assert.AreEqual("Ceramic Artwork", catalog.Store(7000), "A store no NPC runs keeps its label.");
        Assert.AreEqual("65535", catalog.Store(65535));
    }
}
