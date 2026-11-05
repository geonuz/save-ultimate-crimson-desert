using System.Buffers.Binary;
using SaveUltimate.Core.Catalogs;

namespace SaveUltimate.Core.Saves;

public sealed record ItemSocket(uint Key, ushort Endurance, bool HasGem)
{
    internal SaveField Record { get; init; } = new();
}

public sealed record SocketState(int Capacity, int Unlocked, IReadOnlyList<ItemSocket> Slots, string? Error = null)
{
    internal static readonly SocketState Empty = new(0, 0, Array.Empty<ItemSocket>());
    private SaveField valid = new();
    private int maskOffset, maskLength;

    internal static SocketState Read(byte[] raw, SaveField item)
    {
        try
        {
            var fields = item.ChildFields;
            int Count(string name) { var f = fields.Named(name); return f.Present ? (int)f.Scalar(raw, "TSocketSlotNo", 1) : 0; }
            var slots = fields.Named("_socketSaveDataList").Elements("ItemSocketSaveData").Select(record =>
            {
                var key = record.ChildFields.Named("_itemKey");
                var endurance = record.ChildFields.Named("_currentEndurance");
                return new ItemSocket(key.Present ? (uint)key.Scalar(raw, "ItemKey", 4) : 0,
                    endurance.Present ? (ushort)endurance.Scalar(raw, "TEndurance", 2) : (ushort)0, key.Present) { Record = record };
            }).ToArray();
            int capacity = Count("_maxSocketCount"), unlocked = Count("_validSocketCount");
            if (unlocked > capacity || capacity > slots.Length) throw new InvalidDataException("Socket counts exceed decoded records.");
            return new(capacity, unlocked, Array.AsReadOnly(slots))
            {
                valid = fields.Named("_validSocketCount"),
                maskOffset = item.StartOffset + 2, maskLength = item.ChildMaskByteCount
            };
        }
        catch (InvalidDataException error) { return new(0, 0, Array.Empty<ItemSocket>(), error.Message); }
    }

    internal byte[] SetUnlocked(byte[] raw, uint itemKey, int count, Func<int, byte[]> bufferForEdit)
    {
        if (Error is not null) throw new InvalidDataException(Error);
        int limit = ItemRules.Sockets(itemKey);
        if (limit == 0 || count < 0 || count > Math.Min(limit, Capacity))
            throw new InvalidOperationException("Socket count exceeds equipment limits.");
        if (Slots.Skip(count).Any(s => s.HasGem))
            throw new InvalidOperationException("Remove Abyss Gear before locking its socket.");
        if (count == Unlocked) return raw;
        if (valid.TypeName != "TSocketSlotNo" || valid.MetaKind is not 0 and not 2 || valid.MetaSize != 1 || valid.FieldIndex / 8 >= maskLength ||
            BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(maskOffset - 2)) != maskLength)
            throw new InvalidDataException("Unsupported omitted socket count layout.");
        // An absent field starts and ends where it would go, so this sets it or inserts it alike.
        int mask = maskOffset + valid.FieldIndex / 8;
        byte flagged = (byte)(raw[mask] | 1 << (valid.FieldIndex % 8));
        return RecordLists.RewriteRegions(raw, bufferForEdit, [(mask, mask + 1, _ => [flagged]), (valid.StartOffset, valid.EndOffset, _ => [(byte)count])]);
    }

    internal byte[] Replace(byte[] raw, ParcLayout layout, uint itemKey, uint[] gems, Func<int, byte[]> bufferForEdit)
    {
        int limit = ItemRules.Sockets(itemKey);
        if (Error is not null) throw new InvalidDataException(Error);
        if (limit == 0 || gems.Length > Math.Min(limit, Unlocked) || Slots.Skip(limit).Any(s => s.HasGem) || gems.Any(k => k > int.MaxValue))
            throw new InvalidOperationException("Socket edit exceeds equipment limits.");
        if (Slots.Count is < 1 or > 6) throw new InvalidDataException("Unsupported socket list.");
        if (gems.Select((key, i) => key == Slots[i].Key && (key != 0) == Slots[i].HasGem).All(same => same)) return raw;
        int index = layout.TypeIndex("ItemSocketSaveData");
        var type = layout.Types[index];
        var regions = new List<(int Start, int End, Func<int, byte[]> Encode)>();
        for (int slot = 0; slot < gems.Length; slot++)
        {
            var current = Slots[slot];
            uint key = gems[slot];
            if ((key != 0) == current.HasGem && key == current.Key) continue;
            var values = RecordLists.Fields(raw, type, current.Record) ?? throw new InvalidDataException("Unsupported socket record layout.");
            values.Remove("_currentEndurance");
            values.Remove("_itemKey");
            if (key != 0)
            {
                values["_currentEndurance"] = BitConverter.GetBytes(current.HasGem ? current.Endurance : ushort.MaxValue);
                values["_itemKey"] = BitConverter.GetBytes(key);
            }
            regions.Add((current.Record.StartOffset, current.Record.EndOffset, start => RecordLists.RowBytes(start, type, index, values)));
        }
        return RecordLists.RewriteRegions(raw, bufferForEdit, regions);
    }
}
