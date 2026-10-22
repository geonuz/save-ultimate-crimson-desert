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
