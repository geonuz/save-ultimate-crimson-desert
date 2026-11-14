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
}
