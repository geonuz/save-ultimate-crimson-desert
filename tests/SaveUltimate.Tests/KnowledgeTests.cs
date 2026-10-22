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
    [TestCategory("PrivateFixtures")]
    public void KnowledgeActionsMatchPythonAndPreserveUndoAndLedgers()
    {
        string? reference = Environment.GetEnvironmentVariable("SAVEULTIMATE_KNOWLEDGE_REFERENCE");
        if (reference is null) Assert.Inconclusive("Set the private knowledge reference directory.");
        foreach (string path in Directory.GetFiles(reference, "python-*.json"))
        {
            using var json = JsonDocument.Parse(File.ReadAllBytes(path));
            var row = json.RootElement;
            var document = SaveDocument.Open(row.GetProperty("path").GetString()!);
            string original = document.BlobSha256;
            var before = document.ReadSkills();
            var rawLevels = document.ReadKnowledge();
            var levels = row.GetProperty("levels");
            Assert.AreEqual(levels.EnumerateObject().Count(), rawLevels.Count);
            foreach (var p in levels.EnumerateObject()) Assert.AreEqual(p.Value.GetUInt32(), rawLevels[uint.Parse(p.Name)]);
            uint[] keys = row.GetProperty("keys").EnumerateArray().Select(k => k.GetUInt32()).ToArray();
            var action = Enum.Parse<KnowledgeAction>(row.GetProperty("action").GetString()!);
            document.EditKnowledge(keys, action, row.GetProperty("level").GetUInt32());
            string changed = document.BlobSha256;
            Assert.AreEqual(row.GetProperty("sha256").GetString(), changed, path);
            var after = document.ReadSkills();
            CollectionAssert.AreEqual(before.Paid.OrderBy(p => p.Key).ToArray(), after.Paid.OrderBy(p => p.Key).ToArray());
            CollectionAssert.AreEqual(before.Points.OrderBy(p => p.Key).ToArray(), after.Points.OrderBy(p => p.Key).ToArray());
            CollectionAssert.AreEqual(before.Follow.OrderBy(p => p.Key).ToArray(), after.Follow.OrderBy(p => p.Key).ToArray());
            if (action == KnowledgeAction.AllMaximum)
            {
                string copy = Path.Combine(Path.GetTempPath(), $"knowledge-{Guid.NewGuid():N}.save");
                try
                {
                    document.SaveCopy(copy);
                    var reopened = SaveDocument.Open(copy);
                    Assert.AreEqual(changed, reopened.BlobSha256);
                    Assert.AreEqual(JsonSerializer.Serialize(reopened.Items), JsonSerializer.Serialize(document.Items));
                }
                finally { File.Delete(copy); }
                uint learned = KnowledgeCatalog.Shared.ItemPaths.Keys.First();
                document.EditKnowledge([learned], KnowledgeAction.Unlearn);
                document.Undo();
                Assert.AreEqual(changed, document.BlobSha256, "A scalar edit after insertion must not break structural Undo.");
                document.EditKnowledge([], KnowledgeAction.AllMaximum);
                Assert.AreEqual(changed, document.BlobSha256, "Reapplying maximum is a no-op.");
            }
            Assert.Throws<InvalidOperationException>(() => document.EditKnowledge([uint.MaxValue], KnowledgeAction.Learn));
            Assert.Throws<InvalidOperationException>(() => document.EditKnowledge([], KnowledgeAction.Level));
            Assert.Throws<InvalidOperationException>(() => document.EditKnowledge(keys, KnowledgeAction.Level, 256));
            Assert.AreEqual(changed, document.BlobSha256);
            if (document.CanUndo) document.Undo();
            Assert.AreEqual(original, document.BlobSha256);
            Assert.AreEqual(action == KnowledgeAction.AllMaximum, document.IsDirty, "Undo after SaveCopy differs from the saved copy.");
            Assert.IsFalse(document.CanUndo);
            document.EditKnowledge([], KnowledgeAction.Learn);
            Assert.IsFalse(document.CanUndo);
        }
        Assert.AreEqual(6, Directory.GetFiles(reference, "python-*.json").Length);
    }

    [STATestMethod]
    [TestCategory("PrivateFixtures")]
    public void KnowledgeNavigationFilteringSelectionEditingAndUndoStayConnected()
    {
        string? fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES");
        if (fixtures is null) Assert.Inconclusive("Set private fixtures.");
        if (Application.Current is null) { var app = new App.App(); app.InitializeComponent(); app.ShutdownMode = ShutdownMode.OnExplicitShutdown; }
        var document = SaveDocument.Open(Path.Combine(fixtures, "save_v2.03.02.save"));
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
