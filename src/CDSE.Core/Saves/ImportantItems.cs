using System.Buffers.Binary;

namespace CDSE.Core.Saves;

// Important items. A quest item (iteminfo's pickup bag is the quest bag, 5) stays an important item, shown purple, outside the
// inventory limit and impossible to store, sell or drop, until the game lists it in InventorySaveData._inventoryChangedItemDataList
// as moved to the inventory (2). Reading a document that teaches new knowledge does that and moves its row to bag 2. In every
// save played without an editor (32 saves, five players) each quest item outside bag 5 is listed and none in it is; a quest
// item in the collection box that is not listed shows as important in the box too (game check). Only the collection box's
// quest items are handled here: their key list is in the collection catalog, and the box takes one of each.
public sealed partial class SaveDocument
{
    private const string MovedType = "InventorySaveData_InventoryChangedItemData";
    private const ushort QuestBag = 5, InventoryBag = 2;

    private static SaveField MovedList(byte[] raw, ParcLayout layout)
        => SaveRecords.Read(raw, layout, "InventorySaveData").Fields.Named("_inventoryChangedItemDataList");
    private static HashSet<uint> MovedKeys(byte[] raw, ParcLayout layout) => MovedList(raw, layout) is { Present: true } list
        ? list.Elements(MovedType).Select(r => (uint)r.ChildFields.Named("_itemKey").Scalar(raw, "ItemKey", 4)).ToHashSet() : [];
    private static HashSet<uint> QuestCollectionKeys()
        => CollectionCatalog.Shared.Items.Where(e => e.Quest && (e.Base || !DlcContent.Hidden)).Select(e => e.Key).ToHashSet();

    /// <summary>Quest items of the collection box that are not listed as moved out of the quest bag: those in the box, and those of
    /// <paramref name="add"/> about to go in. A key also held in the quest bag is left out, as the game never lists those.</summary>
    internal uint[] ImportantInBox(uint[] add)
    {
        var listed = MovedKeys(blob, Layout);
        var quest = QuestCollectionKeys();
        var held = Items.Where(i => i.Source == ItemSource.Inventory).ToLookup(i => i.Bag, i => i.Key);
        var inQuestBag = held[QuestBag].ToHashSet();
        return held[CollectionBag].Concat(add).Where(k => quest.Contains(k) && !listed.Contains(k) && !inQuestBag.Contains(k)).Distinct().Order().ToArray();
    }

    /// <summary>Box items the game shows as important, by key. Save Doctor reports these.</summary>
    public uint[] ImportantCollectionItems() => ImportantInBox([]);
    /// <summary>Whether this save has the list the game records moved quest items in; without it nothing can be released.</summary>
    public bool CanReleaseImportantItems => MovedList(blob, Layout).Present;

    /// <summary>Lists every important item in the collection box as moved to the inventory, as the game does after reading it.</summary>
    public uint[] ReleaseCollection()
    {
        uint[] keys = ImportantInBox([]);
        if (keys.Length > 0) FillCollection([], learn: false);
        return keys;
    }

    // The rows that list keys as moved to the inventory. A save without the list (a new game before any quest item was moved) is
    // refused: putting a quest item in the box there would make it important.
    private IEnumerable<(int Start, int End, Func<int, byte[]> Encode)> MovedRegions(uint[] keys)
    {
        var list = MovedList(blob, Layout);
        if (!list.Present) throw new InvalidOperationException("This save has no list of items moved out of the quest bag yet, so quest items cannot go in the box as normal items. Read one document in the game, save, and try again.");
        if (!Layout.Types.Single(t => t.Name == MovedType).Is(("_itemKey", "ItemKey", 0, 4), ("_changedInventoryKey", "InventoryKey", 0, 2)))
            throw new InvalidDataException("Unsupported moved item layout.");
        return RecordLists.Regions(blob, Layout, [new(list, MovedType, "_itemKey", keys.ToDictionary(k => k, k => new Dictionary<string, ScalarValue>
            { ["_itemKey"] = new("ItemKey", 4, k), ["_changedInventoryKey"] = new("InventoryKey", 2, InventoryBag) }))]);
    }

    /// <summary>Keys of collection quest items not listed as moved: one of these in the quest bag is an important item
    /// <see cref="ReleaseQuestItems"/> can move to the inventory. Empty when the save has no list to add to.</summary>
    public IReadOnlySet<uint> ReleasableKeys()
    {
        if (!CanReleaseImportantItems) return new HashSet<uint>();
        var listed = MovedKeys(blob, Layout);
        return QuestCollectionKeys().Where(k => !listed.Contains(k)).ToHashSet();
    }
    public static bool InQuestBag(InventoryItem item) => item.Source == ItemSource.Inventory && item.Bag == QuestBag;

    /// <summary>Moves collection quest items from the quest bag to the inventory and lists them as moved, as the game does when
    /// reading one teaches new knowledge. Each keeps its item number and takes the lowest free inventory slot.</summary>
    public void ReleaseQuestItems(IEnumerable<InventoryItem> selected)
    {
        var chosen = ValidateSelection(selected, requireStackEditable: false);
        var keys = ReleasableKeys();
        if (chosen.Length == 0 || chosen.Any(i => !InQuestBag(i) || !keys.Contains(i.Key))) throw new InvalidOperationException("Only important documents in the quest bag that the collection box can hold can be released.");
        if (chosen.Select(i => i.Key).Distinct().Count() != chosen.Length) throw new InvalidOperationException("Release one of each item at a time.");
        var bags = SaveRecords.Read(blob, Layout, "InventorySaveData").Fields.Named("_inventorylist").Elements("InventoryElementSaveData");
        SaveField Bag(ushort key) => bags.SingleOrDefault(b => b.ChildFields.Named("_inventoryKey").Scalar(blob, "InventoryKey", 2) == key)?.ChildFields.Named("_itemList")
            is { Present: true } list ? list : throw new InvalidDataException($"Bag {key} has no item list.");
        SaveField from = Bag(QuestBag), to = Bag(InventoryBag);
        var fromRows = from.Elements("ItemSaveData"); var toRows = to.Elements("ItemSaveData");
        var rows = chosen.Select(i => fromRows.Single(r => r.StartOffset == i.RecordOffset)).OrderBy(r => r.StartOffset).ToArray();
        if (toRows.Length == 0 || toRows[^1].EndOffset != to.EndOffset || fromRows[^1].EndOffset != from.EndOffset) throw new InvalidDataException("Item list does not end with its last row.");
        var used = toRows.Select(r => r.ChildFields.Named("_slotNo")).Where(f => f.Present).Select(f => f.Scalar(blob, "TItemSlotNo", 2)).ToHashSet();
        ulong[] slots = Enumerable.Range(0, ushort.MaxValue).Select(i => (ulong)i).Where(s => !used.Contains(s)).Take(rows.Length).ToArray();
        var moved = rows.Select((r, i) =>
        {
            var value = Decode(r);
            // Rewriting the row where it is must give back its bytes, or this reader misses part of it.
            if (!RecordLists.ObjectBytes(Layout, value, r.StartOffset).AsSpan().SequenceEqual(blob.AsSpan(r.StartOffset, r.EndOffset - r.StartOffset)))
                throw new InvalidDataException("Unsupported item record.");
            var values = new Dictionary<string, object>(value.Values) { ["_slotNo"] = BitConverter.GetBytes((ushort)slots[i]) };
            return value with { Values = values };
        }).ToArray();
        static byte[] Count(int value) => BitConverter.GetBytes(value);
        var regions = new List<(int Start, int End, Func<int, byte[]> Encode)>
        {
            (from.StartOffset + 1, from.StartOffset + 5, _ => Count(fromRows.Length - rows.Length)),
            (to.StartOffset + 1, to.StartOffset + 5, _ => Count(toRows.Length + rows.Length)),
            (to.EndOffset, to.EndOffset, start =>
            {
                using var output = new MemoryStream();
                foreach (var row in moved) output.Write(RecordLists.ObjectBytes(Layout, row, checked(start + (int)output.Length)));
                return output.ToArray();
            })
        };
        regions.AddRange(rows.Select(r => (r.StartOffset, r.EndOffset, (Func<int, byte[]>)(_ => []))));
        regions.AddRange(MovedRegions(chosen.Select(i => i.Key).Order().ToArray()));
        byte[] candidate = RecordLists.RewriteRegions(blob, EditBuffer, regions);
        Transact(candidate, ["InventorySaveData"], (layout, items) =>
        {
            var listed = MovedKeys(candidate, layout);
            if (chosen.Any(c => !listed.Contains(c.Key) || items.Count(i => i.Source == ItemSource.Inventory && i.Number == c.Number) != 1 ||
                items.Single(i => i.Source == ItemSource.Inventory && i.Number == c.Number) is not { Bag: InventoryBag } after || after.Key != c.Key || after.Quantity != c.Quantity))
                throw new InvalidDataException("Release verification failed.");
        });
    }

    // A record's present fields as ObjectBytes takes them: fixed fields as bytes, objects as Nested, object lists as Nested[].
    private RecordLists.Nested Decode(SaveField record)
    {
        var values = new Dictionary<string, object>();
        foreach (var f in record.ChildFields.Where(f => f.Present))
            values[f.Name] = f.MetaKind switch
            {
                0 or 2 when f.MetaSize > 0 && f.EndOffset - f.StartOffset == f.MetaSize => blob[f.StartOffset..f.EndOffset],
                4 => Decode(f),
                6 => f.ListElements.Select(Decode).ToArray(),
                _ => throw new InvalidDataException($"Unsupported item field {f.Name}.")
            };
        return new(record.ChildTypeName, values);
    }
}
