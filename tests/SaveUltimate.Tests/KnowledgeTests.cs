using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.App;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class KnowledgeTests
{
    [TestMethod]
    [TestCategory("Scenario")]
    [TestCategory("PrivateFixtures")]
    public void KnowledgeActionsAgreeWithEachOtherAndPreserveUndoAndLedgers()
    {
        string? fixtures = FixtureSaves.Root;
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        var document = SaveDocument.Open(FixtureSaves.Named("editing_baseline"));
        string original = document.BlobSha256;
        var skills = document.ReadSkills();
        var levels = document.ReadKnowledge();
        void Ledgers()
        {
            var after = document.ReadSkills();
            CollectionAssert.AreEqual(skills.Paid.OrderBy(p => p.Key).ToArray(), after.Paid.OrderBy(p => p.Key).ToArray());
            CollectionAssert.AreEqual(skills.Points.OrderBy(p => p.Key).ToArray(), after.Points.OrderBy(p => p.Key).ToArray());
            CollectionAssert.AreEqual(skills.Follow.OrderBy(p => p.Key).ToArray(), after.Follow.OrderBy(p => p.Key).ToArray());
        }
        // 2147483059 has no record in this save, so learning inserts one.
        Assert.IsFalse(levels.ContainsKey(2147483059));
        document.EditKnowledge([2147483059], KnowledgeAction.Learn);
        Assert.AreEqual(1u, document.ReadKnowledge()[2147483059]); Ledgers();
        document.Undo(); Assert.AreEqual(original, document.BlobSha256);
        // Key 1 is at its maximum: lowering it and raising it back restores the original bytes.
        Assert.AreEqual(KnowledgeCatalog.Shared.LevelCounts[1], levels[1]);
        document.EditKnowledge([1], KnowledgeAction.Level, 1);
        string lowered = document.BlobSha256;
        Assert.AreEqual(1u, document.ReadKnowledge()[1]);
        document.EditKnowledge([1], KnowledgeAction.Maximum);
        Assert.AreEqual(original, document.BlobSha256); Ledgers();
        document.Undo(); document.Undo();
        // Unlearning keeps the record at level 0, and learning it again matches setting level 1.
        document.EditKnowledge([1], KnowledgeAction.Unlearn);
        Assert.AreEqual(0u, document.ReadKnowledge()[1]);
        document.EditKnowledge([1], KnowledgeAction.Learn);
        Assert.AreEqual(lowered, document.BlobSha256); Ledgers();
        document.Undo(); document.Undo();
        Assert.AreEqual(original, document.BlobSha256);
        document.EditKnowledge([], KnowledgeAction.AllMaximum);
        string changed = document.BlobSha256;
        var maximum = document.ReadKnowledge();
        Assert.IsTrue(KnowledgeCatalog.Shared.ItemPaths.Keys.All(k => maximum.GetValueOrDefault(k) >= KnowledgeCatalog.Shared.LevelCounts[k]));
        Ledgers();
        string copy = Path.Combine(Path.GetTempPath(), $"knowledge-{Guid.NewGuid():N}.save");
        try
        {
            document.SaveCopy(copy);
            var reopened = SaveDocument.Open(copy);
            Assert.AreEqual(changed, reopened.BlobSha256);
            Assert.AreEqual(JsonSerializer.Serialize(reopened.Items), JsonSerializer.Serialize(document.Items));
        }
        finally { File.Delete(copy); }
        document.EditKnowledge([1], KnowledgeAction.Unlearn);
        document.Undo();
        Assert.AreEqual(changed, document.BlobSha256, "A scalar edit after insertion must not break structural Undo.");
        document.EditKnowledge([], KnowledgeAction.AllMaximum);
        Assert.AreEqual(changed, document.BlobSha256, "Reapplying maximum is a no-op.");
        Assert.Throws<InvalidOperationException>(() => document.EditKnowledge([uint.MaxValue], KnowledgeAction.Learn));
        Assert.Throws<InvalidOperationException>(() => document.EditKnowledge([], KnowledgeAction.Level));
        Assert.Throws<InvalidOperationException>(() => document.EditKnowledge([1], KnowledgeAction.Level, 256));
        Assert.AreEqual(changed, document.BlobSha256);
        document.Undo();
        Assert.AreEqual(original, document.BlobSha256);
        Assert.IsTrue(document.IsDirty, "Undo after SaveCopy differs from the saved copy.");
        Assert.IsFalse(document.CanUndo);
        document.EditKnowledge([], KnowledgeAction.Learn);
        Assert.IsFalse(document.CanUndo);
    }

    [STATestMethod]
    [TestCategory("UI")]
    [DoNotParallelize]
    [TestCategory("PrivateFixtures")]
    public void KnowledgeNavigationFilteringSelectionEditingAndUndoStayConnected()
    {
        string? fixtures = FixtureSaves.Root;
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        if (Application.Current is null) { var app = new App.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; }
        var document = SaveDocument.Open(FixtureSaves.Named("editing_baseline"));
        var model = new MainViewModel(document);
        var window = new MainWindow(model) { Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
        window.Show();
        try
        {
            ((ListBox)window.FindName("PrimaryNavigation")).SelectedIndex = 1;
            Assert.IsTrue(model.IsKnowledge);
            var page = (KnowledgePage)window.FindName("KnowledgeArea");
            var rows = (DataGrid)page.FindName("Rows");
            Assert.AreEqual(0, rows.Items.Count, "Original knowledge page requires an explicit load.");
            WindowTests.Pump(() => model.LoadKnowledgeCommand.ExecuteAsync(null));
            Assert.AreEqual(KnowledgeCatalog.Shared.ItemPaths.Count, rows.Items.Count);
            var selected = rows.Items.Cast<KnowledgeRow>().First(r => r.Learned);
            rows.SelectedItem = selected;
            Assert.IsTrue(((Button)page.FindName("Unlearn")).IsEnabled);
            WindowTests.Capture(window, "knowledge");
            string hash = document.BlobSha256;
            WindowTests.Pump(() => model.EditKnowledgeAsync([selected.Key], KnowledgeAction.Unlearn));
            Assert.AreEqual(0u, model.Knowledge![selected.Key]);
            Assert.IsFalse(((Button)page.FindName("Apply")).IsEnabled);
            WindowTests.Pump(() => model.UndoCommand.ExecuteAsync(null));
            Assert.AreEqual(hash, document.BlobSha256);
            Assert.AreEqual(selected.Level, model.Knowledge![selected.Key]);
            ((RadioButton)page.FindName("Unlearned")).IsChecked = true;
            Assert.IsTrue(rows.Items.Cast<KnowledgeRow>().All(r => !r.Learned));
            ((ComboBox)page.FindName("Root")).SelectedIndex = 1;
            var root = KnowledgeCatalog.Shared.Roots[0];
            Assert.IsTrue(rows.Items.Cast<KnowledgeRow>().All(r => KnowledgeCatalog.Shared.ItemPaths[r.Key].Contains(root)));
            ((TextBox)page.FindName("Search")).Text = "no_matching_knowledge_12345";
            Assert.AreEqual(0, rows.Items.Count);
            Assert.IsFalse(((Button)page.FindName("Learn")).IsEnabled);
            ((TextBox)page.FindName("Search")).Text = "";
            window.UpdateLayout();
            window.Width = 800; window.UpdateLayout();
            WindowTests.Capture(window, "knowledge-narrow");
        }
        finally
        {
            while (document.CanUndo) document.Undo();
            window.Close();
        }
    }
}
