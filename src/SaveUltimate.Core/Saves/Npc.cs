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
    // Kliff's love-story missions are left alone: the game completes a stage from the bond itself on the next talk, and
    // their _completeCount stays where the game last wrote it (Lyselia_2 completed at 32 after a bond of 37 rose to 42).

    private static NpcFriendship[] ReadFriendship(byte[] raw, ParcLayout layout)
    {
        static FriendshipValue Value(byte[] raw, SaveField parent)
        {
            if (!parent.Present || parent.ChildTypeName != "ExperienceLevelSaveData")
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

    private NpcFriendship[] SetGauge(uint key, uint? playable, ulong expectedValue, int value)
    {
        // The game raises gauges by uneven steps (3, 7), so any whole value up to the cap is valid.
        if (value is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(value));
        static NpcGauge? Find(NpcFriendship[] rows, uint key, uint? playable)
            => rows.SingleOrDefault(r => r.Key == key) is not { } row ? null : playable is null ? row : row.Affinity.SingleOrDefault(a => a.Playable == playable);
        var before = ReadFriendship();
        var gauge = Find(before, key, playable);
        if (gauge is null || !gauge.Editable || gauge.Value != expectedValue) throw new InvalidOperationException("This NPC bond is not editable or changed. Load the save again.");
        // Love-story stages already passed stay complete, so a lower bond would leave the quest ahead of it.
        if (playable is not null && (ulong)value < expectedValue) throw new InvalidOperationException("Bond cannot be lowered below its current value.");
        var patches = gauge.Fields.Prepend(gauge.Global).Where(v => v.Value != (ulong)value)
            .Select(v => CreatePatch(v.Offset, BitConverter.GetBytes((ulong)value))).ToList();
        if (patches.Count == 0) return before;
        byte[] candidate = CopyForEdit(); foreach (var p in patches) p.After.CopyTo(candidate, p.Offset);
        var after = ReadFriendship(candidate, Layout);
        var changed = Find(after, key, playable)!;
        if (changed.Value != (ulong)value || changed.NeedsSync) throw new InvalidDataException("NPC synchronization failed.");
        Edit(patches); return after;
    }
}
