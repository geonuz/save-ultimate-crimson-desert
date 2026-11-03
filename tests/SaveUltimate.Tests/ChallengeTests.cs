using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.App;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class ChallengeTests
{
    [TestMethod]
    public void ShippedCompletionCoversEveryOfferedChallenge()
    {
        var catalog = ChallengeCatalog.Shared; var completion = ChallengeCatalog.Completion;
        CollectionAssert.AreEquivalent(catalog.SourceSha256.ToArray(), completion.ClientSources.ToArray());
        Assert.IsTrue(completion.Missions.Keys.All(catalog.Missions.ContainsKey));
        var roots = catalog.Groups.SelectMany(g => g.Quests).SelectMany(q => catalog.Quests[q].Missions!).ToHashSet();
        Assert.IsTrue(completion.Roots.Length > 0 && completion.Roots.All(roots.Contains));
        Assert.IsTrue(completion.Roots.All(r => catalog.Descendants(r).All(completion.Missions.ContainsKey)));
    }

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    public void SelectedListReaderMatchesFullMetadataAndRejectsUnsupportedFields()
    {
        string? fixtures = FixtureSaves.Scenarios;
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        byte[] raw = SaveContainer.Decode(File.ReadAllBytes(Path.Combine(fixtures, "fishing_no_first_catch.save"))).Blob;
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
        string? fixtures = FixtureSaves.Scenarios;
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        string source = Path.Combine(fixtures, "challenges_partly_incomplete_2.03.02.save");
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

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    public void CheckpointsFamiliesAndBatchCompleteOnlyTheirMissionsAndUndoExactly()
    {
        string? fixtures = FixtureSaves.Scenarios;
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        uint[] batch = [1000912, 1000913, 1000915, 1001044, 1001048, 1003194, 1000423, 1003957, 1004406, 1004407, 1004408, 1004409, 1004410];
        // An existing and a missing checkpoint record, one family, and a batch; the counts are what those saves leave incomplete.
        foreach (var (file, roots, checkpoints, completed) in new (string, uint[], uint[]?, int)[] {
            ("challenges_partly_incomplete_2.03.02.save", [1000912], [4294966659], 1), ("early_progress_2.03.02.save", [1000912], [4294966659], 1),
            ("fishing_no_first_catch.save", [1000912], null, 7), ("challenges_partly_incomplete_2.03.02.save", batch, null, 38) })
        {
            string path = Path.Combine(fixtures, file);
            byte[] source = File.ReadAllBytes(path);
            var document = SaveDocument.Open(path);
            string original = document.BlobSha256;
            string items = JsonSerializer.Serialize(document.Items);
            var knowledge = document.ReadKnowledge();
            var before = document.ReadChallenges();
            Assert.Throws<InvalidOperationException>(() => document.CompleteChallenges([]));
            Assert.Throws<InvalidOperationException>(() => document.CompleteChallenges([roots[0], roots[0]]));
            Assert.Throws<InvalidOperationException>(() => document.CompleteChallenges([4294966659]));
            Assert.Throws<InvalidOperationException>(() => document.CompleteChallenges([1000912], [1000912]));
            Assert.Throws<InvalidOperationException>(() => document.CompleteChallenges([1000912], [1000603]));
            Assert.Throws<InvalidOperationException>(() => document.CompleteChallenges([1000912], [4294966659, 4294966659]));
            Assert.AreEqual(original, document.BlobSha256);
            var result = document.CompleteChallenges(roots, checkpoints);
            Assert.AreEqual(completed, result.Completed, file);
            var targets = (checkpoints ?? roots.SelectMany(r => ChallengeCatalog.Shared.Descendants(r).Append(r))).ToHashSet();
            Assert.IsTrue(targets.All(k => result.State.Mission[k].State == 5), file);
            foreach (var (key, record) in before.Mission.Where(p => !ChallengeCatalog.Completion.Missions.ContainsKey(p.Key)))
                Assert.AreEqual(record, result.State.Mission[key], $"{file} mission {key} is outside every completion");
            Assert.AreEqual(completed, result.State.Mission.Count(p => p.Value.State == 5) - before.Mission.Count(p => p.Value.State == 5), file);
            Assert.AreEqual(items, JsonSerializer.Serialize(document.Items), "Rewards must not be granted.");
            CollectionAssert.AreEqual(knowledge.OrderBy(p => p.Key).ToArray(), document.ReadKnowledge().OrderBy(p => p.Key).ToArray());
            Assert.AreEqual(0, document.CompleteChallenges(roots, checkpoints).Completed);
            if (roots.Length > 1)
            {
                string copy = Path.Combine(Path.GetTempPath(), $"challenges-{Guid.NewGuid():N}.save");
                try
                {
                    document.SaveCopy(copy);
                    var reopened = SaveDocument.Open(copy);
                    Assert.AreEqual(document.BlobSha256, reopened.BlobSha256);
                    CollectionAssert.AreEquivalent(result.State.Mission.ToArray(), reopened.ReadChallenges().Mission.ToArray());
                    Assert.AreEqual(items, JsonSerializer.Serialize(reopened.Items));
                }
                finally { File.Delete(copy); }
            }
            document.Undo();
            Assert.AreEqual(original, document.BlobSha256);
            Assert.IsFalse(document.CanUndo, "No-op must not add history.");
            CollectionAssert.AreEquivalent(before.Mission.ToArray(), document.ReadChallenges().Mission.ToArray());
            CollectionAssert.AreEqual(source, File.ReadAllBytes(path));
        }
    }

    [STATestMethod]
    [TestCategory("PrivateFixtures")]
    public void ChallengeSelectionUndoAndFiltersStayConnected()
    {
        string? fixtures = FixtureSaves.Scenarios;
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        if (Application.Current is null) { var app = new App.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; }
        var document = SaveDocument.Open(Path.Combine(fixtures, "challenges_partly_incomplete_2.03.02.save"));
        var model = new MainViewModel(document);
        var window = new MainWindow(model) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            var navigation = (ListBox)window.FindName("PrimaryNavigation");
            navigation.SelectedIndex = 1;
            var tabs = (TabControl)window.FindName("JournalTabs");
            tabs.SelectedIndex = 1;
            Assert.IsTrue(model.IsChallenges);
            var page = (ChallengePage)window.FindName("ChallengeArea");
            var tree = (TreeView)page.FindName("Tree");
            var rows = (DataGrid)page.FindName("Rows");
            Assert.AreEqual(0, tree.Items.Count);
            WindowTests.Pump(() => model.LoadChallengesCommand.ExecuteAsync(null));
            WindowTests.Pump(() => model.LoadMapCommand.ExecuteAsync(null));
            ChallengeNode[] Roots() => tree.Items.Cast<ChallengeNode>().SelectMany(n => n.Children).SelectMany(n => n.Children).ToArray();
            // View-only challenges (no completion data yet) are listed too.
            var catalog = ChallengeCatalog.Shared;
            Assert.AreEqual(catalog.Groups.SelectMany(g => g.Quests).Sum(q => catalog.Quests[q].Missions!.Length), Roots().Length);
            var node = Roots().Single(n => n.Root == 1000912);
            // The page virtualizes the tree; the walk below needs every container realized.
            VirtualizingPanel.SetIsVirtualizing(tree, false);
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
            Assert.AreEqual(Tr.Text("challenge.complete_selected") + " (1)", complete.Content);
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
            Assert.AreEqual(catalog.Groups.SelectMany(g => g.Quests).SelectMany(q => catalog.Quests[q].Missions!).Count(k => model.Challenges!.Mission.GetValueOrDefault(k)?.State != 5), Roots().Length);
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
