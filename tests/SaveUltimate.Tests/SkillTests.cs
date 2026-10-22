using System.IO;
using System.Text.Json;
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
    public void RealSkillRecordsAndEditsMatchPythonExactlyAndUndo()
    {
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        string? reference = Environment.GetEnvironmentVariable("SAVEULTIMATE_SKILL_REFERENCE");
        if (fixtures is null || reference is null) Assert.Inconclusive("Set private skill fixtures and Python reference.");
        using var json = JsonDocument.Parse(File.ReadAllBytes(reference));
        foreach (var expected in json.RootElement.EnumerateArray())
        {
            string path = Path.Combine(fixtures, expected.GetProperty("file").GetString()!);
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
            var source = expected.GetProperty("state");
            Assert.AreEqual(source.GetProperty("levels").EnumerateObject().Count(), state.Levels.Count, path);
            foreach (var p in source.GetProperty("levels").EnumerateObject()) Assert.AreEqual(p.Value.GetUInt32(), state.Level(uint.Parse(p.Name)), path);
            Assert.AreEqual(source.GetProperty("paid").EnumerateObject().Count(), state.Paid.Count, path);
            foreach (var p in source.GetProperty("paid").EnumerateObject())
                Assert.AreEqual(new SkillPayment(p.Value[0].GetUInt64(), p.Value[1].GetInt32()), state.Paid[uint.Parse(p.Name)], path);
            foreach (var p in source.GetProperty("points").EnumerateObject())
                Assert.AreEqual(new SkillPoints(p.Value[0].GetUInt64(), p.Value[1].GetUInt64()), state.Points[int.Parse(p.Name)], path);
            Assert.AreEqual(source.GetProperty("follow").EnumerateObject().Count(), state.Follow.Count, path);
            foreach (var p in source.GetProperty("follow").EnumerateObject()) Assert.AreEqual(p.Value[0].GetUInt32(), state.Follow[uint.Parse(p.Name)], path);
            foreach (var p in source.GetProperty("inventory").EnumerateObject()) Assert.AreEqual(p.Value.GetUInt64(), state.Inventory[uint.Parse(p.Name)], path);
            string hash = document.BlobSha256;
            foreach (var edit in expected.GetProperty("cases").EnumerateArray())
            {
                uint key = edit.GetProperty("key").GetUInt32(), target = edit.GetProperty("target").GetUInt32();
                var after = document.LearnSkill(key, target, edit.GetProperty("watch").GetBoolean());
                Assert.AreEqual(target, after.Level(key), $"{path} {key}");
                Assert.AreEqual(edit.GetProperty("sha256").GetString()!.ToUpperInvariant(), document.BlobSha256, $"{path} {key}");
                string changed = document.BlobSha256;
                Assert.Throws<InvalidOperationException>(() => document.LearnSkill(key, target));
                Assert.AreEqual(changed, document.BlobSha256);
                if (key == 40064)
                {
                    string copy = Path.Combine(Path.GetTempPath(), $"skills-{Guid.NewGuid():N}.save");
                    try { document.SaveCopy(copy); Assert.AreEqual(changed, SaveDocument.Open(copy).BlobSha256); }
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
        CollectionAssert.AreEqual(new[] { "클리프", "데미안", "웅카" }, tabs.Items.Cast<TabItem>().Select(t => (string)t.Header).ToArray());
        tabs.SelectedIndex = 1; window.UpdateLayout();
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
