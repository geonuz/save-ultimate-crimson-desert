using System.IO;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using CDSE.Core.Catalogs;
using CDSE.Core.Saves;

namespace CDSE.Tests;

[TestClass]
[TestCategory("Unit")]
public sealed class GearTests
{
    [TestMethod]
    public void GearCatalogIsConsistent()
    {
        var catalog = GearCatalog.Shared;
        Assert.IsTrue(catalog.Dye.Count > 1000 && catalog.Dye.Values.All(p => p.Length <= GearCatalog.DyeParts && p.All("clmnx-".Contains)));
        Assert.AreEqual(GearCatalog.Textures, catalog.ColorGroups.Count);
        foreach (var (character, mount) in catalog.Mounts)
        {
            Assert.AreEqual(2, mount.Length);
            Assert.IsTrue(catalog.SlotsOf(character).Length > 0, $"{character}");
        }
        Assert.IsTrue(catalog.Slots.Values.SelectMany(s => s).SelectMany(t => t).All(catalog.EquipTypes.ContainsKey));
        foreach (var (group, keys) in catalog.Addable)
            Assert.IsTrue(keys.All(k => CompanionTypes.Shared.Group(k) == group), group);
        Assert.IsFalse(catalog.CanWear(0, 0, catalog.Wearables.Keys.First()));
        // What a mount comes with goes in one slot each, and every ship an addition offers comes with something.
        foreach (var (character, gear) in catalog.Comes)
            Assert.AreEqual(gear.Length, gear.SelectMany(k => Enumerable.Range(0, catalog.SlotsOf(character).Length).Where(s => catalog.CanWear(character, s, k))).Distinct().Count(), $"{character}");
        Assert.IsTrue(catalog.Addable["ship"].All(catalog.Comes.ContainsKey));
        Assert.IsTrue(catalog.Addable["wagon"].Length > 0 && catalog.Services.Count > 0 && catalog.Parts.Keys.All(catalog.Wearables.ContainsKey) && catalog.Parts.Values.All(c => c > 0));
    }

    [TestMethod]
    public void DyeFilesReadBackAndRefuseDamage()
    {
        uint item = GearCatalog.Shared.Dye.First().Key;
        DyeEntry[] entries =
        [
            new(item, "이름", [new(0, true, 1, 2, 255, 3363967477, 3, 0, true), new(11, false, 0, 0, 0, 0, 10, null, false)]),
            new(item, "", [])
        ];
        using var temp = new TempFolder();
        string path = temp.Named("a" + DyeFiles.Extension);
        DyeFiles.Write(path, entries);
        var back = DyeFiles.Read(path);
        Assert.AreEqual(2, back.Length);
        Assert.AreEqual(entries[0] with { Parts = back[0].Parts }, back[0]);
        CollectionAssert.AreEqual(entries[0].Parts, back[0].Parts);
        Assert.AreEqual(0, back[1].Parts.Length);
        string text = File.ReadAllText(path);
        // A dye code carries the same entries without their names, survives line breaks and refuses anything else.
        string code = DyeFiles.ToCode(entries);
        StringAssert.StartsWith(code, DyeFiles.CodePrefix);
        var pasted = DyeFiles.FromCode(" " + code[..20] + "\r\n" + code[20..] + "\n");
        Assert.AreEqual(2, pasted.Length);
        Assert.AreEqual("", pasted[0].Name);
        CollectionAssert.AreEqual(entries[0].Parts, pasted[0].Parts);
        CollectionAssert.AreEqual(entries[1].Parts, DyeFiles.FromCode(code[DyeFiles.CodePrefix.Length..])[1].Parts);
        foreach (string wrong in new[] { "", "CDSE-DYE:", "hello", "CDSE-DYE:!!!!", DyeFiles.CodePrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)), code[..^8] })
            Assert.ThrowsExactly<InvalidDataException>(() => DyeFiles.FromCode(wrong), wrong);
        // The name a later version writes reads too.
        Assert.AreEqual(2, DyeFiles.Read(Encoding.UTF8.GetBytes(text.Replace("SaveUltimate.Dye", "CDSE.Dye"))).Length);
        foreach (string damaged in new[] { "", "[]", "{}", text.Replace("SaveUltimate.Dye", "Other"), text.Replace("\"version\": 1", "\"version\": 2"), text.Replace("#0102FF", "#0102F"), text.Replace("\"part\": 11", "\"part\": 12"),
            text.Replace("\"material\": 10", "\"material\": 70000"), text.Replace("\"grime\": 0", "\"grime\": 256"), text[..^20] })
            Assert.ThrowsExactly<InvalidDataException>(() => DyeFiles.Read(Encoding.UTF8.GetBytes(damaged)), damaged);
    }

    [TestMethod]
    public void HalfTurnMovesEveryKindOfPartAndTwoGiveTheHouseBack()
    {
        JsonObject Part(int key, int[] min, int[] max, float[] rotation, float[] position) => new()
        {
            ["_housingObjectKey"] = key, ["_minLocalIndex"] = new JsonArray(min.Select(v => (JsonNode)v).ToArray()), ["_maxLocalIndex"] = new JsonArray(max.Select(v => (JsonNode)v).ToArray()),
            ["_localTransform"] = new JsonArray(new[] { 1f, 1f, 1f }.Concat(rotation).Concat(position).Select(v => (JsonNode)v).ToArray())
        };
        var wall = Part(16960, [2, 0, 4], [10, 8, 4], [0, 0, 0, 1], [3, 0, 2]);
        wall["_textureHousingObjectKeyList"] = new JsonArray(1, 2);
        var table = Part(17183, [1, 0, 1], [3, 2, 5], [0, 0, 0, 1], [1, 0, 1.5f]);
        var house = new JsonObject
        {
            ["_housingObjectSaveDataList"] = new JsonArray(wall, table),
            ["_customHouseRoomSaveDataList"] = new JsonArray(new JsonObject { ["_localAnchorIndex"] = new JsonArray(0, 5) }),
            ["_housingPlacedPathSubGridSaveDataList"] = new JsonArray(new JsonObject { ["_subGridIndex"] = new JsonArray(-1, 2), ["_placedHousingObjectKeyList"] = new JsonArray(Enumerable.Range(0, 64).Select(i => (JsonNode)i).ToArray()) })
        };
        string before = house.ToJsonString();
        HousingPlot Plot() => new(HousingPlotInfo.All[0], true, HousingPlot.FreeHouse, true, SaveDocument.CountHouse(house));
        Assert.IsNull(SaveDocument.RotationBlock(Plot()));
        table["_housingObjectKey"] = SaveDocument.LastHousingObject + 1;
        Assert.AreEqual("newer", SaveDocument.RotationBlock(Plot()), "A part newer than the lists may be a wall.");
        table["_housingObjectKey"] = 17183;
        SaveDocument.TurnAround(house);
        var turned = house["_housingObjectSaveDataList"]!.AsArray();
        Assert.AreEqual("[-10,0,-4]", turned[0]!["_minLocalIndex"]!.ToJsonString());
        Assert.AreEqual("[2,1]", turned[0]!["_textureHousingObjectKeyList"]!.ToJsonString(), "A wall keeps its direction and trades its sides.");
        Assert.AreEqual(1f, turned[0]!["_localTransform"]![6]!.GetValue<float>());
        Assert.AreEqual((4, 1f), (turned[1]!["_rotationStep"]!.GetValue<int>(), turned[1]!["_localTransform"]![4]!.GetValue<float>()), "Other parts turn.");
        Assert.AreEqual("[-1,-6]", house["_customHouseRoomSaveDataList"]![0]!["_localAnchorIndex"]!.ToJsonString());
        var grid = house["_housingPlacedPathSubGridSaveDataList"]![0]!;
        // A zero pair is left out of a row, but this one is (0, -3).
        Assert.AreEqual("[0,-3]", grid["_subGridIndex"]!.ToJsonString());
        Assert.AreEqual(63, grid["_placedHousingObjectKeyList"]![0]!.GetValue<int>());
        SaveDocument.TurnAround(house);
        // A quaternion and its negative are one rotation; two half turns give the negative.
        Assert.AreEqual(-1f, house["_housingObjectSaveDataList"]![1]!["_localTransform"]![6]!.GetValue<float>());
        house["_housingObjectSaveDataList"]![1]!["_localTransform"]![6] = 1f;
        Assert.AreEqual(Plain(before), Plain(house.ToJsonString()));
        static string Plain(string json) => string.Join(",", json.Split(',').Order());
    }
}
