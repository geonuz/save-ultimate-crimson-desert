using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using CDSE.App;
using CDSE.Core.Catalogs;
using CDSE.Core.Saves;

namespace CDSE.Tests;

[TestClass]
public sealed class ItemAreaTests
{
    [STATestMethod]
    [TestCategory("UI")]
    [DoNotParallelize]
    public void GemPickerListsFittingAndSelectedAndImagesAreShared()
    {
        var catalog = ItemCatalog.Shared;
        Assert.AreEqual(210, catalog.Gems.Length, "Clear and every item the game groups as an Abyss gear.");
        Assert.AreEqual(Tr.Text("Special Effects"), catalog.Gems.Single(g => g.Key == 1006108).Category, "A DLC gear is listed.");
        DlcContent.Hidden = true; ItemCatalog.Reset();
        try { Assert.IsFalse(ItemCatalog.Shared.Gems.Any(g => DlcContent.Shared.Items.Contains(g.Key)), "Hidden DLC hides its gears."); }
        finally { DlcContent.Hidden = false; ItemCatalog.Reset(); }
        catalog = ItemCatalog.Shared;
        // A socket's picker lists what fits the equipment, and the gear already in the socket whatever it is.
        var unknown = new SocketRow(0, 1, uint.MaxValue, false, true);
        Assert.IsFalse(unknown.IsEditable || unknown.CanClear);
        Assert.IsTrue(unknown.Label.Contains("잠김"));
        Assert.IsTrue(unknown.Choices().Any(g => g.Key == uint.MaxValue));
        // Every gear and every socketed equipment has its tags, and each equipment takes some gears and refuses others.
        uint[] gears = catalog.Gems.Where(g => g.Key != 0).Select(g => g.Key).ToArray();
        foreach (uint equipment in catalog.Choices.Select(c => c.Key).Where(k => ItemRules.Sockets(k) > 0))
            Assert.IsTrue(gears.Count(g => ItemRules.Fits(equipment, g)) is > 0 and < 209, equipment.ToString());
        uint first = catalog.Choices.First(c => ItemRules.Sockets(c.Key) > 0).Key;
        var fitting = new SocketRow(first, 0, 0, true, false);
        CollectionAssert.AreEquivalent(gears.Where(g => ItemRules.Fits(first, g)).ToArray(), fitting.Choices().Select(c => c.Key).ToArray());
        Assert.IsTrue(fitting.Choices().All(c => c.Category.Length > 0), "The picker shows each gear's category.");
        // And what each gear does: the amount of a stat gear, the level of a resistance, the effect a skill gear's knowledge names.
        Assert.IsTrue(catalog.Gems.Where(g => g.Key != 0).All(g => g.Effect.Length > 0 && g.Detail.Length > 0), "Every gear says what it does.");
        Assert.AreEqual(Tr.Text("Damage") + " +2", catalog.GemEffect(1002786), "Destruction II");
        Assert.AreEqual(Tr.Text("Crit Rate") + " +" + 2.9.ToString(System.Globalization.CultureInfo.CurrentCulture) + "%", catalog.GemEffect(1002791), "Insight I");
        Assert.AreEqual(Tr.Text("Fire Resist") + " Lv.3", catalog.GemEffect(1001426), "Flameward III");
        Assert.IsTrue(catalog.GemEffect(1002578).StartsWith("벼락이") && catalog.GemDetail(1002578).Contains("천둥의 송곳니"), catalog.GemEffect(1002578));
        Assert.IsTrue(catalog.GemDetail(1002786).StartsWith(catalog.GemEffect(1002786) + "\n\n"), "The detail starts with the amount.");
        // A skill gear with an effect line of its own shows the game's tooltip line for its level, not the knowledge text.
        Assert.AreEqual("반려동물 친밀도 획득 시 2 추가", catalog.GemEffect(1000861), "Companionship II");
        Assert.AreEqual("100% 확률로 화살 미소모", catalog.GemEffect(1002982), "Greater Infinite Arrows");
        Assert.IsTrue(catalog.GemDetail(1000861).StartsWith(catalog.GemEffect(1000861) + "\n\n"));
        Assert.IsTrue(fitting.Choices().All(c => c.Effect.Length > 0), "The picker shows each gear's effect.");
        Assert.IsFalse(fitting.CanClear, "An empty socket has nothing to take out.");
        fitting.SelectedKey = gears.First(g => ItemRules.Fits(first, g));
        Assert.IsTrue(fitting.CanClear && fitting.Name == catalog.Name(fitting.SelectedKey));
        // What can be added: named inventory items only, never what the collection box holds.
        Assert.IsTrue(catalog.Choices.Count(c => ItemRules.CanAdd(c.Key)) > 3000);
        Assert.IsFalse(CollectionCatalog.Shared.Items.Any(i => ItemRules.CanAdd(i.Key)));
        Assert.IsTrue(catalog.Choices.Where(c => ItemRules.CanAdd(c.Key)).All(c => c.Name.Length > 0 && ItemRules.Stack(c.Key) > 0));
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
