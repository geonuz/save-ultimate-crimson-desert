using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.App;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class WorldTests
{
    [TestMethod]
    [TestCategory("PrivateFixtures")]
    public void CompanionProjectionPreservesDirectFieldsAndBoundaries()
    {
        byte[] raw = SaveContainer.Decode(File.ReadAllBytes(Path.Combine(Fixtures, "save_v2.03.02.save"))).Blob;
        var layout = ParcLayout.Parse(raw);
        var full = SaveRecords.ReadLists(raw, layout, "MercenaryClanSaveData", "_mercenaryDataList")
            .Fields.Named("_mercenaryDataList").ListElements;
        var shallow = SaveRecords.ReadLists(raw, layout, "MercenaryClanSaveData", true, "_mercenaryDataList")
            .Fields.Named("_mercenaryDataList").ListElements;
        Assert.IsTrue(full.Length > 0);
        Assert.AreEqual(full.Length, shallow.Length);
        for (int i = 0; i < full.Length; i++)
        {
            foreach (var field in full[i].ChildFields)
            {
                field.ChildFields = [];
                field.ListElements = [];
            }
            Assert.AreEqual(JsonSerializer.Serialize(full[i]), JsonSerializer.Serialize(shallow[i]));
        }
        Assert.Throws<InvalidDataException>(() => SaveRecords.ReadLists(raw, layout, "QuestSaveData", true, "_questStateList"));
    }

    private static string Reference => Environment.GetEnvironmentVariable("SAVEULTIMATE_WORLD_REFERENCE") ?? throw new AssertInconclusiveException("Set world references.");
    private static string Fixtures => Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES") ?? throw new AssertInconclusiveException("Set private fixtures.");
    internal static void Apply(SaveDocument doc, JsonElement row)
    {
        switch (row.GetProperty("operation").GetString())
        {
            case "fog": doc.RevealMap(); break;
            case "map": doc.ActivateMap(row.GetProperty("action").GetInt32()); break;
            case "npc": doc.SetFriendship(row.GetProperty("key").GetUInt32(), row.GetProperty("expected").GetUInt64(), row.GetProperty("value").GetInt32()); break;
            case "rename": doc.RenameCompanions([doc.ReadCompanions().Single(r => r.Number == row.GetProperty("number").GetUInt64())], row.GetProperty("name").GetString()); break;
            case "clear-all": doc.RenameCompanions(doc.ReadCompanions().Where(r => r.Editable && r.Name is not null).ToArray(), null); break;
        }
    }
    private static void Same(SaveDocument doc, string operation, JsonElement expected)
    {
        if (operation is "map" or "fog")
        {
            var map = doc.ReadMap();
            Assert.AreEqual(expected.GetProperty("fog").GetString(), Convert.ToHexString(SHA256.HashData(map.Fog.Codes)).ToLowerInvariant());
            var discoveries = expected.GetProperty("discoveries"); Assert.AreEqual(discoveries.EnumerateObject().Count(), map.Discoveries.Count);
            foreach (var p in discoveries.EnumerateObject())
            {
                byte? Flag(string name) => p.Value.GetProperty(name).ValueKind == JsonValueKind.Null ? null : p.Value.GetProperty(name).GetByte();
                Assert.AreEqual(new MapDiscovery(Flag("_isCompleted"), Flag("_isDiscoverd"), p.Value.GetProperty("_fogPivotPosition").GetString()), map.Discoveries[p.Name]);
            }
            CollectionAssert.AreEqual(expected.GetProperty("knowledge").EnumerateObject().Select(p => (uint.Parse(p.Name), p.Value.GetUInt32())).OrderBy(p => p.Item1).ToArray(),
                map.Knowledge.Select(p => (p.Key, p.Value)).OrderBy(p => p.Key).ToArray());
            CollectionAssert.AreEqual(expected.GetProperty("completed").EnumerateArray().Select(p => p.GetUInt32()).Order().ToArray(),
                map.Progress.Mission.Where(p => p.Value.State == 5).Select(p => p.Key).Order().ToArray());
        }
        else if (operation == "npc")
        {
            var actual = doc.ReadFriendship(); Assert.AreEqual(expected.GetArrayLength(), actual.Length);
            foreach (var r in expected.EnumerateArray())
            {
                var a = actual.Single(a => a.Key == r.GetProperty("key").GetUInt32());
                ulong? Number(string name) => r.GetProperty(name).ValueKind == JsonValueKind.Null ? null : r.GetProperty(name).GetUInt64();
                Assert.AreEqual(Number("value"), a.Value); Assert.AreEqual(Number("global_value"), a.Global.Value);
                Assert.AreEqual(Number("level"), a.Global.Level); Assert.AreEqual(r.GetProperty("needs_sync").GetBoolean(), a.NeedsSync);
                Assert.AreEqual(r.GetProperty("field_records").GetArrayLength(), a.Fields.Length);
            }
        }
        else CollectionAssert.AreEqual(expected.EnumerateArray().Select(r => new Companion(r.GetProperty("number").GetUInt64(), r.GetProperty("key").GetUInt32(), r.GetProperty("name").GetString())).ToArray(), doc.ReadCompanions());
    }
    [TestMethod]
    [TestCategory("PrivateFixtures")]
    public void MapNpcAndCompanionsMatchPythonAndUndoAsOneTransaction()
    {
        foreach (int i in Enumerable.Range(0, 12))
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Reference, $"python-{i}.json")));
            var r = json.RootElement; string op = r.GetProperty("operation").GetString()!;
            var doc = SaveDocument.Open(r.GetProperty("path").GetString()!); string original = doc.BlobSha256;
            var items = doc.Items.Select(r => (r.Key, r.Number, r.Quantity, r.Source)).ToArray();
            Same(doc, op, r.GetProperty("before"));
            Apply(doc, r); Assert.AreEqual(r.GetProperty("sha256").GetString(), doc.BlobSha256, $"Python case {i}");
            Same(doc, op, r.GetProperty("after"));
            CollectionAssert.AreEqual(items, doc.Items.Select(r => (r.Key, r.Number, r.Quantity, r.Source)).ToArray());
            if (op is "map" or "fog") { Apply(doc, r); Assert.AreEqual(r.GetProperty("sha256").GetString(), doc.BlobSha256); }
            if (i is 4 or 7 or 9)
            {
                string output = Path.Combine(Path.GetTempPath(), $"world-{Guid.NewGuid():N}.save");
                try { doc.SaveCopy(output); Assert.AreEqual(doc.BlobSha256, SaveDocument.Open(output).BlobSha256); }
                finally { if (File.Exists(output)) File.Delete(output); }
            }
            doc.Undo(); Assert.AreEqual(original, doc.BlobSha256); Assert.IsFalse(doc.CanUndo);
            Same(doc, op, r.GetProperty("before"));
        }
    }
    [TestMethod]
    [TestCategory("PrivateFixtures")]
    public void InvalidSelectionsAndReadOnlyNpcRecordsNeverCommit()
    {
        var doc = SaveDocument.Open(Path.Combine(Fixtures, "save_v2.03.02.save")); string original = doc.BlobSha256;
        var npc = doc.ReadFriendship(); var row = npc.First(r => r.Editable);
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.SetFriendship(row.Key, row.Value!.Value, 99));
        Assert.Throws<InvalidOperationException>(() => doc.SetFriendship(row.Key, 999, 50));
        var absent = npc.First(r => !r.Editable); Assert.Throws<InvalidOperationException>(() => doc.SetFriendship(absent.Key, 0, 50));
        var companion = doc.ReadCompanions().First(r => r.Editable);
        Assert.Throws<InvalidOperationException>(() => doc.RenameCompanions([companion, companion], "X"));
        Assert.Throws<InvalidOperationException>(() => doc.RenameCompanions([companion with { Name = "stale" }], "X"));
        var readOnly = doc.ReadCompanions().FirstOrDefault(r => !r.Editable);
        if (readOnly is not null) Assert.Throws<InvalidOperationException>(() => doc.RenameCompanions([readOnly], "X"));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.ActivateMap(-1));
        Assert.AreEqual(original, doc.BlobSha256); Assert.IsFalse(doc.CanUndo);
    }

    [STATestMethod]
    [TestCategory("PrivateFixtures")]
    public void OriginalWorldNavigationLabelsFiltersAndUndo()
    {
        if (Application.Current is null) { var app = new App.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; }
        using var json = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Reference, "python-ui.json")));
        var labels = json.RootElement;
        var doc = SaveDocument.Open(Path.Combine(Fixtures, "save_v2.03.02.save"));
        var model = new MainViewModel(doc);
        var window = new MainWindow(model) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            var nav = (ListBox)window.FindName("PrimaryNavigation"); nav.SelectedIndex = 3; Assert.IsTrue(model.IsMap); Assert.IsNull(model.Map);
            WindowTests.Pump(() => model.LoadMapCommand.ExecuteAsync(null)); Assert.IsNotNull(model.Map);
            var map = (MapPage)window.FindName("MapArea");
            Assert.AreEqual(labels.GetProperty("load").GetString(), ((Button)map.FindName("LoadData")).Content);
            Assert.AreEqual(labels.GetProperty("reveal").GetString(), ((Button)map.FindName("Reveal")).Content);
            foreach (var box in new[] { "LeftCards", "RightCards" }.SelectMany(n => ((StackPanel)map.FindName(n)).Children.Cast<GroupBox>()))
                foreach (var actionRow in ((StackPanel)box.Content).Children.OfType<Grid>())
                {
                    var button = actionRow.Children.OfType<Button>().Single();
                    var expected = labels.GetProperty("mapActions").EnumerateArray().Single(a => a.GetProperty("key").GetString() == MapAction.All[(int)button.Tag].Names["en"]);
                    Assert.AreEqual(expected.GetProperty("name").GetString(), ((TextBlock)actionRow.Children[0]).Text);
                    Assert.AreEqual(expected.GetProperty("count").GetString(), ((TextBlock)actionRow.Children[1]).Text);
                    Assert.AreEqual(expected.GetProperty("enabled").GetBoolean(), button.IsEnabled);
                }
            WindowTests.Capture(window, "map");
            nav.SelectedIndex = 4; Assert.IsTrue(model.IsCompanions);
            var tabs = (TabControl)window.FindName("NpcTabs");
            CollectionAssert.AreEqual(labels.GetProperty("tabs").EnumerateArray().Select(e => e.GetString()).ToArray(), tabs.Items.Cast<TabItem>().Select(t => (string)t.Header).ToArray());
            WindowTests.Pump(() => model.LoadCompanionsCommand.ExecuteAsync(null));
            var page = (CompanionPage)window.FindName("CompanionArea"); var rows = (DataGrid)page.FindName("Rows");
            CollectionAssert.AreEqual(labels.GetProperty("companionButtons").EnumerateArray().Select(e => e.GetString()).ToArray(),
                new[] { "Rename", "Clear", "ClearAll" }.Select(n => (string)((Button)page.FindName(n)).Content).ToArray());
            var selected = rows.Items.Cast<CompanionRow>().First(r => r.Record.Editable);
            rows.SelectedItem = selected; string original = doc.BlobSha256;
            WindowTests.Pump(() => model.RenameCompanionsAsync([selected.Record], "동료 테스트"));
            Assert.AreEqual(selected.Record.Number, ((CompanionRow)rows.SelectedItem).Record.Number);
            WindowTests.Pump(() => model.UndoCommand.ExecuteAsync(null)); Assert.AreEqual(original, doc.BlobSha256);
            WindowTests.Capture(window, "companions");
            tabs.SelectedIndex = 1; Assert.IsTrue(model.IsFriendship); Assert.IsNull(model.Friendship);
            WindowTests.Pump(() => model.LoadFriendshipCommand.ExecuteAsync(null));
            var npcPage = (NpcPage)window.FindName("NpcArea"); var npcRows = (DataGrid)npcPage.FindName("Rows");
            CollectionAssert.AreEqual(labels.GetProperty("npcHeaders").EnumerateArray().Select(e => e.GetString()).ToArray(), npcRows.Columns.Select(c => (string)c.Header).ToArray());
            Assert.AreEqual(labels.GetProperty("npcApply").GetString(), ((Button)npcPage.FindName("Apply")).Content);
            var search = (TextBox)npcPage.FindName("Search"); search.Text = "마구";
            Assert.IsTrue(npcRows.Items.Cast<NpcRow>().Any(r => r.Name == "엘드레드"));
            search.Text = "no_matching_npc"; Assert.AreEqual(0, npcRows.Items.Count); search.Text = "";
            var npc = npcRows.Items.Cast<NpcRow>().First(r => r.Record.Editable);
            npcRows.SelectedItem = npc;
            WindowTests.Pump(() => model.SetFriendshipAsync(npc.Key, npc.Record.Value!.Value, npc.Record.Value == 95 ? 90 : 95));
            Assert.AreSame(npc, npcRows.SelectedItem); Assert.IsTrue(((Button)npcPage.FindName("Apply")).IsEnabled);
            WindowTests.Pump(() => model.UndoCommand.ExecuteAsync(null)); Assert.AreEqual(original, doc.BlobSha256);
            WindowTests.Capture(window, "npc");
            window.Width = 800; window.UpdateLayout(); WindowTests.Capture(window, "npc-narrow");
            nav.SelectedIndex = 0; nav.SelectedIndex = 4; Assert.IsTrue(model.IsFriendship);
        }
        finally { while (doc.CanUndo) doc.Undo(); window.Close(); }
    }
}
