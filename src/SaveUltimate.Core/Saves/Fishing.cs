using System.Text.Json;

namespace SaveUltimate.Core.Saves;

public sealed record Fish(uint Key, string Name, uint GroupKey, string GroupName, bool RecordSupported);
public sealed record FishingCatalog(Fish[] Fish)
{
    public static FishingCatalog Shared { get; } = Load();
    private static FishingCatalog Load()
    {
        using var stream = typeof(FishingCatalog).Assembly.GetManifestResourceStream("Fishing.json")!;
        return JsonSerializer.Deserialize<FishingCatalog>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
public sealed record FishingRecord(Fish Fish, uint Level, float? Size, bool RecordTypeAvailable);

public sealed partial class SaveDocument
{
    private static Dictionary<uint, (float Size, SaveField Field)> FishRecords(byte[] raw, SaveField list)
    {
        var result = new Dictionary<uint, (float, SaveField)>();
        if (!list.Present) return result;
        foreach (var row in list.Elements("MiniGameRecordSaveData"))
        {
            uint key = checked((uint)row.ChildFields.Named("_key").Scalar(raw, "KnowledgeKey", 4));
            var field = row.ChildFields.Named("_record");
            float size = BitConverter.UInt32BitsToSingle(checked((uint)field.Scalar(raw, "float", 4)));
            if (!float.IsFinite(size) || !result.TryAdd(key, (size, field)))
                throw new InvalidDataException("Duplicate key or non-finite catch record.");
        }
        return result;
    }

    private static FishingRecord[] FishRows(ParcLayout layout, SaveField list,
        IReadOnlyDictionary<uint, (float Size, SaveField Field)> records, IReadOnlyDictionary<uint, uint> levels)
    {
        bool available = list.Present && layout.Types.Any(t => t.Name == "MiniGameRecordSaveData");
        return FishingCatalog.Shared.Fish.Select(f => new FishingRecord(f, levels.GetValueOrDefault(f.Key),
            records.TryGetValue(f.Key, out var r) ? r.Size : null, available)).ToArray();
    }

    public FishingRecord[] ReadFishing()
    {
        var list = SaveRecords.Read(blob, Layout, "ContentsMiscSaveData").Fields.Named("_miniGameRecords");
        return FishRows(Layout, list, FishRecords(blob, list), ReadKnowledge());
    }

    public FishingRecord[] SetFishingRecord(uint key, double sizeCm)
    {
        if (!FishingCatalog.Shared.Fish.Any(f => f.Key == key && f.RecordSupported))
            throw new InvalidOperationException("Catch record editing is not verified for this fish.");
        DlcContent.Refuse(DlcContent.Shared.Knowledge.Contains(key));
        if (!double.IsFinite(sizeCm) || sizeCm <= 0 || sizeCm > 1000 || (float)sizeCm <= 0)
            throw new ArgumentOutOfRangeException(nameof(sizeCm), "Size must be greater than 0 and at most 1000 cm.");
        float size = (float)sizeCm;
        var list = SaveRecords.Read(blob, Layout, "ContentsMiscSaveData").Fields.Named("_miniGameRecords");
        var records = FishRecords(blob, list);
        var levels = ReadKnowledge();
        if (levels.GetValueOrDefault(key) == 0) throw new InvalidOperationException("Learn the fish knowledge first.");
        if (!list.Present || !Layout.Types.Any(t => t.Name == "MiniGameRecordSaveData"))
            throw new InvalidOperationException("Catch one fish in game, save, and load the save again.");
        if (records.TryGetValue(key, out var existing))
        {
            Edit([CreatePatch(existing.Field.StartOffset, BitConverter.GetBytes(size))]);
            records[key] = (size, existing.Field);
            return FishRows(Layout, list, records, levels);
        }
        var type = Layout.Types.Single(t => t.Name == "MiniGameRecordSaveData");
        if (!type.Fields.Select(f => (f.Name, f.TypeName, (int)f.Kind, (int)f.Size)).SequenceEqual(new[]
            { ("_key", "KnowledgeKey", 0, 4), ("_record", "float", 0, 4) }))
            throw new InvalidDataException("Unsupported catch record insertion layout.");
        byte[] candidate = RecordLists.Rewrite(blob, Layout, EditBuffer,
            new ListEdit(list, "MiniGameRecordSaveData", "_key", new()
            {
                [key] = new() { ["_key"] = new("KnowledgeKey", 4, key), ["_record"] = new("float", 4, BitConverter.SingleToUInt32Bits(size)) }
            }));
        FishingRecord[] result = [];
        Transact(candidate, ["ContentsMiscSaveData"], (layout, _) =>
        {
            var current = SaveRecords.Read(candidate, layout, "ContentsMiscSaveData").Fields.Named("_miniGameRecords");
            var after = FishRecords(candidate, current);
            if (after.Count != records.Count + 1 || after.GetValueOrDefault(key).Size != size ||
                records.Any(p => !after.TryGetValue(p.Key, out var value) || value.Size != p.Value.Size))
                throw new InvalidDataException("Catch record verification failed.");
            result = FishRows(layout, current, after, levels);
        });
        return result;
    }
}
