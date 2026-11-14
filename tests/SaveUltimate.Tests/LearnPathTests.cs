using Microsoft.VisualStudio.TestTools.UnitTesting;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Tests;

[TestClass]
public sealed class LearnPathTests
{
    [TestMethod]
    public void EditsChooseTheLearnPathPlayersHold()
    {
        var paths = LearnPathCatalog.Shared;
        // Every player who has the first Goldleaf guildhouse clue zoomed into it.
        Assert.AreEqual((uint)LearnFrom.Zoom, paths.Choose(1000938));
        Assert.IsNull(paths.Choose(SkillCatalog.Shared.Skills.Keys.First()), "Skill knowledge follows how the skill was learned.");
        Assert.IsTrue(KnowledgeCatalog.Shared.ItemPaths.Keys.Any(k => !paths.Knowledge.ContainsKey(k) && paths.Choose(k) is not null),
            "Knowledge no player has learned takes the record of its journal group.");
        // The Timberham sawmill notice carries the zoom action, but every player who read it holds Read.
        var notice = CollectionCatalog.Shared.Items.Single(i => i.Key == 1006663).Knowledge.Single(k => k.Key == 1006717);
        Assert.AreEqual((uint)LearnFrom.Zoom, notice.From);
        Assert.AreEqual((uint)LearnFrom.Read, paths.Among(1006717, LearnFrom.Read, LearnFrom.Zoom));
        Assert.IsNull(paths.Among(1005744, LearnFrom.Read, LearnFrom.Zoom), "Players learned it another way.");
    }

    [TestMethod]
    public void UnobservedKnowledgeTakesTheClientsFixedPriority()
    {
        var paths = LearnPathCatalog.Shared;
        // Each kind of client event gives the record players hold most for the knowledge it is the first source of.
        foreach (var (source, path) in LearnPathCatalog.Priority)
        {
            var held = new Dictionary<string, int>();
            foreach (var (key, kinds) in paths.Sources)
                if (!SkillCatalog.Shared.Skills.ContainsKey(key) && LearnPathCatalog.Priority.First(p => kinds.Contains(p.Source)).Source == source &&
                    paths.Knowledge.TryGetValue(key, out var counts))
                    foreach (var (record, n) in counts) held[record] = held.GetValueOrDefault(record) + n;
            if (source == "meditation") { Assert.AreEqual(0, held.Count, "Knowledge only meditation teaches was not seen learned."); continue; }
            Assert.AreEqual(path, held.MaxBy(p => p.Value).Key, source);
        }
        // An item's own knowledge outranks its inspect entry.
        CollectionAssert.AreEquivalent(new[] { "item", "read" }, paths.Sources[1006301]);
        Assert.AreEqual("None", paths.Client(1006301));
        // No player learned these nor anything in their journal groups: a cargo hold blueprint is read, a direct-fire recipe
        // comes from meditation.
        foreach (uint key in new uint[] { 1005893, 1000418 })
            Assert.IsFalse(paths.Knowledge.ContainsKey(key) || KnowledgeCatalog.Shared.ItemPaths[key][^1] is var group &&
                paths.Knowledge.Keys.Any(k => KnowledgeCatalog.Shared.ItemPaths.TryGetValue(k, out var p) && p[^1] == group), $"{key}");
        Assert.AreEqual((uint)LearnFrom.Read, paths.Choose(1005893));
        Assert.AreEqual((uint)LearnFrom.Meditation, paths.Choose(1000418));
        // Outside the journal (map icons): Hernandian Territory is a region's knowledge.
        Assert.IsFalse(KnowledgeCatalog.Shared.ItemPaths.ContainsKey(1000103) || paths.Knowledge.ContainsKey(1000103));
        Assert.AreEqual((uint)LearnFrom.Region, paths.Choose(1000103));
    }
}
