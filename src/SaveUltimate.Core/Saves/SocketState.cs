using System.Buffers.Binary;
using System.Text;
using SaveUltimate.Core.Catalogs;

namespace SaveUltimate.Core.Saves;

public sealed record ItemSocket(uint Key, ushort Endurance, bool HasGem)
{
    internal SaveField Record { get; init; } = new();
}

public sealed record SocketState(int Capacity, int Unlocked, IReadOnlyList<ItemSocket> Slots, string? Error = null)
{
    internal static readonly SocketState Empty = new(0, 0, Array.Empty<ItemSocket>());
    private static Dictionary<uint, int> Limits = LoadLimits();
    internal static void SetLimits(Dictionary<uint, int>? values) => Limits = values ?? LoadLimits();
    public static int Limit(uint key) => Limits.GetValueOrDefault(key);
    private SaveField maximum = new(), valid = new();
    private int maskOffset, maskLength;
    internal bool HasUnlockedField => valid.Present;
    internal SocketState Relocate(int delta) => delta == 0 || Error is not null ? this : this with
    {
        maximum = maximum.Relocate(delta), valid = valid.Relocate(delta),
        maskOffset = checked(maskOffset + delta),
        Slots = Array.AsReadOnly(Slots.Select(s => s with { Record = s.Record.Relocate(delta) }).ToArray())
    };
    internal IEnumerable<(int Offset, int Size)> ScalarFields
    {
        get
        {
            if (Error is not null) yield break;
            if (valid.Present) yield return (valid.StartOffset, 1);
            foreach (var slot in Slots.Where(s => s.HasGem))
                yield return (slot.Record.ChildFields.Named("_itemKey").StartOffset, 4);
        }
    }

    internal SocketState RefreshScalars(byte[] raw) => this with
    {
        Unlocked = valid.Present ? checked((int)valid.Scalar(raw, "TSocketSlotNo", 1)) : 0,
        Slots = Array.AsReadOnly(Slots.Select(slot => slot with
        {
            Key = slot.HasGem ? checked((uint)slot.Record.ChildFields.Named("_itemKey").Scalar(raw, "ItemKey", 4)) : 0
        }).ToArray())
    };

    private static Dictionary<uint, int> LoadLimits()
    {
        using var stream = typeof(SocketState).Assembly.GetManifestResourceStream("ItemCatalog.json")!;
        return ItemRules.ReadSocketLimits(stream);
    }

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
                maximum = fields.Named("_maxSocketCount"), valid = fields.Named("_validSocketCount"),
                maskOffset = item.StartOffset + 2, maskLength = item.ChildMaskByteCount
            };
        }
        catch (InvalidDataException error) { return new(0, 0, Array.Empty<ItemSocket>(), error.Message); }
    }

    internal byte[] SetUnlocked(byte[] raw, uint itemKey, int count, Func<int, byte[]> bufferForEdit)
    {
        if (Error is not null) throw new InvalidDataException(Error);
        int limit = Limit(itemKey);
        if (limit == 0 || count < 0 || count > Math.Min(limit, Capacity))
            throw new InvalidOperationException("Socket count exceeds equipment limits.");
        if (Slots.Skip(count).Any(s => s.HasGem))
            throw new InvalidOperationException("Remove Abyss Gear before locking its socket.");
        if (count == Unlocked) return raw;
        if (valid.Present)
        {
            valid.Scalar(raw, "TSocketSlotNo", 1);
            byte[] candidate = bufferForEdit(raw.Length);
            raw.CopyTo(candidate, 0);
            candidate[valid.StartOffset] = (byte)count;
            return candidate;
        }
        maximum.Scalar(raw, "TSocketSlotNo", 1);
        if (valid.TypeName != "TSocketSlotNo" || valid.MetaKind is not 0 and not 2 || valid.MetaSize != 1 ||
            valid.FieldIndex != maximum.FieldIndex + 1 || valid.FieldIndex < 0 || valid.FieldIndex / 8 >= maskLength ||
            maskOffset < 2 || maskOffset + maskLength > maximum.StartOffset ||
            BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(maskOffset - 2)) != maskLength ||
            (raw[maskOffset + valid.FieldIndex / 8] & (1 << (valid.FieldIndex % 8))) != 0)
            throw new InvalidDataException("Unsupported omitted socket count layout.");
        using var packet = new MemoryStream();
        using var writer = new BinaryWriter(packet);
        writer.Write((uint)maximum.EndOffset); writer.Write((uint)maximum.EndOffset); writer.Write(1u); writer.Write((byte)count);
        byte[] result = RecordLists.Relocate(raw, packet.ToArray(), bufferForEdit);
        result[maskOffset + valid.FieldIndex / 8] |= (byte)(1 << (valid.FieldIndex % 8));
        return result;
    }

    internal byte[] Replace(byte[] raw, uint itemKey, uint[] gems, Func<int, byte[]> bufferForEdit)
    {
        int limit = Limit(itemKey);
        if (Error is not null) throw new InvalidDataException(Error);
        if (limit == 0 || gems.Length > Math.Min(limit, Unlocked) || Slots.Skip(limit).Any(s => s.HasGem) || gems.Any(k => k > int.MaxValue))
            throw new InvalidOperationException("Socket edit exceeds equipment limits.");
        if (Slots.Count is < 1 or > 6) throw new InvalidDataException("Unsupported socket list.");
        if (gems.Select((key, i) => key == Slots[i].Key && (key != 0) == Slots[i].HasGem).All(same => same)) return raw;
        if (gems.Select((key, i) => (key != 0) == Slots[i].HasGem).All(same => same))
        {
            byte[] candidate = bufferForEdit(raw.Length);
            raw.CopyTo(candidate, 0);
            for (int i = 0; i < gems.Length; i++)
                if (gems[i] != 0)
                {
                    var field = Slots[i].Record.ChildFields.Named("_itemKey");
                    field.Scalar(raw, "ItemKey", 4);
                    BinaryPrimitives.WriteUInt32LittleEndian(candidate.AsSpan(field.StartOffset, 4), gems[i]);
                }
            return candidate;
        }
        using var packet = new MemoryStream();
        using var writer = new BinaryWriter(packet, Encoding.UTF8, true);
        int delta = 0;
        for (int slot = 0; slot < gems.Length; slot++)
        {
            var current = Slots[slot];
            uint key = gems[slot];
            if ((key != 0) == current.HasGem && key == current.Key) continue;
            var record = current.Record;
            int start = record.StartOffset, end = record.EndOffset;
            if (start < 0 || end > raw.Length || end - start < 26) throw new InvalidDataException("Invalid socket boundary.");
            byte mask = raw[start + 2];
            if (BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(start)) != 1 || mask is not 0 and not 3 ||
                end - start != (mask == 3 ? 32 : 26) || record.ChildPayloadOffset != start + 18 ||
                BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(start + 14)) != start + 18 ||
                BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(end - 4)) != end - start - 22)
                throw new InvalidDataException("Unsupported socket record layout.");
            byte[] replacement = new byte[key == 0 ? 26 : 32];
            raw.AsSpan(start, 22).CopyTo(replacement);
            replacement[2] = key == 0 ? (byte)0 : (byte)3;
            BinaryPrimitives.WriteUInt32LittleEndian(replacement.AsSpan(14), checked((uint)(start + delta + 18)));
            if (key != 0)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(replacement.AsSpan(22), current.HasGem ? current.Endurance : ushort.MaxValue);
                BinaryPrimitives.WriteUInt32LittleEndian(replacement.AsSpan(24), key);
            }
            BinaryPrimitives.WriteUInt32LittleEndian(replacement.AsSpan(replacement.Length - 4), (uint)replacement.Length - 22);
            writer.Write((uint)start); writer.Write((uint)end); writer.Write((uint)replacement.Length); writer.Write(replacement);
            delta += replacement.Length - (end - start);
        }
        if (packet.Length == 0) return raw;
        return RecordLists.Relocate(raw, packet.ToArray(), bufferForEdit);
    }

}
