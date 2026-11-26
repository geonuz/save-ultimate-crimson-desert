using System.Text.Json.Nodes;

namespace CDSE.Core.Saves;

public sealed partial class SaveDocument
{
    // The last key of HousingObjectInfo, 2.05.01 and 2.06.00. A newer part could be a wall or hang on one, so its house is not turned.
    internal const int LastHousingObject = 18378;
    // HousingObjectInfo category 2 (walls and fences) and 3 (pillars), 2.05.01 and 2.06.00. The game keeps these in one of two directions and
    // gives a wall its two sides by its two materials, so a wall that turns around keeps its direction and trades its materials.
    internal static readonly HashSet<int> Walls = [16960, 16961, 16967, 16968, 16969, 16970, 16971, 16972, 16973, 16974, 16975, 16976, 17957, 18118, 18119, 18120, 18121, 18322];
    internal static readonly HashSet<int> Pillars = [17898, 17899, 17900, 17901, 17902, 17903, 18091, 18092];
    // Decorations that only hang on walls (HousingObjectInfo +24 is 2; the category is at +1): the wall gives their rotation, so they hold no step.
    internal static bool Hangs(int key) => key is >= 17084 and <= 17086 or 17093 or 17094 or >= 17096 and <= 17101 or >= 17105 and <= 17107 or 17171
        or >= 17227 and <= 17304 or >= 17910 and <= 17912 or 18201 or 18224 or 18225 or 18345;
    // A stair's landings are two half meters deep.
    private const int LandingDepth = 2;

    /// <summary>Why a plot's house cannot be turned around ("curved" or "holes", rows nobody has seen, or "newer", parts
    /// of a later game version), or null when it can.</summary>
    public static string? RotationBlock(HousingPlot plot) => plot.Counts.CurvedWalls > 0 ? "curved" : plot.Counts.Holes > 0 ? "holes" : plot.Counts.Newer > 0 ? "newer" : null;

    /// <summary>
    /// Turns the plot's whole free-construction house around by 180 degrees. The build box is 28 x 24 m on every plot, so a house
    /// can only turn in half turns and stay inside it.
    /// </summary>
    public HousingPlot[] RotateHouse(uint plot)
    {
        var layout = ExportHouse(plot);
        if (ReadHousing().Single(p => p.Key == plot) is var target && RotationBlock(target) is not null) throw new InvalidOperationException("This house holds parts that cannot be turned.");
        var turned = (JsonObject)layout.House.DeepClone();
        TurnAround(turned);
        return ImportHouse(plot, layout with { House = (JsonObject)JsonNode.Parse(InFieldOrder(turned, layout.Types)!.ToJsonString())! });
    }

    // A row's values in the order of its type, as reading the save back gives them.
    private static JsonNode? InFieldOrder(JsonNode? node, IReadOnlyList<ParcType>? types)
    {
        if (node is JsonArray list) return new JsonArray(list.Select(n => InFieldOrder(n, types)).ToArray());
        if (node is not JsonObject o) return node?.DeepClone();
        var order = types?.FirstOrDefault(t => t.Name == o["$type"]?.GetValue<string>())?.Fields.ToDictionary(f => f.Name, f => f.Index) ?? [];
        return new JsonObject(o.OrderBy(p => p.Key == "$type" ? -1 : order.GetValueOrDefault(p.Key, int.MaxValue)).Select(p => KeyValuePair.Create(p.Key, InFieldOrder(p.Value, types))));
    }

    // A half turn about the plot origin: (x, z) becomes (-x, -z). Cells (rooms, path tiles) are named by their low corner, so
    // their number becomes -n - 1; boxes trade their low and high corners.
    internal static void TurnAround(JsonObject house)
    {
        IEnumerable<JsonObject> Rows(string list) => house[list] is JsonArray rows ? rows.OfType<JsonObject>() : [];
        foreach (var part in Rows("_housingObjectSaveDataList")) TurnPart(part, 8);
        foreach (string list in new[] { "_fenceHousingObjectSaveDataList", "_roofHousingObjectSaveDataList" })
            foreach (var row in Rows(list)) TurnPart(Nested(row), 8);
        foreach (var row in Rows("_stairHousingObjectSaveDataList"))
        {
            // The landings lie before and after the run, as wide as the stair; they share the coordinate across it.
            int[] lower = Ints(row, "_stairLocalLowerLandingIndex", 3), upper = Ints(row, "_stairLocalUpperLandingIndex", 3);
            var part = Nested(row);
            int[] min = Ints(part, "_minLocalIndex", 3), max = Ints(part, "_maxLocalIndex", 3);
            bool alongZ = lower[0] == upper[0];
            int wide = alongZ ? max[0] - min[0] : LandingDepth, deep = alongZ ? LandingDepth : max[2] - min[2];
            SetInts(row, "_stairLocalLowerLandingIndex", [-lower[0] - wide, lower[1], -lower[2] - deep]);
            SetInts(row, "_stairLocalUpperLandingIndex", [-upper[0] - wide, upper[1], -upper[2] - deep]);
            // Stairs count their rotation in quarter turns, every other part in eighths.
            TurnPart(part, 4);
        }
        foreach (var room in Rows("_customHouseRoomSaveDataList"))
        {
            int[] anchor = Ints(room, "_localAnchorIndex", 2);
            SetInts(room, "_localAnchorIndex", [-anchor[0] - 1, -anchor[1] - 1]);
        }
        // Half a turn of an 8 x 8 sub-grid reverses its 64 cells.
        foreach (var grid in Rows("_housingPlacedPathSubGridSaveDataList"))
        {
            int[] index = Ints(grid, "_subGridIndex", 2);
            SetInts(grid, "_subGridIndex", [-index[0] - 1, -index[1] - 1]);
            if (grid["_placedHousingObjectKeyList"] is JsonArray cells)
                grid["_placedHousingObjectKeyList"] = new JsonArray(cells.Reverse().Select(c => c?.DeepClone()).ToArray());
        }
    }

    private static JsonObject Nested(JsonObject row) => row["_housingObjectSaveData"] as JsonObject ?? throw new InvalidDataException("A house part has no object.");
    private static int[] Ints(JsonObject o, string name, int count) => o[name] is JsonArray a ? a.Select(v => v!.GetValue<int>()).ToArray() : new int[count];
    // The game leaves zero fields out of a row.
    private static void SetInts(JsonObject o, string name, int[] values)
    {
        if (values.All(v => v == 0)) o.Remove(name);
        else o[name] = new JsonArray(values.Select(v => (JsonNode)v).ToArray());
    }

    private static void TurnPart(JsonObject part, int steps)
    {
        int[] min = Ints(part, "_minLocalIndex", 3), max = Ints(part, "_maxLocalIndex", 3);
        SetInts(part, "_minLocalIndex", [-max[0], min[1], -max[2]]);
        SetInts(part, "_maxLocalIndex", [-min[0], max[1], -min[2]]);
        int key = part["_housingObjectKey"]?.GetValue<int>() ?? 0;
        if (part["_localTransform"] is not JsonArray { Count: 10 } t) throw new InvalidDataException("A house part has no transform.");
        // Scale 3, rotation 4, position 3.
        float[] v = t.Select(n => n!.GetValue<float>()).ToArray();
        (v[7], v[9]) = (-v[7], -v[9]);
        if (Walls.Contains(key))
        {
            if (part["_textureHousingObjectKeyList"] is JsonArray { Count: 2 } sides)
                part["_textureHousingObjectKeyList"] = new JsonArray(sides[1]?.DeepClone(), sides[0]?.DeepClone());
        }
        else if (!Pillars.Contains(key))
        {
            // Parts the player turns hold their rotation as a step too; doors, windows and what hangs on walls take it from the wall.
            if ((part["_placementType"]?.GetValue<int>() ?? 0) == 0 && !Hangs(key))
            {
                int step = ((part["_rotationStep"]?.GetValue<int>() ?? 0) + steps / 2) % steps;
                if (step == 0) part.Remove("_rotationStep"); else part["_rotationStep"] = step;
            }
            // (0, 1, 0, 0) * rotation.
            (v[3], v[4], v[5], v[6]) = (v[5], v[6], -v[3], -v[4]);
        }
        part["_localTransform"] = new JsonArray(v.Select(n => (JsonNode)(n == 0 ? 0f : n)).ToArray());
    }
}
