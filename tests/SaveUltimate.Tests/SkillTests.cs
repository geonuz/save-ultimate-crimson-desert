using System.IO;
using System.Windows.Controls;
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
        var bindings = SaveUltimate.Tools.SkillCatalogBuilder.KeyBindings("""
            <SubGroup Name="Skill_17"><Input Name="Key_Skill_17"/></SubGroup>
            <!-- <Input Name="Key_Jump"><KeyboardMouse Key="j"/></> -->
            <Input Name="Key_Roll"><GamePad Key="buttonB"/><KeyboardMouse Name="OverrideKey1" Key="alt"/><KeyboardMouse Name="OverrideKey2" Key="mouseX2"/></>
            <Input Name="Key_Skill_17" Consume="True"><KeyboardMouse Name="OverrideKey" Key="f mouseR" Method="press"/></>
            <Input Name="Key_Focus_Mabongpa"><KeyboardMouse Key="tab"/></>
            <Input Name="Key_Skill_17"><KeyboardMouse Key="q"/></>
            """);
        CollectionAssert.AreEquivalent(new[] { "Roll", "Skill17", "FocusMabongpa" }, bindings.Keys.ToArray());
        Assert.AreEqual("사용 방법: 달리는 중 [F + 우클릭]", SaveUltimate.Tools.SkillCatalogBuilder.KeyLabels("사용 방법: 달리는 중 {Key:Key_Skill_17}", bindings, 1));
        Assert.AreEqual("[Alt][Tab], [F + Right Click] or [Jump]",
            SaveUltimate.Tools.SkillCatalogBuilder.KeyLabels("{Key:Key_Roll}{Key:Key_FocusMabongpa}, {Key:Key_Skill_17} or {Key:Key_Jump}", bindings, 0));
        // Only skills learned by watching carry a description: the one of level 1, which watching teaches.
        var watched = SkillCatalog.Shared.Skills.Values.Where(d => d.Observe is not null).ToArray();
        Assert.IsTrue(SkillCatalog.Shared.Skills.Values.All(d => d.Observe is null == d.Description is null));
        var descriptions = watched.SelectMany(d => d.Description!).ToArray();
        CollectionAssert.AreEqual(Array.Empty<string>(), descriptions.Where(d => d.Length == 0 || d.Contains("  ") || d.Contains('{') || d.Contains("Knowledge_") || d.Contains("[Skill ")).ToArray());
        Assert.AreEqual("사용 방법: 달리는 중 [F + 우클릭]\n\n달리는 중에 적을 걸어 메친다.", SkillCatalog.Shared.Skills[40028].Description![1]);
        // The ja/ru packs carry the same skills, links resolved to their own names.
        foreach (var (code, clothesline) in new[] { ("ja", "[F + 右クリック]"), ("ru", "[F + ПКМ]") })
        {
            var pack = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllBytes(Path.Combine(TestFiles.Resources, $"locale/names_{code}.json")))!["skill_descriptions"]!.AsObject();
            CollectionAssert.AreEquivalent(watched.Select(d => d.Key.ToString()).ToArray(), pack.Select(p => p.Key).ToArray(), code);
            StringAssert.Contains(pack["40028"]!.GetValue<string>(), clothesline);
            CollectionAssert.AreEqual(Array.Empty<string>(), pack.Select(p => p.Value!.GetValue<string>()).Where(d => d.Contains('{') || d.Contains("Knowledge_") || d.Contains("[Skill ")).ToArray(), code);
        }
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void WatchingWritesWhatTheGameWrites()
    {
        var state = new SkillState(new Dictionary<uint, uint>(), new Dictionary<uint, uint>());
        // A monster or boss in combat records FollowComplete (7); a demonstration in a region FollowManualLockOn (15).
        var (levels, follow) = state.Observe([40024]);
        CollectionAssert.AreEquivalent(new Dictionary<uint, uint> { [40024] = 1 }, levels);
        CollectionAssert.AreEquivalent(new Dictionary<uint, uint> { [40024] = 7 }, follow);
        Assert.AreEqual(15u, state.Observe([1003108]).Follow[1003108]);
        Assert.AreEqual(15u, state.Observe([40065]).Follow[40065]);
        // Kliff's Double Jump has no observation route in the game data, but saves show it watched in a region.
        Assert.AreEqual(15u, state.Observe([1001116]).Follow[1001116]);
        // What level 1 grants is learned too, with LearnedPriorKnowledge as natural saves hold it; a record already there stays.
        (levels, follow) = state.Observe([40086]);
        CollectionAssert.AreEquivalent(new Dictionary<uint, uint> { [40086] = 1, [1001117] = 1, [1000929] = 1 }, levels);
        CollectionAssert.AreEquivalent(new Dictionary<uint, uint> { [40086] = 15, [1001117] = 20, [1000929] = 20 }, follow);
        Assert.IsFalse((state with { Follow = new Dictionary<uint, uint> { [1001117] = 22 } }).Observe([40086]).Follow.ContainsKey(1001117));
        // A skill learned another way keeps its level and gets only the record; LearnedPriorKnowledge is not a watch.
        var learned = state with { Levels = new Dictionary<uint, uint> { [40024] = 1 }, Follow = new Dictionary<uint, uint> { [40024] = 20 } };
        (levels, follow) = learned.Observe([40024]);
        Assert.AreEqual(0, levels.Count); Assert.AreEqual(7u, follow[40024]);
        // A record a reset leaves (22) still counts as watched, so there is nothing to do.
        var reset = learned with { Follow = new Dictionary<uint, uint> { [40024] = 22 } };
        Assert.IsTrue(reset.Observed(40024));
        Assert.Throws<InvalidOperationException>(() => reset.Observe([40024]));
        Assert.AreEqual(1, reset.Observe([40024, 40065]).Follow.Count);
        Assert.Throws<InvalidOperationException>(() => state.Observe([1005840]), "Only skills the game teaches by watching.");
    }
}
