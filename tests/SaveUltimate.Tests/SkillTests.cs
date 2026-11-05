using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.App;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class SkillTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void SkillDescriptionsNameKeyPromptsAndResolveLabelledLinks()
    {
        var bindings = SaveUltimate.Core.Catalogs.SkillCatalogBuilder.KeyBindings("""
            <SubGroup Name="Skill_17"><Input Name="Key_Skill_17"/></SubGroup>
            <!-- <Input Name="Key_Jump"><KeyboardMouse Key="j"/></> -->
            <Input Name="Key_Roll"><GamePad Key="buttonB"/><KeyboardMouse Name="OverrideKey1" Key="alt"/><KeyboardMouse Name="OverrideKey2" Key="mouseX2"/></>
            <Input Name="Key_Skill_17" Consume="True"><KeyboardMouse Name="OverrideKey" Key="f mouseR" Method="press"/></>
            <Input Name="Key_Focus_Mabongpa"><KeyboardMouse Key="tab"/></>
            <Input Name="Key_Skill_17"><KeyboardMouse Key="q"/></>
            """);
        CollectionAssert.AreEquivalent(new[] { "Roll", "Skill17", "FocusMabongpa" }, bindings.Keys.ToArray());
        Assert.AreEqual("사용 방법: 달리는 중 [F + 우클릭]", SaveUltimate.Core.Catalogs.SkillCatalogBuilder.KeyLabels("사용 방법: 달리는 중 {Key:Key_Skill_17}", bindings, 1));
        Assert.AreEqual("[Alt][Tab], [F + Right Click] or [Jump]",
            SaveUltimate.Core.Catalogs.SkillCatalogBuilder.KeyLabels("{Key:Key_Roll}{Key:Key_FocusMabongpa}, {Key:Key_Skill_17} or {Key:Key_Jump}", bindings, 0));
        var resources = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(resources.FullName, "resources/data/skills.json"))) resources = resources.Parent ?? throw new AssertInconclusiveException("Repository resources not found.");
        var skills = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(Path.Combine(resources.FullName, "resources/data/skills.json")))!["skills"]!.AsObject();
        var descriptions = skills.SelectMany(s => s.Value!["levels"]!.AsArray()).SelectMany(l => l!["description"]!.AsArray()).Select(d => d!.GetValue<string>()).ToArray();
        CollectionAssert.AreEqual(Array.Empty<string>(), descriptions.Where(d => d.Contains("  ") || d.Contains('{') || d.Contains("Knowledge_") || d.Contains("[Skill ")).ToArray());
        Assert.IsTrue(descriptions.Contains("사용 방법: 달리는 중 [F + 우클릭]\n\n달리는 중에 적을 걸어 메친다."));
        // The ja/ru packs carry every level with the same keys, links resolved to their own names.
        int levels = skills.Sum(s => s.Value!["levels"]!.AsArray().Count);
        foreach (var (code, clothesline) in new[] { ("ja", "[F + 右クリック]"), ("ru", "[F + ПКМ]") })
        {
            var pack = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(Path.Combine(resources.FullName, $"resources/locale/names_{code}.json")))!["skill_descriptions"]!.AsObject();
            Assert.AreEqual(levels, pack.Count, code);
            StringAssert.Contains(pack["40028:1"]!.GetValue<string>(), clothesline);
            CollectionAssert.AreEqual(Array.Empty<string>(), pack.Select(p => p.Value!.GetValue<string>()).Where(d => d.Contains('{') || d.Contains("Knowledge_") || d.Contains("[Skill ")).ToArray(), code);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void PlansPreserveSharedFundingObservationAndRequirements()
    {
        var state = new SkillState(new Dictionary<uint, uint>(), new Dictionary<uint, SkillPayment>(),
            Enumerable.Range(0, 3).ToDictionary(i => i, _ => new SkillPoints(0, 0)),
            new Dictionary<uint, uint>(), new Dictionary<uint, ulong>());
        var plan = SkillPlan.Create(state, 1005840, 3);
        Assert.AreEqual(4UL, plan.Artifacts);
        Assert.AreEqual(0UL, plan.Costs[75001]);
        Assert.AreEqual(new SkillPoints(0, 4), plan.Points[0]);
        Assert.AreEqual(new SkillPoints(4, 4), plan.Points[2]);
        var second = SkillPlan.Create(state with { Points = plan.Points }, 1005842, 1);
        Assert.AreEqual(1UL, second.Bonds); Assert.AreEqual(0UL, second.Artifacts);
        var watched = SkillPlan.Create(state, 40024, 1, true);
        Assert.AreEqual(7u, watched.Follow[40024]); Assert.AreEqual(0UL, watched.Investment);
        // A learned skill can still be observed: only the observation record is added.
        var learned = state with { Levels = new Dictionary<uint, uint> { [40024] = 1 } };
        var observed = SkillPlan.Create(learned, 40024, 1, true);
        Assert.AreEqual(7u, observed.Follow[40024]); Assert.AreEqual(0, observed.Changes.Count); Assert.AreEqual(0, observed.Costs.Count);
        // Nearly every knowledge has a learn-from record; only an observation one blocks observing (20: granted with a prior skill).
        var quest = learned with { Follow = new Dictionary<uint, uint> { [40024] = 20 } };
        Assert.AreEqual(7u, SkillPlan.Create(quest, 40024, 1, true).Follow[40024]);
        // Skills shown in a region (route mode 2) are recorded as 15, as the game does for Pull Up and Wind Arrow.
        Assert.AreEqual(15u, SkillPlan.Create(state, 1003108, 1, true).Follow[1003108]);
        Assert.AreEqual(15u, SkillPlan.Create(state with { Levels = new Dictionary<uint, uint> { [40065] = 1 } }, 40065, 1, true).Follow[40065]);
        // Kliff's Double Jump has no observation route in the game data, but saves show it observed in a region.
        Assert.IsTrue(SkillCatalog.Shared.Skills[1001116].Watch);
        Assert.AreEqual(15u, SkillPlan.Create(state, 1001116, 1, true).Follow[1001116]);
        Assert.IsTrue((state with { Levels = new Dictionary<uint, uint> { [1001116] = 2 }, Follow = new Dictionary<uint, uint> { [1001116] = (uint)LearnFrom.ResetKnowledgeLevel } }).Observed(1001116));
        Assert.Throws<InvalidOperationException>(() => SkillPlan.Create(learned with { Follow = new Dictionary<uint, uint> { [40024] = 22 } }, 40024, 1, true));
        // Observing a learned skill changes only its record, and that is a pending change too.
        var observedState = learned with { Follow = new Dictionary<uint, uint> { [40024] = 7 } };
        Assert.IsTrue(observedState.Changed(quest, 40024)); Assert.IsTrue(observedState.Changed(learned, 40024));
        Assert.IsFalse(quest.Changed(quest with { }, 40024)); Assert.IsFalse(learned.Changed(learned, 40003));
        Assert.IsTrue(learned.Changed(state, 40024));
        Assert.Throws<InvalidOperationException>(() => SkillPlan.Create(state, 40001, 1));
        Assert.Throws<InvalidOperationException>(() => SkillPlan.Create(state, 1004772, 1));
        Assert.Throws<InvalidOperationException>(() => SkillPlan.Create(state, 1005840, 1, true));
        Assert.Throws<InvalidOperationException>(() => SkillPlan.Create(state with
        {
            Points = Enumerable.Range(0, 3).ToDictionary(i => i, _ => new SkillPoints(0, 65535))
        }, 1005840, 1));
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void InvestedSkillCanBeObservedRoundTripAndUndo()
    {
        string fixtures = FixtureSaves.Root ?? throw new AssertInconclusiveException("Set fixtures.");
        var document = SaveDocument.Open(FixtureSaves.Named("dlc_fog_and_skill_observe")); string original = document.BlobSha256;
        var before = document.ReadSkills();
        uint key = SkillCatalog.Shared.Skills.Values.First(d => d.Watch && before.Level(d.Key) > 0 && before.Paid.ContainsKey(d.Key) && !before.Follow.ContainsKey(d.Key)).Key;
        var after = document.LearnSkill(key, 1, true);
        Assert.IsTrue(after.Observed(key));
        CollectionAssert.AreEquivalent(before.Levels.ToArray(), after.Levels.ToArray());
        CollectionAssert.AreEquivalent(before.Paid.ToArray(), after.Paid.ToArray());
        Assert.Throws<InvalidOperationException>(() => document.LearnSkill(key, 1, true));
        string output = Path.Combine(Path.GetTempPath(), $"observe-{Guid.NewGuid():N}.save");
        try { document.SaveCopy(output); Assert.IsTrue(SaveDocument.Open(output).ReadSkills().Observed(key)); }
        finally { if (File.Exists(output)) File.Delete(output); }
        document.Undo(); Assert.AreEqual(original, document.BlobSha256);
    }

    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void RealSkillRecordsRejectBrokenLedgersAndEditsUndo()
    {
        string? fixtures = FixtureSaves.Root;
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        string[] editable = ["early_progress", "fishing_no_first_catch", "map_partly_explored"];
        foreach (string file in editable.Concat(["old_version", "editing_baseline", "challenges_partly_incomplete"]))
        {
            string path = FixtureSaves.Named(file);
            byte[] original = File.ReadAllBytes(path);
            var document = SaveDocument.Open(path);
            var state = document.ReadSkills();
            byte[] corrupt = SaveContainer.Decode(original).Blob;
            var layout = ParcLayout.Parse(corrupt);
            var pointsList = SaveRecords.Read(corrupt, layout, "KnowledgeSaveData").Fields.Named("_skillPointSaveDataList");
            var total = pointsList.ListElements.Select(r => r.ChildFields.Named("_elapsedSkillPoint")).FirstOrDefault(f => f.Present);
            if (total is not null)
            {
                corrupt[total.StartOffset] ^= 1;
                Assert.Throws<InvalidDataException>(() => SkillState.Read(corrupt, layout, document.Items), "A mismatched investment ledger must not be accepted.");
            }
            if (!editable.Contains(file)) continue;
            string hash = document.BlobSha256;
            // Invest one and two levels, observe two skills, and invest a two-level node.
            foreach (var (key, target, watch) in new[] { (1005842u, 1u, false), (1005842u, 2u, false), (40064u, 1u, true), (1003474u, 1u, true), (40003u, 2u, false) })
            {
                var after = document.LearnSkill(key, target, watch);
                Assert.AreEqual(target, after.Level(key), $"{file} {key}");
                if (watch) Assert.IsTrue(after.Observed(key), $"{file} {key}");
                string changed = document.BlobSha256;
                Assert.Throws<InvalidOperationException>(() => document.LearnSkill(key, target));
                Assert.AreEqual(changed, document.BlobSha256);
                if (key == 40064)
                {
                    string copy = Path.Combine(Path.GetTempPath(), $"skills-{Guid.NewGuid():N}.save");
                    try
                    {
                        document.SaveCopy(copy);
                        var reopened = SaveDocument.Open(copy);
                        Assert.AreEqual(changed, reopened.BlobSha256); Assert.IsTrue(reopened.ReadSkills().Observed(key));
                    }
                    finally { File.Delete(copy); }
                }
                document.Undo();
                Assert.AreEqual(hash, document.BlobSha256);
            }
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
        }
    }

    [STATestMethod]
    [TestCategory("UI")]
    [DoNotParallelize]
    [TestCategory("PrivateFixtures")]
    public void SkillTreePreservesOriginalInteractionAndSharedUndo()
    {
        string? fixtures = FixtureSaves.Root;
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        if (Application.Current is null) { var app = new App.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; }
        var document = SaveDocument.Open(FixtureSaves.Named("fishing_no_first_catch"));
        string hash = document.BlobSha256;
        var model = new MainViewModel(document);
        WindowTests.Pump(() => model.LoadSkillsCommand.ExecuteAsync(null));
        var window = new MainWindow(model) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Show(); model.CurrentPage = AppPage.Skills; window.UpdateLayout();
        var tabs = (SkillsView)window.FindName("SkillsArea");
        CollectionAssert.AreEqual(new[] { "클리프", "웅카", "데미안" }, tabs.Items.Cast<TabItem>().Select(t => (string)t.Header).ToArray());
        tabs.SelectedIndex = 2; window.UpdateLayout();
        var page = (SkillPage)((TabItem)tabs.SelectedItem).Content;
        var tree = (Canvas)page.FindName("Tree");
        var node = tree.Children.OfType<Button>().Single(b => Equals(b.Tag, 1005842u));
        node.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        var invest = (Button)page.FindName("Invest");
        Assert.IsTrue(invest.IsEnabled);
        WindowTests.Capture(window, "skills");
        WindowTests.Pump(() => model.LearnSkillAsync(1005842, false));
        Assert.AreEqual(1u, model.Skills!.Level(1005842));
        Assert.IsTrue(model.UndoCommand.CanExecute(null));
        WindowTests.Pump(() => model.UndoCommand.ExecuteAsync(null));
        Assert.AreEqual(hash, document.BlobSha256);
        ((TextBox)page.FindName("Search")).Text = "no_matching_skill_12345";
        Assert.IsFalse(invest.IsEnabled);
        Assert.IsTrue(tree.Children.OfType<Button>().All(b => !b.IsEnabled));
        ((TextBox)page.FindName("Search")).Text = "";
        ((ComboBox)page.FindName("Filter")).SelectedIndex = 1;
        Assert.IsTrue(tree.Children.OfType<Button>().Any(b => b.IsEnabled));
        window.Width = 800; window.UpdateLayout();
        Assert.AreEqual(2, Grid.GetRow((ScrollViewer)page.FindName("Details")));
        WindowTests.Capture(window, "skills-narrow");
        window.Close();
    }

    [STATestMethod]
    [TestCategory("UI")]
    [DoNotParallelize]
    [TestCategory("PrivateFixtures")]
    public void SkillPageMarksOnlyPendingEditsUnderItsTitle()
    {
        string? fixtures = FixtureSaves.Root;
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        if (Application.Current is null) { var app = new App.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; }
        var model = new MainViewModel();
        var window = new MainWindow(model) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Show(); model.CurrentPage = AppPage.Skills; window.UpdateLayout();
        // The page is already on screen when the save loads, so it renders as soon as the skills arrive.
        WindowTests.Pump(() => model.LoadFileAsync(FixtureSaves.Named("dlc_bonds_per_character")));
        window.UpdateLayout();
        var tabs = (SkillsView)window.FindName("SkillsArea");
        var page = (SkillPage)((TabItem)tabs.SelectedItem).Content;
        var tree = (Canvas)page.FindName("Tree");
        string[] Marked() => tree.Children.OfType<TextBlock>().Where(t => t.Text.EndsWith(" *")).Select(t => t.Text).ToArray();
        CollectionAssert.AreEqual(Array.Empty<string>(), Marked(), "A freshly loaded save has no pending skill edits.");
        Assert.IsFalse(tree.Children.OfType<Button>().Any(b => b.BorderBrush == Brushes.DeepSkyBlue));
        // Explosive Evasive Shot came with a prior skill (record 20): it can be observed, and observing it is a visible change.
        Assert.IsTrue(model.Skills!.Level(1000908) > 0 && !model.Skills.Observed(1000908));
        WindowTests.Pump(() => model.LearnSkillAsync(1000908, true));
        Assert.IsTrue(model.Skills!.Observed(1000908));
        Assert.AreEqual(1, Marked().Length);
        Assert.AreEqual(Brushes.DeepSkyBlue, tree.Children.OfType<Button>().Single(b => SkillCatalog.Shared.Related((uint)b.Tag).Contains(1000908u)).BorderBrush);
        // The character tabs are the menu's sub-tabs, under its title.
        var header = (PageHeader)((DockPanel)tabs.Parent).Children[0];
        Assert.IsTrue(header.TranslatePoint(new Point(0, header.ActualHeight), window).Y <= tabs.TranslatePoint(new Point(), window).Y);
        // A selected tab keeps its fill after another menu was shown, without keyboard focus.
        model.CurrentPage = AppPage.Backups; window.UpdateLayout(); model.CurrentPage = AppPage.Skills; window.UpdateLayout();
        var selected = (TabItem)tabs.ItemContainerGenerator.ContainerFromIndex(tabs.SelectedIndex);
        Assert.IsFalse(selected.IsKeyboardFocused);
        Assert.AreEqual(((SolidColorBrush)window.FindResource("selected")).Color, ((SolidColorBrush)((Border)selected.Template.FindName("Box", selected)).Background).Color);
        WindowTests.Capture(window, "skills-loaded");
        // Undo the edit so closing does not ask to save it.
        WindowTests.Pump(() => model.UndoCommand.ExecuteAsync(null));
        Assert.IsFalse(model.HasUnsavedChanges);
        window.Close();
    }
}
