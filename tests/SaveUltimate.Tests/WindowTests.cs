using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.App;
using SaveUltimate.Core.Catalogs;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class WindowTests
{
    [STATestMethod]
    [TestCategory("UI")]
    [DoNotParallelize]
    public void RepurchaseSearchListsFittingItemsAndClearsSelectionOnFilter()
    {
        TestUi.EnsureApp();
        var window = TestUi.Hidden(new ItemSearchWindow(Sold(quantity: 2)));
        window.Show(); window.UpdateLayout();
        var results = (DataGrid)window.FindName("Results");
        var search = (TextBox)window.FindName("SearchBox");
        Assert.IsTrue(results.Items.Count is > 0 and <= 100);
        Assert.IsTrue(results.Items.Cast<ItemSearchWindow.Choice>().All(c => Sold(quantity: 2).CanBecome(c.Key)));
        results.SelectedItem = results.Items[0];
        Assert.IsNotNull(window.SelectedKey);
        search.Text = "unlikely_missing_item_12345";
        Assert.AreEqual(0, results.Items.Count);
        Assert.IsNull(window.SelectedKey);
        Assert.IsFalse(((Button)window.FindName("Accept")).IsEnabled);
        search.Text = "1004316"; // Lumen Sword holds one per stack.
        Assert.AreEqual(0, results.Items.Count);
        search.Text = "1002785";
        Assert.IsTrue(results.Items.Cast<ItemSearchWindow.Choice>().Any(c => c.Key == 1002785));
        TestUi.Capture(window, "search");
        window.Close();
    }

    private static InventoryItem Sold(ulong quantity = 1, ushort? enchant = null, ushort endurance = ushort.MaxValue, int gems = 0, int unlocked = 0,
        bool gear = false, bool stack = false, bool contents = false) => new(1, 1002785, quantity, 0, ItemSource.Vendor, 0, 0, Guid.Empty)
    {
        EnchantLevel = enchant, Endurance = endurance, HoldsGearState = gear, HoldsStackState = stack, HoldsContents = contents,
        Sockets = new(5, unlocked, Enumerable.Repeat(new ItemSocket(1, 0, true), gems).ToArray())
    };

    [TestMethod]
    [TestCategory("UI")]
    [DoNotParallelize]
    public void SoldRecordOnlyBecomesItemsThatCanHoldItsState()
    {
        const uint lumenSword = 1004316, stone = 1002785; // +10, 3 sockets, single / no enchant, no sockets, stacks to 50
        Assert.IsTrue(Sold().CanBecome(lumenSword), "A plain record has the same fields as an unenchanted sword.");
        Assert.IsTrue(Sold(enchant: 10, gems: 3, unlocked: 3, gear: true).CanBecome(lumenSword));
        Assert.IsFalse(Sold(quantity: 2).CanBecome(lumenSword));
        Assert.IsFalse(Sold(gems: 4).CanBecome(lumenSword));
        Assert.IsFalse(Sold(unlocked: 4).CanBecome(lumenSword));
        Assert.IsFalse(Sold(stack: true).CanBecome(lumenSword));
        Assert.IsTrue(Sold(quantity: 50, stack: true).CanBecome(stone));
        Assert.IsFalse(Sold(quantity: 51).CanBecome(stone));
        Assert.IsFalse(Sold(enchant: 0).CanBecome(stone));
        Assert.IsFalse(Sold(gear: true).CanBecome(stone));
        Assert.IsFalse(Sold(gems: 1).CanBecome(stone));
        Assert.IsFalse(Sold(endurance: 30).CanBecome(lumenSword));
        Assert.IsFalse(Sold(contents: true).CanBecome(stone));
        Assert.IsFalse(Sold().CanBecome(uint.MaxValue), "Unknown items are refused.");
    }
}
