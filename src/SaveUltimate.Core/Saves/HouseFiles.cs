using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SaveUltimate.Core.Saves;

/// <summary>
/// House files. SaveUltimate writes its own .suhouse: UTF-8 JSON holding the plot row's lists under the game's field names, in
/// the plot frame every plot shares. It also reads the SHPACK01 layout format.
/// </summary>
public static class HouseFiles
{
    public const string Extension = ".suhouse";
    private const string Format = "SaveUltimate.House";
    private const int Version = 1;
    private const int MaxBytes = 64 << 20;

    public static void Write(string path, HouseLayout layout)
    {
        var counts = layout.Counts;
        var root = new JsonObject
        {
            ["format"] = Format, ["version"] = Version, ["gameVersion"] = layout.GameVersion, ["sourcePlot"] = layout.SourcePlot,
            ["counts"] = new JsonObject
            {
                ["parts"] = counts.Parts, ["fences"] = counts.Fences, ["roofs"] = counts.Roofs, ["stairs"] = counts.Stairs,
                ["curvedWalls"] = counts.CurvedWalls, ["holes"] = counts.Holes, ["rooms"] = counts.Rooms, ["pathTiles"] = counts.PathTiles,
            },
            ["types"] = layout.Types is { } types
                ? new JsonObject(types.Select(t => KeyValuePair.Create(t.Name, (JsonNode?)new JsonArray(t.Fields.Select(f =>
                    (JsonNode)new JsonArray(f.Name, f.TypeName, f.Kind, f.Size, f.Auxiliary)).ToArray()))))
                : null,
            ["house"] = layout.House.DeepClone(),
        };
        string temp = path + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    public static HouseLayout Read(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxBytes) throw new InvalidDataException("The house file is too large.");
        return Read(File.ReadAllBytes(path));
    }

    /// <summary>
    /// A house file is data from someone else. It is only parsed as JSON: nothing in it is run, opened or named as a path, and
    /// no .NET type is chosen from it. What reaches the save is held to house rows: every object must be a house part type,
    /// the row count is bounded and every number fits its field (finite floats), so a crafted file cannot put other game
    /// records into the save. Any damage reads as InvalidDataException rather than stopping the editor.
    /// </summary>
    public static HouseLayout Read(byte[] bytes)
    {
        HouseLayout layout;
        try { layout = bytes.AsSpan().StartsWith("SHPACK01"u8) ? LayoutFile.Read(bytes) : ReadOwn(bytes); }
        catch (Exception e) when (e is not InvalidDataException and not OutOfMemoryException) { throw new InvalidDataException("The house file is damaged.", e); }
        CheckContent(layout);
        return layout;
    }

    // The most rows a house may hold. The largest house seen holds about 3,500; far above that is no house anyone built.
    internal const int MaxRows = 50_000;
    // The part types a house row may hold, besides those a file defines for parts the save has not seen (named like the game's own).
    private static readonly HashSet<string> PartTypes = SaveDocument.KnownHouseTypes.Select(t => t.Name).Where(n => n is not "CustomHouseSaveData" and not "CustomHouseElementSaveData").ToHashSet(StringComparer.Ordinal);
    // The game names part types "…HousingObjectSaveData" (CurvedWallHousingObjectSaveData) or "CustomHouse…SaveData"
    // (CustomHouseHoleAnchorSaveData); the house block and row types are excluded where this is used.
    private static bool HouseTypeName(string name) => PartTypes.Contains(name) || name.EndsWith("SaveData", StringComparison.Ordinal) &&
        (name.EndsWith("HousingObjectSaveData", StringComparison.Ordinal) || name.StartsWith("CustomHouse", StringComparison.Ordinal));

    private static void CheckContent(HouseLayout layout)
    {
        var lists = SaveDocument.KnownHouseTypes.Single(t => t.Name == "CustomHouseElementSaveData").Fields.Where(f => f.Kind == 6).Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var defined = layout.Types?.Select(t => t.Name).Where(n => n is not "CustomHouseSaveData" and not "CustomHouseElementSaveData").ToHashSet(StringComparer.Ordinal) ?? [];
        int rows = 0;
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    if (++rows > MaxRows) throw new InvalidDataException("The house file holds too many parts.");
                    if (o["$type"] is not JsonValue v || !v.TryGetValue<string>(out var type) || !(PartTypes.Contains(type) || defined.Contains(type)))
                        throw new InvalidDataException("The house file holds something that is not a house part.");
                    foreach (var (_, value) in o) Walk(value);
                    break;
                case JsonArray a: foreach (var item in a) Walk(item); break;
                case JsonValue value when value.TryGetValue<double>(out double number) && !double.IsFinite(number):
                    throw new InvalidDataException("The house file holds a value out of range.");
            }
        }
        foreach (var (name, value) in layout.House)
        {
            if (!lists.Contains(name) || value is not JsonArray list) throw new InvalidDataException("The house file is damaged.");
            Walk(list);
        }
    }

    private static HouseLayout ReadOwn(byte[] bytes)
    {
        JsonObject root;
        try { root = JsonNode.Parse(bytes) as JsonObject ?? throw new InvalidDataException("Not a house file."); }
        catch (JsonException) { throw new InvalidDataException("Not a house file."); }
        if (root["format"]?.GetValue<string>() != Format) throw new InvalidDataException("Not a house file.");
        if (root["version"]?.GetValue<int>() is not { } version || version > Version) throw new InvalidDataException("This house file needs a newer SaveUltimate.");
        var house = root["house"] as JsonObject ?? throw new InvalidDataException("The house file has no house.");
        try
        {
            return new((JsonObject)house.DeepClone(), root["sourcePlot"]?.GetValue<uint>(), root["gameVersion"]?.GetValue<string>(), "suhouse", Types(root["types"]));
        }
        catch (Exception e) when (e is InvalidOperationException or FormatException or OverflowException) { throw new InvalidDataException("The house file is damaged."); }
    }

    // The schema of the types the house uses, as the exporting save defined them. Only shapes an import can write are accepted.
    private static ParcType[]? Types(JsonNode? node)
    {
        if (node is null) return null;
        var types = node as JsonObject ?? throw new InvalidDataException("The house file is damaged.");
        if (types.Count > 64) throw new InvalidDataException("The house file is damaged.");
        static bool Name(string? s) => s is { Length: > 0 and <= 128 } && (char.IsAsciiLetter(s[0]) || s[0] == '_') && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');
        var result = new List<ParcType>();
        foreach (var (name, value) in types)
        {
            var fields = value as JsonArray;
            // Only house types: a file must not define other game records. An export also defines the house block and row types,
            // which CheckContent keeps out of the rows.
            if (!Name(name) || !HouseTypeName(name) || fields is null || fields.Count is 0 or > 64)
                throw new InvalidDataException("The house file is damaged.");
            var list = new List<ParcField>();
            foreach (var f in fields)
            {
                if (f is not JsonArray { Count: 5 } a) throw new InvalidDataException("The house file is damaged.");
                string field = a[0]!.GetValue<string>(), type = a[1]!.GetValue<string>();
                ushort kind = a[2]!.GetValue<ushort>(), size = a[3]!.GetValue<ushort>();
                bool shape = kind switch { 0 or 2 or 3 => size is > 0 and <= 64, 4 => size == 8, 6 => size == 0, _ => false };
                if (!Name(field) || !Name(type) || !shape || list.Any(x => x.Name == field)) throw new InvalidDataException("The house file is damaged.");
                list.Add(new(field, type, kind, size, a[4]!.GetValue<uint>(), list.Count));
            }
            result.Add(new(name, list));
        }
        return result.ToArray();
    }
}

/// <summary>
/// An SHPACK01 layout, read from its JSON part: "SHPACK01", u32 JSON length, four u32, the JSON at 0x1C, then a
/// binary section this reader does not use. Parts are in the editor's plot frame, which editorToSavedQuarterTurns turns into
/// the saved frame: each quarter turn maps (x, z) to (z, -x), swaps the x- and z-running placements (1 and 2, 3 and 4), and
/// adds a 90 degree yaw. Rooms, stairs and path tiles carry their saved grid already. Pieces the rooms generate (editorKind
/// other than 0) have no save rows and are left out; path parts are the path tiles again. Curved walls and floor openings are left
/// out too (HouseLeftOut): no save that holds them has been seen, so their rows are unknown.
/// </summary>
internal static class LayoutFile
{
    internal static HouseLayout Read(byte[] file)
    {
        if (file.Length < 0x1C) throw new InvalidDataException("The house file is damaged.");
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(8));
        if (length == 0 || length > file.Length - 0x1C) throw new InvalidDataException("The house file is damaged.");
        JsonElement root;
        try { root = JsonDocument.Parse(file.AsMemory(0x1C, (int)length)).RootElement; }
        catch (JsonException) { throw new InvalidDataException("The house file is damaged."); }
        if (root.TryGetProperty("format", out var format) is false || format.GetString()?.StartsWith("sharehouse-live-layout", StringComparison.Ordinal) != true)
            throw new InvalidDataException("Unsupported house file format.");
        int turns = root.GetProperty("editorToSavedQuarterTurns").GetInt32();
        if (turns is < 0 or > 3) throw new InvalidDataException("Unsupported house file rotation.");
        // Floor openings and curved walls come only in the binary section, whose save rows are not known yet: the rest goes in
        // without them, and the import says so.
        bool holes = root.TryGetProperty("hasNativeHoleData", out var openings) && openings.ValueKind == JsonValueKind.True;
        int curved = 0;

        var objects = new JsonArray(); var fences = new JsonArray(); var roofs = new JsonArray(); var stairs = new JsonArray();
        foreach (var p in root.GetProperty("parts").EnumerateArray())
        {
            if (p.GetProperty("editorKind").GetInt32() != 0) continue;
            if (p.TryGetProperty("nativeCurveSize", out var curve) && curve.GetInt32() != 0) { curved++; continue; }
            int category = p.GetProperty("nativeCategory").GetInt32();
            if (category == 14) continue;
            if (category is 1 or 12) throw new InvalidDataException("The house file is damaged.");
            if (p.TryGetProperty("nativeFenceBoundary", out var boundary))
            {
                var fence = Typed("FenceHousingObjectSaveData"); fence["_housingObjectSaveData"] = Part(p, turns);
                if (boundary.ValueKind == JsonValueKind.True || boundary.ValueKind == JsonValueKind.Number && boundary.GetInt32() != 0) fence["_isUsedForRoomBoundary"] = true;
                fences.Add(fence);
            }
            else if (category == 8)
            {
                var roof = Typed("RoofHousingObjectSaveData"); roof["_housingObjectSaveData"] = Part(p, turns);
                float deform = BitConverter.Int32BitsToSingle(unchecked((int)p.GetProperty("roofDeformBits").GetUInt32()));
                if (deform != 0) roof["_roofDeformCurveKeyR"] = deform;
                roofs.Add(roof);
            }
            else if (category == 10)
            {
                var s = p.GetProperty("stair");
                var stair = Typed("StairHousingObjectSaveData"); stair["_housingObjectSaveData"] = Part(p, turns);
                PutInts(stair, "_stairLocalLowerLandingIndex", s.GetProperty("lowerSavedGrid"));
                PutInts(stair, "_stairLocalUpperLandingIndex", s.GetProperty("upperSavedGrid"));
                Put(stair, "_stairLowerFloor", s.GetProperty("lowerFloor").GetInt32());
                Put(stair, "_stairUpperFloor", s.GetProperty("upperFloor").GetInt32());
                Put(stair, "_stairInstanceCount", s.GetProperty("instanceCount").GetInt32());
                Put(stair, "_stairWidth", s.GetProperty("width").GetInt32());
                Put(stair, "_stairRailingHousingObjectKey", s.GetProperty("railingKey").GetInt32());
                if (s.GetProperty("leftRailing").ValueKind == JsonValueKind.True) stair["_hasLeftStairRailing"] = true;
                if (s.GetProperty("rightRailing").ValueKind == JsonValueKind.True) stair["_hasRightStairRailing"] = true;
                stairs.Add(stair);
            }
            else objects.Add(Part(p, turns));
        }

        var rooms = new JsonArray();
        foreach (var r in root.GetProperty("rooms").EnumerateArray())
        {
            var room = Typed("CustomHouseRoomSaveData");
            Put(room, "_floor", r.GetProperty("floorIndex").GetInt32());
            PutInts(room, "_localAnchorIndex", r.GetProperty("anchorSavedGrid"));
            Put(room, "_wallScaleY", r.GetProperty("wallScaleY").GetInt32());
            Put(room, "_floorOffsetIndexY", r.GetProperty("nativeFloorOffset").GetInt32());
            if (r.TryGetProperty("ceilingPartIndices", out var ceiling) && ceiling.GetArrayLength() > 0) room["_hasCeiling"] = true;
            Put(room, "_floorTextureHousingObjectKey", Key(r, "floorMaterialKey"));
            Put(room, "_ceilingTextureHousingObjectKey", Key(r, "ceilingMaterialKey"));
            var moldings = new JsonArray();
            if (r.TryGetProperty("moldings", out var list))
                foreach (var m in list.EnumerateArray())
                {
                    var molding = Typed("MoldingHousingObjectSaveData");
                    Put(molding, "_moldingHousingObjectKey", m.GetProperty("partKey").GetInt32());
                    if (m.GetProperty("height").GetSingle() is var height && height != 0) molding["_localPositionY"] = height;
                    Put(molding, "_minIndexY", m.GetProperty("lowerGridY").GetInt32());
                    Put(molding, "_maxIndexY", m.GetProperty("upperGridY").GetInt32());
                    moldings.Add(molding);
                }
            if (moldings.Count > 0) room["_moldingHousingObjectSaveDataList"] = moldings;
            rooms.Add(room);
        }

        // One 8 x 8 sub-grid per 8 cells: grid (x, z) is cell (z mod 8) * 8 + (x mod 8) of sub-grid (x div 8, z div 8).
        var grids = new Dictionary<(int X, int Z), int[]>();
        foreach (var t in root.GetProperty("pathTiles").EnumerateArray())
        {
            var g = t.GetProperty("savedGrid"); int x = g[0].GetInt32(), z = g[1].GetInt32();
            var at = (X: (int)Math.Floor(x / 8.0), Z: (int)Math.Floor(z / 8.0));
            if (!grids.TryGetValue(at, out var cells)) grids[at] = cells = new int[64];
            int cell = (z - at.Z * 8) * 8 + (x - at.X * 8);
            if (cells[cell] != 0) throw new InvalidDataException("The house file is damaged.");
            cells[cell] = t.GetProperty("partKey").GetInt32();
        }
        var paths = new JsonArray();
        foreach (var (at, cells) in grids)
        {
            var grid = Typed("HousingPlacedPathSubGridSaveData");
            if (at.X != 0 || at.Z != 0) grid["_subGridIndex"] = new JsonArray(at.X, at.Z);
            grid["_placedHousingObjectKeyList"] = new JsonArray(cells.Select(c => (JsonNode)c).ToArray());
            paths.Add(grid);
        }

        var house = new JsonObject();
        void List(string name, JsonArray rows) { if (rows.Count > 0) house[name] = rows; }
        List("_housingObjectSaveDataList", objects);
        List("_fenceHousingObjectSaveDataList", fences);
        List("_roofHousingObjectSaveDataList", roofs);
        List("_stairHousingObjectSaveDataList", stairs);
        List("_customHouseRoomSaveDataList", rooms);
        List("_housingPlacedPathSubGridSaveDataList", paths);
        uint? plot = root.TryGetProperty("plotCandidate", out var candidate) && candidate.ValueKind == JsonValueKind.Number ? candidate.GetUInt32() : null;
        // Through text, so values read back the way a parsed .suhouse reads.
        return new((JsonObject)JsonNode.Parse(house.ToJsonString())!, plot, null, "layout", null, new(curved, holes));
    }

    private static JsonObject Typed(string type) => new() { ["$type"] = type };
    // The game leaves zero fields out of a row, so a converted row does too.
    private static void Put(JsonObject o, string name, int value) { if (value != 0) o[name] = value; }
    private static void PutInts(JsonObject o, string name, JsonElement values)
    {
        var a = values.EnumerateArray().Select(v => v.GetInt32()).ToArray();
        if (a.Any(v => v != 0)) o[name] = new JsonArray(a.Select(v => (JsonNode)v).ToArray());
    }
    private static int Key(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static (int X, int Z) Turn(int x, int z, int turns) { for (int i = 0; i < turns; i++) (x, z) = (z, -x); return (x, z); }

    private static JsonObject Part(JsonElement p, int turns)
    {
        var part = Typed("HousingObjectSaveData");
        Put(part, "_housingObjectKey", p.GetProperty("partKey").GetInt32());
        Put(part, "_floor", p.GetProperty("floorIndex").GetInt32());
        Put(part, "_rotationStep", p.GetProperty("rotationStep").GetInt32());
        int placement = p.GetProperty("placementType").GetInt32();
        if (turns % 2 == 1) placement = placement switch { 1 => 2, 2 => 1, 3 => 4, 4 => 3, _ => placement };
        Put(part, "_placementType", placement);
        int[] min = p.GetProperty("minGrid").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        int[] max = p.GetProperty("maxGrid").EnumerateArray().Select(v => v.GetInt32()).ToArray();
        var (ax, az) = Turn(min[0], min[2], turns); var (bx, bz) = Turn(max[0], max[2], turns);
        PutGrid(part, "_minLocalIndex", [Math.Min(ax, bx), min[1], Math.Min(az, bz)]);
        PutGrid(part, "_maxLocalIndex", [Math.Max(ax, bx), max[1], Math.Max(az, bz)]);
        PutGrid(part, "_overridedHalfMeterSize", p.GetProperty("halfMeterSize").EnumerateArray().Select(v => v.GetInt32()).ToArray());
        var t = p.GetProperty("plotRelativeTransform");
        float[] scale = Floats(t.GetProperty("scaleXYZ")), rotation = Floats(t.GetProperty("rotationXYZW")), position = Floats(t.GetProperty("translationXYZ"));
        if (turns != 0)
        {
            double x = position[0], z = position[2];
            for (int i = 0; i < turns; i++) (x, z) = (z, -x);
            (position[0], position[2]) = ((float)x, (float)z);
            // (0, s, 0, c) * rotation: a yaw of turns * 90 degrees before the part's own rotation.
            double s = Math.Sin(turns * Math.PI / 4), c = Math.Cos(turns * Math.PI / 4);
            double rx = rotation[0], ry = rotation[1], rz = rotation[2], rw = rotation[3];
            rotation = [(float)(c * rx + s * rz), (float)(c * ry + s * rw), (float)(c * rz - s * rx), (float)(c * rw - s * ry)];
        }
        part["_localTransform"] = new JsonArray(scale.Concat(rotation).Concat(position).Select(v => (JsonNode)v).ToArray());
        // A saved row always holds two material slots; this format drops trailing empty ones.
        var materials = p.GetProperty("materialKeys").EnumerateArray().Select(v => v.GetInt32()).ToList();
        if (materials.Count > 2) throw new InvalidDataException("The house file is damaged.");
        while (materials.Count < 2) materials.Add(0);
        part["_textureHousingObjectKeyList"] = new JsonArray(materials.Select(v => (JsonNode)v).ToArray());
        return part;
    }

    private static void PutGrid(JsonObject o, string name, int[] values) { if (values.Any(v => v != 0)) o[name] = new JsonArray(values.Select(v => (JsonNode)v).ToArray()); }
    private static float[] Floats(JsonElement values) => values.EnumerateArray().Select(v => v.GetSingle()).ToArray();
}
