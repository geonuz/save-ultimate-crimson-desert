using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.App;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class FishingTests
{
    private static string Fixtures => Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES") ?? throw new AssertInconclusiveException("Set private fixtures.");
    private static string Reference => Environment.GetEnvironmentVariable("SAVEULTIMATE_FISHING_REFERENCE") ?? throw new AssertInconclusiveException("Set Python fishing reference.");

    private static void Same(JsonElement expected, FishingRecord[] actual)
    {
        Assert.AreEqual(expected.GetArrayLength(), actual.Length);
        foreach (var row in expected.EnumerateArray())
        {
            var fish = actual.Single(r => r.Fish.Key == row.GetProperty("key").GetUInt32());
            Assert.AreEqual(row.GetProperty("level").GetUInt32(), fish.Level);
            Assert.AreEqual(row.GetProperty("recordSupported").GetBoolean(), fish.Fish.RecordSupported);
            Assert.AreEqual(row.GetProperty("recordTypeAvailable").GetBoolean(), fish.RecordTypeAvailable);
            Assert.AreEqual(row.GetProperty("size_cm").ValueKind == JsonValueKind.Null ? (float?)null : row.GetProperty("size_cm").GetSingle(), fish.Size);
        }
    }

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    public void PythonParityScalarInsertionSaveAndUndo()
    {
        foreach (int i in Enumerable.Range(0, 3))
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Reference, $"python-{i}.json")));
            var row = json.RootElement;
            var document = SaveDocument.Open(row.GetProperty("path").GetString()!);
            string original = document.BlobSha256;
            var knowledge = document.ReadKnowledge().OrderBy(p => p.Key).ToArray();
            var items = document.Items.Select(r => (r.Key, r.Number, r.Quantity, r.Source)).ToArray();
            Same(row.GetProperty("before"), document.ReadFishing());
            uint key = row.GetProperty("key").GetUInt32();
            double size = row.GetProperty("size").GetDouble();
            Same(row.GetProperty("after"), document.SetFishingRecord(key, size));
            Assert.AreEqual(row.GetProperty("sha256").GetString(), document.BlobSha256);
            CollectionAssert.AreEqual(knowledge, document.ReadKnowledge().OrderBy(p => p.Key).ToArray());
            CollectionAssert.AreEqual(items, document.Items.Select(r => (r.Key, r.Number, r.Quantity, r.Source)).ToArray());
            document.SetFishingRecord(key, size);
            string output = Path.Combine(Path.GetTempPath(), $"fishing-{Guid.NewGuid():N}.save");
            try
            {
                document.SaveCopy(output);
                Assert.AreEqual(document.BlobSha256, SaveDocument.Open(output).BlobSha256);
            }
            finally { if (File.Exists(output)) File.Delete(output); }
            document.Undo();
            Assert.AreEqual(original, document.BlobSha256);
            Assert.IsFalse(document.CanUndo);
            Same(row.GetProperty("before"), document.ReadFishing());
        }
    }

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    public void MissingFirstCatchInvalidInputAndUnlearnedFishCannotEdit()
    {
        var empty = SaveDocument.Open(Path.Combine(Fixtures, "save_fishnot.save"));
        Assert.IsTrue(empty.ReadFishing().All(r => !r.RecordTypeAvailable && r.Size is null));
        string original = empty.BlobSha256;
        Assert.Throws<InvalidOperationException>(() => empty.SetFishingRecord(2220, 43.4));
        Assert.AreEqual(original, empty.BlobSha256); Assert.IsFalse(empty.CanUndo);
        var document = SaveDocument.Open(Path.Combine(Fixtures, "save_v2.03.02.save"));
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
    public void OriginalLabelsSelectionLearnApplyUndoAndFirstCatchBanner()
    {
        if (Application.Current is null) { var app = new App.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; }
        using var labels = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Reference, "python-ui-labels.json")));
        string Label(string key) => labels.RootElement.GetProperty(key).GetString()!;
        var document = SaveDocument.Open(Path.Combine(Fixtures, "save_v2.03.02.save"));
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
            foreach (var pair in new[] { ("LoadData", "load"), ("Learn", "learn"), ("Apply", "apply") })
                Assert.AreEqual(Label(pair.Item2), ((Button)page.FindName(pair.Item1)).Content);
            CollectionAssert.AreEqual(labels.RootElement.GetProperty("headers").EnumerateArray().Select(r => r.GetString()).ToArray(),
                rows.Columns.Select(c => (string)c.Header).ToArray());
            WindowTests.Pump(() => model.LoadFishingCommand.ExecuteAsync(null));
            Assert.AreEqual(43, rows.Items.Count);
            CollectionAssert.AreEqual(labels.RootElement.GetProperty("names").EnumerateArray().Select(r => r.GetString()).ToArray(), rows.Items.Cast<FishingRow>().Select(r => r.Name).ToArray());
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
            page.DataContext = new MainViewModel(SaveDocument.Open(Path.Combine(Fixtures, "save_fishnot.save")));
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
        string source = Path.Combine(Fixtures, "save_v2.03.02.save");
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
