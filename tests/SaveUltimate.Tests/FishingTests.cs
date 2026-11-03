using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.App;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class FishingTests
{
    private static string Fixtures => FixtureSaves.Scenarios ?? throw new AssertInconclusiveException("Set private fixtures.");
    [TestMethod]
    [TestCategory("PrivateFixtures")]
    public void ExistingAndInsertedRecordsChangeOnlyTheirFishSaveAndUndo()
    {
        // 2006 already has a record; 2001 and 2038 insert small and large values.
        foreach (var (key, size) in new[] { (2006u, 52.125), (2001u, 7.25), (2038u, 150.5) })
        {
            var document = SaveDocument.Open(Path.Combine(Fixtures, "editing_baseline_2.03.02.save"));
            string original = document.BlobSha256;
            var knowledge = document.ReadKnowledge().OrderBy(p => p.Key).ToArray();
            var items = document.Items.Select(r => (r.Key, r.Number, r.Quantity, r.Source)).ToArray();
            var before = document.ReadFishing();
            var after = document.SetFishingRecord(key, size);
            CollectionAssert.AreEqual(before.Select(r => r.Fish.Key == key ? r with { Size = (float)size, RecordTypeAvailable = true } : r).ToArray(), after);
            CollectionAssert.AreEqual(knowledge, document.ReadKnowledge().OrderBy(p => p.Key).ToArray());
            CollectionAssert.AreEqual(items, document.Items.Select(r => (r.Key, r.Number, r.Quantity, r.Source)).ToArray());
            string changed = document.BlobSha256;
            document.SetFishingRecord(key, size);
            Assert.AreEqual(changed, document.BlobSha256);
            string output = Path.Combine(Path.GetTempPath(), $"fishing-{Guid.NewGuid():N}.save");
            try
            {
                document.SaveCopy(output);
                var reopened = SaveDocument.Open(output);
                Assert.AreEqual(changed, reopened.BlobSha256);
                CollectionAssert.AreEqual(after, reopened.ReadFishing());
            }
            finally { if (File.Exists(output)) File.Delete(output); }
            document.Undo();
            Assert.AreEqual(original, document.BlobSha256);
            Assert.IsFalse(document.CanUndo);
            CollectionAssert.AreEqual(before, document.ReadFishing());
        }
    }

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    public void MissingFirstCatchInvalidInputAndUnlearnedFishCannotEdit()
    {
        var empty = SaveDocument.Open(Path.Combine(Fixtures, "fishing_no_first_catch.save"));
        Assert.IsTrue(empty.ReadFishing().All(r => !r.RecordTypeAvailable && r.Size is null));
        string original = empty.BlobSha256;
        Assert.Throws<InvalidOperationException>(() => empty.SetFishingRecord(2220, 43.4));
        Assert.AreEqual(original, empty.BlobSha256); Assert.IsFalse(empty.CanUndo);
        var document = SaveDocument.Open(Path.Combine(Fixtures, "editing_baseline_2.03.02.save"));
        original = document.BlobSha256;
        foreach (double value in new[] { double.NaN, double.PositiveInfinity, 0, -1, 1000.001, double.Epsilon })
            Assert.Throws<ArgumentOutOfRangeException>(() => document.SetFishingRecord(2006, value));
        Assert.Throws<InvalidOperationException>(() => document.SetFishingRecord(1000652, 1));
        Assert.AreEqual(original, document.BlobSha256); Assert.IsFalse(document.CanUndo);
        document.EditKnowledge([2006], KnowledgeAction.Unlearn);
        string unlearned = document.BlobSha256;
        Assert.Throws<InvalidOperationException>(() => document.SetFishingRecord(2006, 1));
        Assert.AreEqual(unlearned, document.BlobSha256);
        document.Undo(); Assert.AreEqual(original, document.BlobSha256); Assert.IsFalse(document.CanUndo);
    }

    [STATestMethod]
    [TestCategory("PrivateFixtures")]
    public void SelectionLearnApplyUndoAndFirstCatchBanner()
    {
        if (Application.Current is null) { var app = new App.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; }
        var document = SaveDocument.Open(Path.Combine(Fixtures, "editing_baseline_2.03.02.save"));
        var model = new MainViewModel(document);
        var window = new MainWindow(model) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            ((ListBox)window.FindName("PrimaryNavigation")).SelectedIndex = 1;
            var tabs = (TabControl)window.FindName("JournalTabs");
            Assert.AreEqual(3, tabs.Items.Count, "Quest database is intentionally excluded.");
            tabs.SelectedIndex = 2; Assert.IsTrue(model.IsFishing);
            var page = (FishingPage)window.FindName("FishingArea");
            var rows = (DataGrid)page.FindName("Rows");
            Assert.AreEqual(0, rows.Items.Count);
            WindowTests.Pump(() => model.LoadFishingCommand.ExecuteAsync(null));
            Assert.AreEqual(model.Fishing!.Count(r => r.Fish.RecordSupported), rows.Items.Count);
            var selected = rows.Items.Cast<FishingRow>().Single(r => r.Key == 2220);
            rows.SelectedItem = selected;
            var apply = (Button)page.FindName("Apply");
            var input = (TextBox)page.FindName("SizeInput");
            Assert.IsTrue(apply.IsEnabled); input.Text = "NaN"; Assert.IsFalse(apply.IsEnabled); input.Text = "43.400";
            string original = document.BlobSha256;
            WindowTests.Pump(() => model.SetFishingRecordAsync(2220, 43.4));
            Assert.AreSame(selected, rows.SelectedItem); Assert.AreEqual("43.400", selected.Size);
            WindowTests.Pump(() => model.UndoCommand.ExecuteAsync(null)); Assert.AreEqual(original, document.BlobSha256);
            WindowTests.Pump(() => model.EditKnowledgeAsync([2220], KnowledgeAction.Unlearn));
            Assert.IsFalse(apply.IsEnabled); Assert.IsTrue(((Button)page.FindName("Learn")).IsEnabled);
            WindowTests.Pump(() => model.EditKnowledgeAsync([2220], KnowledgeAction.Learn));
            Assert.IsTrue(apply.IsEnabled); Assert.AreSame(selected, rows.SelectedItem);
            rows.ScrollIntoView(selected);
            WindowTests.Capture(window, "fishing");
            window.Width = 800; window.UpdateLayout(); WindowTests.Capture(window, "fishing-narrow");
            Assert.AreEqual(2, Grid.GetRow((ScrollViewer)page.FindName("Details")), "Original detail panel moves below the table under 900px.");
            page.DataContext = new MainViewModel(SaveDocument.Open(Path.Combine(Fixtures, "fishing_no_first_catch.save")));
            WindowTests.Pump(() => ((MainViewModel)page.DataContext).LoadFishingCommand.ExecuteAsync(null));
            rows.SelectedIndex = 0;
            Assert.AreEqual(Visibility.Visible, ((Border)page.FindName("SetupBanner")).Visibility);
            Assert.IsFalse(apply.IsEnabled); Assert.IsFalse(((DockPanel)page.FindName("SizeEditor")).IsEnabled);
        }
        finally
        {
            while (document.CanUndo) document.Undo();
            window.Close();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("PrivateFixtures")]
    public void InvalidStoredCatchRecordsCannotBeReadOrEdited(bool duplicate)
    {
        string source = Path.Combine(Fixtures, "editing_baseline_2.03.02.save");
        byte[] raw = SaveContainer.Decode(File.ReadAllBytes(source)).Blob;
        var rows = SaveRecords.ReadLists(raw, ParcLayout.Parse(raw), "ContentsMiscSaveData", "_miniGameRecords")
            .Fields.Named("_miniGameRecords").Elements("MiniGameRecordSaveData");
        var field = rows[1].ChildFields.Named(duplicate ? "_key" : "_record");
        uint value = duplicate ? (uint)rows[0].ChildFields.Named("_key").Scalar(raw, "KnowledgeKey", 4) : BitConverter.SingleToUInt32Bits(float.NaN);
        BitConverter.GetBytes(value).CopyTo(raw, field.StartOffset);
        string path = Path.Combine(Path.GetTempPath(), $"invalid-fishing-{Guid.NewGuid():N}.save");
        try
        {
            NativeSave.Write(source, raw, path);
            var document = SaveDocument.Open(path);
            string original = document.BlobSha256;
            Assert.Throws<InvalidDataException>(() => document.ReadFishing());
            Assert.Throws<InvalidDataException>(() => document.SetFishingRecord(2006, 1));
            Assert.AreEqual(original, document.BlobSha256);
            Assert.IsFalse(document.CanUndo);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
