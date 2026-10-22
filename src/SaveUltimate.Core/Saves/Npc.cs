namespace SaveUltimate.Core.Saves;

public sealed record FriendshipValue(uint? Level, ulong? Value, int Offset);
public sealed record NpcFriendship(uint Key, FriendshipValue Global, FriendshipValue[] Fields)
{
    public ulong? Value => Fields.Length > 0 && Fields.Select(f => f.Value).Distinct().Count() == 1 && Fields[0].Value is not null ? Fields[0].Value : Global.Value;
    public bool NeedsSync => Value != Global.Value;
    public bool Editable => Global.Level == 0 && Global.Value is not null && Value is <= 100 &&
        Fields.Select(f => f.Value).Distinct().Count() <= 1 && Fields.All(f => f.Level == 0 && f.Value is not null);
}

public sealed partial class SaveDocument
{
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
        var list = SaveRecords.Read(raw, layout, "FriendlySaveData").Fields.Named("_friendlyDataList");
        if (!list.Present) return [];
        var rows = list.Elements("FriendlyElementSaveData").ToDictionary(
            r => (uint)r.ChildFields.Named("_characterKey").Scalar(raw, "CharacterKey", 4), r => Value(raw, r.ChildFields.Named("_levelData")));
        var local = new Dictionary<uint, List<FriendshipValue>>();
        if (layout.Blocks.Any(b => b.Name == "FieldNPCSaveData"))
            foreach (var block in SaveRecords.ReadObjects(raw, layout, "FieldNPCSaveData"))
            {
                if (block.UndecodedRanges.Length != 0) throw new InvalidDataException("Incomplete field NPC.");
                uint key = (uint)block.Fields.Named("_characterKey").Scalar(raw, "CharacterKey", 4);
                if (!local.TryGetValue(key, out var values)) local[key] = values = [];
                values.Add(Value(raw, block.Fields.Named("_friendly")));
            }
        return rows.Select(p => new NpcFriendship(p.Key, p.Value, local.GetValueOrDefault(p.Key)?.ToArray() ?? [])).ToArray();
    }
    public NpcFriendship[] ReadFriendship() => ReadFriendship(blob, Layout);
    public NpcFriendship[] SetFriendship(uint key, ulong expectedValue, int value)
    {
        if (value is < 0 or > 100 || value % 5 != 0) throw new ArgumentOutOfRangeException(nameof(value));
        var before = ReadFriendship();
        var row = before.SingleOrDefault(r => r.Key == key);
        if (row is null || !row.Editable || row.Value != expectedValue) throw new InvalidOperationException("편집할 수 없거나 변경된 NPC 친밀도입니다. 다시 불러오세요.");
        var patches = row.Fields.Prepend(row.Global).Where(v => v.Value != (ulong)value)
            .Select(v => CreatePatch(v.Offset, BitConverter.GetBytes((ulong)value)) with { CheckedScalar = true }).ToArray();
        if (patches.Length == 0) return before;
        byte[] candidate = CopyForEdit(); foreach (var p in patches) p.After.CopyTo(candidate, p.Offset);
        var after = ReadFriendship(candidate, Layout);
        var changed = after.Single(r => r.Key == key);
        if (changed.Value != (ulong)value || changed.NeedsSync) throw new InvalidDataException("NPC synchronization failed.");
        Edit(patches); return after;
    }
}
