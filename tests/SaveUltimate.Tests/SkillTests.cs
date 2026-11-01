using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.App;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class SkillTests
{
    [TestMethod]
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
        Assert.Throws<InvalidOperationException>(() => SkillPlan.Create(learned with { Follow = new Dictionary<uint, uint> { [40024] = 20 } }, 40024, 1, true));
        Assert.Throws<InvalidOperationException>(() => SkillPlan.Create(state, 40001, 1));
        Assert.Throws<InvalidOperationException>(() => SkillPlan.Create(state, 1004772, 1));
        Assert.Throws<InvalidOperationException>(() => SkillPlan.Create(state, 1005840, 1, true));
        Assert.Throws<InvalidOperationException>(() => SkillPlan.Create(state with
        {
            Points = Enumerable.Range(0, 3).ToDictionary(i => i, _ => new SkillPoints(0, 65535))
        }, 1005840, 1));
    }

    [TestMethod]
    [TestCategory("PrivateFixtures")]
    public void InvestedSkillCanBeObservedRoundTripAndUndo()
    {
        string fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES") ?? throw new AssertInconclusiveException("Set fixtures.");
        var document = SaveDocument.Open(Path.Combine(fixtures, "save_v2.04.00.save")); string original = document.BlobSha256;
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
    [TestCategory("PrivateFixtures")]
    public void RealSkillRecordsRejectBrokenLedgersAndEditsUndo()
    {
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        string[] editable = ["new.save", "save_fishnot.save", "save_friendship_not_working.save", "save_friendship_not_working2.save", "save_memory.save"];
        foreach (string file in editable.Concat(["save_v2.00.02.save", "save_v2.03.02.save", "save_도전과제 일부 미완료.save"]))
        {
            string path = Path.Combine(fixtures, file);
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
    [TestCategory("PrivateFixtures")]
    public void SkillTreePreservesOriginalInteractionAndSharedUndo()
    {
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        if (Application.Current is null) { var app = new App.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; }
        var document = SaveDocument.Open(Path.Combine(fixtures, "save_fishnot.save"));
        string hash = document.BlobSha256;
        var model = new MainViewModel(document);
        WindowTests.Pump(() => model.LoadSkillsCommand.ExecuteAsync(null));
        var window = new MainWindow(model) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Show(); model.PageIndex = 4; window.UpdateLayout();
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
}
