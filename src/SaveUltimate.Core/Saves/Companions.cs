using System.Buffers.Binary;
using System.Text;

namespace SaveUltimate.Core.Saves;

public sealed record CompanionTypes(Dictionary<uint, uint> Characters, Dictionary<uint, string> Types)
{
    public static CompanionTypes Shared { get; } = Embedded.Load<CompanionTypes>("CompanionTypes.json");
    // Mercenary character key to its work-skill knowledge keys, in game order.
    public Dictionary<uint, uint[]> Skills { get; init; } = [];
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
    private static SaveField MercenaryList(byte[] raw, ParcLayout layout) => SaveRecords.Read(raw, layout, "MercenaryClanSaveData").Fields.Named("_mercenaryDataList");
    // Adopts the candidate only if it reads back as the expected roster.
    private Companion[] CommitCompanions(byte[] candidate, Companion[] expected, string failure)
    {
        Companion[] after = [];
        Transact(candidate, ["MercenaryClanSaveData"], (layout, _) =>
        {
            after = CompanionRecords(candidate, layout).Select(r => r.Value).ToArray();
            if (!after.SequenceEqual(expected)) throw new InvalidDataException(failure);
        });
        return after;
    }
    private static (Companion Value, int MaskOffset, int NameIndex, int Start, int End)[] CompanionRecords(byte[] raw, ParcLayout layout)
    {
        var list = MercenaryList(raw, layout);
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
        return CommitCompanions(RecordLists.RewriteRegions(blob, EditBuffer, regions), expected, "Companion name verification failed.");
    }
}

public sealed record CompanionDetail(uint? Level, ulong? Experience, ulong? SkillPoints, ulong? Health, ulong? Mana, bool Main, bool Dead, uint? Workplace);

public sealed partial class SaveDocument
{
    public const uint RobotWorker = 1000006;

    public Dictionary<ulong, CompanionDetail> ReadCompanionDetails() => MercenaryList(blob, Layout).Elements("MercenarySaveData").ToDictionary(r => r.ChildFields.Named("_mercenaryNo").Scalar(blob, "MercenaryNo", 8), r => Detail(r.ChildFields));

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

    // Matthias joins through a quest, not a festival: he is in every DLC save, with equipment.
    private const uint QuestGuestWorker = 50901;
    /// <summary>Grey-0 and the NPCs a festival recruits (MercenaryInfo 73, Mercenary_GuestWorker) can be added and removed.</summary>
    public static bool IsWorker(uint key) => key == RobotWorker || key != QuestGuestWorker && CompanionTypes.Shared.Characters.GetValueOrDefault(key) == 73;
    /// <summary>Workers an addition can create: Grey-0 any number of times, each recruitable NPC once.</summary>
    public static uint[] WorkerChoices(IEnumerable<Companion> roster)
    {
        var present = roster.Select(c => c.Key).ToHashSet();
        // Festivals are DLC content, so a game without the DLC is offered Grey-0 only.
        return [RobotWorker, .. CompanionTypes.Shared.Characters.Keys.Where(k => !DlcContent.Hidden && k != RobotWorker && IsWorker(k) && !present.Contains(k)).Order()];
    }
    /// <summary>Worker numbers FactionSaveData names (workplace operators, camp spawns); removing one would leave a dangling reference.</summary>
    public HashSet<ulong> FactionWorkers() => ObjectNumbers(["FactionSaveData"], "_mercenaryNo");
    // The values of these number fields anywhere in the named blocks a save has.
    private HashSet<ulong> ObjectNumbers(string[] blocks, params string[] names)
    {
        var found = new HashSet<ulong>();
        void Visit(SaveField f)
        {
            if (!f.Present) return;
            if (names.Contains(f.Name)) found.Add(f.Scalar(blob, f.TypeName, 8));
            foreach (var c in f.ChildFields) Visit(c);
            foreach (var e in f.ListElements) Visit(e);
        }
        foreach (var block in blocks.Where(b => Layout.Blocks.Any(l => l.Name == b)))
            foreach (var f in SaveRecords.ReadObjects(blob, Layout, block).SelectMany(o => o.Fields)) Visit(f);
        return found;
    }
    /// <summary>Why a mercenary cannot be removed ("quest", "workplace", "spawn"), or null when it can.</summary>
    public static string? RemoveBlock(Companion worker, CompanionDetail? detail, IReadOnlySet<ulong> factionWorkers) =>
        !IsWorker(worker.Key) ? "quest"
        : detail?.Workplace is not null ? "workplace"
        : factionWorkers.Contains(worker.Number) ? "spawn" : null;
    public Companion[] AddWorker(uint key) => EditWorkers(key, null);
    public Companion[] RemoveWorker(Companion worker) => EditWorkers(0, worker);

    // The fields a recruited worker's record holds, as the game writes it (slot pair 106 → 105, 2026-11-11).
    private static readonly (string Name, string Type, int Kind, int Size)[] WorkerFields =
        [("_characterKey", "CharacterKey", 0, 4), ("_mercenaryNo", "MercenaryNo", 0, 8), ("_levelData", "ReflectObject", 4, 8),
         ("_lastPaidTime", "uint64", 0, 8), ("_lastBreedingTime", "uint64", 0, 8), ("_isInitialize", "bool", 0, 1)];
    /// <summary>Whether this save's schema has every type a new worker record needs. Saves from before the first companion may not.</summary>
    public bool CanAddWorkers => Layout.Blocks.Any(b => b.Name == "MercenaryClanSaveData") && WorkerRecord(0, 0, 0) is not null;

    // A recruit's record as the game writes it: key, number, an experience record holding only its empty daily counters, and
    // pay and breeding times both set to the field time of the recruitment. Null when the schema lacks a type it needs.
    private RecordLists.Nested? WorkerRecord(uint key, ulong number, ulong time)
    {
        var types = Layout.Types.ToDictionary(t => t.Name);
        if (!types.TryGetValue("MercenarySaveData", out var mercenary) || !types.TryGetValue("ExperienceLevelSaveData", out var level) ||
            !types.TryGetValue("FriendlyDailyCountSaveData", out _))
            return null;
        if (!WorkerFields.All(w => mercenary.Fields.Any(f => (f.Name, f.TypeName, (int)f.Kind, (int)f.Size) == w))) return null;
        // Every object field of the experience record is a daily counter, and the game writes each one, empty.
        var counters = level.Fields.Where(f => f.Kind == 4).ToArray();
        if (counters.Length == 0 || counters.Any(f => !f.Name.EndsWith("DailyCountData", StringComparison.Ordinal))) return null;
        return new("MercenarySaveData", new Dictionary<string, object>
        {
            ["_characterKey"] = BitConverter.GetBytes(key), ["_mercenaryNo"] = BitConverter.GetBytes(number),
            ["_levelData"] = new RecordLists.Nested("ExperienceLevelSaveData",
                counters.ToDictionary(f => f.Name, object (_) => new RecordLists.Nested("FriendlyDailyCountSaveData", new Dictionary<string, object>()))),
            ["_lastPaidTime"] = BitConverter.GetBytes(time), ["_lastBreedingTime"] = BitConverter.GetBytes(time), ["_isInitialize"] = new byte[] { 1 }
        });
    }

    // Grey-0 robots and festival recruits hold only their key, number, pay times and flags. Only FactionSaveData names them
    // elsewhere (workplace operators, camp spawns), so an addition, or a removal of one it does not name, touches only this list.
    private Companion[] EditWorkers(uint add, Companion? remove)
    {
        var list = MercenaryList(blob, Layout);
        var rows = list.Elements("MercenarySaveData");
        var before = ReadCompanions();
        ulong NumberOf(SaveField r) => r.ChildFields.Named("_mercenaryNo").Scalar(blob, "MercenaryNo", 8);
        if (remove is not null && (!IsWorker(remove.Key) || !before.Contains(remove)))
            throw new InvalidOperationException("The mercenary selection changed. Load the save again.");
        if (remove is null && (!IsWorker(add) || add != RobotWorker && before.Any(c => c.Key == add)))
            throw new InvalidOperationException("This character cannot be added or is already in the roster.");
        if (remove is null) DlcContent.Refuse(add != RobotWorker);
        if (!list.Present) throw new InvalidOperationException("This save has no companion list yet.");
        // A list header holds the marker byte, then the count.
        byte[] countBytes = BitConverter.GetBytes(rows.Length + (remove is null ? 1 : -1));
        var regions = new List<(int Start, int End, Func<int, byte[]> Encode)> { (list.StartOffset + 1, list.StartOffset + 5, _ => countBytes) };
        Companion[] expected;
        if (remove is null)
        {
            if (!CanAddWorkers) throw new InvalidOperationException("This save's format has no room for a new worker yet. Recruit one in game first.");
            ulong number = NewNumbers(1)[0];
            // The game appends a recruit and stamps it with the current field time.
            var record = WorkerRecord(add, number, FieldTime())!;
            regions.Add((list.EndOffset, list.EndOffset, start => RecordLists.ObjectBytes(Layout, record, start)));
            expected = [.. before, new Companion(number, add, null)];
        }
        else
        {
            var row = rows.Single(r => NumberOf(r) == remove.Number);
            if (FactionWorkers().Contains(remove.Number)) throw new InvalidOperationException("A workplace or camp spawn record still names this worker, so it cannot be removed.");
            regions.Add((row.StartOffset, row.EndOffset, _ => []));
            expected = before.Where(c => c.Number != remove.Number).ToArray();
        }
        return CommitCompanions(RecordLists.RewriteRegions(blob, EditBuffer, regions), expected, "Worker verification failed.");
    }

    // Every object and list element in a record points at its own payload with an absolute u32 just before it, and list
    // headers hold only counts, so a record moves by shifting those pointers and nothing else.
    private byte[] MovedRecord(SaveField record, int start, ulong number, uint key = 0)
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
        if (key != 0) BinaryPrimitives.WriteUInt32LittleEndian(copy.AsSpan(record.ChildFields.Named("_characterKey").StartOffset - record.StartOffset), key);
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
        var rows = MercenaryList(blob, Layout).Elements("MercenarySaveData");
        SaveField Row(Companion c) => rows.Single(r => r.ChildFields.Named("_mercenaryNo").Scalar(blob, "MercenaryNo", 8) == c.Number);
        SaveField a = Row(first), b = Row(second);
        byte[] candidate = RecordLists.RewriteRegions(blob, EditBuffer,
            [(a.StartOffset, a.EndOffset, start => MovedRecord(b, start, first.Number)), (b.StartOffset, b.EndOffset, start => MovedRecord(a, start, second.Number))]);
        var expected = before.Select(c => c.Number == first.Number ? second with { Number = first.Number } : c.Number == second.Number ? first with { Number = second.Number } : c).ToArray();
        return CommitCompanions(candidate, expected, "Companion order verification failed.");
    }
}
