using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SaveUltimate.Core.Saves;

/// <summary>
/// A house plot: the GamePlayTrigger key the save files a house under. Region is a ManagementCatalog region code, or empty for
/// the two Ocean plots, which go by Name. Kliff marks the two story houses; the others are numbered within their region.
/// Variable and Knowledge are what activating the plot sets in the game (House_Construction_* and Knowledge_Node_*_Housing_*),
/// 0 for plots the editor does not activate.
/// </summary>
public sealed record HousingPlotInfo(uint Key, string Region, int Number, bool Kliff, string Name = "", uint Variable = 0, uint Knowledge = 0)
{
    /// <summary>The 19 plots DefaultHouse_Lv4 (free construction) lists in houseinfo (2.05.01).</summary>
    public static readonly HousingPlotInfo[] All =
    [
        new(1000257, "Her", 0, true), new(1000258, "Kwe", 0, true),
        Site(1000463, "Her", 1, 1006843), Site(1000464, "Her", 2, 1006844), Site(1000465, "Her", 3, 1006845),
        Site(1000466, "Dem", 1, 1006850), Site(1000467, "Dem", 2, 1006851), Site(1000468, "Dem", 3, 1006852),
        Site(1000469, "Del", 1, 1006853), Site(1000470, "Del", 2, 1006854), Site(1000471, "Del", 3, 1006855),
        Site(1000472, "Cal", 1, 1006846), Site(1000473, "Cal", 2, 1006847),
        Site(1000474, "Kwe", 1, 1006848), Site(1000475, "Kwe", 2, 1006849),
        Site(1000476, "Crim", 1, 1006856), Site(1000477, "Crim", 2, 1006857),
        new(1000485, "", 0, false, "Pragma"), new(1000486, "", 0, false, "Estra"),
    ];
    // The 15 DLC plots: House_Construction_* runs 1000095..1000109 in plot order (each one seen set with its plot in saves);
    // the knowledge is the Knowledge_Node_<region>_Housing_<number> of the same name as the plot's trigger.
    private static HousingPlotInfo Site(uint key, string region, int number, uint knowledge) => new(key, region, number, false, "", key - 1000463 + 1000095, knowledge);
    public static HousingPlotInfo Find(uint key) => All.FirstOrDefault(p => p.Key == key) ?? new(key, "", 0, false, key.ToString());
    // The knowledge whose name the game gives the place: the plot's own for the DLC plots, the island's for the two Ocean plots.
    public uint NameKnowledge => Knowledge != 0 ? Knowledge : Key switch { 1000485 => 1006364u, 1000486 => 1006405u, _ => 0u };
}

/// <summary>What stands on an active plot: one of the ready-built houses (houseinfo Lv1 to Lv3, and Pailune's) or free construction.</summary>
public enum HouseKind { None, Small, Medium, Large, Free }

/// <summary>How many rows each part list of a house holds; path tiles are the filled cells of its sub-grids. Decorations are
/// the part rows with placement 0 (furniture and ornaments, placed from inventory items), counted within Parts.</summary>
public sealed record HouseCounts(int Parts, int Fences, int Roofs, int Stairs, int CurvedWalls, int Holes, int Rooms, int PathTiles, int Decorations = 0)
{
    public int Total => Parts + Fences + Roofs + Stairs + CurvedWalls + Holes + Rooms + PathTiles;
    /// <summary>What the build mode puts up: walls, floors, doors and the like, fences, roofs, stairs, curved walls and path
    /// tiles. Rooms (named areas) and hole anchors are not objects.</summary>
    public int Structures => Parts - Decorations + Fences + Roofs + Stairs + CurvedWalls + PathTiles;
}

/// <summary>
/// A plot the save knows: owned (in the player's house list) and, once switched to free construction and built on, holding a
/// house row (FreeBuild). A DLC plot the player has not activated yet is listed too, so the editor can activate it. Placed counts
/// the furniture and path tiles the game keeps on the plot outside a house row, which a ready-built house may hold.
/// </summary>
public sealed record HousingPlot(HousingPlotInfo Info, bool Owned, ushort? HouseKey, bool FreeBuild, HouseCounts Counts, int Placed = 0)
{
    // DefaultHouse_Lv4, the house key a plot switched to free construction holds.
    public const ushort FreeHouse = 16971;
    public uint Key => Info.Key;
    public bool Active => Owned || FreeBuild;
    public bool CanActivate => !Active && Info.Variable != 0;
    // An import switches a ready-built house to free construction itself, but leaves furniture standing on the plot to the game.
    public bool CanImport => FreeBuild || (Owned && Placed == 0);
    public HouseKind Kind => FreeBuild || HouseKey == FreeHouse ? HouseKind.Free : HouseKey switch
    {
        16967 => HouseKind.Small, 16968 => HouseKind.Medium, 16969 or 16970 => HouseKind.Large, _ => HouseKind.None,
    };
}

/// <summary>
/// One free-construction house, plot-independent. House holds the plot row's lists as the save has them, keyed by the game's
/// field names; every object names its type in "$type". Grid and positions are in the plot's own frame, the build box every plot
/// shares, so a house fits any plot. Source says which format the file was; SourcePlot is where it was built.
/// Types holds the schema of the types the house uses as the source save had them, so a save that never held one can take it.
/// LeftOut counts what the file had but the layout could not carry (curved walls and floor openings).
/// </summary>
public sealed record HouseLayout(JsonObject House, uint? SourcePlot, string? GameVersion, string Source, IReadOnlyList<ParcType>? Types = null, HouseLeftOut? LeftOut = null)
{
    public HouseCounts Counts => SaveDocument.CountHouse(House);
}

/// <summary>Parts a layout file holds whose save rows are not known yet, so an import leaves them out.</summary>
public sealed record HouseLeftOut(int CurvedWalls, bool Holes)
{
    public bool Any => CurvedWalls > 0 || Holes;
}

public sealed partial class SaveDocument
{
    private const string HouseBlock = "CustomHouseSaveData";
    private const string HouseRows = "_customHouseElementSaveDataList";
    private const string HouseOwner = "_ownerGamePlayTriggerInfoKey";
    // The build box every plot holds in its level (system_boundingbox_cube256, 28 x 20 x 24 m), in half meters about the plot origin.
    internal const int BoxHalfX = 28, BoxHalfZ = 24;

    private static ParcType Type(string name, params (string Name, string Type, ushort Kind, ushort Size, uint Auxiliary)[] fields)
        => new(name, fields.Select((f, i) => new ParcField(f.Name, f.Type, f.Kind, f.Size, f.Auxiliary, i)).ToArray());

    // A save holds only the types it has used, so one that never had a stair has no stair type. These are the house types as a
    // 2.05.01 save defines them; an import appends the ones the save lacks. The game reads a save's schema by type name.
    internal static readonly ParcType[] KnownHouseTypes =
    [
        Type("CustomHouseSaveData", ("_customHouseElementSaveDataList", "ReflectObject", 6, 0, 4104)),
        Type("CustomHouseElementSaveData", ("_ownerGamePlayTriggerInfoKey", "GamePlayTriggerInfoKey", 0, 4, 0), ("_housingObjectSaveDataList", "ReflectObject", 6, 0, 4104),
            ("_curvedWallHousingObjectSaveDataList", "ReflectObject", 6, 0, 4104), ("_fenceHousingObjectSaveDataList", "ReflectObject", 6, 0, 4104),
            ("_roofHousingObjectSaveDataList", "ReflectObject", 6, 0, 4104), ("_stairHousingObjectSaveDataList", "ReflectObject", 6, 0, 4104),
            ("_customHouseRoomSaveDataList", "ReflectObject", 6, 0, 4104), ("_customHouseHoleAnchorSaveDataList", "ReflectObject", 6, 0, 4104),
            ("_housingPlacedPathSubGridSaveDataList", "ReflectObject", 6, 0, 4104)),
        Type("HousingObjectSaveData", ("_housingObjectKey", "HousingObjectKey", 0, 2, 0), ("_floor", "int32", 0, 4, 0), ("_rotationStep", "uint32", 0, 4, 0),
            ("_placementType", "HousingPlaceType", 2, 1, 0), ("_minLocalIndex", "int3", 0, 12, 0), ("_maxLocalIndex", "int3", 0, 12, 0),
            ("_overridedHalfMeterSize", "int2", 0, 8, 0), ("_localTransform", "Transform", 0, 40, 0), ("_textureHousingObjectKeyList", "HousingObjectKey", 3, 2, 4096)),
        Type("FenceHousingObjectSaveData", ("_housingObjectSaveData", "ReflectObject", 4, 8, 8), ("_isUsedForRoomBoundary", "bool", 0, 1, 0)),
        Type("RoofHousingObjectSaveData", ("_housingObjectSaveData", "ReflectObject", 4, 8, 8), ("_roofDeformCurveKeyR", "float", 0, 4, 0)),
        Type("StairHousingObjectSaveData", ("_housingObjectSaveData", "ReflectObject", 4, 8, 8), ("_stairLocalLowerLandingIndex", "int3", 0, 12, 0),
            ("_stairLocalUpperLandingIndex", "int3", 0, 12, 0), ("_stairLowerFloor", "int32", 0, 4, 0), ("_stairUpperFloor", "int32", 0, 4, 0),
            ("_stairInstanceCount", "int32", 0, 4, 0), ("_stairWidth", "int32", 0, 4, 0), ("_stairRailingHousingObjectKey", "HousingObjectKey", 0, 2, 0),
            ("_hasLeftStairRailing", "bool", 0, 1, 0), ("_hasRightStairRailing", "bool", 0, 1, 0)),
        Type("CustomHouseRoomSaveData", ("_floor", "int32", 0, 4, 0), ("_localAnchorIndex", "int2", 0, 8, 0), ("_wallScaleY", "int32", 0, 4, 0),
            ("_floorOffsetIndexY", "int32", 0, 4, 0), ("_hasCeiling", "bool", 0, 1, 0), ("_floorTextureHousingObjectKey", "HousingObjectKey", 0, 2, 0),
            ("_ceilingTextureHousingObjectKey", "HousingObjectKey", 0, 2, 0), ("_moldingHousingObjectSaveDataList", "ReflectObject", 6, 0, 4104)),
        Type("MoldingHousingObjectSaveData", ("_moldingHousingObjectKey", "HousingObjectKey", 0, 2, 0), ("_localPositionY", "float", 0, 4, 0),
            ("_minIndexY", "int32", 0, 4, 0), ("_maxIndexY", "int32", 0, 4, 0)),
        Type("HousingPlacedPathSubGridSaveData", ("_subGridIndex", "int2", 0, 8, 0), ("_placedHousingObjectKeyList", "HousingObjectKey", 3, 2, 4096)),
    ];

    internal static bool SameType(ParcType a, ParcType b) => a.Name == b.Name &&
        a.Fields.Select(f => (f.Name, f.TypeName, f.Kind, f.Size, f.Auxiliary)).SequenceEqual(b.Fields.Select(f => (f.Name, f.TypeName, f.Kind, f.Size, f.Auxiliary)));

    private static SaveField? HouseRow(byte[] raw, ParcLayout layout, uint plot)
    {
        if (!layout.Blocks.Any(b => b.Name == HouseBlock)) return null;
        var rows = SaveRecords.Read(raw, layout, HouseBlock).Fields.Named(HouseRows);
        var found = rows.Present ? rows.ListElements.Where(e => e.ChildFields.Named(HouseOwner).Scalar(raw, "GamePlayTriggerInfoKey", 4) == plot).ToArray() : [];
        return found.Length switch { 0 => null, 1 => found[0], _ => throw new InvalidDataException("A plot has two house rows.") };
    }

    /// <summary>Every plot the save owns or has a free-construction row for, in catalog order.</summary>
    public HousingPlot[] ReadHousing()
    {
        var owned = new Dictionary<uint, ushort>();
        // Saves before houses (2.03) and before the first house have no house list at all.
        var houses = HouseList(blob, Layout);
        if (houses is not null)
            foreach (var h in houses.ListElements)
            {
                var c = h.ChildFields;
                owned[(uint)c.Named("_gamePlayTriggerInfoKey").Scalar(blob, "GamePlayTriggerInfoKey", 4)] =
                    c.Named("_houseKey") is { Present: true } k ? (ushort)k.Scalar(blob, "HouseKey", 2) : (ushort)0;
            }
        var built = new Dictionary<uint, HouseCounts>();
        if (Layout.Blocks.Any(b => b.Name == HouseBlock) && SaveRecords.Read(blob, Layout, HouseBlock).Fields.Named(HouseRows) is { Present: true } rows)
            foreach (var row in rows.ListElements)
                built[(uint)row.ChildFields.Named(HouseOwner).Scalar(blob, "GamePlayTriggerInfoKey", 4)] = CountHouse(HouseJson(blob, row));
        // Once the save has a house, the DLC plots it has not activated are listed for activation.
        var sites = houses is null || DlcContent.Hidden ? [] : HousingPlotInfo.All.Where(p => p.Variable != 0).Select(p => p.Key);
        var keys = owned.Keys.Union(built.Keys).Union(sites).OrderBy(k => Array.FindIndex(HousingPlotInfo.All, p => p.Key == k) is >= 0 and var i ? i : int.MaxValue).ThenBy(k => k);
        var placed = owned.Keys.Any(k => !built.ContainsKey(k)) ? PlacedOnPlots(blob, Layout) : [];
        return keys.Select(k => new HousingPlot(HousingPlotInfo.Find(k), owned.ContainsKey(k), owned.TryGetValue(k, out var house) ? house : null,
            built.ContainsKey(k), built.GetValueOrDefault(k) ?? new(0, 0, 0, 0, 0, 0, 0, 0), placed.GetValueOrDefault(k))).ToArray();
    }

    // Furniture the field keeps on a plot (FieldGimmickSaveData._housingPlacedGamePlayTriggerInfoKey; switching Pailune's house to
    // free construction in the game took its six away) and path tiles kept outside a house row (HousingPlacedPathSaveData, 2.04).
    private static Dictionary<uint, int> PlacedOnPlots(byte[] raw, ParcLayout layout)
    {
        var placed = new Dictionary<uint, int>();
        void Count(SaveField f, string name, string type)
        {
            if (f.Name == name && f.Present && f.MetaKind == 0 && f.MetaSize == 4 && f.TypeName == type)
                placed[(uint)f.Scalar(raw, type, 4)] = placed.GetValueOrDefault((uint)f.Scalar(raw, type, 4)) + 1;
            foreach (var c in f.ChildFields) Count(c, name, type);
            foreach (var e in f.ListElements) Count(e, name, type);
        }
        foreach (var (block, field) in new[] { ("FieldSaveData", "_housingPlacedGamePlayTriggerInfoKey"), ("HousingPlacedPathSaveData", "_placedGamePlayTriggerInfoKey") })
            if (layout.Blocks.Any(b => b.Name == block))
                foreach (var o in SaveRecords.ReadObjects(raw, layout, block))
                    foreach (var f in o.Fields) Count(f, field, "GamePlayTriggerInfoKey");
        return placed;
    }

    private const string MiscBlock = "ContentsMiscSaveData", VariableBlock = "GamePlayVariableSaveData", KnowledgeBlock = "KnowledgeSaveData";
    private const string PhaseList = "_activatedContentsPhaseGamePlayTriggerInfoKeyList";
    // DefaultHouse_Lv1, the house a plot's construction work leaves.
    private const ushort FirstHouse = 16967;

    private static SaveField? HouseList(byte[] raw, ParcLayout layout)
        => layout.Blocks.Any(b => b.Name == MiscBlock) && layout.Types.Any(t => t.Name == "PlayerHouseSaveData") &&
           SaveRecords.Read(raw, layout, MiscBlock).Fields.FirstOrDefault(f => f.Name == "_playerHouseList") is { Present: true } list ? list : null;

    private static uint[] PhaseKeys(byte[] raw, SaveField field)
    {
        if (field.MetaKind != 3 || field.MetaSize != 4 || field.TypeName != "GamePlayTriggerInfoKey") throw new InvalidDataException("Unsupported activated contents list.");
        if (!field.Present) return [];
        int count = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(field.StartOffset + 1));
        if (count < 0 || field.EndOffset - field.StartOffset != 5 + 4L * count) throw new InvalidDataException("Invalid activated contents list.");
        return Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(field.StartOffset + 5 + 4 * i))).ToArray();
    }

    private static Dictionary<uint, bool> Variables(byte[] raw, SaveField list)
        => list.Elements("GamePlayVariableElementSaveData").ToDictionary(r => (uint)r.ChildFields.Named("_gamePlayVariableKey").Scalar(raw, "GamePlayVariableKey", 4),
            r => r.ChildFields.Named("_currentVariable") is { Present: true } v && v.Scalar(raw, "bool", 1) != 0);

    /// <summary>
    /// Activates DLC plots the way the game does once the player finds the plot and the mercenaries finish its construction work
    /// (2.05.01 saves before and after): the plot's knowledge is learned, the plot joins the activated contents list and the house
    /// list with DefaultHouse_Lv1, and its House_Construction variable turns on. Switching to free construction stays in the game.
    /// </summary>
    public HousingPlot[] ActivatePlots(IEnumerable<uint> plots)
    {
        var sites = plots.Distinct().Select(HousingPlotInfo.Find).ToArray();
        if (sites.Length == 0 || sites.Any(p => p.Variable == 0)) throw new InvalidOperationException("Only the DLC house plots can be activated.");
        DlcContent.Refuse(true);
        var houses = HouseList(blob, Layout) ?? throw new InvalidOperationException("This save has no house yet; get the first house in the game.");
        var before = ReadHousing();
        sites = sites.Where(p => !before.Any(h => h.Key == p.Key && (h.Owned || h.FreeBuild))).ToArray();
        if (sites.Length == 0) return before;

        var houseType = Layout.Types[Layout.TypeIndex("PlayerHouseSaveData")];
        var variableType = Layout.Types.FirstOrDefault(t => t.Name == "GamePlayVariableElementSaveData");
        if (!houseType.Is(("_gamePlayTriggerInfoKey", "GamePlayTriggerInfoKey", 0, 4), ("_ownerCharacterKey", "CharacterKey", 0, 4), ("_houseKey", "HouseKey", 0, 2)) ||
            variableType is null || !variableType.Is(("_gamePlayVariableKey", "GamePlayVariableKey", 0, 4), ("_currentVariable", "bool", 0, 1)))
            throw new InvalidDataException("Unsupported house or variable layout.");
        var misc = SaveRecords.Read(blob, Layout, MiscBlock);
        var phase = misc.Fields.Named(PhaseList);
        uint[] phaseBefore = PhaseKeys(blob, phase);
        var variableList = SaveRecords.Read(blob, Layout, VariableBlock).Fields.Named("_gamePlayVariableElementSaveData");
        if (!variableList.Present) throw new InvalidDataException("This save has no gameplay variables.");
        var variablesBefore = Variables(blob, variableList);
        var knowledge = SaveRecords.Read(blob, Layout, KnowledgeBlock);
        var levels = KnowledgeLevels(blob, knowledge);

        var edits = new List<ListEdit>
        {
            new(houses, houseType.Name, "_gamePlayTriggerInfoKey", sites.ToDictionary(p => p.Key, p => new Dictionary<string, ScalarValue>
                { ["_gamePlayTriggerInfoKey"] = new("GamePlayTriggerInfoKey", 4, p.Key), ["_houseKey"] = new("HouseKey", 2, FirstHouse) })),
        };
        var variables = sites.Where(p => !variablesBefore.GetValueOrDefault(p.Variable)).ToDictionary(p => p.Variable, p => new Dictionary<string, ScalarValue>
            { ["_gamePlayVariableKey"] = new("GamePlayVariableKey", 4, p.Variable), ["_currentVariable"] = new("bool", 1, 1) });
        if (variables.Count > 0) edits.Add(new(variableList, variableType.Name, "_gamePlayVariableKey", variables));
        // The game holds a found plot's knowledge at its last level (2).
        var learned = sites.Where(p => levels.GetValueOrDefault(p.Knowledge) == 0).ToDictionary(p => p.Knowledge, p => KnowledgeCatalog.Shared.LevelCounts.GetValueOrDefault(p.Knowledge, 1u));
        var paths = NewLearnPaths(knowledge, levels, learned, SkillState.Read(blob, knowledge).Follow);
        if (learned.Count > 0) edits.AddRange(KnowledgeEdits(knowledge, levels, learned, paths));

        // The activated list is an array on the block itself; an absent one also needs its presence bit.
        uint[] phaseAfter = [.. phaseBefore, .. sites.Select(p => p.Key).Where(k => !phaseBefore.Contains(k))];
        var regions = RecordLists.Regions(blob, Layout, edits).ToList();
        if (phaseAfter.Length != phaseBefore.Length)
        {
            regions.Add((phase.StartOffset, phase.EndOffset, _ => RecordLists.ArrayBytes(phaseAfter)));
            if (!phase.Present)
            {
                int bit = Layout.Blocks.Single(b => b.Name == MiscBlock).Offset + 2 + phase.FieldIndex / 8;
                byte mask = (byte)(blob[bit] | (1 << (phase.FieldIndex % 8)));
                regions.Add((bit, bit + 1, _ => [mask]));
            }
        }
        byte[] candidate = RecordLists.RewriteRegions(blob, EditBuffer, regions);
        Transact(candidate, [MiscBlock, VariableBlock, KnowledgeBlock], (written, _) =>
        {
            var after = HouseList(candidate, written)!.ListElements;
            var owned = after.ToDictionary(h => (uint)h.ChildFields.Named("_gamePlayTriggerInfoKey").Scalar(candidate, "GamePlayTriggerInfoKey", 4),
                h => h.ChildFields.Named("_houseKey") is { Present: true } k ? k.Scalar(candidate, "HouseKey", 2) : 0);
            var vars = Variables(candidate, SaveRecords.Read(candidate, written, VariableBlock).Fields.Named("_gamePlayVariableElementSaveData"));
            var known = KnowledgeLevels(candidate, SaveRecords.Read(candidate, written, KnowledgeBlock));
            if (after.Length != houses.ListElements.Length + sites.Length || sites.Any(p => owned.GetValueOrDefault(p.Key) != FirstHouse || !vars.GetValueOrDefault(p.Variable) || known.GetValueOrDefault(p.Knowledge) == 0) ||
                vars.Count != variablesBefore.Count + variables.Keys.Count(k => !variablesBefore.ContainsKey(k)) || known.Count != levels.Count + learned.Count ||
                !PhaseKeys(candidate, SaveRecords.Read(candidate, written, MiscBlock).Fields.Named(PhaseList)).SequenceEqual(phaseAfter))
                throw new InvalidDataException("Plot activation verification failed.");
        });
        return ReadHousing();
    }

    /// <summary>The plot's house as a plot-independent layout.</summary>
    public HouseLayout ExportHouse(uint plot)
    {
        var row = HouseRow(blob, Layout, plot) ?? throw new InvalidOperationException("This plot has no free-construction house.");
        var house = HouseJson(blob, row);
        var types = UsedHouseTypes(house).Select(t => Layout.Types[Layout.TypeIndex(t)]).ToArray();
        return new(house, plot, GameVersionText(SavedGameVersion(blob, Layout)), "suhouse", types);
    }

    /// <summary>
    /// Replaces the plot's whole house with the layout. Any active plot takes it: a ready-built house is switched to free
    /// construction the way the game does (its house key becomes DefaultHouse_Lv4, as saves before and after the switch show) and given its house
    /// row, in a new CustomHouseSaveData block if the save has never had one, where the game puts it. Furniture still standing on a
    /// ready-built house is left to the game, so such a plot is refused. Types the layout uses that the save has never held are
    /// appended to its schema, from the editor's own definitions or else the file's.
    /// </summary>
    public HousingPlot[] ImportHouse(uint plot, HouseLayout layout)
    {
        var target = ReadHousing().FirstOrDefault(p => p.Key == plot);
        if (target is not { Active: true }) throw new InvalidOperationException("Activate this plot first.");
        if (!target.CanImport) throw new InvalidOperationException("Furniture still stands on this house; take it in or switch to free construction in the game first.");
        var (added, unknown) = HouseTypes(layout);
        if (unknown.Length > 0) throw new InvalidDataException("The house uses part types this editor does not know: " + string.Join(", ", unknown) + ".");
        CheckBuildBox(layout.House);
        string wanted = layout.House.ToJsonString();
        var row = HouseRow(blob, Layout, plot);
        if (row is not null && HouseJson(blob, row).ToJsonString() == wanted && target.HouseKey is null or HousingPlot.FreeHouse) return ReadHousing();
        bool hadBlock = Layout.Blocks.Any(b => b.Name == HouseBlock);
        int plots = hadBlock && SaveRecords.Read(blob, Layout, HouseBlock).Fields.Named(HouseRows) is { Present: true } before ? before.ListElements.Length : 0;

        byte[] grown = RecordLists.AddTypes(blob, added);
        var grownLayout = added.Length == 0 ? Layout : ParcLayout.Parse(grown);
        var regions = new List<(int Start, int End, Func<int, byte[]> Encode)>();
        // A ready-built house becomes free construction: only its house key changes.
        bool switches = target.Owned && target.HouseKey != HousingPlot.FreeHouse;
        if (switches)
        {
            var key = HouseList(grown, grownLayout)!.ListElements.Single(h => h.ChildFields.Named("_gamePlayTriggerInfoKey").Scalar(grown, "GamePlayTriggerInfoKey", 4) == plot).ChildFields.Named("_houseKey");
            if (!key.Present || key.EndOffset - key.StartOffset != 2) throw new InvalidDataException("Unsupported house key.");
            regions.Add((key.StartOffset, key.EndOffset, _ => BitConverter.GetBytes(HousingPlot.FreeHouse)));
        }
        if (HouseRow(grown, grownLayout, plot) is { } existing)
            regions.Add((existing.StartOffset, existing.EndOffset, start => HouseRowBytes(grownLayout, plot, layout.House, start)));
        else if (hadBlock)
        {
            // A new row goes at the end of the list, which is the whole block after its header.
            var houseBlock = grownLayout.Blocks.Single(b => b.Name == HouseBlock);
            var list = SaveRecords.Read(grown, grownLayout, HouseBlock).Fields.Named(HouseRows);
            if (list.Present)
            {
                regions.Add((list.StartOffset + 1, list.StartOffset + 5, _ => BitConverter.GetBytes(list.ListElements.Length + 1)));
                regions.Add((list.EndOffset, list.EndOffset, start => HouseRowBytes(grownLayout, plot, layout.House, start)));
            }
            else
            {
                int bit = houseBlock.Offset + 2 + list.FieldIndex / 8;
                regions.Add((bit, bit + 1, _ => [(byte)(grown[bit] | (1 << (list.FieldIndex % 8)))]));
                regions.Add((list.StartOffset, list.EndOffset, start => HouseListBytes(grownLayout, plot, layout.House, start)));
            }
        }
        byte[] candidate = RecordLists.RewriteRegions(grown, EditBuffer, regions);
        if (!hadBlock)
        {
            var type = grownLayout.Types[grownLayout.TypeIndex(HouseBlock)];
            if (!type.Is((HouseRows, "ReflectObject", 6, 0))) throw new InvalidDataException("Unsupported house block layout.");
            // The block as the game first writes it: u16 mask size, the mask, u32 0, then the list.
            candidate = RecordLists.AddBlock(candidate, HouseBlock, "FactionSaveData", start => [1, 0, 1, 0, 0, 0, 0, .. HouseListBytes(grownLayout, plot, layout.House, start + 7)]);
        }
        Transact(candidate, switches ? [HouseBlock, MiscBlock] : [HouseBlock], (written, _) =>
        {
            // The row reads back as the layout, the other plots keep their rows, the house is free construction, and added types are exactly the ones meant.
            var back = HouseRow(candidate, written, plot) ?? throw new InvalidDataException("The house row went missing.");
            if (HouseJson(candidate, back).ToJsonString() != wanted) throw new InvalidDataException("House import verification failed.");
            if (SaveRecords.Read(candidate, written, HouseBlock).Fields.Named(HouseRows).ListElements.Length != plots + (row is null ? 1 : 0)) throw new InvalidDataException("House import changed the plot count.");
            if (written.Types.Count != Layout.Types.Count + added.Length || !written.Types.Skip(Layout.Types.Count).Zip(added).All(p => SameType(p.First, p.Second)))
                throw new InvalidDataException("House import changed the schema unexpectedly.");
            var houses = HouseList(candidate, written)?.ListElements ?? [];
            if (target.Owned && !houses.Any(h => h.ChildFields.Named("_gamePlayTriggerInfoKey").Scalar(candidate, "GamePlayTriggerInfoKey", 4) == plot &&
                h.ChildFields.Named("_houseKey") is { Present: true } k && k.Scalar(candidate, "HouseKey", 2) == HousingPlot.FreeHouse))
                throw new InvalidDataException("House import left the plot out of free construction.");
        }, addsTypes: added.Length > 0, addsBlock: hadBlock ? null : HouseBlock);
        return ReadHousing();
    }

    // A house list holding the one plot row: 00, u32 count, 13 zero bytes, then the row.
    private static byte[] HouseListBytes(ParcLayout layout, uint plot, JsonObject house, int start)
        => [0, 1, 0, 0, 0, .. new byte[13], .. HouseRowBytes(layout, plot, house, start + 18)];

    /// <summary>Part types the layout uses that this save has never held, which an import adds, as the game names them.</summary>
    public string[] AddedHouseTypes(HouseLayout layout) => HouseTypes(layout).Added.Select(t => t.Name).Where(n => n is not HouseBlock and not "CustomHouseElementSaveData").ToArray();

    /// <summary>Part types the layout uses that neither this save nor the editor nor the file defines; such a house cannot go in.</summary>
    public string[] UnknownHouseTypes(HouseLayout layout) => HouseTypes(layout).Unknown;

    // The house's own types and, for a save that has never had a free-construction house, the block and row types it needs.
    private (ParcType[] Added, string[] Unknown) HouseTypes(HouseLayout layout)
    {
        var added = new List<ParcType>(); var unknown = new List<string>();
        foreach (string name in new[] { HouseBlock, "CustomHouseElementSaveData" }.Concat(UsedHouseTypes(layout.House)))
        {
            if (Layout.Types.Any(t => t.Name == name)) continue;
            if ((KnownHouseTypes.FirstOrDefault(t => t.Name == name) ?? layout.Types?.FirstOrDefault(t => t.Name == name)) is { } type) added.Add(type);
            else unknown.Add(name);
        }
        return (added.ToArray(), unknown.ToArray());
    }

    private static SortedSet<string> UsedHouseTypes(JsonObject house)
    {
        var used = new SortedSet<string>(StringComparer.Ordinal);
        void Walk(JsonNode? node)
        {
            if (node is JsonObject o)
            {
                if (o["$type"]?.GetValue<string>() is { } t) used.Add(t);
                foreach (var (_, v) in o) Walk(v);
            }
            else if (node is JsonArray a) foreach (var v in a) Walk(v);
        }
        Walk(house);
        return used;
    }

    private static string? GameVersionText(uint version) => version == 0 ? null : $"{version >> 16}.{(version >> 8) & 0xFF:00}.{version & 0xFF:00}";

    // Structures must stand in the build box; decorations (placement 0 in the object list), fences and path tiles may reach the yard.
    private static void CheckBuildBox(JsonObject house)
    {
        static bool Outside(JsonObject part)
        {
            int[] Grid(string name) => part[name] is JsonArray a ? a.Select(v => v!.GetValue<int>()).ToArray() : [0, 0, 0];
            int[] min = Grid("_minLocalIndex"), max = Grid("_maxLocalIndex");
            return min[0] < -BoxHalfX || max[0] > BoxHalfX || min[2] < -BoxHalfZ || max[2] > BoxHalfZ;
        }
        foreach (var (list, nested) in new[] { ("_housingObjectSaveDataList", false), ("_roofHousingObjectSaveDataList", true), ("_stairHousingObjectSaveDataList", true), ("_curvedWallHousingObjectSaveDataList", true) })
            if (house[list] is JsonArray rows)
                foreach (var r in rows.OfType<JsonObject>())
                {
                    var part = nested ? r["_housingObjectSaveData"] as JsonObject : r;
                    if (part is null) continue;
                    if (!nested && (part["_placementType"]?.GetValue<int>() ?? 0) == 0) continue;
                    if (Outside(part)) throw new InvalidDataException("A structure stands outside the plot's build area.");
                }
    }

    internal static HouseCounts CountHouse(JsonObject house)
    {
        int N(string name) => house[name] is JsonArray a ? a.Count : 0;
        int tiles = house["_housingPlacedPathSubGridSaveDataList"] is JsonArray grids
            ? grids.OfType<JsonObject>().Sum(g => g["_placedHousingObjectKeyList"] is JsonArray k ? k.Count(v => v?.ToJsonString() != "0") : 0) : 0;
        int decorations = house["_housingObjectSaveDataList"] is JsonArray parts
            ? parts.OfType<JsonObject>().Count(p => (p["_placementType"]?.GetValue<int>() ?? 0) == 0) : 0;
        return new(N("_housingObjectSaveDataList"), N("_fenceHousingObjectSaveDataList"), N("_roofHousingObjectSaveDataList"), N("_stairHousingObjectSaveDataList"),
            N("_curvedWallHousingObjectSaveDataList"), N("_customHouseHoleAnchorSaveDataList"), N("_customHouseRoomSaveDataList"), tiles, decorations);
    }

    // ---- the plot row as JSON and back. Fixed and array fields follow their schema type; objects carry "$type".

    // Parsed back from its text, so every value reads the same way whether it came from a save or a file.
    private static JsonObject HouseJson(byte[] raw, SaveField row)
    {
        var house = ObjectJson(raw, row);
        house.Remove("$type");
        house.Remove(HouseOwner);
        return (JsonObject)JsonNode.Parse(house.ToJsonString())!;
    }

    private static JsonObject ObjectJson(byte[] raw, SaveField o)
    {
        var json = new JsonObject { ["$type"] = o.ChildTypeName };
        foreach (var c in o.ChildFields.Where(c => c.Present))
            json[c.Name] = c.MetaKind switch
            {
                0 or 2 => ScalarJson(raw.AsSpan(c.StartOffset, c.EndOffset - c.StartOffset), c.TypeName),
                3 => ArrayJson(raw.AsSpan(c.StartOffset, c.EndOffset - c.StartOffset), c.TypeName, c.MetaSize),
                4 => ObjectJson(raw, c),
                6 => new JsonArray(c.ListElements.Select(e => (JsonNode)ObjectJson(raw, e)).ToArray()),
                _ => throw new InvalidDataException($"Unsupported house field {o.ChildTypeName}.{c.Name}."),
            };
        return json;
    }

    private static JsonArray ArrayJson(ReadOnlySpan<byte> bytes, string type, int size)
    {
        if (bytes.Length < 5 || bytes[0] != 0) throw new InvalidDataException("Unsupported house array.");
        int count = BinaryPrimitives.ReadInt32LittleEndian(bytes[1..]);
        if (5L + (long)count * size != bytes.Length) throw new InvalidDataException("House array size mismatch.");
        var a = new JsonArray();
        for (int i = 0; i < count; i++) a.Add(ScalarJson(bytes.Slice(5 + i * size, size), type));
        return a;
    }

    private static readonly Dictionary<string, (int Count, bool Float)> Vectors = new(StringComparer.Ordinal)
    {
        ["int2"] = (2, false), ["int3"] = (3, false), ["int4"] = (4, false),
        ["float2"] = (2, true), ["float3"] = (3, true), ["float4"] = (4, true), ["Transform"] = (10, true),
    };

    private static JsonNode ScalarJson(ReadOnlySpan<byte> b, string type)
    {
        if (Vectors.TryGetValue(type, out var v) && b.Length == v.Count * 4)
        {
            var a = new JsonArray();
            for (int i = 0; i < v.Count; i++)
                a.Add(v.Float ? JsonValue.Create(BinaryPrimitives.ReadSingleLittleEndian(b[(i * 4)..])) : JsonValue.Create(BinaryPrimitives.ReadInt32LittleEndian(b[(i * 4)..])));
            return a;
        }
        if (type == "float" && b.Length == 4) return JsonValue.Create(BinaryPrimitives.ReadSingleLittleEndian(b));
        if (type == "bool" && b.Length == 1 && b[0] <= 1) return JsonValue.Create(b[0] == 1);
        bool signed = type.StartsWith("int", StringComparison.Ordinal);
        return b.Length switch
        {
            1 => JsonValue.Create(signed ? (sbyte)b[0] : b[0]),
            2 => signed ? JsonValue.Create(BinaryPrimitives.ReadInt16LittleEndian(b)) : JsonValue.Create(BinaryPrimitives.ReadUInt16LittleEndian(b)),
            4 => signed ? JsonValue.Create(BinaryPrimitives.ReadInt32LittleEndian(b)) : JsonValue.Create(BinaryPrimitives.ReadUInt32LittleEndian(b)),
            8 => signed ? JsonValue.Create(BinaryPrimitives.ReadInt64LittleEndian(b)) : JsonValue.Create(BinaryPrimitives.ReadUInt64LittleEndian(b)),
            _ => JsonValue.Create("hex:" + Convert.ToHexString(b)),
        };
    }

    private static byte[] ScalarBytes(JsonNode? node, string type, int size, string where)
    {
        byte[] b = new byte[size];
        try
        {
            if (Vectors.TryGetValue(type, out var v) && size == v.Count * 4)
            {
                var a = node as JsonArray ?? throw new FormatException();
                if (a.Count != v.Count) throw new FormatException();
                for (int i = 0; i < v.Count; i++)
                    if (v.Float) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(i * 4), a[i]!.GetValue<float>());
                    else BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(i * 4), a[i]!.GetValue<int>());
                return b;
            }
            var value = node as JsonValue ?? throw new FormatException();
            if (type == "float" && size == 4) { BinaryPrimitives.WriteSingleLittleEndian(b, value.GetValue<float>()); return b; }
            if (type == "bool" && size == 1) { b[0] = value.GetValue<bool>() ? (byte)1 : (byte)0; return b; }
            if (value.TryGetValue<string>(out var text))
            {
                if (!text.StartsWith("hex:", StringComparison.Ordinal)) throw new FormatException();
                byte[] raw = Convert.FromHexString(text[4..]);
                if (raw.Length != size) throw new FormatException();
                return raw;
            }
            bool signed = type.StartsWith("int", StringComparison.Ordinal);
            switch (size)
            {
                case 1: b[0] = signed ? unchecked((byte)value.GetValue<sbyte>()) : value.GetValue<byte>(); break;
                case 2: if (signed) BinaryPrimitives.WriteInt16LittleEndian(b, value.GetValue<short>()); else BinaryPrimitives.WriteUInt16LittleEndian(b, value.GetValue<ushort>()); break;
                case 4: if (signed) BinaryPrimitives.WriteInt32LittleEndian(b, value.GetValue<int>()); else BinaryPrimitives.WriteUInt32LittleEndian(b, value.GetValue<uint>()); break;
                case 8: if (signed) BinaryPrimitives.WriteInt64LittleEndian(b, value.GetValue<long>()); else BinaryPrimitives.WriteUInt64LittleEndian(b, value.GetValue<ulong>()); break;
                default: throw new FormatException();
            }
            return b;
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or OverflowException or ArgumentException or NullReferenceException)
        {
            throw new InvalidDataException($"House value {where} does not fit {type}.");
        }
    }

    private static byte[] HouseRowBytes(ParcLayout layout, uint plot, JsonObject house, int start)
    {
        var row = (JsonObject)house.DeepClone();
        row["$type"] = "CustomHouseElementSaveData";
        row[HouseOwner] = plot;
        using var output = new MemoryStream();
        WriteHouseObject(output, start, layout, row);
        return output.ToArray();
    }

    // The object layout RecordLists.ObjectBytes writes, with arrays (kind 3) as well: 00, u32 count, the elements.
    private static void WriteHouseObject(MemoryStream s, int start, ParcLayout layout, JsonObject value)
    {
        string typeName = value["$type"]?.GetValue<string>() ?? throw new InvalidDataException("A house object has no type.");
        int typeIndex = layout.TypeIndex(typeName);
        var type = layout.Types[typeIndex];
        foreach (var (name, _) in value)
            if (name != "$type" && !type.Fields.Any(f => f.Name == name)) throw new InvalidDataException($"{typeName} has no field {name}.");
        byte[] mask = new byte[type.MaskSize];
        foreach (var f in type.Fields) if (value.ContainsKey(f.Name)) mask[f.Index / 8] |= (byte)(1 << (f.Index % 8));
        void U16(int x) => s.Write(BitConverter.GetBytes(checked((ushort)x)));
        void U32(long x) => s.Write(BitConverter.GetBytes(checked((uint)x)));
        U16(mask.Length); s.Write(mask); U16(typeIndex); s.WriteByte(0);
        s.Write(BitConverter.GetBytes(ulong.MaxValue));
        long payload = s.Position + 4;
        U32(start + payload); U32(0);
        foreach (var f in type.Fields)
        {
            if (!value.TryGetPropertyValue(f.Name, out var node)) { if (f.Kind is 3 or 6 or 7) s.WriteByte(1); continue; }
            string where = typeName + "." + f.Name;
            switch (f.Kind)
            {
                case 0 or 2 when f.Size > 0: s.Write(ScalarBytes(node, f.TypeName, f.Size, where)); break;
                case 3:
                    var items = node as JsonArray ?? throw new InvalidDataException($"House value {where} is not a list.");
                    s.WriteByte(0); U32(items.Count);
                    foreach (var item in items) s.Write(ScalarBytes(item, f.TypeName, f.Size, where));
                    break;
                case 4: WriteHouseObject(s, start, layout, node as JsonObject ?? throw new InvalidDataException($"House value {where} is not an object.")); break;
                case 6:
                    var rows = node as JsonArray ?? throw new InvalidDataException($"House value {where} is not a list.");
                    s.WriteByte(0); U32(rows.Count); s.Write(new byte[13]);
                    foreach (var r in rows) WriteHouseObject(s, start, layout, r as JsonObject ?? throw new InvalidDataException($"House value {where} is not an object."));
                    break;
                default: throw new InvalidDataException($"Unsupported house field {where}.");
            }
        }
        U32(s.Position - payload);
    }
}
