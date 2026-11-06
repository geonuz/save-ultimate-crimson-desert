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
    [TestCategory("Unit")]
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
    [TestCategory("Scenario")]
    [DataRow(false)]
    [DataRow(true)]
    [TestCategory("PrivateFixtures")]
    public void InvalidClockOrUnknownStateDoesNotCommit(bool invalidClock)
    {
        string source = FixtureSaves.Named("challenges_partly_incomplete");
        byte[] raw = SaveContainer.Decode(File.ReadAllBytes(source)).Blob;
        var block = SaveRecords.Read(raw, ParcLayout.Parse(raw), "QuestSaveData");
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
        using var temp = new TempFolder();
        string path = temp.Named("path.save");
        SaveContainer.Write(File.ReadAllBytes(source), raw, path);
        var document = SaveDocument.Open(path);
        string hash = document.BlobSha256;
        Assert.Throws<InvalidDataException>(() => document.CompleteChallenges([1000912], [4294966659]));
        Assert.AreEqual(hash, document.BlobSha256);
        Assert.IsFalse(document.CanUndo);
        Assert.IsFalse(document.IsDirty);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void CompletedTagsFollowDefinitionsAndLeaveNewPatchTagsToTheGame()
    {
        const uint start = 0x872571C6, empty = 0xEAC5E173, complete = 0x599D2AA7, patchDay1 = 0xF4A09B0C, patch2_04_06 = 0x62316B37;
        CollectionAssert.AreEqual(new[] { start, empty, patchDay1, complete }, ChallengeCatalog.CompletedTags(1000217));
        CollectionAssert.AreEqual(new[] { start, empty, complete }, ChallengeCatalog.CompletedTags(uint.MaxValue));
        // Saves show this mission without the empty tag.
        CollectionAssert.AreEqual(new[] { start, complete }, ChallengeCatalog.CompletedTags(1004951));
        // The definition names the new patch tag, but no save has recorded it: the game adds such tags to completed rows on load.
        CollectionAssert.Contains(ChallengeCatalog.Shared.Missions[1004960].Tags, patch2_04_06);
        CollectionAssert.DoesNotContain(ChallengeCatalog.CompletedTags(1004960), patch2_04_06);
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void DoctorRepairsRowsWithoutArrayMarkersAndKeepsEverythingElse()
    {
        foreach (var (name, damaged) in new[] { ("mission_rows_41", 41), ("mission_rows_8", 8) })
        {
            string source = FixtureSaves.Named(name);
            var document = SaveDocument.Open(source);
            Assert.AreEqual(damaged, document.DamagedRows, name);
            var before = document.ReadChallenges();
            var knowledge = document.ReadKnowledge();
            byte[] original = SaveContainer.Decode(File.ReadAllBytes(source)).Blob;
            Assert.ThrowsExactly<InvalidDataException>(() => document.CompleteChallenges(ChallengeCatalog.Completion.Roots.Where(r => before.Mission.GetValueOrDefault(r)?.State != 5).Take(1).ToArray(), null));
            Assert.AreEqual(damaged, document.RepairMissionRows(), name);
            Assert.AreEqual(0, document.RepairMissionRows(), name);
            Assert.IsTrue(before.Mission.OrderBy(p => p.Key).SequenceEqual(document.ReadChallenges().Mission.OrderBy(p => p.Key)), name);
            Assert.IsTrue(knowledge.OrderBy(p => p.Key).SequenceEqual(document.ReadKnowledge().OrderBy(p => p.Key)), name);
            using var temp = new TempFolder();
            string directory = temp.Root;
            string copy = Path.Combine(directory, "save.save");
            document.SaveCopy(copy);
            byte[] raw = SaveContainer.Decode(File.ReadAllBytes(copy)).Blob;
            AssertGameLayout(raw);
            Assert.AreEqual(0, SaveDocument.Open(copy).DamagedRows, name);
            // Rows the game wrote come through unchanged apart from their absolute payload pointers.
            var repaired = SaveRecords.Read(raw, ParcLayout.Parse(raw), "QuestSaveData").Fields.Named("_missionStateList").ListElements;
            var originals = SaveRecords.Read(original, ParcLayout.Parse(original), "QuestSaveData").Fields.Named("_missionStateList").ListElements;
            Assert.AreEqual(originals.Length, repaired.Length);
            int same = 0;
            for (int i = 0; i < repaired.Length; i++)
                if (original.AsSpan(originals[i].ChildPayloadOffset, originals[i].EndOffset - originals[i].ChildPayloadOffset)
                    .SequenceEqual(raw.AsSpan(repaired[i].ChildPayloadOffset, repaired[i].EndOffset - repaired[i].ChildPayloadOffset))) same++;
            Assert.AreEqual(repaired.Length - damaged, same, name);
            // Undo returns the damaged bytes; nothing else was touched.
            document = SaveDocument.Open(source);
            document.RepairMissionRows();
            document.Undo();
            string undone = Path.Combine(directory, "undone.save");
            document.SaveCopy(undone);
            CollectionAssert.AreEqual(original, SaveContainer.Decode(File.ReadAllBytes(undone)).Blob, name);
            // A repaired save can be edited again.
            document = SaveDocument.Open(copy);
            var progress = document.ReadChallenges();
            var open = ChallengeCatalog.Completion.Roots.Where(r => progress.Mission.GetValueOrDefault(r)?.State != 5).Take(3).ToArray();
            Assert.IsTrue(document.CompleteChallenges(open).Completed > 0, name);
        }
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void DlcCressetsActivateWithTheirMissions()
    {
        var document = SaveDocument.Open(FixtureSaves.Named("dlc_fog_and_skill_observe"));
        int index = Array.FindIndex(MapAction.All, a => a.ChallengeRoots is { Length: > 0 });
        var action = MapAction.All[index];
        var scenes = action.Scenes.Where(DlcContent.Shared.Scenes.Contains).ToArray();
        var roots = action.ChallengeRoots!.Where(DlcContent.Shared.Missions.Contains).ToArray();
        Assert.AreEqual(29, scenes.Length);
        Assert.AreEqual(29, roots.Length);
        document.ActivateMap(index);
        var map = document.ReadMap();
        foreach (string scene in scenes)
        {
            byte[] pivot = Convert.FromHexString(map.Discoveries[scene].Pivot!);
            float x = BitConverter.ToSingle(pivot, 0), z = BitConverter.ToSingle(pivot, 8);
            Assert.AreEqual(0f, BitConverter.ToSingle(pivot, 4), scene);
            Assert.IsTrue(x * x + z * z is > 0 and <= 400, scene);
        }
        foreach (uint key in roots.SelectMany(ChallengeCatalog.Shared.Descendants)) Assert.AreEqual((byte?)5, map.Progress.Mission[key].State, key.ToString());
        foreach (uint quest in new uint[] { 1000830, 1000887 }) Assert.AreEqual((byte?)5, map.Progress.Quest[quest].State, quest.ToString());
        using var temp = new TempFolder();
        string copy = temp.Named("copy.save");
        document.SaveCopy(copy);
        AssertGameLayout(SaveContainer.Decode(File.ReadAllBytes(copy)).Blob, roots.SelectMany(ChallengeCatalog.Shared.Descendants).ToHashSet());
    }

    // The layout every game-written row follows: a row one byte off makes the game read the next row's mask as a length and overrun a stack buffer.
    private static void AssertGameLayout(byte[] raw, ISet<uint>? tagged = null)
    {
        var layout = ParcLayout.Parse(raw);
        var type = layout.Types.Single(t => t.Name == "MissionStateData");
        foreach (var row in SaveRecords.Read(raw, layout, "QuestSaveData").Fields.Named("_missionStateList").ListElements)
        {
            int cursor = row.ChildPayloadOffset + 4, alarm = -1;
            uint key = BitConverter.ToUInt32(raw, cursor);
            uint[] tags = [];
            for (int i = 0; i < type.Fields.Count; i++)
            {
                var field = type.Fields[i];
                bool present = (raw[row.StartOffset + 2 + i / 8] & (1 << (i % 8))) != 0;
                if (field.Kind == 3)
                {
                    Assert.AreEqual(present ? 0 : 1, raw[cursor]);
                    cursor++;
                    if (!present) continue;
                    int count = BitConverter.ToInt32(raw, cursor);
                    if (field.Name == "_usedTagList") tags = Enumerable.Range(0, count).Select(t => BitConverter.ToUInt32(raw, cursor + 4 + t * 4)).ToArray();
                    cursor += 4 + count * field.Size;
                }
                else if (present)
                {
                    if (field.Name == "_newAlarm") alarm = raw[cursor];
                    cursor += field.Size;
                }
            }
            Assert.AreEqual(row.EndOffset - 4, cursor);
            Assert.AreEqual(0, alarm);
            if (row.ChildFields.Named("_completedTime").Present) Assert.IsTrue(tags.Length > 0, "Every completed row the game writes has used tags.");
            if (tagged?.Contains(key) == true) CollectionAssert.IsSubsetOf(ChallengeCatalog.CompletedTags(key), tags);
        }
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void CompletedMissionRowsKeepTheLayoutTheGameReads()
    {
        var document = SaveDocument.Open(FixtureSaves.Named("early_progress"));
        var before = document.ReadChallenges();
        var after = document.CompleteChallenges(ChallengeCatalog.Completion.Roots.Where(r => before.Mission.GetValueOrDefault(r)?.State != 5).ToArray()).State;
        var inserted = after.Mission.Keys.Except(before.Mission.Keys).ToHashSet();
        var completed = after.Mission.Where(p => p.Value.State == 5 && before.Mission.GetValueOrDefault(p.Key)?.State != 5).Select(p => p.Key).ToHashSet();
        Assert.IsTrue(inserted.Count > 0, "The fixture must be missing mission rows.");
        Assert.IsTrue(completed.Count > inserted.Count, "The fixture must have started mission rows to complete.");
        using var temp = new TempFolder();
        string copy = temp.Named("copy.save");
        document.SaveCopy(copy);
        byte[] raw = SaveContainer.Decode(File.ReadAllBytes(copy)).Blob;
        AssertGameLayout(raw, completed);
        var rows = SaveRecords.Read(raw, ParcLayout.Parse(raw), "QuestSaveData").Fields.Named("_missionStateList").ListElements;
        Assert.IsTrue(inserted.All(k => rows.Any(r => r.ChildFields.Named("_key").Scalar(raw, "MissionKey", 4) == k)));
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void CheckpointsFamiliesAndBatchCompleteOnlyTheirMissionsAndUndoExactly()
    {
        uint[] batch = [1000912, 1000913, 1000915, 1001044, 1001048, 1003194, 1000423, 1003957, 1004406, 1004407, 1004408, 1004409, 1004410];
        // An existing and a missing checkpoint record, one family, and a batch; the counts are what those saves leave incomplete.
        foreach (var (file, roots, checkpoints, completed) in new (string, uint[], uint[]?, int)[] {
            ("challenges_partly_incomplete", [1000912], [4294966659], 1), ("early_progress", [1000912], [4294966659], 1),
            ("fishing_no_first_catch", [1000912], null, 7), ("challenges_partly_incomplete", batch, null, 38) })
        {
            string path = FixtureSaves.Named(file);
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
                using var temp = new TempFolder();
                string copy = temp.Named("copy.save");
                document.SaveCopy(copy);
                var reopened = SaveDocument.Open(copy);
                Assert.AreEqual(document.BlobSha256, reopened.BlobSha256);
                CollectionAssert.AreEquivalent(result.State.Mission.ToArray(), reopened.ReadChallenges().Mission.ToArray());
                Assert.AreEqual(items, JsonSerializer.Serialize(reopened.Items));
            }
            document.Undo();
            Assert.AreEqual(original, document.BlobSha256);
            Assert.IsFalse(document.CanUndo, "No-op must not add history.");
            CollectionAssert.AreEquivalent(before.Mission.ToArray(), document.ReadChallenges().Mission.ToArray());
            CollectionAssert.AreEqual(source, File.ReadAllBytes(path));
        }
    }

    [STATestMethod]
    [TestCategory("UI")]
    [DoNotParallelize]
    [TestCategory("PrivateFixtures")]
    public void ChallengeSelectionUndoAndFiltersStayConnected()
    {
        TestUi.EnsureApp();
        var document = SaveDocument.Open(FixtureSaves.Named("challenges_partly_incomplete"));
        var model = new MainViewModel(document);
        var window = TestUi.Hidden(new MainWindow(model));
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
