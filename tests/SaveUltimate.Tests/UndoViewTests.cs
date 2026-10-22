using System.IO;
using System.Text.Json;
using System.Windows;
using SaveUltimate.App;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class UndoViewTests
{
    [STATestMethod]
    [TestCategory("PrivateFixtures")]
    public void UndoRefreshesAffectedViewsAndPreservesUnrelatedViews()
    {
        string fixtures = Environment.GetEnvironmentVariable("SAVEULTIMATE_FIXTURES") ?? throw new AssertInconclusiveException("Set fixtures.");
        if (Application.Current is null) _ = new App.App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var doc = SaveDocument.Open(Path.Combine(fixtures, "save_v2.03.02.save"));
        string original = doc.BlobSha256;
        var model = new MainViewModel(doc);
        var window = new MainWindow(model) { Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false };
        window.Show();
        void Run(Func<Task> action) => WindowTests.Pump(action);
        void Undo()
        {
            Run(() => model.UndoCommand.ExecuteAsync(null));
            Assert.AreEqual(original, doc.BlobSha256);
            Assert.IsFalse(doc.CanUndo);
            Assert.IsFalse(doc.IsDirty);
        }
        try
        {
            Run(() => model.LoadSkillsCommand.ExecuteAsync(null));
            Run(() => model.LoadKnowledgeCommand.ExecuteAsync(null));
            Run(() => model.LoadChallengesCommand.ExecuteAsync(null));
            Run(() => model.LoadFishingCommand.ExecuteAsync(null));
            Run(() => model.LoadMapCommand.ExecuteAsync(null));
            Run(() => model.LoadCompanionsCommand.ExecuteAsync(null));
            Run(() => model.LoadFriendshipCommand.ExecuteAsync(null));
            var knowledge = model.Knowledge; var skills = model.Skills; var challenges = model.Challenges;
            var map = model.Map; var companions = model.Companions; var npc = model.Friendship;
            Run(() => model.SetFishingRecordAsync(2001, 7.25));
            Undo();
            Assert.AreEqual(JsonSerializer.Serialize(doc.ReadFishing()), JsonSerializer.Serialize(model.Fishing));
            Assert.AreSame(knowledge, model.Knowledge); Assert.AreSame(skills, model.Skills);
            Assert.AreSame(challenges, model.Challenges); Assert.AreSame(map, model.Map);
            Assert.AreSame(companions, model.Companions); Assert.AreSame(npc, model.Friendship);

            Run(() => model.RenameCompanionsAsync([model.Companions!.Single(c => c.Number == 352)], "Undo view test"));
            Undo();
            CollectionAssert.AreEqual(doc.ReadCompanions(), model.Companions!);
            Assert.AreSame(knowledge, model.Knowledge); Assert.AreSame(map, model.Map); Assert.AreSame(npc, model.Friendship);

            uint key = model.Knowledge!.First(p => p.Value > 0 && KnowledgeCatalog.Shared.ItemPaths.ContainsKey(p.Key)).Key;
            Run(() => model.EditKnowledgeAsync([key], KnowledgeAction.Unlearn));
            Assert.AreEqual(0u, model.Map!.Knowledge.GetValueOrDefault(key));
            Undo();
            Assert.AreEqual(knowledge![key], model.Knowledge![key]);
            Assert.AreSame(model.Skills!.Levels, model.Knowledge);
            Assert.AreSame(model.Knowledge, model.Map!.Knowledge);
            Assert.IsTrue(model.Fishing!.All(r => r.Level == model.Knowledge.GetValueOrDefault(r.Fish.Key)));
            Assert.AreSame(challenges, model.Challenges);
            Assert.AreSame(npc, model.Friendship);

        }
        finally { while (doc.CanUndo) doc.Undo(); window.Close(); }
    }
}
