using System.Buffers.Binary;
using CDSE.Core.Catalogs;

namespace CDSE.Core.Saves;

/// <summary>What a quantity does to an item: Set writes the number on its row, Split makes one row per item, Refused cannot be done.</summary>
public enum QuantityEffect { Set, Split, Refused }

// Quantities and new rows. A bag keeps one row per stacking item, whatever the number, and the game shows it over as many slots
// as its stack limit asks for (no save has two rows of one stacking item in a bag). An item that never stacks has one row each,
// with its own number; only the bank piles those up (gold bars).
public sealed partial class SaveDocument
{
    public const ulong MaxQuantity = 999_999_999;
    private const ushort StorageBag = 8, BankBag = 10, WardrobeBag = 15;
    // Inventory table (2.05.01): slots a bag starts with and can grow to. Only the inventory records how far it has grown.
    private const int InventorySlots = 50;
    private static readonly Dictionary<ushort, int> MostSlots = new() { [InventoryBag] = 240, [StorageBag] = 1000, [WardrobeBag] = 1000 };

    /// <summary>What setting this quantity would do.</summary>
    public static QuantityEffect EffectOf(InventoryItem item, ulong value)
    {
        if (value is 0 or > MaxQuantity || !item.CanEditQuantity) return QuantityEffect.Refused;
        if (value == 1 || ItemRules.Stack(item.Key) != 1 || item.Source == ItemSource.Inventory && item.Bag == BankBag) return QuantityEffect.Set;
        return item.Source == ItemSource.Equipment || item.Source == ItemSource.Inventory && MostSlots.ContainsKey(item.Bag) && !item.HoldsContents
            ? QuantityEffect.Split : QuantityEffect.Refused;
    }
    /// <summary>Slots the game shows for a number of an item.</summary>
    public static ulong SlotsFor(uint key, ulong quantity) => ItemRules.Stack(key) is 0 or 1 ? quantity : (quantity + ItemRules.Stack(key) - 1) / ItemRules.Stack(key);
    /// <summary>The bag a split item's copies go to. Worn equipment has no bag of its own, so its copies go to the inventory.</summary>
    public static ushort CopyBag(InventoryItem item) => item.Source == ItemSource.Inventory ? item.Bag : InventoryBag;

    public void SetQuantity(InventoryItem item, ulong value) => SetQuantity([item], value);

    /// <summary>Sets the quantity of every item, as one edit. Items that never stack become that many separate items, each a copy
    /// of the chosen one with its own number.</summary>
    public void SetQuantity(IEnumerable<InventoryItem> selected, ulong value)
    {
        if (value is 0 or > MaxQuantity) throw new ArgumentOutOfRangeException(nameof(value), "Quantity must be 1–999,999,999.");
        var items = ValidateSelection(selected);
        if (items.Any(i => EffectOf(i, value) == QuantityEffect.Refused))
            throw new InvalidOperationException("This item cannot be more than one here.");
        var split = items.Where(i => EffectOf(i, value) == QuantityEffect.Split).ToArray();
        if (split.Length == 0)
        {
            byte[] bytes = new byte[8];
            BinaryPrimitives.WriteUInt64LittleEndian(bytes, value);
            Edit(items.Select(item => CreatePatch(item.QuantityOffset, bytes)));
            return;
        }
        var records = OwnedRecords();
        var rows = new List<(ushort Bag, RecordLists.Nested Row)>();
        foreach (var bag in split.GroupBy(CopyBag)) Room(bag.Key, checked((decimal)bag.Count() * (value - 1)));
        foreach (var item in split)
        {
            var row = Copyable(records[item.RecordOffset]);
            for (ulong i = 1; i < value; i++) rows.Add((CopyBag(item), row));
        }
        var patches = items.Select(i => (i.QuantityOffset, i.QuantityOffset + 8, BitConverter.GetBytes(EffectOf(i, value) == QuantityEffect.Split ? 1UL : value)));
        AddRows(rows, patches);
    }

    /// <summary>Puts this many of each item in the inventory: on the item's row when it stacks and has one, otherwise on new rows.</summary>
    public void AddItems(IEnumerable<uint> keys, ulong quantity)
    {
        uint[] chosen = keys.Distinct().ToArray();
        if (chosen.Length == 0) return;
        if (quantity is 0 or > MaxQuantity) throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be 1–999,999,999.");
        if (chosen.Any(k => !ItemRules.CanAdd(k))) throw new InvalidOperationException("This item cannot be added.");
        var held = Items.Where(i => i.Source == ItemSource.Inventory && i.Bag == InventoryBag).ToLookup(i => i.Key);
        var rows = new List<(ushort, RecordLists.Nested)>();
        var patches = new List<(int, int, byte[])>();
        Room(InventoryBag, SlotsToAdd(chosen, quantity));
        foreach (uint key in chosen)
        {
            bool stacks = ItemRules.Stack(key) != 1;
            if (stacks && held[key].FirstOrDefault() is { } row)
            {
                if (row.Quantity + quantity > MaxQuantity) throw new InvalidOperationException("Quantity must be 1–999,999,999.");
                patches.Add((row.QuantityOffset, row.QuantityOffset + 8, BitConverter.GetBytes(row.Quantity + quantity)));
            }
            else for (ulong i = 0; i < (stacks ? 1 : quantity); i++) rows.Add((InventoryBag, PlainRow(key, stacks ? quantity : 1)));
        }
        AddRows(rows, patches);
    }
    /// <summary>Inventory slots <see cref="AddItems"/> would take.</summary>
    public decimal SlotsToAdd(IEnumerable<uint> keys, ulong quantity)
    {
        var held = Items.Where(i => i.Source == ItemSource.Inventory && i.Bag == InventoryBag).ToLookup(i => i.Key, i => i.Quantity);
        return keys.Distinct().Sum(key => ItemRules.Stack(key) != 1 && held.Contains(key)
            ? (decimal)(SlotsFor(key, held[key].First() + quantity) - SlotsFor(key, held[key].First())) : SlotsFor(key, quantity));
    }

    /// <summary>Slots a bag shows as taken, and has. Only for a bag that split items and new ones go to.</summary>
    public (decimal Used, int Total) BagSpace(ushort bag = InventoryBag) => (UsedSlots(bag), SlotLimit(bag));
    private decimal UsedSlots(ushort bag) => Items.Where(i => i.Source == ItemSource.Inventory && i.Bag == bag).Sum(i => (decimal)(ItemRules.Stack(i.Key) is 0 or 1 ? 1 : SlotsFor(i.Key, i.Quantity)));
    private int SlotLimit(ushort bag)
    {
        if (bag != InventoryBag) return MostSlots[bag];
        var grown = Bag(bag)?.ChildFields.Named("_varyExpandSlotCount");
        return Math.Min(MostSlots[bag], InventorySlots + (grown is { Present: true } ? (int)grown.Scalar(blob, "TInventorySlotNo", 2) : 0));
    }
    private bool HasRoom(ushort bag, decimal slots) => slots <= 0 || UsedSlots(bag) + slots <= SlotLimit(bag);
    private void Room(ushort bag, decimal slots)
    {
        if (!HasRoom(bag, slots)) throw new InvalidOperationException("The bag does not have enough empty slots.");
    }

    private SaveField? Bag(ushort key) => SaveRecords.Read(blob, Layout, "InventorySaveData").Fields.Named("_inventorylist").Elements("InventoryElementSaveData")
        .SingleOrDefault(b => b.ChildFields.Named("_inventoryKey").Scalar(blob, "InventoryKey", 2) == key);
    // Item records of the bags and of worn equipment, by where they start.
    private Dictionary<int, SaveField> OwnedRecords()
    {
        var bags = SaveRecords.Read(blob, Layout, "InventorySaveData").Fields.Named("_inventorylist").Elements("InventoryElementSaveData")
            .SelectMany(b => b.ChildFields.Named("_itemList") is { Present: true } list ? list.Elements("ItemSaveData") : []);
        var worn = SaveRecords.Read(blob, Layout, "EquipmentSaveData").Fields.Named("_list").Elements("EquipSlotElementSaveData").Select(s => s.ChildFields.Named("_item")).Where(i => i.Present);
        return bags.Concat(worn).ToDictionary(r => r.StartOffset);
    }

    // The game's own row for a new item in any bag (collection box, storage, wardrobe, inventory; 60,690 rows of 69 saves hold
    // these fields): an absent enchant level is +0 and an absent socket count is none opened.
    private static readonly (string Name, string Type, int Kind, int Size)[] PlainRowFields =
        [("_saveVersion", "uint32", 0, 4), ("_itemNo", "ItemNo", 0, 8), ("_itemKey", "ItemKey", 0, 4), ("_slotNo", "TItemSlotNo", 0, 2),
         ("_stackCount", "TStackCount", 0, 8), ("_endurance", "TEndurance", 0, 2), ("_maxSocketCount", "TSocketSlotNo", 0, 1),
         ("_socketSaveDataList", "ReflectObject", 6, 0), ("_transferredItemKey", "ItemKey", 0, 4), ("_maxChargeUseableCount", "uint32", 0, 4),
         ("_chargedUseableCount", "TickCount64", 0, 8), ("_timeWhenPushItem", "Ctc64", 0, 8)];
    private static byte[] Bytes(ulong value, int size) { byte[] b = new byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(b, value); return b[..size]; }
    private static RecordLists.Nested With(RecordLists.Nested row, params (string Name, object Value)[] fields)
    {
        var values = new Dictionary<string, object>(row.Values);
        foreach (var (name, value) in fields) values[name] = value;
        return row with { Values = values };
    }
    private RecordLists.Nested PlainRow(uint key, ulong quantity)
    {
        var item = Layout.Types.Single(t => t.Name == "ItemSaveData");
        if (!PlainRowFields.All(w => item.Fields.Any(f => (f.Name, f.TypeName, (int)f.Kind, (int)f.Size) == w)) || !Layout.Types.Any(t => t.Name == "ItemSocketSaveData"))
            throw new InvalidDataException("Unsupported item record layout.");
        return new("ItemSaveData", new Dictionary<string, object>
        {
            ["_saveVersion"] = Bytes(1, 4), ["_itemKey"] = Bytes(key, 4), ["_stackCount"] = Bytes(quantity, 8), ["_endurance"] = Bytes(ushort.MaxValue, 2),
            ["_maxSocketCount"] = Bytes(5, 1), ["_socketSaveDataList"] = Enumerable.Range(0, 5).Select(_ => new RecordLists.Nested("ItemSocketSaveData", new Dictionary<string, object>())).ToArray(),
            ["_transferredItemKey"] = Bytes(key, 4), ["_maxChargeUseableCount"] = Bytes(1, 4), ["_chargedUseableCount"] = Bytes(1, 8)
        });
    }
    // A copy of a row: everything but its number, slot, time and lock.
    private RecordLists.Nested Copyable(SaveField record)
    {
        var value = Movable(record);
        return With(value with { Values = value.Values.Where(f => f.Key != "_isLocked").ToDictionary(f => f.Key, f => f.Value) }, ("_stackCount", Bytes(1, 8)));
    }

    // Adds rows, numbered as the game would, together with patches of rows already there and other regions of the named blocks.
    private void AddRows(IReadOnlyList<(ushort Bag, RecordLists.Nested Row)> rows, IEnumerable<(int Start, int End, byte[] Value)> patches,
        IEnumerable<(int Start, int End, Func<int, byte[]> Encode)>? others = null, IEnumerable<string>? blocks = null, Action<byte[], ParcLayout, ulong[]>? verify = null)
    {
        var regions = patches.Select(p => (p.Start, p.End, (Func<int, byte[]>)(_ => p.Value))).Concat(others ?? []).ToList();
        ulong[] numbers = rows.Count == 0 ? [] : NewNumbers(rows.Count);
        int next = 0;
        foreach (var bag in rows.GroupBy(r => r.Bag))
            regions.AddRange(AppendRegions(bag.Key, bag.Select(r => With(r.Row, ("_itemNo", Bytes(numbers[next++], 8)))).ToArray()));
        var before = Items.Where(i => i.Source == ItemSource.Inventory).GroupBy(i => (i.Bag, i.Key)).ToDictionary(g => g.Key, g => g.Count());
        var added = rows.GroupBy(r => (r.Bag, Key: BinaryPrimitives.ReadUInt32LittleEndian((byte[])r.Row.Values["_itemKey"]))).ToDictionary(g => g.Key, g => g.Count());
        var patched = regions.Select(r => r.Item1).ToHashSet();
        string[] changed = Items.Where(i => patched.Contains(i.QuantityOffset)).Select(i => Inventory.ClassName(i.Source)).Append("InventorySaveData").Concat(blocks ?? []).Distinct().ToArray();
        byte[] candidate = RecordLists.RewriteRegions(blob, EditBuffer, regions);
        Transact(candidate, changed, (layout, items) =>
        {
            verify?.Invoke(candidate, layout, numbers);
            var inventory = items.Where(i => i.Source == ItemSource.Inventory).ToArray();
            var after = inventory.GroupBy(i => (i.Bag, i.Key)).ToDictionary(g => g.Key, g => g.Count());
            if (after.Keys.Union(before.Keys).Any(k => after.GetValueOrDefault(k) != before.GetValueOrDefault(k) + added.GetValueOrDefault(k)) ||
                numbers.Length > 0 && numbers.Any(n => inventory.Count(i => i.Number == n) != 1) ||
                added.Keys.Select(k => k.Bag).Distinct().Any(b => inventory.Where(i => i.Bag == b).GroupBy(i => i.Slot).Any(g => g.Count() > 1)))
                throw new InvalidDataException("New item verification failed.");
        });
    }

    // Rows go after the bag's last row, each in the lowest free slot, or into a new list when the bag was never used (it then
    // holds no list at all). They are pushed now.
    private IEnumerable<(int Start, int End, Func<int, byte[]> Encode)> AppendRegions(ushort bagKey, IReadOnlyList<RecordLists.Nested> rows)
    {
        var bag = Bag(bagKey) ?? throw new InvalidOperationException("This save does not have that bag yet.");
        var list = bag.ChildFields.Named("_itemList");
        var present = list.Present ? list.Elements("ItemSaveData") : [];
        var used = present.Select(r => r.ChildFields.Named("_slotNo")).Where(f => f.Present).Select(f => f.Scalar(blob, "TItemSlotNo", 2)).ToHashSet();
        ulong[] slots = Enumerable.Range(0, ushort.MaxValue).Select(i => (ulong)i).Where(s => !used.Contains(s)).Take(rows.Count).ToArray();
        if (slots.Length != rows.Count) throw new InvalidOperationException("The bag does not have enough empty slots.");
        ulong pushed = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var placed = rows.Select((row, i) => With(row, ("_slotNo", Bytes(slots[i], 2)), ("_timeWhenPushItem", Bytes(pushed, 8)))).ToArray();
        if (!list.Present) return ListRegions(bag, list, placed);
        if ((present.Length > 0 ? present[^1].EndOffset : list.StartOffset + 18) != list.EndOffset) throw new InvalidDataException("Item list does not end with its last row.");
        byte[] count = BitConverter.GetBytes(present.Length + rows.Count);
        return [(list.StartOffset + 1, list.StartOffset + 5, _ => count), (list.EndOffset, list.EndOffset, start => RowsBytes(placed, start))];
    }
    private byte[] RowsBytes(IReadOnlyList<RecordLists.Nested> rows, int start)
    {
        using var output = new MemoryStream();
        foreach (var row in rows) output.Write(RecordLists.ObjectBytes(Layout, row, checked(start + (int)output.Length)));
        return output.ToArray();
    }
}
