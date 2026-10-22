using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.App;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class ChallengeTests
{
    [TestMethod]
    [TestCategory("PrivateFixtures")]
    public void SelectedListReaderMatchesFullMetadataAndRejectsUnsupportedFields()
    {
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        byte[] raw = SaveContainer.Decode(File.ReadAllBytes(Path.Combine(fixtures, "save_fishnot.save"))).Blob;
        var layout = ParcLayout.Parse(raw);
        var expected = SaveRecords.Read(raw, layout, "KnowledgeSaveData").Fields.Named("_list");
        var selected = SaveRecords.ReadLists(raw, layout, "KnowledgeSaveData", "_list");
        Assert.AreEqual(1, selected.Fields.Length);
        Assert.AreEqual(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(selected.Fields[0]));
        Assert.Throws<InvalidDataException>(() => SaveRecords.ReadLists(raw, layout, "KnowledgeSaveData", "_not_a_list"));
        Assert.Throws<InvalidDataException>(() => SaveRecords.ReadLists(raw, layout, "KnowledgeSaveData", "_list", "_list"));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("PrivateFixtures")]
    public void InvalidClockOrUnknownStateDoesNotCommit(bool invalidClock)
    {
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        string source = Path.Combine(fixtures, "save_도전과제 일부 미완료.save");
        byte[] raw = SaveContainer.Decode(File.ReadAllBytes(source)).Blob;
        var block = SaveRecords.ReadLists(raw, ParcLayout.Parse(raw), "QuestSaveData", "_questStateList", "_missionStateList");
        if (invalidClock)
        {
            foreach (var field in block.Fields.SelectMany(f => f.ListElements).SelectMany(r => r.ChildFields)
                .Where(f => f.Present && f.Name is "_completedTime" or "_branchedTime"))
                raw.AsSpan(field.StartOffset, 8).Clear();
        }
        else
        {
            var row = block.Fields.Named("_missionStateList").ListElements.Single(r => r.ChildFields.Named("_key").Scalar(raw, "MissionKey", 4) == 4294966659);
            raw[row.ChildFields.Named("_state").StartOffset] = 6;
        }
        string path = Path.Combine(Path.GetTempPath(), $"challenge-invalid-{Guid.NewGuid():N}.save");
        try
        {
            NativeSave.Write(source, raw, path);
            var document = SaveDocument.Open(path);
            string hash = document.BlobSha256;
            Assert.Throws<InvalidDataException>(() => document.CompleteChallenges([1000912], [4294966659]));
            Assert.AreEqual(hash, document.BlobSha256);
            Assert.IsFalse(document.CanUndo);
            Assert.IsFalse(document.IsDirty);
        }
        finally { File.Delete(path); }
    }

    private static void Equal(JsonElement expected, ChallengeState actual)
    {
        foreach (var (name, records) in new[] { ("quest", actual.Quest), ("mission", actual.Mission) })
        {
            var rows = expected.GetProperty(name);
            Assert.AreEqual(rows.EnumerateObject().Count(), records.Count);
            foreach (var property in rows.EnumerateObject())
            {
                var row = property.Value;
                ulong? Value(string key) => row.TryGetProperty(key, out var p) && p.ValueKind != JsonValueKind.Null ? p.GetUInt64() : null;
                Assert.AreEqual(new JournalRecord((byte?)Value("state"), Value("_completedTime"), Value("_branchedTime"),
                    (uint?)Value("_completeCount"), (byte?)Value("_uiState")), records[uint.Parse(property.Name)], property.Name);
            }
        }
    }
    [TestMethod]
    [TestCategory("PrivateFixtures")]
    public void CheckpointsFamiliesAndBatchMatchPythonAndUndoExactly()
    {
        string? directory = Environment.GetEnvironmentVariable("SAVEULTIMATE_CHALLENGE_REFERENCE");
        if (directory is null) Assert.Inconclusive("Set private challenge references.");
        string[] files = Enumerable.Range(0, 4).Select(i => Path.Combine(directory, $"python-{i}.json")).ToArray();
        foreach (string file in files)
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(file));
            var row = json.RootElement;
            string path = row.GetProperty("path").GetString()!;
            byte[] source = File.ReadAllBytes(path);
            var document = SaveDocument.Open(path);
            string original = document.BlobSha256;
            string items = JsonSerializer.Serialize(document.Items);
            var knowledge = document.ReadKnowledge();
            Equal(row.GetProperty("before"), document.ReadChallenges());
            uint[] roots = row.GetProperty("roots").EnumerateArray().Select(k => k.GetUInt32()).ToArray();
            uint[]? checkpoints = row.GetProperty("checkpoints").ValueKind == JsonValueKind.Null ? null :
                row.GetProperty("checkpoints").EnumerateArray().Select(k => k.GetUInt32()).ToArray();
            Assert.Throws<InvalidOperationException>(() => document.CompleteChallenges([]));
            Assert.Throws<InvalidOperationException>(() => document.CompleteChallenges([roots[0], roots[0]]));
            Assert.Throws<InvalidOperationException>(() => document.CompleteChallenges([4294966659]));
            Assert.Throws<InvalidOperationException>(() => document.CompleteChallenges([1000912], [1000912]));
            Assert.Throws<InvalidOperationException>(() => document.CompleteChallenges([1000912], [1000603]));
            Assert.Throws<InvalidOperationException>(() => document.CompleteChallenges([1000912], [4294966659, 4294966659]));
            Assert.AreEqual(original, document.BlobSha256);
            var result = document.CompleteChallenges(roots, checkpoints);
            Assert.AreEqual(row.GetProperty("completed").GetInt32(), result.Completed);
            Equal(row.GetProperty("after"), result.State);
            Assert.AreEqual(row.GetProperty("sha256").GetString(), document.BlobSha256);
            Assert.AreEqual(items, JsonSerializer.Serialize(document.Items), "Rewards must not be granted.");
            CollectionAssert.AreEqual(knowledge.OrderBy(p => p.Key).ToArray(), document.ReadKnowledge().OrderBy(p => p.Key).ToArray());
            Assert.AreEqual(0, document.CompleteChallenges(roots, checkpoints).Completed);
            if (row.GetProperty("operation").GetString() == "challenge-batch")
            {
                string copy = Path.Combine(Path.GetTempPath(), $"challenges-{Guid.NewGuid():N}.save");
                try
                {
                    document.SaveCopy(copy);
                    var reopened = SaveDocument.Open(copy);
                    Assert.AreEqual(document.BlobSha256, reopened.BlobSha256);
                    Equal(row.GetProperty("after"), reopened.ReadChallenges());
                    Assert.AreEqual(items, JsonSerializer.Serialize(reopened.Items));
                }
                finally { File.Delete(copy); }
            }
            document.Undo();
            Assert.AreEqual(original, document.BlobSha256);
            Assert.IsFalse(document.CanUndo, "No-op must not add history.");
            Equal(row.GetProperty("before"), document.ReadChallenges());
            CollectionAssert.AreEqual(source, File.ReadAllBytes(path));
        }
    }

    [STATestMethod]
    [TestCategory("PrivateFixtures")]
    public void OriginalLabelsAndChallengeSelectionUndoAndFiltersMatch()
    {
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        using var locale = JsonDocument.Parse(File.ReadAllBytes(Path.GetFullPath(Path.Combine(fixtures, "../../archive/editor-python/resources/locale/ko.json"))));
        string Text(string key) => locale.RootElement.GetProperty(key).GetString()!.Replace("&&", "&");
        if (Application.Current is null) { var app = new App.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; }
        var document = SaveDocument.Open(Path.Combine(fixtures, "save_도전과제 일부 미완료.save"));
        var model = new MainViewModel(document);
        var window = new MainWindow(model) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            var navigation = (ListBox)window.FindName("PrimaryNavigation");
            CollectionAssert.AreEqual(new[] { "tab.items", "tab.knowledge_quest", "skills.title", "tab.maps", "tab.mercenary", "tab.backup" }.Select(Text).ToArray(),
                navigation.Items.Cast<ListBoxItem>().Select(AutomationProperties.GetName).ToArray());
            var itemTabs = (TabControl)window.FindName("ItemTabs");
            CollectionAssert.AreEqual(new[] { "tab.inventory", "tab.repurchase", "tab.equipment", "tab.database" }.Select(Text).ToArray(),
                itemTabs.Items.Cast<TabItem>().Select(t => (string)t.Header).ToArray());
            navigation.SelectedIndex = 1;
            var tabs = (TabControl)window.FindName("JournalTabs");
            CollectionAssert.AreEqual(new[] { Text("tab.knowledge"), Text("Challenges"), "낚시" },
                tabs.Items.Cast<TabItem>().Select(t => (string)t.Header).ToArray());
            tabs.SelectedIndex = 1;
            Assert.IsTrue(model.IsChallenges);
            var page = (ChallengePage)window.FindName("ChallengeArea");
            var tree = (TreeView)page.FindName("Tree");
            var rows = (DataGrid)page.FindName("Rows");
            string? reference = Environment.GetEnvironmentVariable("SAVEULTIMATE_CHALLENGE_REFERENCE");
            if (reference is null) Assert.Inconclusive("Set captured Python UI reference.");
            using var labels = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(reference, "python-ui-labels.json")));
            string[] Captured(string key) => labels.RootElement.GetProperty(key).EnumerateArray().Select(v => v.GetString()!.Replace("&&", "&")).ToArray();
            CollectionAssert.AreEqual(Captured("navigation"), navigation.Items.Cast<ListBoxItem>().Select(AutomationProperties.GetName).ToArray());
            CollectionAssert.AreEqual(Captured("items"), itemTabs.Items.Cast<TabItem>().Select(t => (string)t.Header).ToArray());
            CollectionAssert.AreEqual(Captured("journal").Take(2).Append("낚시").ToArray(), tabs.Items.Cast<TabItem>().Select(t => (string)t.Header).ToArray());
            Assert.AreEqual(labels.RootElement.GetProperty("load").GetString(), ((Button)page.FindName("LoadData")).Content);
            var knowledgePage = (KnowledgePage)window.FindName("KnowledgeArea");
            CollectionAssert.AreEqual(Captured("knowledge"), new[] { "Learn", "Unlearn", "Apply", "Maximum", "AllMaximum" }
                .Select(n => (string)((Button)knowledgePage.FindName(n)).Content).ToArray());
            var skillPage = (SkillPage)((TabItem)((SkillsView)window.FindName("SkillsArea")).Items[0]).Content;
            CollectionAssert.AreEqual(Captured("skills").Take(2).ToArray(), new[] { "Invest", "Watch" }.Select(n => (string)((Button)skillPage.FindName(n)).Content).ToArray());
            Assert.AreEqual(0, tree.Items.Count);
            WindowTests.Pump(() => model.LoadChallengesCommand.ExecuteAsync(null));
            WindowTests.Pump(() => model.LoadMapCommand.ExecuteAsync(null));
            ChallengeNode[] Roots() => tree.Items.Cast<ChallengeNode>().SelectMany(n => n.Children).SelectMany(n => n.Children).ToArray();
            Assert.AreEqual(ChallengeCatalog.Completion.Roots.Length, Roots().Length);
            var node = Roots().Single(n => n.Root == 1000912);
            bool Contains(ChallengeNode parent, ChallengeNode target) => parent == target || parent.Children.Any(n => Contains(n, target));
            void Select(ItemsControl parent, ChallengeNode target)
            {
                parent.UpdateLayout();
                var current = parent.Items.Cast<ChallengeNode>().Single(n => Contains(n, target));
                var container = (TreeViewItem)parent.ItemContainerGenerator.ContainerFromItem(current);
                if (current == target) { container.IsSelected = true; container.BringIntoView(); window.UpdateLayout(); }
                else { container.IsExpanded = true; Select(container, target); }
            }
            Select(tree, node);
            Assert.AreEqual(ChallengeCatalog.Shared.Descendants(1000912).Length, rows.Items.Count);
            rows.SelectedItem = rows.Items.Cast<ChallengeRow>().Single(r => r.Key == 4294966659);
            var complete = (Button)page.FindName("CompleteSelected");
            Assert.IsTrue(complete.IsEnabled);
            Assert.AreEqual(Text("challenge.complete_selected") + " (1)", complete.Content);
            string hash = document.BlobSha256;
            WindowTests.Capture(window, "challenges");
            WindowTests.Pump(() => model.CompleteChallengesAsync([1000912], [4294966659]));
            Assert.AreEqual((byte)5, model.Challenges!.Mission[4294966659].State);
            Assert.AreNotEqual((byte)5, model.Challenges.Mission[1000912].State);
            Assert.IsFalse(complete.IsEnabled);
            Assert.AreEqual(4294966659u, ((ChallengeRow)rows.SelectedItem).Key);
            WindowTests.Pump(() => model.UndoCommand.ExecuteAsync(null));
            Assert.AreEqual(hash, document.BlobSha256);
            Assert.AreSame(model.Challenges, model.Map!.Progress, "Undo must also refresh the hidden map's challenge progress.");
            Assert.IsTrue(complete.IsEnabled);
            ((CheckBox)page.FindName("Incomplete")).IsChecked = true;
            Assert.AreEqual(13, Roots().Length);
            ((TextBox)page.FindName("Search")).Text = "no_matching_challenge_123";
            Assert.AreEqual(0, tree.Items.Count);
            Assert.IsFalse(((Button)page.FindName("CompleteAll")).IsEnabled);
            ((TextBox)page.FindName("Search")).Text = "";
            var quest = tree.Items.Cast<ChallengeNode>().SelectMany(n => n.Children).First(n => n.Children.Length > 0);
            Select(tree, quest);
            foreach (var row in rows.Items.Cast<ChallengeRow>().Take(2)) rows.SelectedItems.Add(row);
            Assert.IsTrue(((Button)page.FindName("CompleteAll")).IsEnabled);
            Assert.AreEqual(Visibility.Collapsed, complete.Visibility);
            navigation.SelectedIndex = 0; navigation.SelectedIndex = 1;
            Assert.IsTrue(model.IsChallenges, "Returning to the journal preserves its sub-tab.");
            window.Width = 800; window.UpdateLayout();
            WindowTests.Capture(window, "challenges-narrow");
        }
        finally
        {
            while (document.CanUndo) document.Undo();
            window.Close();
        }
    }
}
