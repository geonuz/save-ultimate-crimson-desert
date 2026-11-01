using System.Buffers.Binary;
using System.Text;

namespace SaveUltimate.Core.Saves;

public sealed record CompanionTypes(Dictionary<uint, uint> Characters, Dictionary<uint, string> Types)
{
    public static CompanionTypes Shared { get; } = ChallengeCatalog.Load<CompanionTypes>("CompanionTypes.json");
    public static string[] Groups { get; } = ["all", "horse", "pet", "special", "mercenary", "playable", "dedicated", "domestic", "fish", "wagon", "ship", "internal", "unknown"];
    public string Group(uint key) => Characters.GetValueOrDefault(key) switch
    {
        64 => "playable", 78 => "horse", 67 => "pet", 81 => "special", 79 or 80 or 82 => "dedicated",
        70 or 71 or 72 or 73 or 74 => "mercenary", 68 or 84 => "domestic", 69 => "fish", 66 => "wagon", 85 => "ship", 83 => "internal", _ => "unknown"
    };
}
public sealed record Companion(ulong Number, uint Key, string? Name)
{
    public string Group => CompanionTypes.Shared.Group(Key);
    public bool Editable => Group != "internal";
}

public sealed partial class SaveDocument
{
    private static (Companion Value, int MaskOffset, int NameIndex, int Start, int End)[] CompanionRecords(byte[] raw, ParcLayout layout)
    {
        var list = SaveRecords.ReadLists(raw, layout, "MercenaryClanSaveData", true, "_mercenaryDataList").Fields.Named("_mercenaryDataList");
        var seen = new HashSet<ulong>();
        return list.Elements("MercenarySaveData").Select(row =>
        {
            uint key = (uint)row.ChildFields.Named("_characterKey").Scalar(raw, "CharacterKey", 4);
            ulong number = row.ChildFields.Named("_mercenaryNo").Scalar(raw, "MercenaryNo", 8);
            var name = row.ChildFields.Named("_mercenaryName");
            if (!seen.Add(number) || row.ChildUndecodedRanges.Length != 0 || name.TypeName != "staticstringA" || name.MetaKind != 1 ||
                row.ChildPayloadOffset != row.StartOffset + 17 + row.ChildMaskByteCount)
                throw new InvalidDataException("Unsupported companion record.");
            int insert = name.Present ? name.StartOffset : row.ChildFields.Where(f => f.FieldIndex < name.FieldIndex && f.Present)
                .Select(f => f.EndOffset).DefaultIfEmpty(row.ChildPayloadOffset + 4).Max();
            if (insert < row.ChildPayloadOffset + 4 || insert > row.EndOffset - 4) throw new InvalidDataException("Invalid companion name position.");
            return (new Companion(number, key, name.Present ? name.Text(raw) : null),
                row.StartOffset + 2 + name.FieldIndex / 8, name.FieldIndex, insert, name.Present ? name.EndOffset : insert);
        }).ToArray();
    }
    public Companion[] ReadCompanions() => CompanionRecords(blob, Layout).Select(r => r.Value).ToArray();
    public Companion[] RenameCompanions(IReadOnlyList<Companion> selected, string? name)
    {
        if (name is not null)
        {
            name = name.Trim();
            if (name.Length == 0 || name.Contains('\0')) throw new ArgumentException("이름을 입력하세요.", nameof(name));
        }
        var records = CompanionRecords(blob, Layout);
        var current = records.ToDictionary(r => r.Value.Number);
        if (selected.Count == 0 || selected.Select(r => r.Number).Distinct().Count() != selected.Count ||
            selected.Any(r => !r.Editable || !current.TryGetValue(r.Number, out var found) || found.Value != r))
            throw new InvalidOperationException("동료 선택이 변경되었거나 읽기 전용입니다.");
        var targets = selected.Where(r => r.Name != name).ToArray();
        if (targets.Length == 0) return records.Select(r => r.Value).ToArray();
        var replacements = new List<(int Start, int End, byte[] Bytes)>();
        var masks = new List<(int Offset, byte Bit, bool Present)>();
        foreach (var target in targets)
        {
            var row = current[target.Number];
            byte[] bytes = name is null ? [] : new UTF8Encoding(false, true).GetBytes(name);
            if (name is not null) bytes = [.. BitConverter.GetBytes(bytes.Length), .. bytes];
            replacements.Add((row.Start, row.End, bytes));
            masks.Add((row.MaskOffset, (byte)(1 << (row.NameIndex % 8)), name is not null));
        }
        var expected = records.Select(r => targets.Any(t => t.Number == r.Value.Number) ? r.Value with { Name = name } : r.Value).ToArray();
        bool fixedSize = replacements.All(r => r.End - r.Start == r.Bytes.Length);
        byte[] candidate;
        if (fixedSize)
        {
            candidate = CopyForEdit();
            foreach (var r in replacements) r.Bytes.CopyTo(candidate, r.Start);
        }
        else candidate = RecordLists.RewriteRegions(blob, EditBuffer, replacements.Select(r => (r.Start, r.End, (Func<int, byte[]>)(_ => r.Bytes))));
        foreach (var mask in masks)
        {
            int offset = mask.Offset + replacements.Where(r => r.End <= mask.Offset).Sum(r => r.Bytes.Length - (r.End - r.Start));
            candidate[offset] = mask.Present ? (byte)(candidate[offset] | mask.Bit) : (byte)(candidate[offset] & ~mask.Bit);
        }
        Companion[] after = [];
        void Verify()
        {
            after = CompanionRecords(candidate, fixedSize ? Layout : Layout.Reparse(candidate, blob)).Select(r => r.Value).ToArray();
            if (!after.SequenceEqual(expected)) throw new InvalidDataException("Companion name verification failed.");
        }
        if (fixedSize) { Verify(); Edit(replacements.Select(r => CreatePatch(r.Start, r.Bytes) with { CheckedScalar = true })); }
        else
        {
            var snapshots = PrepareSnapshots(blob, candidate);
            Commit(candidate, _ => Verify(), changed: ["MercenaryClanSaveData"]);
            undo.Push((null, snapshots.Before, snapshots.After, ["MercenaryClanSaveData"]));
        }
        return after;
    }
}

public sealed record CompanionDetail(uint? Level, ulong? Experience, ulong? SkillPoints, ulong? Health, ulong? Mana, bool Main, bool Dead, uint? Workplace);

public sealed partial class SaveDocument
{
    public const uint RobotWorker = 1000006;

    public Dictionary<ulong, CompanionDetail> ReadCompanionDetails() => SaveRecords.ReadLists(blob, Layout, "MercenaryClanSaveData", "_mercenaryDataList")
        .Fields.Named("_mercenaryDataList").Elements("MercenarySaveData").ToDictionary(r => r.ChildFields.Named("_mercenaryNo").Scalar(blob, "MercenaryNo", 8), r => Detail(r.ChildFields));

    private CompanionDetail Detail(SaveField[] row)
    {
        // Mercenary HP and MP are present but empty in game saves; only stored values are shown.
        ulong? Value(string name, string type, int size) => row.Named(name) is { Present: true } f && f.EndOffset - f.StartOffset == size ? f.Scalar(blob, type, size) : null;
        var level = row.Named("_levelData");
        var levels = level.Present ? level.ChildFields : [];
        return new(levels.FirstOrDefault(f => f.Name == "_level" && f.Present)?.Scalar(blob, "TLevel", 4) is ulong l ? (uint)l : null,
            levels.FirstOrDefault(f => f.Name == "_exp" && f.Present)?.Scalar(blob, "TExperience", 8),
            Value("_remainSkillPoint", "TSkillPoint", 2), Value("_currentHp", "TStat", 8), Value("_currentMp", "TStat", 8),
            Value("_isMainMercenary", "bool", 1) == 1, Value("_isDead", "bool", 1) == 1, (uint?)Value("_workPlaceFactionNodeKey", "FactionNodeKey", 4));
    }

    public Companion[] AddRobotWorker() => EditRobotWorkers(null);
    public Companion[] RemoveRobotWorker(Companion robot) => EditRobotWorkers(robot);

    // Grey-0 robot workers are records that nothing else references, so a copy or removal touches only this list.
    private Companion[] EditRobotWorkers(Companion? remove)
    {
        var list = SaveRecords.ReadLists(blob, Layout, "MercenaryClanSaveData", "_mercenaryDataList").Fields.Named("_mercenaryDataList");
        var rows = list.Elements("MercenarySaveData");
        var before = ReadCompanions();
        ulong NumberOf(SaveField r) => r.ChildFields.Named("_mercenaryNo").Scalar(blob, "MercenaryNo", 8);
        var robots = rows.Where(r => r.ChildFields.Named("_characterKey").Scalar(blob, "CharacterKey", 4) == RobotWorker).ToArray();
        if (robots.Length == 0) throw new InvalidOperationException("Grey-0 용병이 없는 세이브입니다.");
        // Keep one robot so a later addition still has a template.
        if (remove is not null && (remove.Key != RobotWorker || robots.Length < 2 || !before.Contains(remove)))
            throw new InvalidOperationException("마지막 Grey-0이거나 변경된 선택입니다. 다시 불러오세요.");
        var template = robots[^1];
        // Counts follow Encode in RecordLists: 18-byte headers keep 3 count bytes at 1, 19-byte headers 4 at 2.
        int countStart = list.ListHeaderSize switch { 18 => 1, 19 => 2, _ => throw new InvalidDataException("Unsupported companion list header.") };
        int countSize = list.ListHeaderSize - 15;
        uint count = 0;
        for (int i = 0; i < countSize; i++) count |= (uint)blob[list.StartOffset + countStart + i] << (8 * i);
        if (count != rows.Length) throw new InvalidDataException("Unsupported companion list count.");
        byte[] countBytes = BitConverter.GetBytes(remove is null ? count + 1 : count - 1)[..countSize];
        var regions = new List<(int Start, int End, Func<int, byte[]> Encode)> { (list.StartOffset + countStart, list.StartOffset + countStart + countSize, _ => countBytes) };
        Companion[] expected;
        if (remove is null)
        {
            // shortcut: the game's next-number counter is not in the decoded save; numbers from 900,000,000 stay clear of its
            // sequence (about 1,000,000 after a full playthrough). Use the counter if it is ever found.
            ulong number = Math.Max(900_000_000, rows.Max(NumberOf) + 1);
            regions.Add((template.EndOffset, template.EndOffset, start => MovedRecord(template, start, number)));
            int at = Array.FindIndex(before, c => c.Number == NumberOf(template)) + 1;
            expected = [.. before[..at], new Companion(number, RobotWorker, before[at - 1].Name), .. before[at..]];
        }
        else
        {
            var row = robots.Single(r => NumberOf(r) == remove.Number);
            regions.Add((row.StartOffset, row.EndOffset, _ => []));
            expected = before.Where(c => c.Number != remove.Number).ToArray();
        }
        byte[] candidate = RecordLists.RewriteRegions(blob, EditBuffer, regions);
        var snapshots = PrepareSnapshots(blob, candidate);
        Companion[] after = [];
        Commit(candidate, _ =>
        {
            after = CompanionRecords(candidate, Layout.Reparse(candidate, blob)).Select(r => r.Value).ToArray();
            if (!after.SequenceEqual(expected)) throw new InvalidDataException("Grey-0 verification failed.");
        }, changed: ["MercenaryClanSaveData"]);
        undo.Push((null, snapshots.Before, snapshots.After, ["MercenaryClanSaveData"]));
        return after;
    }

    // Every object and list element in a record points at its own payload with an absolute u32 just before it, and list
    // headers hold only counts, so a record moves by shifting those pointers and nothing else.
    private byte[] MovedRecord(SaveField record, int start, ulong number)
    {
        byte[] copy = blob.AsSpan(record.StartOffset, record.EndOffset - record.StartOffset).ToArray();
        void Walk(SaveField field)
        {
            if (!field.Present) return;
            if (field.ChildUndecodedRanges.Length != 0 || field.ListElements.Length != field.ListCount) throw new InvalidDataException("Unsupported companion record.");
            if (field.ChildFields.Length != 0)
            {
                int pointer = field.ChildPayloadOffset - 4;
                if (pointer < record.StartOffset || pointer + 4 > record.EndOffset || BinaryPrimitives.ReadUInt32LittleEndian(blob.AsSpan(pointer)) != field.ChildPayloadOffset)
                    throw new InvalidDataException("Unsupported companion record.");
                BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(pointer - record.StartOffset), checked((uint)(field.ChildPayloadOffset + start - record.StartOffset)));
            }
            foreach (var child in field.ChildFields) Walk(child);
            foreach (var element in field.ListElements) Walk(element);
        }
        Walk(record);
        BinaryPrimitives.WriteUInt64LittleEndian(copy.AsSpan(record.ChildFields.Named("_mercenaryNo").StartOffset - record.StartOffset), number);
        return copy;
    }

    // The game lists pets and special mounts in acquisition order, which is both their list order and their number order.
    // Their numbers are referenced nowhere outside their own records (horses and dedicated mounts appear in quests), so two of
    // them trade places by exchanging everything except the number.
    public Companion[] SwapCompanions(Companion first, Companion second)
    {
        var before = ReadCompanions();
        if (first.Number == second.Number || first.Group != second.Group || first.Group is not ("pet" or "special") || !before.Contains(first) || !before.Contains(second))
            throw new InvalidOperationException("순서를 바꿀 수 없거나 변경된 선택입니다. 다시 불러오세요.");
        var rows = SaveRecords.ReadLists(blob, Layout, "MercenaryClanSaveData", "_mercenaryDataList").Fields.Named("_mercenaryDataList").Elements("MercenarySaveData");
        SaveField Row(Companion c) => rows.Single(r => r.ChildFields.Named("_mercenaryNo").Scalar(blob, "MercenaryNo", 8) == c.Number);
        SaveField a = Row(first), b = Row(second);
        byte[] candidate = RecordLists.RewriteRegions(blob, EditBuffer,
            [(a.StartOffset, a.EndOffset, start => MovedRecord(b, start, first.Number)), (b.StartOffset, b.EndOffset, start => MovedRecord(a, start, second.Number))]);
        var expected = before.Select(c => c.Number == first.Number ? second with { Number = first.Number } : c.Number == second.Number ? first with { Number = second.Number } : c).ToArray();
        var snapshots = PrepareSnapshots(blob, candidate);
        Companion[] after = [];
        Commit(candidate, _ =>
        {
            after = CompanionRecords(candidate, Layout.Reparse(candidate, blob)).Select(r => r.Value).ToArray();
            if (!after.SequenceEqual(expected)) throw new InvalidDataException("Companion order verification failed.");
        }, changed: ["MercenaryClanSaveData"]);
        undo.Push((null, snapshots.Before, snapshots.After, ["MercenaryClanSaveData"]));
        return after;
    }
}
