using System.IO;
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
        byte[] raw = SaveContainer.Decode(File.ReadAllBytes(Path.Combine(Fixtures, "editing_baseline_2.03.02.save"))).Blob;
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

    private static string Fixtures => FixtureSaves.Scenarios ?? throw new AssertInconclusiveException("Set private fixtures.");
    [TestMethod]
    [TestCategory("PrivateFixtures")]
    public void MapNpcAndCompanionEditsTouchOnlyTheirRecordsAndUndo()
    {
        static (uint, ulong, ulong, string)[] Items(SaveDocument doc) => doc.Items.Select(r => (r.Key, r.Number, r.Quantity, r.Source.ToString())).ToArray();
        // Every map action and the fog reveal on a partly explored save: idempotent, items untouched, undone exactly.
        var map = SaveDocument.Open(Path.Combine(Fixtures, "map_partly_explored.save")); string original = map.BlobSha256; var items = Items(map);
        Assert.IsTrue(map.RevealMap().Full); string revealed = map.BlobSha256;
        map.RevealMap(); Assert.AreEqual(revealed, map.BlobSha256);
        map.Undo(); Assert.AreEqual(original, map.BlobSha256);
        for (int i = 0; i < MapAction.All.Length; i++)
        {
            map.ActivateMap(i); string changed = map.BlobSha256;
            map.ActivateMap(i); Assert.AreEqual(changed, map.BlobSha256, MapAction.All[i].Names["en"]);
            CollectionAssert.AreEqual(items, Items(map));
            if (map.CanUndo) map.Undo();
            Assert.AreEqual(original, map.BlobSha256, MapAction.All[i].Names["en"]); Assert.IsFalse(map.CanUndo);
        }
        var doc = SaveDocument.Open(Path.Combine(Fixtures, "editing_baseline_2.03.02.save")); original = doc.BlobSha256; items = Items(doc);
        var npc = doc.ReadFriendship();
        var friendship = doc.SetFriendship(4009, 5, 95);
        CollectionAssert.AreEqual(npc.Select(r => r.Key == 4009 ? (r.Key, (ulong?)95, false) : (r.Key, r.Value, r.NeedsSync)).ToArray(), friendship.Select(r => (r.Key, r.Value, r.NeedsSync)).ToArray());
        var companions = doc.ReadCompanions();
        var renamed = doc.RenameCompanions([companions.Single(c => c.Number == 352)], "동료 이름");
        CollectionAssert.AreEqual(companions.Select(c => c.Number == 352 ? c with { Name = "동료 이름" } : c).ToArray(), renamed);
        var cleared = doc.RenameCompanions(renamed.Where(c => c.Editable && c.Name is not null).ToArray(), null);
        CollectionAssert.AreEqual(renamed.Select(c => c.Editable ? c with { Name = null } : c).ToArray(), cleared);
        CollectionAssert.AreEqual(items, Items(doc));
        string output = Path.Combine(Path.GetTempPath(), $"world-{Guid.NewGuid():N}.save");
        try
        {
            doc.SaveCopy(output);
            var reopened = SaveDocument.Open(output);
            Assert.AreEqual(doc.BlobSha256, reopened.BlobSha256);
            CollectionAssert.AreEqual(cleared, reopened.ReadCompanions());
            Assert.AreEqual((ulong?)95, reopened.ReadFriendship().Single(r => r.Key == 4009).Value);
        }
        finally { if (File.Exists(output)) File.Delete(output); }
        while (doc.CanUndo) doc.Undo();
        Assert.AreEqual(original, doc.BlobSha256);
    }
    [TestMethod]
    [TestCategory("PrivateFixtures")]
    public void InvalidSelectionsAndReadOnlyNpcRecordsNeverCommit()
    {
        var doc = SaveDocument.Open(Path.Combine(Fixtures, "editing_baseline_2.03.02.save")); string original = doc.BlobSha256;
        var npc = doc.ReadFriendship(); var row = npc.First(r => r.Editable);
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.SetFriendship(row.Key, row.Value!.Value, 101));
        Assert.Throws<InvalidOperationException>(() => doc.SetFriendship(row.Key, 999, 50));
        foreach (uint absent in npc.Where(r => !r.Editable).Select(r => r.Key).Append(uint.MaxValue)) Assert.Throws<InvalidOperationException>(() => doc.SetFriendship(absent, 0, 50));
        var companion = doc.ReadCompanions().First(r => r.Editable);
        Assert.Throws<InvalidOperationException>(() => doc.RenameCompanions([companion, companion], "X"));
        Assert.Throws<InvalidOperationException>(() => doc.RenameCompanions([companion with { Name = "stale" }], "X"));
        var readOnly = doc.ReadCompanions().FirstOrDefault(r => !r.Editable);
        if (readOnly is not null) Assert.Throws<InvalidOperationException>(() => doc.RenameCompanions([readOnly], "X"));
        Assert.Throws<ArgumentOutOfRangeException>(() => doc.ActivateMap(-1));
        Assert.AreEqual(original, doc.BlobSha256); Assert.IsFalse(doc.CanUndo);
    }

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    [DataRow("dlc_fog_and_skill_observe_2.04.00.save")]
    [DataRow("other_editor_layout.save")] // another editor's 1024-slot fog jump table
    public void ExtendedDlcFogRevealsAndRoundTrips(string file)
    {
        var doc = SaveDocument.Open(Path.Combine(Fixtures, file)); string original = doc.BlobSha256;
        var before = doc.ReadMap().Fog;
        Assert.IsFalse(before.Full);
        Assert.IsTrue(doc.RevealMap().Full);
        string output = Path.Combine(Path.GetTempPath(), $"fog-{Guid.NewGuid():N}.save");
        try
        {
            doc.SaveCopy(output);
            var reopened = SaveDocument.Open(output).ReadMap().Fog;
            Assert.IsTrue(reopened.Full); Assert.AreEqual(before.Codes.Length, reopened.Codes.Length);
        }
        finally { if (File.Exists(output)) File.Delete(output); }
        doc.Undo(); Assert.AreEqual(original, doc.BlobSha256);
    }

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    public void HiddenDlcContentNeverReachesABaseGameSave()
    {
        var dlc = DlcContent.Shared;
        var doc = SaveDocument.Open(Path.Combine(Fixtures, "editing_baseline_2.03.02.save")); string original = doc.BlobSha256;
        DlcContent.Hidden = true;
        try
        {
            var nexus = MapAction.All.Single(a => a.Mode == "travel");
            Assert.AreEqual(266, nexus.Keys.Length); // 293 with the DLC's 27
            Assert.IsFalse(MapAction.All.Any(a => a.Keys.Any(dlc.Knowledge.Contains) || a.Scenes.Any(dlc.Scenes.Contains) || a.ChallengeRoots?.Any(dlc.Missions.Contains) == true));
            uint dlcKnowledge = KnowledgeCatalog.Shared.ItemPaths.Keys.First(dlc.Knowledge.Contains);
            Assert.ThrowsExactly<InvalidOperationException>(() => doc.EditKnowledge([dlcKnowledge], KnowledgeAction.Learn));
            Assert.ThrowsExactly<InvalidOperationException>(() => doc.CompleteChallenges([dlc.Missions.First(ChallengeCatalog.Completion.Roots.Contains)]));
            Assert.ThrowsExactly<InvalidOperationException>(() => doc.LearnSkill(SkillCatalog.Shared.Skills.Keys.First(dlc.Knowledge.Contains), 1));
            Assert.AreEqual(original, doc.BlobSha256);
            doc.EditKnowledge([], KnowledgeAction.AllMaximum);
            Assert.IsFalse(doc.ReadKnowledge().Any(p => p.Value > 0 && dlc.Knowledge.Contains(p.Key)));
            for (int i = 0; i < MapAction.All.Length; i++) doc.ActivateMap(i);
            var map = doc.ReadMap();
            Assert.IsFalse(map.Discoveries.Keys.Any(dlc.Scenes.Contains) || map.Knowledge.Any(p => p.Value > 0 && dlc.Knowledge.Contains(p.Key)));
        }
        finally { DlcContent.Hidden = false; }
        while (doc.CanUndo) doc.Undo();
        Assert.AreEqual(original, doc.BlobSha256);
    }

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    public void PerCharacterBondsAndPetOrderRoundTripAndUndo()
    {
        // dlc_bonds_per_character_2.04.03.save: Elowen holds Kliff 30 and Oongka 10, Tina (an ordinary NPC) Oongka 55; Lyselia_1 counts 1 at bond 20.
        var doc = SaveDocument.Open(Path.Combine(Fixtures, "dlc_bonds_per_character_2.04.03.save")); string original = doc.BlobSha256;
        var npc = doc.ReadFriendship();
        CollectionAssert.AreEqual(new[] { (1u, (ulong?)30), (6u, (ulong?)10) }, npc.Single(r => r.Key == 1002939).Affinity.Select(a => (a.Playable, a.Value)).ToArray());
        Assert.AreEqual(1, npc.Single(r => r.Key == 1002940).Affinity.Length); // Bari's empty Oongka and Damiane slots are hidden
        Assert.AreEqual((ulong?)100, doc.SetAffinity(4011, 6, 55, 100).Single(r => r.Key == 4011).Affinity.Single().Value);
        doc.SetAffinity(1002941, 1, 20, 25);
        Assert.AreEqual(1u, doc.ReadChallenges().Mission[1004746].Count); // not a bond counter
        // The game raises bonds by uneven steps; bonds never go down.
        Assert.AreEqual((ulong?)37, doc.SetAffinity(1002939, 1, 30, 37).Single(r => r.Key == 1002939).Affinity[0].Value);
        Assert.ThrowsExactly<InvalidOperationException>(() => doc.SetAffinity(1002939, 1, 37, 35));
        CollectionAssert.AreEqual(new[] { 37, 40, 45 }, NpcPage.Choices(37, raiseOnly: true).Take(3).ToArray());
        CollectionAssert.Contains(NpcPage.Choices(37), 35);
        var pets = doc.ReadCompanions();
        Companion Pet(uint key) => pets.Single(c => c.Key == key);
        var details = doc.ReadCompanionDetails();
        // Loafy Cat carries three items with sockets; Yellow Cat has no lists, so the records differ in size.
        var (cat, yellow) = (Pet(1000319), Pet(1000338));
        var swapped = doc.SwapCompanions(cat, yellow);
        Assert.AreEqual((1000338u, 1000319u), (swapped.Single(c => c.Number == cat.Number).Key, swapped.Single(c => c.Number == yellow.Number).Key));
        Assert.AreEqual(pets.Length, swapped.Length);
        var moved = doc.ReadCompanionDetails();
        Assert.AreEqual(details[yellow.Number], moved[cat.Number]); Assert.AreEqual(details[cat.Number], moved[yellow.Number]);
        Assert.ThrowsExactly<InvalidOperationException>(() => doc.SwapCompanions(Pet(31378), Pet(31377))); // horses are referenced by quests
        Assert.ThrowsExactly<InvalidOperationException>(() => doc.SwapCompanions(Pet(1004233), Pet(1001618))); // special mount and pet
        string output = Path.Combine(Path.GetTempPath(), $"order-{Guid.NewGuid():N}.save");
        try
        {
            doc.SaveCopy(output);
            var reopened = SaveDocument.Open(output);
            CollectionAssert.AreEqual(swapped, reopened.ReadCompanions());
            Assert.AreEqual((ulong?)100, reopened.ReadFriendship().Single(r => r.Key == 4011).Affinity.Single().Value);
        }
        finally { if (File.Exists(output)) File.Delete(output); }
        while (doc.CanUndo) doc.Undo();
        Assert.AreEqual(original, doc.BlobSha256);
    }

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    public void DlcBondAndGreyZeroRosterEditsRoundTripAndUndo()
    {
        // dlc_bond_courting_grey0_2.04.00.save: Bari's bond toward Kliff is 35 and courting; three Grey-0 robots.
        var doc = SaveDocument.Open(Path.Combine(Fixtures, "dlc_bond_courting_grey0_2.04.00.save")); string original = doc.BlobSha256;
        var npc = doc.ReadFriendship();
        // 21 wanted criminals and Corbin only record a threat reward.
        Assert.AreEqual(134, npc.Length); Assert.IsFalse(npc.Any(r => r.Key is 1001875 or 1003530));
        var bond = npc.Single(r => r.Key == 1002940).Affinity.Single();
        Assert.AreEqual((1u, (ulong?)35, (byte?)1, true), (bond.Playable, bond.Value, bond.MaritalState, bond.Editable));
        // Mission_Graymane_Kliff_LoveStory_Bari_2 counts the bond up to its target of 40 and follows the edit.
        Assert.AreEqual(35u, doc.ReadChallenges().Mission[1004753].Count);
        Assert.AreEqual((ulong?)100, doc.SetAffinity(1002940, 1, 35, 100).Single(r => r.Key == 1002940).Affinity[0].Value);
        Assert.AreEqual(40u, doc.ReadChallenges().Mission[1004753].Count);
        Assert.ThrowsExactly<InvalidOperationException>(() => doc.SetAffinity(1002940, 1, 35, 50));
        Assert.ThrowsExactly<InvalidOperationException>(() => doc.SetAffinity(1002940, 1, 100, 50));
        var roster = doc.ReadCompanions();
        var added = doc.AddRobotWorker();
        Assert.AreEqual(roster.Length + 1, added.Length);
        Assert.AreEqual(4, added.Count(c => c.Key == SaveDocument.RobotWorker));
        Assert.AreEqual(900_000_000UL, added.Last(c => c.Key == SaveDocument.RobotWorker).Number);
        var removed = doc.RemoveRobotWorker(added.First(c => c.Key == SaveDocument.RobotWorker));
        Assert.AreEqual(3, removed.Count(c => c.Key == SaveDocument.RobotWorker));
        Assert.ThrowsExactly<InvalidOperationException>(() => doc.RemoveRobotWorker(roster.First(c => c.Key != SaveDocument.RobotWorker)));
        string output = Path.Combine(Path.GetTempPath(), $"dlc-{Guid.NewGuid():N}.save");
        try
        {
            doc.SaveCopy(output);
            var reopened = SaveDocument.Open(output);
            CollectionAssert.AreEqual(removed, reopened.ReadCompanions());
            Assert.AreEqual((ulong?)100, reopened.ReadFriendship().Single(r => r.Key == 1002940).Affinity[0].Value);
            Assert.AreEqual(40u, reopened.ReadChallenges().Mission[1004753].Count);
        }
        finally { if (File.Exists(output)) File.Delete(output); }
        while (doc.CanUndo) doc.Undo();
        Assert.AreEqual(original, doc.BlobSha256);
    }

    [STATestMethod]
    [TestCategory("PrivateFixtures")]
    public void WorldNavigationFiltersAndUndo()
    {
        if (Application.Current is null) { var app = new App.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; }
        var doc = SaveDocument.Open(Path.Combine(Fixtures, "editing_baseline_2.03.02.save"));
        var model = new MainViewModel(doc);
        var window = new MainWindow(model) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            var nav = (ListBox)window.FindName("PrimaryNavigation"); nav.SelectedIndex = 3; Assert.IsTrue(model.IsMap); Assert.IsNull(model.Map);
            WindowTests.Pump(() => model.LoadMapCommand.ExecuteAsync(null)); Assert.IsNotNull(model.Map);
            var map = (MapPage)window.FindName("MapArea");
            Assert.IsTrue(new[] { "LeftCards", "RightCards" }.SelectMany(n => ((StackPanel)map.FindName(n)).Children.Cast<GroupBox>()).Any());
            WindowTests.Capture(window, "map");
            nav.SelectedIndex = 4; Assert.IsTrue(model.IsCompanions);
            var tabs = (TabControl)window.FindName("NpcTabs");
            WindowTests.Pump(() => model.LoadCompanionsCommand.ExecuteAsync(null));
            tabs.SelectedIndex = 1; Assert.IsTrue(model.IsPets);
            var page = (CompanionPage)window.FindName("PetArea"); var rows = (DataGrid)page.FindName("Rows");
            var selected = rows.Items.Cast<CompanionRow>().First(r => r.Record.Editable);
            rows.SelectedItem = selected; string original = doc.BlobSha256;
            WindowTests.Pump(() => model.RenameCompanionsAsync([selected.Record], "동료 테스트"));
            Assert.AreEqual(selected.Record.Number, ((CompanionRow)rows.SelectedItem).Record.Number);
            WindowTests.Pump(() => model.UndoCommand.ExecuteAsync(null)); Assert.AreEqual(original, doc.BlobSha256);
            WindowTests.Capture(window, "companions");
            tabs.SelectedIndex = 2; Assert.IsTrue(model.IsFriendship); Assert.IsNull(model.Friendship);
            WindowTests.Pump(() => model.LoadFriendshipCommand.ExecuteAsync(null));
            var npcPage = (NpcPage)window.FindName("NpcArea"); var npcRows = (DataGrid)npcPage.FindName("Rows");
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
