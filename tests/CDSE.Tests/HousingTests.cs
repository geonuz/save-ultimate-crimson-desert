using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using CDSE.Core.Saves;

namespace CDSE.Tests;

[TestClass]
public sealed class HousingTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public void EveryPlotHasOneKeyAndANamedRegion()
    {
        var plots = HousingPlotInfo.All;
        Assert.AreEqual(19, plots.Length);
        Assert.AreEqual(plots.Length, plots.Select(p => p.Key).Distinct().Count());
        foreach (var plot in plots)
            if (plot.Region.Length > 0)
                foreach (string language in new[] { "ko", "en", "ja", "ru", "fr", "de", "es", "pt" })
                    Assert.IsTrue(ManagementCatalog.Shared.Regions[plot.Region].ContainsKey(language), $"{plot.Key} {language}");
            else Assert.IsTrue(plot.Name.Length > 0, plot.Key.ToString());
        Assert.AreEqual(2, plots.Count(p => p.Kliff));
        // The 15 DLC plots each turn on their own House_Construction variable and learn their own plot knowledge, which is DLC content.
        var sites = plots.Where(p => p.Variable != 0).ToArray();
        CollectionAssert.AreEqual(Enumerable.Range(1000095, 15).Select(v => (uint)v).ToArray(), sites.Select(p => p.Variable).ToArray());
        CollectionAssert.AreEquivalent(Enumerable.Range(1006843, 15).Select(v => (uint)v).ToArray(), sites.Select(p => p.Knowledge).ToArray());
        Assert.IsTrue(sites.All(p => !p.Kliff && p.Region.Length > 0 && DlcContent.Shared.Knowledge.Contains(p.Knowledge)));
        // Any active plot takes an import unless furniture stands in its ready-built house; DefaultHouse_Lv4 without a row is free construction too.
        var empty = new HouseCounts(0, 0, 0, 0, 0, 0, 0, 0);
        var small = new HousingPlot(sites[0], true, 16967, false, empty);
        Assert.IsTrue(small.Active && small.CanImport && !small.CanActivate);
        Assert.AreEqual(HouseKind.Small, small.Kind);
        Assert.IsFalse((small with { Placed = 2 }).CanImport);
        Assert.AreEqual(HouseKind.Free, new HousingPlot(sites[0], true, HousingPlot.FreeHouse, false, empty).Kind);
        Assert.AreEqual(HouseKind.Large, new HousingPlot(plots[1], true, 16970, false, empty).Kind);
        var inactive = new HousingPlot(sites[0], false, null, false, empty);
        Assert.IsTrue(inactive.CanActivate && !inactive.CanImport && inactive.Kind == HouseKind.None);
        // The Ocean plots are named by their islands' knowledge.
        Assert.AreEqual(1006364u, HousingPlotInfo.Find(1000485).NameKnowledge);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void HouseFilesRefuseWhatTheyDoNotKnow()
    {
        Assert.ThrowsExactly<InvalidDataException>(() => HouseFiles.Read(Encoding.UTF8.GetBytes("{\"format\":\"other\"}")));
        Assert.ThrowsExactly<InvalidDataException>(() => HouseFiles.Read(Encoding.UTF8.GetBytes("{\"format\":\"SaveUltimate.House\",\"version\":2,\"house\":{}}")));
        Assert.ThrowsExactly<InvalidDataException>(() => HouseFiles.Read(Encoding.UTF8.GetBytes("not json")));
        // Type definitions an import could not write.
        foreach (string types in new[] { """{"T":[["_a","int32",5,4,0]]}""", """{"T":[["_a","int32",0,0,0]]}""", """{"T":[["_a","int32",0,4,0],["_a","int32",0,4,0]]}""",
            """{"T":[]}""", """{"bad name":[["_a","int32",0,4,0]]}""", """{"T":[["_a","int32",0,4]]}""" })
            Assert.ThrowsExactly<InvalidDataException>(() => HouseFiles.Read(Encoding.UTF8.GetBytes("""{"format":"SaveUltimate.House","version":1,"types":""" + types + ""","house":{}}""")), types);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void HouseFilesHoldOnlyHouseParts()
    {
        static byte[] File(string house, string types = "null") => Encoding.UTF8.GetBytes("""{"format":"SaveUltimate.House","version":1,"types":""" + types + ""","house":""" + house + "}");
        // A house part reads; other game records, a type of the file's own outside the part names, or a list the row has not do not.
        Assert.AreEqual(1, HouseFiles.Read(File("""{"_housingObjectSaveDataList":[{"$type":"HousingObjectSaveData","_housingObjectKey":16967}]}""")).Counts.Parts);
        // So does the name a later version writes.
        Assert.AreEqual(1, HouseFiles.Read(Encoding.UTF8.GetBytes("""{"format":"CDSE.House","version":1,"house":{"_housingObjectSaveDataList":[{"$type":"HousingObjectSaveData","_housingObjectKey":16967}]}}""")).Counts.Parts);
        foreach (var (house, types) in new[]
        {
            ("""{"_housingObjectSaveDataList":[{"$type":"InventorySaveData"}]}""", "null"),
            ("""{"_housingObjectSaveDataList":[{"$type":"InventorySaveData"}]}""", """{"InventorySaveData":[["_a","int32",0,4,0]]}"""),
            ("""{"_housingObjectSaveDataList":[{"$type":"CustomHouseElementSaveData"}]}""", "null"),
            ("""{"_housingObjectSaveDataList":[{"_housingObjectKey":1}]}""", "null"),
            ("""{"_inventoryList":[]}""", "null"),
        })
            Assert.ThrowsExactly<InvalidDataException>(() => HouseFiles.Read(File(house, types)), house + types);
        // A part type the file defines for parts the save has not seen is accepted when named as the game names them.
        Assert.AreEqual(1, HouseFiles.Read(File("""{"_curvedWallHousingObjectSaveDataList":[{"$type":"CurvedWallHousingObjectSaveData","_a":1}]}""",
            """{"CurvedWallHousingObjectSaveData":[["_a","int32",0,4,0]]}""")).Counts.CurvedWalls);
        // Too many rows fail as damage instead of stopping the editor.
        string many = string.Join(",", Enumerable.Repeat("""{"$type":"HousingObjectSaveData"}""", HouseFiles.MaxRows + 1));
        Assert.ThrowsExactly<InvalidDataException>(() => HouseFiles.Read(File("""{"_housingObjectSaveDataList":[""" + many + "]}")));
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void HouseFileKeepsItsHouseAndTypes()
    {
        var house = (JsonObject)JsonNode.Parse("""{"_housingObjectSaveDataList":[{"$type":"HousingObjectSaveData","_housingObjectKey":16967,"_localTransform":[1,1,1,0,0,0,1,0.5,0,7]}]}""")!;
        var types = new[] { new ParcType("HousingObjectSaveData", [new("_housingObjectKey", "HousingObjectKey", 0, 2, 0, 0), new("_textureHousingObjectKeyList", "HousingObjectKey", 3, 2, 4096, 1)]) };
        using var temp = new TempFolder();
        string path = temp.Named("house" + HouseFiles.Extension);
        HouseFiles.Write(path, new(house, 1000258, "2.05.01", "suhouse", types));
        var back = HouseFiles.Read(path);
        Assert.AreEqual(house.ToJsonString(), back.House.ToJsonString());
        Assert.AreEqual((1000258u, "2.05.01"), (back.SourcePlot, back.GameVersion));
        Assert.AreEqual(1, back.Types!.Count);
        Assert.AreEqual("HousingObjectSaveData", back.Types[0].Name);
        CollectionAssert.AreEqual(types[0].Fields.ToArray(), back.Types[0].Fields.ToArray());
    }
}
