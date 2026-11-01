namespace SaveUltimate.Core.Saves;

public sealed record FriendshipValue(uint? Level, ulong? Value, int Offset);
public abstract record NpcGauge(FriendshipValue Global, FriendshipValue[] Fields)
{
    public ulong? Value => Fields.Length > 0 && Fields.Select(f => f.Value).Distinct().Count() == 1 && Fields[0].Value is not null ? Fields[0].Value : Global.Value;
    public bool NeedsSync => Value != Global.Value;
    public bool Editable => Global.Level == 0 && Global.Value is not null && Value is <= 100 &&
        Fields.Select(f => f.Value).Distinct().Count() <= 1 && Fields.All(f => f.Level == 0 && f.Value is not null);
}
/// <summary>DLC romance gauge (확장된 단계의 친밀도, English "bond") of one NPC toward one playable character; MaritalState 1 is courting.</summary>
public sealed record NpcAffinity(uint Playable, byte? MaritalState, FriendshipValue Global, FriendshipValue[] Fields) : NpcGauge(Global, Fields);
public sealed record NpcFriendship(uint Key, FriendshipValue Global, FriendshipValue[] Fields, NpcAffinity[] Affinity) : NpcGauge(Global, Fields);

public sealed partial class SaveDocument
{
    // Affinity lists are positional; every field NPC's marital list names the playable of each position.
    private static readonly uint[] Playables = [1, 6, 4];
    // Kliff's love-story missions per witch (Mission_Graymane_Kliff_LoveStory_<witch>_*); the stage in progress counts the
    // bond in _completeCount (Bari_2 holds 35 for bond 35). shortcut: keys copied from 2.04 missioninfo, read them from
    // the client if more courtships appear.
    private static readonly Dictionary<uint, uint[]> LoveStoryMissions = new()
    {
        [1002940] = [.. Keys(1004751, 1004754), .. Keys(1004888, 1004895)], // Bari
        [1002941] = [.. Keys(1004745, 1004748), .. Keys(1004882, 1004887)], // Lyselia
        [1002938] = [.. Keys(1004774, 1004777), .. Keys(1004896, 1004902)], // Areciel
        [1002939] = [.. Keys(1004786, 1004789), .. Keys(1004903, 1004910)], // Elowen
    };
    // "호감 쌓기" (_2) completes at bond 40; the game completes it before its count reaches 40 (Bari_2 finished at 35, bond 89).
    private static readonly HashSet<uint> BondForty = [1004747, 1004753, 1004776, 1004788];
    private static IEnumerable<uint> Keys(uint first, uint last) => Enumerable.Range((int)first, (int)(last - first + 1)).Select(k => (uint)k);

    private static NpcFriendship[] ReadFriendship(byte[] raw, ParcLayout layout)
    {
        static FriendshipValue Value(byte[] raw, SaveField parent)
        {
            if (!parent.Present || parent.ChildTypeName != "ExperienceLevelSaveData" || parent.ChildUndecodedRanges.Length != 0)
                throw new InvalidDataException("Unsupported friendship level.");
            var level = parent.ChildFields.Named("_level"); var exp = parent.ChildFields.Named("_exp");
            return new(level.Present ? (uint)level.Scalar(raw, "TLevel", 4) : null,
                exp.Present ? exp.Scalar(raw, "TExperience", 8) : null, exp.StartOffset);
        }
        static Dictionary<uint, FriendshipValue> Rows(byte[] raw, IEnumerable<SaveField> rows) => rows.ToDictionary(
            r => (uint)r.ChildFields.Named("_characterKey").Scalar(raw, "CharacterKey", 4), r => Value(raw, r.ChildFields.Named("_levelData")));
        var block = SaveRecords.Read(raw, layout, "FriendlySaveData").Fields;
        var list = block.Named("_friendlyDataList");
        if (!list.Present) return [];
        // Wanted criminals and threatened quest culprits (Corbin) keep an entry only to remember the threat reward; it holds no friendship.
        var rows = Rows(raw, list.Elements("FriendlyElementSaveData").Where(r => !(r.ChildFields.Named("_threatRewarded") is { Present: true } threat &&
            threat.Scalar(raw, "bool", 1) != 0 && !r.ChildFields.Named("_levelData").ChildFields.Named("_exp").Present)));
        // 2.03 saves have no affinity; a DLC save holds one positional entry per playable.
        var affinityList = block.FirstOrDefault(f => f.Name == "_playAbleAffinityDataList");
        var global = affinityList is { Present: true } ? affinityList.Elements("AffinityElementSaveData")
            .Select(e => e.ChildFields.Named("_affinityDataList") is { Present: true } inner ? Rows(raw, inner.Elements("FriendlyElementSaveData")) : []).ToArray() : [];
        if (global.Length is not 0 && global.Length != Playables.Length) throw new InvalidDataException("Unsupported affinity list.");
        var local = new Dictionary<uint, List<FriendshipValue>>();
        var localAffinity = new Dictionary<(uint, int), List<FriendshipValue>>();
        var marital = new Dictionary<(uint, int), byte>();
        if (layout.Blocks.Any(b => b.Name == "FieldNPCSaveData"))
            foreach (var npc in SaveRecords.ReadObjects(raw, layout, "FieldNPCSaveData"))
            {
                if (npc.UndecodedRanges.Length != 0) throw new InvalidDataException("Incomplete field NPC.");
                uint key = (uint)npc.Fields.Named("_characterKey").Scalar(raw, "CharacterKey", 4);
                if (!local.TryGetValue(key, out var values)) local[key] = values = [];
                values.Add(Value(raw, npc.Fields.Named("_friendly")));
                if (global.Length == 0) continue;
                var states = npc.Fields.Named("_npcMaritialStateDataList").Elements("FieldNPCMaritialStateSaveData");
                if (states.Length != 0 && !states.Select(s => (uint)s.ChildFields.Named("_playableCharacterKey").Scalar(raw, "CharacterKey", 4)).SequenceEqual(Playables))
                    throw new InvalidDataException("Unsupported marital state order.");
                for (int i = 0; i < states.Length; i++)
                    if (states[i].ChildFields.Named("_maritalState") is { Present: true } state) marital[(key, i)] = (byte)state.Scalar(raw, "NpcMaritalStateType", 1);
                var affinity = npc.Fields.Named("_affinityList").Elements("ExperienceLevelSaveData");
                if (affinity.Length is not 0 && affinity.Length != Playables.Length) throw new InvalidDataException("Unsupported field NPC affinity.");
                for (int i = 0; i < affinity.Length; i++)
                    if (affinity[i].ChildFields.Any(f => f.Present))
                    {
                        if (!localAffinity.TryGetValue((key, i), out var copies)) localAffinity[(key, i)] = copies = [];
                        copies.Add(Value(raw, affinity[i]));
                    }
            }
        return rows.Select(p => new NpcFriendship(p.Key, p.Value, local.GetValueOrDefault(p.Key)?.ToArray() ?? [],
            Enumerable.Range(0, global.Length).Where(i => global[i].TryGetValue(p.Key, out var bond) && bond.Value is not null).Select(i => new NpcAffinity(Playables[i],
                marital.TryGetValue((p.Key, i), out var m) ? m : null, global[i][p.Key], localAffinity.GetValueOrDefault((p.Key, i))?.ToArray() ?? [])).ToArray())).ToArray();
    }
    public NpcFriendship[] ReadFriendship() => ReadFriendship(blob, Layout);
    public NpcFriendship[] SetFriendship(uint key, ulong expectedValue, int value) => SetGauge(key, null, expectedValue, value);
    public NpcFriendship[] SetAffinity(uint key, uint playable, ulong expectedValue, int value) => SetGauge(key, playable, expectedValue, value);

    // The in-progress love-story mission whose count is the bond; other stages count other things (Lyselia_1 holds 1 at bond 20).
    private (uint Key, int Offset)? LoveStoryCount(byte[] raw, uint key, ulong bond)
    {
        if (!LoveStoryMissions.TryGetValue(key, out var keys)) return null;
        var list = SaveRecords.ReadLists(raw, Layout, "QuestSaveData", "_questStateList", "_missionStateList").Fields.Named("_missionStateList");
        var matches = list.Elements("MissionStateData").Select(r => (Row: r, Key: (uint)r.ChildFields.Named("_key").Scalar(raw, "MissionKey", 4)))
            .Where(r => keys.Contains(r.Key) && r.Row.ChildFields.Named("_state") is { Present: true } state && state.Scalar(raw, "QuestStateType", 1) == 2 &&
                r.Row.ChildFields.Named("_completeCount") is { Present: true } count && count.Scalar(raw, "uint32", 4) == bond).ToArray();
        // Two candidates would leave the bond counter unknown; leave missions alone then.
        return matches.Length == 1 ? (matches[0].Key, matches[0].Row.ChildFields.Named("_completeCount").StartOffset) : null;
    }
    private NpcFriendship[] SetGauge(uint key, uint? playable, ulong expectedValue, int value)
    {
        // The game raises gauges by uneven steps (3, 7), so any whole value up to the cap is valid.
        if (value is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(value));
        static NpcGauge? Find(NpcFriendship[] rows, uint key, uint? playable)
            => rows.SingleOrDefault(r => r.Key == key) is not { } row ? null : playable is null ? row : row.Affinity.SingleOrDefault(a => a.Playable == playable);
        var before = ReadFriendship();
        var gauge = Find(before, key, playable);
        if (gauge is null || !gauge.Editable || gauge.Value != expectedValue) throw new InvalidOperationException("편집할 수 없거나 변경된 NPC 친밀도입니다. 다시 불러오세요.");
        // Love-story stages already passed stay complete, so a lower bond would leave the quest ahead of it.
        if (playable is not null && (ulong)value < expectedValue) throw new InvalidOperationException("친밀도(확장)는 현재 값보다 낮출 수 없습니다.");
        var patches = gauge.Fields.Prepend(gauge.Global).Where(v => v.Value != (ulong)value)
            .Select(v => CreatePatch(v.Offset, BitConverter.GetBytes((ulong)value)) with { CheckedScalar = true }).ToList();
        var mission = playable == 1 ? LoveStoryCount(blob, key, expectedValue) : null;
        uint count = mission is { } target && BondForty.Contains(target.Key) ? (uint)Math.Min(value, 40) : (uint)value;
        if (mission is { } m) patches.Add(CreatePatch(m.Offset, BitConverter.GetBytes(count)) with { CheckedScalar = true });
        if (patches.Count == 0) return before;
        byte[] candidate = CopyForEdit(); foreach (var p in patches) p.After.CopyTo(candidate, p.Offset);
        var after = ReadFriendship(candidate, Layout);
        var changed = Find(after, key, playable)!;
        if (changed.Value != (ulong)value || changed.NeedsSync) throw new InvalidDataException("NPC synchronization failed.");
        if (mission is { } synced && ChallengeState.Read(candidate, SaveRecords.ReadLists(candidate, Layout, "QuestSaveData", "_questStateList", "_missionStateList")).Mission[synced.Key].Count != count) throw new InvalidDataException("Love-story mission synchronization failed.");
        Edit(patches); return after;
    }
}
