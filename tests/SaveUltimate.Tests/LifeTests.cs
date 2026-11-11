using System.Buffers.Binary;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.App;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class LifeTests
{
    private static SaveField[] WorkerRows(SaveDocument doc) =>
        SaveRecords.Read(doc.blob, doc.Layout, "MercenaryClanSaveData").Fields.Named("_mercenaryDataList").ListElements;

    [TestMethod]
    [TestCategory("Unit")]
    public void BusinessCatalogNamesEveryRegionInEveryLanguage()
    {
        var catalog = ManagementCatalog.Shared;
        Assert.AreEqual(18, catalog.Shops.Length);
        Assert.IsTrue(catalog.Shops.All(s => s.Kind != "festival" && s.MaxLevel == 5));
        foreach (var shop in catalog.Shops)
            foreach (string language in new[] { "ko", "en", "ja", "ru" })
                Assert.IsTrue(catalog.Regions[shop.Region].ContainsKey(language), $"{shop.Key} {language}");
        Assert.AreEqual("에르난드", catalog.RegionName("Her", "ko"));
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void WorkerJoinsARosterWithoutGreyZeroOrGuests()
    {
        // early_progress_2.03.02: no Grey-0 and no guest worker, so 1.6 had no record to copy.
        var doc = SaveDocument.Open(FixtureSaves.Named("early_progress")); string original = doc.BlobSha256;
        var roster = doc.ReadCompanions();
        Assert.IsFalse(roster.Any(c => SaveDocument.IsWorker(c.Key)));
        Assert.IsTrue(doc.CanAddWorkers);
        var added = doc.AddWorker(SaveDocument.RobotWorker);
        CollectionAssert.AreEqual(roster, added[..^1], "The game appends a recruit.");
        Assert.AreEqual((SaveDocument.RobotWorker, (string?)null), (added[^1].Key, added[^1].Name));
        // Pay and breeding times are the field time of the hiring, as the game stamps a festival recruit.
        var row = WorkerRows(doc)[^1].ChildFields;
        ulong now = doc.ReadFieldTime().Raw;
        Assert.AreEqual(now, row.Named("_lastPaidTime").Scalar(doc.blob, "uint64", 8));
        Assert.AreEqual(now, row.Named("_lastBreedingTime").Scalar(doc.blob, "uint64", 8));
        Assert.AreEqual(1UL, row.Named("_isInitialize").Scalar(doc.blob, "bool", 1));
        // A Grey-0 added by mistake can go again, back to a roster with none, as the game itself has it.
        Assert.IsNull(SaveDocument.RemoveBlock(added[^1], null, doc.FactionWorkers()));
        CollectionAssert.AreEqual(roster, doc.RemoveWorker(added[^1]));
        added = doc.AddWorker(SaveDocument.RobotWorker);
        using var temp = new TempFolder();
        string output = temp.Named("output.save");
        doc.SaveCopy(output);
        CollectionAssert.AreEqual(added, SaveDocument.Open(output).ReadCompanions());
        while (doc.CanUndo) doc.Undo();
        Assert.AreEqual(original, doc.BlobSha256);
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void NewWorkerRecordHasTheShapeOfTheGamesGreyZero()
    {
        // dlc_bond_courting_grey0_2.04.00: three Grey-0 written by the game. A new one has the same fields and size, and
        // differs only in its number, its times and the addresses of its objects.
        var doc = SaveDocument.Open(FixtureSaves.Named("dlc_bond_courting_grey0"));
        var natural = WorkerRows(doc).First(r => r.ChildFields.Named("_characterKey").Scalar(doc.blob, "CharacterKey", 4) == SaveDocument.RobotWorker);
        doc.AddWorker(SaveDocument.RobotWorker);
        var added = WorkerRows(doc)[^1];
        byte[] Bytes(SaveField r) => doc.blob.AsSpan(r.StartOffset, r.EndOffset - r.StartOffset).ToArray();
        byte[] a = Bytes(natural), b = Bytes(added);
        Assert.AreEqual(a.Length, b.Length);
        // Masks of the record and its experience record and counters.
        static int[] Changing(SaveField r, byte[] raw)
        {
            var offsets = new List<int>();
            void Walk(SaveField f) { if (f.ChildPayloadOffset != 0) offsets.AddRange(Enumerable.Range(f.ChildPayloadOffset - 4 - r.StartOffset, 4)); foreach (var c in f.ChildFields.Where(c => c.Present)) Walk(c); }
            Walk(r);
            foreach (string name in new[] { "_mercenaryNo", "_lastPaidTime", "_lastBreedingTime" })
                offsets.AddRange(Enumerable.Range(r.ChildFields.Named(name).StartOffset - r.StartOffset, 8));
            return offsets.ToArray();
        }
        var skip = Changing(added, doc.blob).ToHashSet();
        for (int i = 0; i < a.Length; i++)
            if (!skip.Contains(i)) Assert.AreEqual(a[i], b[i], $"byte {i}");
        while (doc.CanUndo) doc.Undo();
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void BusinessRenownChangesOnlyARunningShop()
    {
        // latest_progress_2.04.08: Timberham sawmill (16960) runs at renown 3 of 3 with score 191, rewards paid through 3.
        var doc = SaveDocument.Open(FixtureSaves.Named("latest_progress")); string original = doc.BlobSha256;
        var shops = doc.ReadManagement();
        Assert.AreEqual(18, shops.Length);
        var sawmill = shops.Single(s => s.Shop.Key == 16960);
        Assert.AreEqual(((uint?)3, (uint?)3, (uint?)191, (uint?)3, true), (sawmill.Level, sawmill.HighLevel, sawmill.Score, sawmill.RewardedLevel, sawmill.Editable));
        Assert.IsFalse(shops.Single(s => s.Shop.Key == 16961).InSave);
        var raised = doc.SetManagementLevel(16960, 5).Single(s => s.Shop.Key == 16960);
        // Progress toward the next level starts over, as on a shop that reached 5 in the game; rewards are the game's to pay.
        Assert.AreEqual(((uint?)5, (uint?)5, (uint?)null, (uint?)3), (raised.Level, raised.HighLevel, raised.Score, raised.RewardedLevel));
        var lowered = doc.SetManagementLevel(16960, 2).Single(s => s.Shop.Key == 16960);
        Assert.AreEqual(((uint?)2, (uint?)5), (lowered.Level, lowered.HighLevel));
        Assert.ThrowsExactly<InvalidOperationException>(() => doc.SetManagementLevel(16961, 3));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => doc.SetManagementLevel(16960, 6));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => doc.SetManagementLevel(16960, 0));
        using var temp = new TempFolder();
        string output = temp.Named("output.save");
        doc.SaveCopy(output);
        CollectionAssert.AreEqual(doc.ReadManagement(), SaveDocument.Open(output).ReadManagement());
        while (doc.CanUndo) doc.Undo();
        Assert.AreEqual(original, doc.BlobSha256);
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void FieldTimeMovesOnlyForward()
    {
        var doc = SaveDocument.Open(FixtureSaves.Named("latest_progress")); string original = doc.BlobSha256;
        var clock = doc.ReadFieldTime();
        Assert.AreEqual(11226395028UL, clock.Raw);
        Assert.AreEqual((148UL, new TimeSpan(11, 56, 5)), (clock.Days, new TimeSpan(clock.TimeOfDay.Hours, clock.TimeOfDay.Minutes, clock.TimeOfDay.Seconds)));
        var later = doc.AdvanceFieldTime(clock.After(1, 2, 0));
        Assert.AreEqual(clock.Raw + FieldClock.UnitsPerDay + 2 * 3600 * FieldClock.UnitsPerSecond, later.Raw);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => doc.AdvanceFieldTime(clock));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => doc.AdvanceFieldTime(later));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => doc.AdvanceFieldTime(later.After(36501, 0, 0)));
        using var temp = new TempFolder();
        string output = temp.Named("output.save");
        doc.SaveCopy(output);
        Assert.AreEqual(later, SaveDocument.Open(output).ReadFieldTime());
        doc.Undo();
        Assert.AreEqual(original, doc.BlobSha256);
    }

    [STATestMethod]
    [TestCategory("UI")]
    [DoNotParallelize]
    [TestCategory("PrivateFixtures")]
    public void LifeAndOtherMenusOpenTheirPages()
    {
        TestUi.EnsureApp();
        var model = new MainViewModel();
        var window = TestUi.Hidden(new MainWindow(model));
        window.Show();
        try
        {
            // Opening a save reads these pages with the others, without pressing Load Data.
            WindowTests.Pump(() => model.LoadFileAsync(FixtureSaves.Named("latest_progress")));
            Assert.IsNotNull(model.Management); Assert.IsNotNull(model.Clock);
            var nav = (ListBox)window.FindName("PrimaryNavigation");
            nav.SelectedIndex = 5; Assert.IsTrue(model.IsManagement);
            var rows = (DataGrid)((ManagementPage)window.FindName("ManagementArea")).FindName("Rows");
            Assert.AreEqual(18, rows.Items.Count);
            rows.SelectedItem = rows.Items.Cast<ManagementRow>().Single(r => r.Key == 16960);
            WindowTests.Capture(window, "management");
            WindowTests.Pump(() => model.SetManagementLevelAsync(16960, 4));
            Assert.AreEqual("4 / 5", rows.Items.Cast<ManagementRow>().Single(r => r.Key == 16960).Level);
            nav.SelectedIndex = 6; Assert.IsTrue(model.IsFieldTime);
            Assert.AreEqual("148일째 · 11:56", ((TextBlock)((FieldTimePage)window.FindName("FieldTimeArea")).FindName("Current")).Text);
            WindowTests.Capture(window, "field-time");
            nav.SelectedIndex = 7; Assert.IsTrue(model.IsBackups);
            WindowTests.Pump(() => model.UndoCommand.ExecuteAsync(null));
            Assert.IsFalse(model.HasUnsavedChanges);
        }
        finally { window.Close(); }
    }
}
