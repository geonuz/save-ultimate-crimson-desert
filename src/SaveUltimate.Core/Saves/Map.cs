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
    public static MapAction[] All { get; } = ChallengeCatalog.Load<MapAction[]>("MapActions.json");
}
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
        if (!parent.Present || parent.ChildUndecodedRanges.Length != 0) throw new InvalidDataException("Unsupported fog record.");
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
        var expected = new List<uint>(); uint expanded = 0;
        for (int i = 0; i < lengths.Bytes.Length; i++) { expanded += lengths.Bytes[i]; if ((i + 1) % 64 == 0) expected.Add(expanded); }
        if (expected.Count == 0 || expanded != 524288) throw new InvalidDataException("Unsupported fog dimensions.");
        expected[^1] = expanded;
        if (jumps.Bytes.Length != expected.Count * 4 || expected.Where((v, i) => v != BinaryPrimitives.ReadUInt32LittleEndian(jumps.Bytes.AsSpan(i * 4))).Any())
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
    private static SaveField DiscoveryList(byte[] raw, ParcLayout layout) => SaveRecords.ReadLists(raw, layout,
        "DiscoveredLevelGimmickSceneObjectSaveData", "_discoveredLevelGimmickSceneObjectSaveDataList").Fields.Named("_discoveredLevelGimmickSceneObjectSaveDataList");
    public MapState ReadMap() => new(ReadFog(blob, Layout), ReadDiscoveries(blob, DiscoveryList(blob, Layout)), ReadKnowledge(), ReadChallenges());
    public MapFog RevealMap()
    {
        var fog = ReadFog(blob, Layout);
        if (fog.Full) return fog;
        var patch = CreatePatch(fog.Offset, Enumerable.Repeat((byte)255, fog.Codes.Length).ToArray()) with { CheckedScalar = true };
        byte[] candidate = CopyForEdit(); patch.After.CopyTo(candidate, patch.Offset);
        var after = ReadFog(candidate, Layout);
        if (!after.Full) throw new InvalidDataException("Fog verification failed.");
        Edit([patch]); return after;
    }

    private static (List<(int Start, int End, Func<int, byte[]> Encode)> Regions, Dictionary<string, MapDiscovery> Expected)
        PlanDiscoveries(byte[] raw, ParcLayout layout, MapAction action)
    {
        var list = DiscoveryList(raw, layout);
        var before = ReadDiscoveries(raw, list);
        var missing = action.Scenes.Except(before.Keys).Order(StringComparer.Ordinal).ToArray();
        var repair = action.Scenes.Where(s => before.TryGetValue(s, out var r) && r.Pivot is null && action.Pivots?.ContainsKey(s) == true).ToHashSet();
        if (missing.Length == 0 && repair.Count == 0) return ([], before);
        int index = layout.Types.Select((t, i) => (t, i)).Single(p => p.t.Name == "LevelGimmickSceneObjectElementSaveData").i;
        var type = layout.Types[index];
        if (!type.Fields.Select(f => (f.Name, f.TypeName, (int)f.Kind, (int)f.Size)).SequenceEqual(new[]
            { ("_levelGimmickSceneObjectInfoKey", "LevelGimmickSceneObjectInfoKey", 0, 4), ("_sceneObjectUuid", "uint4", 0, 16),
              ("_fogPivotPosition", "float3", 0, 12), ("_isCompleted", "bool", 0, 1), ("_isDiscoverd", "bool", 0, 1) }) ||
            list.ListHeaderSize != 18 || !string.IsNullOrEmpty(list.Note) || before.Count + missing.Length > 0xffffff)
            throw new InvalidDataException("Unsupported discovery insertion layout.");
        byte[] header = raw.AsSpan(list.StartOffset, 18).ToArray();
        if (header[0] != 0 || header.Skip(4).Any(b => b != 0) || (header[1] | header[2] << 8 | header[3] << 16) != before.Count)
            throw new InvalidDataException("Unsupported discovery header.");
        var expected = new Dictionary<string, MapDiscovery>(before);
        var chunks = new List<byte[]>();
        foreach (var row in list.ListElements)
        {
            int start = row.StartOffset, end = row.EndOffset;
            if (row.ChildMaskByteCount != 1 || row.ChildPayloadOffset != start + 18 || row.ChildUndecodedRanges.Length != 0 ||
                BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(start)) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(start + 3)) != index ||
                raw[start + 5] != 0 || raw.AsSpan(start + 6, 8).ToArray().Any(b => b != 255) ||
                BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(start + 14)) != start + 18 ||
                BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(end - 4)) != end - start - 22)
                throw new InvalidDataException("Unsupported discovery record.");
            var uuid = row.ChildFields.Named("_sceneObjectUuid");
            string id = $"{row.ChildFields.Named("_levelGimmickSceneObjectInfoKey").Scalar(raw, "LevelGimmickSceneObjectInfoKey", 4)}:{Convert.ToHexString(raw.AsSpan(uuid.StartOffset, 16)).ToLowerInvariant()}";
            byte[] chunk = raw.AsSpan(start, end - start).ToArray();
            if (repair.Contains(id))
            {
                int offset = uuid.EndOffset - start;
                byte[] pivot = Convert.FromHexString(action.Pivots![id]);
                if (pivot.Length != 12) throw new InvalidDataException("Invalid reference pivot.");
                chunk = [.. chunk.AsSpan(0, offset), .. pivot, .. chunk.AsSpan(offset)];
                chunk[2] |= 4;
                BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(chunk.Length - 4), (uint)chunk.Length - 22);
                expected[id] = expected[id] with { Pivot = action.Pivots[id] };
            }
            chunks.Add(chunk);
        }
        foreach (string id in missing)
        {
            var parts = id.Split(':'); byte[] uuid = Convert.FromHexString(parts[1]);
            byte[] pivot = action.Pivots?.TryGetValue(id, out var p) == true ? Convert.FromHexString(p) : [];
            if (uuid.Length != 16 || pivot.Length is not 0 and not 12) throw new InvalidDataException("Invalid map identity.");
            using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream);
            writer.Write((ushort)1); writer.Write((byte)(pivot.Length == 0 ? 3 : 7)); writer.Write((ushort)index);
            writer.Write((byte)0); writer.Write(ulong.MaxValue); writer.Write(0u); writer.Write(0u);
            writer.Write(uint.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture)); writer.Write(uuid); writer.Write(pivot); writer.Write((uint)(24 + pivot.Length));
            chunks.Add(stream.ToArray()); expected.Add(id, new(null, null, pivot.Length == 0 ? null : Convert.ToHexString(pivot).ToLowerInvariant()));
        }
        int count = before.Count + missing.Length; for (int i = 0; i < 3; i++) header[1 + i] = (byte)(count >> (8 * i));
        byte[] Encode(int start)
        {
            using var stream = new MemoryStream(); stream.Write(header);
            foreach (var chunk in chunks) { BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(14), checked((uint)(start + stream.Length + 18))); stream.Write(chunk); }
            return stream.ToArray();
        }
        return ([(list.StartOffset, list.EndOffset, Encode)], expected);
    }

    public (IReadOnlyDictionary<uint, uint> Levels, SkillState Skills) LearnMapLocations(int actionIndex)
    {
        if (actionIndex < 0 || actionIndex >= MapAction.All.Length || MapAction.All[actionIndex].Mode == "icons")
            throw new ArgumentOutOfRangeException(nameof(actionIndex));
        return EditKnowledgeKeys(MapAction.All[actionIndex].Keys, KnowledgeAction.Learn, 1);
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
        var knowledgeBlock = SaveRecords.Read(blob, Layout, "KnowledgeSaveData");
        var levels = KnowledgeLevels(blob, knowledgeBlock);
        var changes = action.Keys.Where(k => levels.GetValueOrDefault(k) == 0).ToDictionary(k => k, _ => 1u);
        var edits = new List<ListEdit>();
        if (changes.Count > 0) edits.Add(KnowledgeEdit(Layout, knowledgeBlock.Fields.Named("_list"), changes));
        var (regions, discoveries) = PlanDiscoveries(blob, Layout, action);
        ChallengeState? expectedProgress = null;
        if (action.ChallengeRoots is { Length: > 0 })
        {
            var plan = PlanChallenges(blob, Layout, action.ChallengeRoots);
            edits.AddRange(plan.Edits); expectedProgress = plan.Expected;
        }
        regions.AddRange(RecordLists.Regions(blob, Layout, edits));
        if (regions.Count == 0) return (ReadMap(), ReadSkills());
        byte[] candidate = RecordLists.RewriteRegions(blob, EditBuffer, regions);
        foreach (var p in changes) levels[p.Key] = p.Value;
        regions.Clear(); edits.Clear();
        var snapshots = PrepareSnapshots(blob, candidate);
        (MapState Map, SkillState Skills) result = default;
        string[] classes = ["KnowledgeSaveData", "QuestSaveData", "DiscoveredLevelGimmickSceneObjectSaveData"];
        Commit(candidate, items =>
        {
            var layout = Layout.Reparse(candidate, blob);
            var block = SaveRecords.Read(candidate, layout, "KnowledgeSaveData");
            var afterKnowledge = KnowledgeLevels(candidate, block);
            var progress = ChallengeState.Read(candidate, SaveRecords.ReadLists(candidate, layout, "QuestSaveData", "_questStateList", "_missionStateList"));
            var after = new MapState(ReadFog(candidate, layout), ReadDiscoveries(candidate, DiscoveryList(candidate, layout)), afterKnowledge, progress);
            if (!levels.OrderBy(p => p.Key).SequenceEqual(afterKnowledge.OrderBy(p => p.Key)) ||
                !discoveries.OrderBy(p => p.Key).SequenceEqual(after.Discoveries.OrderBy(p => p.Key)) ||
                expectedProgress is not null && (!expectedProgress.Quest.OrderBy(p => p.Key).SequenceEqual(progress.Quest.OrderBy(p => p.Key)) ||
                    !expectedProgress.Mission.OrderBy(p => p.Key).SequenceEqual(progress.Mission.OrderBy(p => p.Key))))
                throw new InvalidDataException("Map transaction verification failed.");
            result = (after, SkillState.Read(candidate, block, items));
        }, changed: classes);
        undo.Push((null, snapshots.Before, snapshots.After, classes));
        return result;
    }
}
