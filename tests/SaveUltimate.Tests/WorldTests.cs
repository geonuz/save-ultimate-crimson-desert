using System.Buffers.Binary;
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
    [TestCategory("Unit")]
    public void QuestLinkedPlacesLearnOnlyTheFirstLevel()
    {
        // Quests raise the later levels of spires, sanctums, hideouts and the Abyss, so only places without quests go to their last level.
        string[] full = MapAction.All.Where(a => a.FullLevel).Select(a => a.Names["en"]).ToArray();
        CollectionAssert.AreEqual(new[] { "Regions", "Hidden Spaces", "Caves", "Greymane Shrines" }, full);
    }
}
