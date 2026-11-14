using System.Buffers.Binary;
using System.Text.Json.Serialization;

namespace SaveUltimate.Core.Saves;

public sealed record MapAction(string Group, Dictionary<string, string> Names, uint[] Keys, string Mode, string[] Scenes,
    [property: JsonPropertyName("journal_keys")] uint[] JournalKeys,
    [property: JsonPropertyName("count_keys")] uint[]? CountKeys,
    [property: JsonPropertyName("reference_pivots")] Dictionary<string, string>? Pivots,
    [property: JsonPropertyName("challenge_roots")] uint[]? ChallengeRoots,
    [property: JsonPropertyName("label_key")] string? LabelKey)
{
    [JsonPropertyName("label_ref")]
    public System.Text.Json.JsonElement[]? LabelRef { get; init; }
    // Abyss Cresset scene id -> the challenge mission it completes.
    [JsonPropertyName("scene_roots")]
    public Dictionary<string, uint>? SceneRoots { get; init; }
    // Bell scene id -> localized name by UI language.
    [JsonPropertyName("scene_names")]
    public Dictionary<string, Dictionary<string, string>>? SceneNames { get; init; }
    // A found cave, region, hidden space or shrine stands at its last level in natural saves, and the game checks it off only then.
    // Nodes keep level 1: their later levels vary between natural saves and their meaning is unknown. Spires, shadow sanctums,
    // witch hideouts and the Abyss also keep level 1: quests raise their later levels.
    private static readonly HashSet<string> FirstLevelOnly = ["Spires", "Shadow Sanctums", "Witch Hideouts", "The Abyss"];
    public bool FullLevel => Mode == "locations" && LabelKey != "map.node" && !FirstLevelOnly.Contains(Names["en"]);
    public uint TargetLevel(uint key) => FullLevel ? KnowledgeCatalog.Shared.LevelCounts.GetValueOrDefault(key, 1u) : 1;
    private static readonly MapAction[] shipped = Embedded.Load<MapAction[]>("MapActions.json");
    private static MapAction[]? baseGame;
    // Same actions in the same order either way, so an index means the same action.
    public static MapAction[] All => DlcContent.Hidden ? baseGame ??= shipped.Select(a => a.WithoutDlc(DlcContent.Shared)).ToArray() : shipped;
    private MapAction WithoutDlc(DlcContent dlc) => this with
    {
        Keys = Keys.Where(k => !dlc.Knowledge.Contains(k)).ToArray(), JournalKeys = JournalKeys.Where(k => !dlc.Knowledge.Contains(k)).ToArray(),
        CountKeys = CountKeys?.Where(k => !dlc.Knowledge.Contains(k)).ToArray(), Scenes = Scenes.Where(s => !dlc.Scenes.Contains(s)).ToArray(),
        Pivots = Pivots?.Where(p => !dlc.Scenes.Contains(p.Key)).ToDictionary(), SceneRoots = SceneRoots?.Where(p => !dlc.Scenes.Contains(p.Key)).ToDictionary(),
        SceneNames = SceneNames?.Where(p => !dlc.Scenes.Contains(p.Key)).ToDictionary(), ChallengeRoots = ChallengeRoots?.Where(r => !dlc.Missions.Contains(r)).ToArray()
    };
}
/// <summary>One map row to activate: a knowledge key, a scene, or (neither) the group's remaining challenges.</summary>
public sealed record MapEntry(int Action, uint? Key = null, string? Scene = null);
public sealed record MapDiscovery(byte? Completed, byte? Discovered, string? Pivot);
public sealed record MapFog(int Offset, byte[] Codes)
{
    public bool Full => Codes.All(c => c == 255);
    public bool Empty => Codes.All(c => c == 0);
}
public sealed record MapState(MapFog Fog, IReadOnlyDictionary<string, MapDiscovery> Discoveries,
    IReadOnlyDictionary<uint, uint> Knowledge, ChallengeState Progress);

public sealed partial class SaveDocument
{
    private static MapFog ReadFog(byte[] raw, ParcLayout layout)
    {
        var parent = SaveRecords.Read(raw, layout, "FogSaveData").Fields.Named("_fogRLESaveData");
        if (!parent.Present) throw new InvalidDataException("Unsupported fog record.");
        (int Offset, byte[] Bytes) Array(string name, string type, int size)
        {
            var f = parent.ChildFields.Named(name);
            if (!f.Present || f.TypeName != type || f.MetaKind != 3 || f.MetaSize != size ||
                f.StartOffset <= 0 || f.EndOffset > raw.Length || f.EndOffset - f.StartOffset < 5 || raw[f.StartOffset] != 0)
                throw new InvalidDataException("Unsupported fog array.");
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(f.StartOffset + 1));
            if (count == 0 || (long)count * size != f.EndOffset - f.StartOffset - 5) throw new InvalidDataException("Invalid fog length.");
            return (f.StartOffset + 5, raw.AsSpan(f.StartOffset + 5, (int)count * size).ToArray());
        }
        var jumps = Array("_jumpingTable", "uint32", 4);
        var codes = Array("_codeList", "uint8", 1);
        var lengths = Array("_lengthList", "uint8", 1);
        if (codes.Bytes.Length != lengths.Bytes.Length || lengths.Bytes.Contains((byte)0)) throw new InvalidDataException("Invalid fog runs.");
        // Slot k holds the cells expanded after (k+1)×stride runs, the last slot the total. The game strides 64 runs;
        // another editor writes 1024 slots, so the stride is derived from the slot count.
        int slots = jumps.Bytes.Length / 4, stride = slots <= lengths.Bytes.Length ? lengths.Bytes.Length / slots : 0;
        if (stride == 0) throw new InvalidDataException("Invalid fog jump table.");
        var expected = new List<uint>(); uint expanded = 0;
        for (int i = 0; i < lengths.Bytes.Length; i++) { expanded += lengths.Bytes[i]; if ((i + 1) % stride == 0 && expected.Count < slots) expected.Add(expanded); }
        // 64×64 tiles of 128 cells until 2.03; 2.04 (dlc_01) extends the grid to 109×109 tiles.
        if (expanded is not (524288 or 1520768)) throw new InvalidDataException("Unsupported fog dimensions.");
        expected[^1] = expanded;
        if (expected.Where((v, i) => v != BinaryPrimitives.ReadUInt32LittleEndian(jumps.Bytes.AsSpan(i * 4))).Any())
            throw new InvalidDataException("Invalid fog jump table.");
        return new(codes.Offset, codes.Bytes);
    }
    private static Dictionary<string, MapDiscovery> ReadDiscoveries(byte[] raw, SaveField list)
    {
        if (!list.Present) throw new InvalidDataException("Missing discoveries.");
        var result = new Dictionary<string, MapDiscovery>();
        foreach (var row in list.Elements("LevelGimmickSceneObjectElementSaveData"))
        {
            uint key = (uint)row.ChildFields.Named("_levelGimmickSceneObjectInfoKey").Scalar(raw, "LevelGimmickSceneObjectInfoKey", 4);
            string Hex(string name, string type, int size)
            {
                var f = row.ChildFields.Named(name);
                if (!f.Present || f.TypeName != type || f.MetaKind != 0 || f.MetaSize != size || f.EndOffset - f.StartOffset != size || f.StartOffset <= 0 || f.EndOffset > raw.Length)
                    throw new InvalidDataException("Unsupported discovery field.");
                return Convert.ToHexString(raw.AsSpan(f.StartOffset, size)).ToLowerInvariant();
            }
            byte? Flag(string name)
            {
                var f = row.ChildFields.Named(name);
                if (!f.Present) return null;
                ulong value = f.Scalar(raw, "bool", 1);
                return value <= 1 ? (byte)value : throw new InvalidDataException("Invalid discovery flag.");
            }
            string identity = $"{key}:{Hex("_sceneObjectUuid", "uint4", 16)}";
            string? pivot = row.ChildFields.Named("_fogPivotPosition").Present ? Hex("_fogPivotPosition", "float3", 12) : null;
            if (pivot is not null && Enumerable.Range(0, 3).Any(i => !float.IsFinite(BitConverter.ToSingle(Convert.FromHexString(pivot), i * 4))))
                throw new InvalidDataException("Invalid discovery pivot.");
            if (!result.TryAdd(identity, new(Flag("_isCompleted"), Flag("_isDiscoverd"), pivot))) throw new InvalidDataException("Duplicate discovery.");
        }
        return result;
    }
    private static SaveField DiscoveryList(byte[] raw, ParcLayout layout) => SaveRecords.Read(raw, layout,
        "DiscoveredLevelGimmickSceneObjectSaveData").Fields.Named("_discoveredLevelGimmickSceneObjectSaveDataList");
    public MapState ReadMap(ChallengeState? challenges = null) => new(ReadFog(blob, Layout), ReadDiscoveries(blob, DiscoveryList(blob, Layout)), ReadKnowledge(), challenges ?? ReadChallenges());
    public MapFog RevealMap()
    {
        var fog = ReadFog(blob, Layout);
        if (fog.Full) return fog;
        var patch = CreatePatch(fog.Offset, Enumerable.Repeat((byte)255, fog.Codes.Length).ToArray());
        byte[] candidate = CopyForEdit(); patch.After.CopyTo(candidate, patch.Offset);
        var after = ReadFog(candidate, Layout);
        if (!after.Full) throw new InvalidDataException("Fog verification failed.");
        Edit([patch]); return after;
    }

    private static (List<(int Start, int End, Func<int, byte[]> Encode)> Regions, Dictionary<string, MapDiscovery> Expected)
        PlanDiscoveries(byte[] raw, ParcLayout layout, IReadOnlyCollection<string> scenes, IReadOnlyDictionary<string, string> pivots)
    {
        var list = DiscoveryList(raw, layout);
        var before = ReadDiscoveries(raw, list);
        var missing = scenes.Except(before.Keys).Order(StringComparer.Ordinal).ToArray();
        var repair = scenes.Where(s => before.TryGetValue(s, out var r) && r.Pivot is null && pivots.ContainsKey(s)).ToHashSet();
        if (missing.Length == 0 && repair.Count == 0) return ([], before);
        int index = layout.TypeIndex("LevelGimmickSceneObjectElementSaveData");
        var type = layout.Types[index];
        if (!type.Is(("_levelGimmickSceneObjectInfoKey", "LevelGimmickSceneObjectInfoKey", 0, 4), ("_sceneObjectUuid", "uint4", 0, 16),
            ("_fogPivotPosition", "float3", 0, 12), ("_isCompleted", "bool", 0, 1), ("_isDiscoverd", "bool", 0, 1)))
            throw new InvalidDataException("Unsupported discovery insertion layout.");
        var expected = new Dictionary<string, MapDiscovery>(before);
        var rows = new List<Dictionary<string, ReadOnlyMemory<byte>>>();
        foreach (var row in list.ListElements)
        {
            var values = RecordLists.Fields(raw, type, row) ?? throw new InvalidDataException("Unsupported discovery record.");
            string id = $"{BinaryPrimitives.ReadUInt32LittleEndian(values["_levelGimmickSceneObjectInfoKey"].Span)}:{Convert.ToHexString(values["_sceneObjectUuid"].Span).ToLowerInvariant()}";
            if (repair.Contains(id))
            {
                byte[] pivot = Convert.FromHexString(pivots[id]);
                if (pivot.Length != 12) throw new InvalidDataException("Invalid reference pivot.");
                values["_fogPivotPosition"] = pivot;
                expected[id] = expected[id] with { Pivot = pivots[id] };
            }
            rows.Add(values);
        }
        foreach (string id in missing)
        {
            var parts = id.Split(':'); byte[] uuid = Convert.FromHexString(parts[1]);
            byte[] pivot = pivots.TryGetValue(id, out var p) ? Convert.FromHexString(p) : [];
            if (uuid.Length != 16 || pivot.Length is not 0 and not 12) throw new InvalidDataException("Invalid map identity.");
            var values = new Dictionary<string, ReadOnlyMemory<byte>>
            {
                ["_levelGimmickSceneObjectInfoKey"] = BitConverter.GetBytes(uint.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture)), ["_sceneObjectUuid"] = uuid
            };
            if (pivot.Length != 0) values["_fogPivotPosition"] = pivot;
            rows.Add(values);
            expected.Add(id, new(null, null, pivot.Length == 0 ? null : Convert.ToHexString(pivot).ToLowerInvariant()));
        }
        return ([(list.StartOffset, list.EndOffset, start => RecordLists.ListBytes(start, type, index, rows))], expected);
    }

    public (IReadOnlyDictionary<uint, uint> Levels, SkillState Skills) LearnMapLocations(int actionIndex)
    {
        if (actionIndex < 0 || actionIndex >= MapAction.All.Length || MapAction.All[actionIndex].Mode == "icons")
            throw new ArgumentOutOfRangeException(nameof(actionIndex));
        var action = MapAction.All[actionIndex];
        return EditKnowledgeKeys(action.Keys, action.FullLevel ? KnowledgeAction.LearnMaximum : KnowledgeAction.Learn, 1);
    }

    public (MapState Map, SkillState Skills) ActivateMap(int actionIndex)
    {
        if (actionIndex < 0 || actionIndex >= MapAction.All.Length) throw new ArgumentOutOfRangeException(nameof(actionIndex));
        var action = MapAction.All[actionIndex];
        if (action.Mode != "icons")
        {
            var knowledge = LearnMapLocations(actionIndex);
            return (ReadMap(), knowledge.Skills);
        }
        return ActivateMapEntries(action.Scenes.Select(s => new MapEntry(actionIndex, Scene: s)).Append(new MapEntry(actionIndex)).ToArray());
    }

    /// <summary>
    /// Activates chosen map rows in one undo step: knowledge rows (places, memory fragments, Abyss Nexus points) learn
    /// their key at the group's target level, scene rows (bells, cressets and the other icon groups) add their discovery
    /// record and, for a cresset, complete its paired challenge. Knowledge the rows share, an icon group's key or the
    /// memory fragment group key, is learned with any row of the group. An entry with neither key nor scene stands for the
    /// whole group's remaining challenges (a full activation).
    /// </summary>
    public (MapState Map, SkillState Skills) ActivateMapEntries(IReadOnlyCollection<MapEntry> entries)
    {
        var all = MapAction.All;
        var targets = new Dictionary<uint, uint>();
        void Want(uint key, uint level) => targets[key] = Math.Max(targets.GetValueOrDefault(key), level);
        var scenes = new HashSet<string>(); var roots = new HashSet<uint>(); var pivots = new Dictionary<string, string>();
        foreach (var group in entries.GroupBy(e => e.Action))
        {
            if (group.Key < 0 || group.Key >= all.Length) throw new ArgumentOutOfRangeException(nameof(entries));
            var action = all[group.Key];
            bool icons = action.Mode == "icons";
            foreach (var entry in group)
            {
                if (entry.Key is uint key)
                {
                    if (icons || entry.Scene is not null || !action.Keys.Contains(key)) throw new ArgumentException("Unknown map knowledge row.", nameof(entries));
                    Want(key, action.TargetLevel(key));
                }
                else if (entry.Scene is string scene)
                {
                    if (!icons || !action.Scenes.Contains(scene)) throw new ArgumentException("Unknown map scene row.", nameof(entries));
                    scenes.Add(scene);
                    if (action.Pivots?.TryGetValue(scene, out string? pivot) == true) pivots[scene] = pivot;
                    if (action.SceneRoots?.TryGetValue(scene, out uint root) == true) roots.Add(root);
                }
                else roots.UnionWith(action.ChallengeRoots ?? []);
            }
            foreach (uint key in icons ? action.Keys : action.Keys.Except(action.CountKeys ?? action.Keys)) Want(key, action.TargetLevel(key));
        }
        var knowledgeBlock = SaveRecords.Read(blob, Layout, "KnowledgeSaveData");
        var levels = KnowledgeLevels(blob, knowledgeBlock);
        var changes = targets.Where(p => levels.GetValueOrDefault(p.Key) < p.Value).ToDictionary();
        var edits = new List<ListEdit>();
        var expectedPaths = new Dictionary<uint, uint>(SkillState.Read(blob, knowledgeBlock).Follow);
        if (changes.Count > 0)
        {
            var paths = NewLearnPaths(knowledgeBlock, levels, changes, expectedPaths);
            edits.AddRange(KnowledgeEdits(knowledgeBlock, levels, changes, paths));
            foreach (var p in paths) expectedPaths[p.Key] = p.Value;
        }
        var (regions, discoveries) = PlanDiscoveries(blob, Layout, scenes, pivots);
        ChallengeState? expectedProgress = null;
        if (roots.Count > 0)
        {
            // Catalog order, as a full activation always used.
            var plan = PlanChallenges(blob, Layout, all.SelectMany(a => a.ChallengeRoots ?? []).Where(roots.Contains).Distinct().ToArray());
            edits.AddRange(plan.Edits); expectedProgress = plan.Expected;
        }
        regions.AddRange(RecordLists.Regions(blob, Layout, edits));
        if (regions.Count == 0) return (ReadMap(), ReadSkills());
        byte[] candidate = RecordLists.RewriteRegions(blob, EditBuffer, regions);
        foreach (var p in changes) levels[p.Key] = p.Value;
        (MapState Map, SkillState Skills) result = default;
        Transact(candidate, ["KnowledgeSaveData", "QuestSaveData", "DiscoveredLevelGimmickSceneObjectSaveData"], (layout, items) =>
        {
            var block = SaveRecords.Read(candidate, layout, "KnowledgeSaveData");
            var afterKnowledge = KnowledgeLevels(candidate, block);
            var progress = ChallengeState.Read(candidate, SaveRecords.Read(candidate, layout, "QuestSaveData"));
            var after = new MapState(ReadFog(candidate, layout), ReadDiscoveries(candidate, DiscoveryList(candidate, layout)), afterKnowledge, progress);
            var skills = SkillState.Read(candidate, block);
            if (!levels.OrderBy(p => p.Key).SequenceEqual(afterKnowledge.OrderBy(p => p.Key)) ||
                !discoveries.OrderBy(p => p.Key).SequenceEqual(after.Discoveries.OrderBy(p => p.Key)) ||
                expectedProgress?.Matches(progress) == false ||
                !expectedPaths.OrderBy(p => p.Key).SequenceEqual(skills.Follow.OrderBy(p => p.Key)))
                throw new InvalidDataException("Map transaction verification failed.");
            result = (after, skills);
        });
        return result;
    }
}
