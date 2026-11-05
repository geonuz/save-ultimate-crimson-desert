using System.Buffers.Binary;
using System.Text;

namespace SaveUltimate.Core.Saves;

public sealed record CompanionTypes(Dictionary<uint, uint> Characters, Dictionary<uint, string> Types)
{
    public static CompanionTypes Shared { get; } = Embedded.Load<CompanionTypes>("CompanionTypes.json");
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
        var list = SaveRecords.Read(raw, layout, "MercenaryClanSaveData").Fields.Named("_mercenaryDataList");
        var seen = new HashSet<ulong>();
        return list.Elements("MercenarySaveData").Select(row =>
        {
            uint key = (uint)row.ChildFields.Named("_characterKey").Scalar(raw, "CharacterKey", 4);
            ulong number = row.ChildFields.Named("_mercenaryNo").Scalar(raw, "MercenaryNo", 8);
            var name = row.ChildFields.Named("_mercenaryName");
            if (!seen.Add(number) || name.TypeName != "staticstringA" || name.MetaKind != 1)
                throw new InvalidDataException("Unsupported companion record.");
            return (new Companion(number, key, name.Present ? name.Text(raw) : null),
                row.StartOffset + 2 + name.FieldIndex / 8, name.FieldIndex, name.StartOffset, name.EndOffset);
        }).ToArray();
    }
    public Companion[] ReadCompanions() => CompanionRecords(blob, Layout).Select(r => r.Value).ToArray();
    public Companion[] RenameCompanions(IReadOnlyList<Companion> selected, string? name)
    {
        if (name is not null)
        {
            name = name.Trim();
            if (name.Length == 0 || name.Contains('\0')) throw new ArgumentException("Enter a name.", nameof(name));
        }
        var records = CompanionRecords(blob, Layout);
        var current = records.ToDictionary(r => r.Value.Number);
        if (selected.Count == 0 || selected.Select(r => r.Number).Distinct().Count() != selected.Count ||
            selected.Any(r => !r.Editable || !current.TryGetValue(r.Number, out var found) || found.Value != r))
            throw new InvalidOperationException("The companion selection changed or is read-only.");
        var targets = selected.Where(r => r.Name != name).ToArray();
        if (targets.Length == 0) return records.Select(r => r.Value).ToArray();
        byte[] bytes = name is null ? [] : new UTF8Encoding(false, true).GetBytes(name);
        if (name is not null) bytes = [.. BitConverter.GetBytes(bytes.Length), .. bytes];
        var regions = new List<(int Start, int End, Func<int, byte[]> Encode)>();
        foreach (var target in targets)
        {
            var row = current[target.Number];
            byte bit = (byte)(1 << (row.NameIndex % 8)), mask = name is not null ? (byte)(blob[row.MaskOffset] | bit) : (byte)(blob[row.MaskOffset] & ~bit);
            regions.Add((row.MaskOffset, row.MaskOffset + 1, _ => [mask]));
            regions.Add((row.Start, row.End, _ => bytes));
        }
        var expected = records.Select(r => targets.Any(t => t.Number == r.Value.Number) ? r.Value with { Name = name } : r.Value).ToArray();
        byte[] candidate = RecordLists.RewriteRegions(blob, EditBuffer, regions);
        Companion[] after = [];
        Transact(candidate, ["MercenaryClanSaveData"], (layout, _) =>
        {
            after = CompanionRecords(candidate, layout).Select(r => r.Value).ToArray();
            if (!after.SequenceEqual(expected)) throw new InvalidDataException("Companion name verification failed.");
        });
        return after;
    }
}

public sealed record CompanionDetail(uint? Level, ulong? Experience, ulong? SkillPoints, ulong? Health, ulong? Mana, bool Main, bool Dead, uint? Workplace);

public sealed partial class SaveDocument
{
    public const uint RobotWorker = 1000006;

    public Dictionary<ulong, CompanionDetail> ReadCompanionDetails() => SaveRecords.Read(blob, Layout, "MercenaryClanSaveData")
        .Fields.Named("_mercenaryDataList").Elements("MercenarySaveData").ToDictionary(r => r.ChildFields.Named("_mercenaryNo").Scalar(blob, "MercenaryNo", 8), r => Detail(r.ChildFields));

    private CompanionDetail Detail(SaveField[] row)
    {
        ulong? Value(string name, string type, int size) => row.Named(name) is { Present: true } f ? f.Scalar(blob, type, size) : null;
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
        var list = SaveRecords.Read(blob, Layout, "MercenaryClanSaveData").Fields.Named("_mercenaryDataList");
        var rows = list.Elements("MercenarySaveData");
        var before = ReadCompanions();
        ulong NumberOf(SaveField r) => r.ChildFields.Named("_mercenaryNo").Scalar(blob, "MercenaryNo", 8);
        var robots = rows.Where(r => r.ChildFields.Named("_characterKey").Scalar(blob, "CharacterKey", 4) == RobotWorker).ToArray();
        if (robots.Length == 0) throw new InvalidOperationException("This save has no Grey-0 mercenary.");
        // Keep one robot so a later addition still has a template.
        if (remove is not null && (remove.Key != RobotWorker || robots.Length < 2 || !before.Contains(remove)))
            throw new InvalidOperationException("This is the last Grey-0 or the selection changed. Load the save again.");
        var template = robots[^1];
        // A list header holds the marker byte, then the count.
        byte[] countBytes = BitConverter.GetBytes(rows.Length + (remove is null ? 1 : -1));
        var regions = new List<(int Start, int End, Func<int, byte[]> Encode)> { (list.StartOffset + 1, list.StartOffset + 5, _ => countBytes) };
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
        Companion[] after = [];
        Transact(candidate, ["MercenaryClanSaveData"], (layout, _) =>
        {
            after = CompanionRecords(candidate, layout).Select(r => r.Value).ToArray();
            if (!after.SequenceEqual(expected)) throw new InvalidDataException("Grey-0 verification failed.");
        });
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
            if (field.ChildPayloadOffset != 0)
                BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(field.ChildPayloadOffset - 4 - record.StartOffset), checked((uint)(field.ChildPayloadOffset + start - record.StartOffset)));
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
            throw new InvalidOperationException("The order cannot change or the selection changed. Load the save again.");
        var rows = SaveRecords.Read(blob, Layout, "MercenaryClanSaveData").Fields.Named("_mercenaryDataList").Elements("MercenarySaveData");
        SaveField Row(Companion c) => rows.Single(r => r.ChildFields.Named("_mercenaryNo").Scalar(blob, "MercenaryNo", 8) == c.Number);
        SaveField a = Row(first), b = Row(second);
        byte[] candidate = RecordLists.RewriteRegions(blob, EditBuffer,
            [(a.StartOffset, a.EndOffset, start => MovedRecord(b, start, first.Number)), (b.StartOffset, b.EndOffset, start => MovedRecord(a, start, second.Number))]);
        var expected = before.Select(c => c.Number == first.Number ? second with { Number = first.Number } : c.Number == second.Number ? first with { Number = second.Number } : c).ToArray();
        Companion[] after = [];
        Transact(candidate, ["MercenaryClanSaveData"], (layout, _) =>
        {
            after = CompanionRecords(candidate, layout).Select(r => r.Value).ToArray();
            if (!after.SequenceEqual(expected)) throw new InvalidDataException("Companion order verification failed.");
        });
        return after;
    }
}
