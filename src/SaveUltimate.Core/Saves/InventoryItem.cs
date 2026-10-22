using System.Text.Json;
using System.Buffers.Binary;

namespace SaveUltimate.Core.Saves;

public enum ItemSource { Inventory, Equipment, Vendor, Mercenary }

public sealed class InventoryItem
{
    public ulong Number { get; }
    public uint Key { get; }
    public ulong Quantity { get; }
    public ushort Bag { get; }
    public ItemSource Source { get; }
    public ushort StoreKey { get; internal init; }
    public ulong OwnerNumber { get; internal init; }
    public uint OwnerCharacter { get; internal init; }
    public string OwnerName { get; internal init; } = "";
    public string OwnerSlot { get; internal init; } = "";
    public bool CanEditQuantity => Source != ItemSource.Mercenary;
    public ushort Slot { get; internal init; }
    public ushort Endurance { get; internal init; }
    public ushort Sharpness { get; internal init; }
    public ushort? EnchantLevel { get; internal init; }
    public int MaxEnchant => EnchantLimits.Get(Key);
    internal int EnchantOffset { get; init; }
    public SocketState Sockets { get; internal init; } = SocketState.Empty;
    internal int QuantityOffset { get; }
    internal int KeyOffset { get; }
    internal int NumberOffset { get; init; }
    internal int RecordStart { get; init; }
    public int RecordOffset => RecordStart;
    internal int RecordEnd { get; init; }
    internal Guid Snapshot { get; }
    internal InventoryItem(ulong number, uint key, ulong quantity, ushort bag, ItemSource source, int offset, int keyOffset, Guid snapshot)
        => (Number, Key, Quantity, Bag, Source, QuantityOffset, KeyOffset, Snapshot) = (number, key, quantity, bag, source, offset, keyOffset, snapshot);

    internal InventoryItem RefreshScalars(byte[] raw, Guid snapshot, SocketState? sockets = null, int delta = 0) => new(Number, Key,
        BinaryPrimitives.ReadUInt64LittleEndian(raw.AsSpan(QuantityOffset + delta)), Bag, Source, QuantityOffset + delta, KeyOffset + delta, snapshot)
    {
        StoreKey = StoreKey, OwnerNumber = OwnerNumber, OwnerCharacter = OwnerCharacter, OwnerName = OwnerName, OwnerSlot = OwnerSlot,
        Slot = Slot, Endurance = Endurance, Sharpness = Sharpness, Sockets = sockets ?? (delta == 0 ? Sockets : Sockets.Relocate(delta)),
        NumberOffset = NumberOffset + delta, RecordStart = RecordStart + delta, RecordEnd = RecordEnd + delta,
        EnchantOffset = EnchantOffset == 0 ? 0 : EnchantOffset + delta,
        EnchantLevel = EnchantLevel.HasValue ? BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(EnchantOffset + delta)) : null
    };
}

internal static class Inventory
{
    internal static string ClassName(ItemSource source) => source switch
    {
        ItemSource.Inventory => "InventorySaveData", ItemSource.Equipment => "EquipmentSaveData",
        ItemSource.Vendor => "StoreSaveData", ItemSource.Mercenary => "MercenaryClanSaveData",
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    internal static InventoryItem[] Refresh(byte[] before, byte[] after, ParcLayout oldLayout, ParcLayout layout,
        IReadOnlyList<InventoryItem> previous, Guid snapshot, string[] changed)
    {
        if (oldLayout.SchemaEnd != layout.SchemaEnd || !before.AsSpan(0, oldLayout.SchemaEnd).SequenceEqual(after.AsSpan(0, layout.SchemaEnd)) ||
            oldLayout.Blocks.Count != layout.Blocks.Count)
            throw new InvalidDataException("Edit changed the save schema.");
        foreach (var (old, next) in oldLayout.Blocks.Zip(layout.Blocks))
            if (old.Name != next.Name || old.TypeIndex != next.TypeIndex || (!changed.Contains(old.Name) && old.Size != next.Size))
                throw new InvalidDataException("Edit changed an unrelated block layout.");
        var items = new List<InventoryItem>(previous.Count);
        foreach (var item in previous.Where(i => !changed.Contains(ClassName(i.Source))))
        {
            var block = oldLayout.Blocks.Single(b => b.Name == ClassName(item.Source) &&
                b.Offset <= item.RecordStart && item.RecordEnd <= b.Offset + b.Size);
            int delta = layout.Blocks[block.Index].Offset - block.Offset;
            foreach (var (offset, size, value) in new[] { (item.NumberOffset, 8, item.Number), (item.KeyOffset, 4, (ulong)item.Key), (item.QuantityOffset, 8, item.Quantity) })
            {
                if (offset < item.RecordStart || offset > item.RecordEnd - size)
                    throw new InvalidDataException("Stale item field boundary.");
                ulong Read(byte[] bytes, int pos) => size == 8 ? BinaryPrimitives.ReadUInt64LittleEndian(bytes.AsSpan(pos))
                    : BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(pos));
                if (Read(before, offset) != value || Read(after, offset + delta) != value)
                    throw new InvalidDataException("Edit changed an unrelated item.");
            }
            items.Add(item.RefreshScalars(after, snapshot, delta: delta));
        }
        items.AddRange(Read(after, layout, snapshot, changed));
        return items.OrderBy(i => i.Source).ThenBy(i => i.KeyOffset).ToArray();
    }

    public static InventoryItem[] Read(byte[] raw, ParcLayout layout, Guid snapshot, string[]? classes = null)
    {
        var items = new List<InventoryItem>();
        var positions = new HashSet<int>();
        if (classes is null || classes.Contains("InventorySaveData"))
        {
            var block = SaveRecords.Read(raw, layout, "InventorySaveData");
            foreach (var bag in block.Fields.Named("_inventorylist").Elements("InventoryElementSaveData"))
            {
                ushort bagKey = checked((ushort)bag.ChildFields.Named("_inventoryKey").Scalar(raw, "InventoryKey", 2));
                foreach (var record in bag.ChildFields.Named("_itemList").Elements("ItemSaveData"))
                    Append(record, ItemSource.Inventory, bagKey);
            }
        }
        if (classes is null || classes.Contains("EquipmentSaveData"))
        {
            var equipment = SaveRecords.Read(raw, layout, "EquipmentSaveData");
            foreach (var slot in equipment.Fields.Named("_list").Elements("EquipSlotElementSaveData"))
            {
                var record = slot.ChildFields.Named("_item");
                if (record.Present) Append(record, ItemSource.Equipment, 0);
            }
        }
        // _useItemSaveList contains references, not additional owned items.
        if (classes is null || classes.Contains("StoreSaveData"))
        {
            var stores = SaveRecords.Read(raw, layout, "StoreSaveData");
            foreach (var store in stores.Fields.Named("_storeDataList").Elements("StoreDataSaveData"))
            {
                ushort key = checked((ushort)store.ChildFields.Named("_storeKey").Scalar(raw, "StoreKey", 2));
                foreach (var record in store.ChildFields.Named("_storeSoldItemDataList").Elements("ItemSaveData"))
                    Append(record, ItemSource.Vendor, 0, storeKey: key);
            }
        }
        if (classes is null || classes.Contains("MercenaryClanSaveData"))
        {
            var clan = SaveRecords.Read(raw, layout, "MercenaryClanSaveData");
            foreach (var mercenary in clan.Fields.Named("_mercenaryDataList").Elements("MercenarySaveData"))
            {
                var fields = mercenary.ChildFields;
                ulong number = fields.Named("_mercenaryNo").Scalar(raw, "MercenaryNo", 8);
                uint character = checked((uint)fields.Named("_characterKey").Scalar(raw, "CharacterKey", 4));
                string name = fields.Named("_mercenaryName").Text(raw);
                foreach (var (field, slot) in new[] { ("_equipItemList", "equipped"), ("_inventoryItemList", "carried") })
                    foreach (var record in fields.Named(field).Elements("ItemSaveData"))
                        Append(record, ItemSource.Mercenary, 0, ownerNumber: number, ownerCharacter: character, ownerName: name, ownerSlot: slot);
            }
        }
        // Reserve-slot caches are excluded, including duplicate Sonic Resonators.
        return items.ToArray();

        void Append(SaveField record, ItemSource source, ushort bagKey, ushort storeKey = 0,
            ulong ownerNumber = 0, uint ownerCharacter = 0, string ownerName = "", string ownerSlot = "")
        {
                if (record.ChildTypeName != "ItemSaveData" || record.ChildFields.Length == 0)
                    throw new InvalidDataException("Incomplete owned item record.");
                var fields = record.ChildFields;
                var key = fields.Named("_itemKey");
                var number = fields.Named("_itemNo");
                if (!key.Present)
                {
                    if (!number.Present || number.Scalar(raw, "ItemNo", 8) is 0 or ulong.MaxValue) return;
                    throw new InvalidDataException("Nonempty item is missing its key.");
                }
                if (fields.Named("_saveVersion").Scalar(raw, "uint32", 4) != 1)
                    throw new InvalidDataException("Unsupported item save version.");
                SaveRecords.ValidateItem(raw, record);
                uint itemKey = checked((uint)key.Scalar(raw, "ItemKey", 4));
                ulong itemNumber = number.Scalar(raw, "ItemNo", 8);
                var stack = fields.Named("_stackCount");
                ulong quantity = stack.Scalar(raw, "TStackCount", 8);
                if (itemKey == 0 && quantity == 0) return;
                if (itemKey == 0 || itemNumber is 0 or ulong.MaxValue || !positions.Add(stack.StartOffset))
                    throw new InvalidDataException("Invalid or duplicated physical item.");
                var enchant = fields.Named("_enchantLevel");
                ushort level = enchant.Present ? checked((ushort)enchant.Scalar(raw, "TEnchantLevel", 2)) : ushort.MaxValue;
                ushort Value(string name, string type) { var field = fields.Named(name); return field.Present ? checked((ushort)field.Scalar(raw, type, 2)) : (ushort)0; }
                items.Add(new(itemNumber, itemKey, quantity, bagKey, source, stack.StartOffset, key.StartOffset, snapshot)
                {
                    StoreKey = storeKey, OwnerNumber = ownerNumber, OwnerCharacter = ownerCharacter,
                    OwnerName = ownerName, OwnerSlot = ownerSlot,
                    EnchantLevel = level == ushort.MaxValue ? null : level, EnchantOffset = enchant.StartOffset,
                    NumberOffset = number.StartOffset, RecordStart = record.StartOffset, RecordEnd = record.EndOffset,
                    Slot = Value("_slotNo", "TItemSlotNo"), Endurance = Value("_endurance", "TEndurance"), Sharpness = Value("_sharpness", "TEndurance"),
                    Sockets = SocketState.Read(raw, record)
                });
        }
    }
}

internal static class EnchantLimits
{
    private static Dictionary<uint, int> Limits = Load();
    internal static void SetLimits(Dictionary<uint, int>? values) => Limits = values ?? Load();
    public static int Get(uint key) => Limits.GetValueOrDefault(key, -1);
    private static Dictionary<uint, int> Load()
    {
        using var stream = typeof(EnchantLimits).Assembly.GetManifestResourceStream("MaxEnchant.json")
            ?? throw new InvalidDataException("Missing enchant limits.");
        return JsonSerializer.Deserialize<Dictionary<uint, int>>(stream)
            ?? throw new InvalidDataException("Invalid enchant limits.");
    }
}
