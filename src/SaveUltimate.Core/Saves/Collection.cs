using System.Buffers.Binary;

namespace SaveUltimate.Core.Saves;

/// <summary>A knowledge entry the game teaches with a collection item, and the learn path it records (Read, Zoom or None).</summary>
public sealed record CollectionKnowledge(uint Key, uint From);
/// <summary>An item the collection box can hold (CollectionCatalogBuilder). Base items exist without the DLC.</summary>
public sealed record CollectionEntry(uint Key, bool Base, CollectionKnowledge[] Knowledge);
public sealed record CollectionCatalog(CollectionEntry[] Items)
{
    public static CollectionCatalog Shared { get; } = Embedded.Load<CollectionCatalog>("Collection.json");
}

/// <summary>Collected: in the box. Elsewhere: held in another bag (quest items, bought maps), so filling would duplicate it.</summary>
public enum CollectionStatus { Missing, Collected, Elsewhere }
public sealed record CollectionState(CollectionEntry Entry, CollectionStatus Status, bool Learned);

public sealed partial class SaveDocument
{
    private const ushort CollectionBag = 18;

    /// <summary>Every item the box can hold (base items only while the DLC is hidden), in the game's order.</summary>
    public CollectionState[] ReadCollection()
    {
        var levels = ReadKnowledge();
        var held = Items.GroupBy(i => i.Key).ToDictionary(g => g.Key, g => g.Any(i => i.Source == ItemSource.Inventory && i.Bag == CollectionBag));
        return CollectionCatalog.Shared.Items.Where(e => e.Base || !DlcContent.Hidden).Select(e => new CollectionState(e,
            !held.TryGetValue(e.Key, out bool inBox) ? CollectionStatus.Missing : inBox ? CollectionStatus.Collected : CollectionStatus.Elsewhere,
            e.Knowledge.All(k => levels.GetValueOrDefault(k.Key) > 0))).ToArray();
    }

    // The game's own box rows (36 saves, 2.00-2.04.08): one of each item, five empty sockets, one charge, pushed now.
    private static readonly (string Name, string Type, int Kind, int Size)[] BoxRowFields =
        [("_saveVersion", "uint32", 0, 4), ("_itemNo", "ItemNo", 0, 8), ("_itemKey", "ItemKey", 0, 4), ("_slotNo", "TItemSlotNo", 0, 2),
         ("_stackCount", "TStackCount", 0, 8), ("_endurance", "TEndurance", 0, 2), ("_maxSocketCount", "TSocketSlotNo", 0, 1),
         ("_socketSaveDataList", "ReflectObject", 6, 0), ("_transferredItemKey", "ItemKey", 0, 4), ("_maxChargeUseableCount", "uint32", 0, 4),
         ("_chargedUseableCount", "TickCount64", 0, 8), ("_timeWhenPushItem", "Ctc64", 0, 8)];

    /// <summary>Puts the missing ones of <paramref name="keys"/> in the collection box, and with <paramref name="learn"/> also
    /// teaches the knowledge of every chosen item not held elsewhere, recording the path reading it would.</summary>
    public CollectionState[] FillCollection(IEnumerable<uint> keys, bool learn)
    {
        var chosen = keys.ToHashSet();
        var before = ReadCollection();
        var states = before.Where(s => chosen.Contains(s.Entry.Key) && s.Status != CollectionStatus.Elsewhere).ToArray();
        var add = states.Where(s => s.Status == CollectionStatus.Missing).Select(s => s.Entry.Key).ToArray();
        DlcContent.Refuse(states.Any(s => !s.Entry.Base));
        var levels = ReadKnowledge();
        // Reading records Read or Zoom. The one players hold wins over the item's inspect action: paper notices and wanted
        // posters with the zoom action are Read in every save that has them.
        var teach = learn ? states.SelectMany(s => s.Entry.Knowledge).Where(k => levels.GetValueOrDefault(k.Key) == 0).DistinctBy(k => k.Key)
            .Select(k => k.From == (uint)LearnFrom.None ? k : k with { From = LearnPathCatalog.Shared.Among(k.Key, LearnFrom.Read, LearnFrom.Zoom) ?? k.From }).ToArray() : [];
        DlcContent.Refuse(teach.Any(k => DlcContent.Shared.Knowledge.Contains(k.Key)));
        if (add.Length == 0 && teach.Length == 0) return before;

        var regions = new List<(int Start, int End, Func<int, byte[]> Encode)>();
        if (add.Length > 0) regions.AddRange(BoxRegions(add));
        if (teach.Length > 0)
        {
            var block = SaveRecords.Read(blob, Layout, "KnowledgeSaveData");
            var follow = SkillState.Read(blob, block).Follow;
            // A save with no learn path at all has no list to add to; the knowledge is still learned.
            var paths = !block.Fields.Named("_learnedFollowLearnKnowledge").Present ? []
                : teach.Where(k => !follow.ContainsKey(k.Key)).ToDictionary(k => k.Key, k => k.From);
            regions.AddRange(RecordLists.Regions(blob, Layout, KnowledgeEdits(block, levels, teach.ToDictionary(k => k.Key, _ => 1u), paths)));
        }
        byte[] candidate = RecordLists.RewriteRegions(blob, EditBuffer, regions);
        string[] changed = [.. add.Length > 0 ? ["InventorySaveData"] : Array.Empty<string>(), .. teach.Length > 0 ? ["KnowledgeSaveData"] : Array.Empty<string>()];
        Transact(candidate, changed, (layout, items) =>
        {
            var box = items.Where(i => i.Source == ItemSource.Inventory && i.Bag == CollectionBag).Select(i => i.Key).ToList();
            var learned = KnowledgeLevels(candidate, SaveRecords.Read(candidate, layout, "KnowledgeSaveData"));
            if (add.Any(k => box.Count(b => b == k) != 1) || teach.Any(k => learned.GetValueOrDefault(k.Key) != 1))
                throw new InvalidDataException("Collection verification failed.");
        });
        return ReadCollection();
    }

    // Rows go after the box's last row, or into a new list when the box was never used (the bag then holds no list at all).
    private IEnumerable<(int Start, int End, Func<int, byte[]> Encode)> BoxRegions(uint[] keys)
    {
        var item = Layout.Types.Single(t => t.Name == "ItemSaveData");
        if (!BoxRowFields.All(w => item.Fields.Any(f => (f.Name, f.TypeName, (int)f.Kind, (int)f.Size) == w)) || !Layout.Types.Any(t => t.Name == "ItemSocketSaveData"))
            throw new InvalidDataException("Unsupported item record layout.");
        var bag = SaveRecords.Read(blob, Layout, "InventorySaveData").Fields.Named("_inventorylist").Elements("InventoryElementSaveData")
            .SingleOrDefault(b => b.ChildFields.Named("_inventoryKey").Scalar(blob, "InventoryKey", 2) == CollectionBag)
            ?? throw new InvalidOperationException("This save has no collection box yet. Play until the collection box opens first.");
        var list = bag.ChildFields.Named("_itemList");
        var rows = list.Present ? list.Elements("ItemSaveData") : [];
        var used = rows.Select(r => r.ChildFields.Named("_slotNo")).Where(f => f.Present).Select(f => f.Scalar(blob, "TItemSlotNo", 2)).ToHashSet();
        ulong[] slots = Enumerable.Range(0, ushort.MaxValue).Select(i => (ulong)i).Where(s => !used.Contains(s)).Take(keys.Length).ToArray();
        ulong[] numbers = NewNumbers(keys.Length);
        ulong pushed = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        static byte[] Bytes(ulong value, int size) { byte[] b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, value); return b[..size]; }
        var sockets = Enumerable.Range(0, 5).Select(_ => new RecordLists.Nested("ItemSocketSaveData", new Dictionary<string, object>())).ToArray();
        byte[] Rows(int start)
        {
            using var output = new MemoryStream();
            for (int i = 0; i < keys.Length; i++)
                output.Write(RecordLists.ObjectBytes(Layout, new("ItemSaveData", new Dictionary<string, object>
                {
                    ["_saveVersion"] = Bytes(1, 4), ["_itemNo"] = Bytes(numbers[i], 8), ["_itemKey"] = Bytes(keys[i], 4), ["_slotNo"] = Bytes(slots[i], 2),
                    ["_stackCount"] = Bytes(1, 8), ["_endurance"] = Bytes(ushort.MaxValue, 2), ["_maxSocketCount"] = Bytes(5, 1), ["_socketSaveDataList"] = sockets,
                    ["_transferredItemKey"] = Bytes(keys[i], 4), ["_maxChargeUseableCount"] = Bytes(1, 4), ["_chargedUseableCount"] = Bytes(1, 8), ["_timeWhenPushItem"] = Bytes(pushed, 8)
                }), checked(start + (int)output.Length)));
            return output.ToArray();
        }
        if (list.Present)
        {
            if ((rows.Length > 0 ? rows[^1].EndOffset : list.StartOffset + 18) != list.EndOffset) throw new InvalidDataException("Collection list does not end with its last row.");
            byte[] count = BitConverter.GetBytes(rows.Length + keys.Length);
            return [(list.StartOffset + 1, list.StartOffset + 5, _ => count), (list.EndOffset, list.EndOffset, Rows)];
        }
        // An absent list is a single 01 byte, and its mask bit is clear.
        int maskByte = bag.StartOffset + 2 + list.FieldIndex / 8;
        if (list.EndOffset != list.StartOffset + 1 || blob[list.StartOffset] != 1 || BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(bag.StartOffset)) != bag.ChildMaskByteCount)
            throw new InvalidDataException("Unsupported empty collection box.");
        byte mask = (byte)(blob[maskByte] | 1 << (list.FieldIndex % 8));
        byte[] header = new byte[18]; BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), keys.Length);
        return [(maskByte, maskByte + 1, _ => [mask]), (list.StartOffset, list.EndOffset, start => [.. header, .. Rows(start + 18)])];
    }
}
