using CDSE.Core.Catalogs;

namespace CDSE.Core.Saves;

/// <summary>
/// One dyed part of an item as the save holds it (ItemDyeSaveData). Texture is the material the part was given, 0 for its own;
/// Group the color group the color was picked from. Grime is 0 for clean and 127 for the most, and null when never set.
/// A row without a color (Colored false) only holds a material or grime.
/// </summary>
public sealed record DyePart(int Part, bool Colored, byte R, byte G, byte B, uint Group, ushort Texture, byte? Grime, bool HideSymbol);

/// <summary>An item that takes dye and what it was dyed; none when it has its own colors.</summary>
public sealed record DyedItem(InventoryItem Item, DyePart[] Parts)
{
    public int PartCount => GearCatalog.Shared.PartsOf(Item.Key).Length;
}

/// <summary>What an import does with one entry of a dye file: dye an owned item, or a new one when Item is null and Problem is too.</summary>
public sealed record DyeTarget(DyeEntry Entry, InventoryItem? Item, string? Problem);

public sealed partial class SaveDocument
{
    private const string DyeList = "_itemDyeDataList", DyeType = "ItemDyeSaveData";
    private static readonly (string Name, string Type, int Kind, int Size)[] DyeFields =
        [("_dyeSlotNo", "int8", 0, 1), ("_dyeColorR", "uint8", 0, 1), ("_dyeColorG", "uint8", 0, 1), ("_dyeColorB", "uint8", 0, 1), ("_dyeColorA", "uint8", 0, 1),
         ("_grimeOpacity", "int8", 0, 1), ("_dyeColorGroupInfoKey", "DyeColorGroupInfoKey", 0, 4), ("_texturePalleteKey", "PartPrefabDyeTexturePalleteKey", 0, 2), ("_disableSymbol", "int8", 0, 1)];
    // Blocks a record of an owned item can be in; a worn item may also have one in a bag, with the same number.
    private static readonly string[] OwnedBlocks = ["InventorySaveData", "EquipmentSaveData", "MercenaryClanSaveData"];

    /// <summary>Whether the save's format holds dyes this editor can write. A save schema has the dye type once an item was dyed.</summary>
    public bool CanDye => Layout.Types.FirstOrDefault(t => t.Name == DyeType) is { } type && type.Is(DyeFields);

    /// <summary>Every item the player owns that takes dye.</summary>
    public DyedItem[] ReadDyes()
    {
        var records = GearRecords();
        return Items.Where(i => i.Source != ItemSource.Vendor && GearCatalog.Shared.CanDye(i.Key) && records.ContainsKey(i.RecordOffset))
            .Select(i => new DyedItem(i, Parts(blob, records[i.RecordOffset]))).ToArray();
    }

    // Item records of the bags, of worn equipment and of what mercenaries wear, by where they start.
    private Dictionary<int, SaveField> GearRecords()
    {
        var records = OwnedRecords();
        if (Layout.Blocks.Any(b => b.Name == "MercenaryClanSaveData"))
            foreach (var row in MercenaryList(blob, Layout).Elements("MercenarySaveData").SelectMany(m => m.ChildFields.Named("_equipItemList").Elements("ItemSaveData")))
                records[row.StartOffset] = row;
        return records;
    }

    private DyePart[] Parts(byte[] raw, SaveField record) => record.ChildFields.Named(DyeList).Elements(DyeType).Select(row =>
    {
        byte? Byte(string name) => row.ChildFields.Named(name) is { Present: true } f ? (byte)f.Scalar(raw, f.TypeName, 1) : null;
        ulong Value(string name, int size) => row.ChildFields.Named(name) is { Present: true } f ? f.Scalar(raw, f.TypeName, size) : 0;
        return new DyePart(Byte("_dyeSlotNo") ?? 0, (Byte("_dyeColorA") ?? 0) != 0, Byte("_dyeColorR") ?? 0, Byte("_dyeColorG") ?? 0, Byte("_dyeColorB") ?? 0,
            (uint)Value("_dyeColorGroupInfoKey", 4), (ushort)Value("_texturePalleteKey", 2), Byte("_grimeOpacity"), (Byte("_disableSymbol") ?? 0) != 0);
    }).ToArray();

    // The game leaves zero fields out of a row, except grime, whose absence means it was never set.
    private static RecordLists.Nested DyeRow(DyePart part)
    {
        var values = new Dictionary<string, object>();
        void Put(string name, ulong value, int size) { if (value != 0) values[name] = Bytes(value, size); }
        Put("_dyeSlotNo", (ulong)part.Part, 1);
        if (part.Colored) { Put("_dyeColorR", part.R, 1); Put("_dyeColorG", part.G, 1); Put("_dyeColorB", part.B, 1); Put("_dyeColorA", 255, 1); }
        if (part.Grime is byte grime) values["_grimeOpacity"] = new[] { grime };
        Put("_dyeColorGroupInfoKey", part.Group, 4); Put("_texturePalleteKey", part.Texture, 2); Put("_disableSymbol", part.HideSymbol ? 1u : 0, 1);
        return new(DyeType, values);
    }

    /// <summary>Why a set of parts cannot be an item's dye, or null when it can.</summary>
    internal static string? DyeProblem(uint item, IReadOnlyList<DyePart> parts)
    {
        var catalog = GearCatalog.Shared;
        if (!catalog.CanDye(item)) return "not_dyeable";
        int[] own = catalog.PartsOf(item);
        return parts.Select(p => p.Part).Distinct().Count() != parts.Count || parts.Any(p => !own.Contains(p.Part) || p.Texture > GearCatalog.Textures ||
            p.Grime > sbyte.MaxValue || p.Group != 0 && !catalog.ColorGroups.ContainsKey(p.Group)) ? "bad_parts" : null;
    }

    /// <summary>
    /// What importing dyes would do: each entry goes to an item the player has (worn first, then the inventory, wardrobe and
    /// storage, each item once) when useOwned, and otherwise, or when there is none left, to a new item in the inventory.
    /// </summary>
    public DyeTarget[] PlanDyes(IReadOnlyList<DyeEntry> entries, bool useOwned)
    {
        var owned = DyeCandidates().ToList();
        return entries.Select(entry =>
        {
            if (DyeProblem(entry.Item, entry.Parts) is { } problem) return new DyeTarget(entry, null, problem);
            if (useOwned && owned.FirstOrDefault(i => i.Key == entry.Item) is { } item) { owned.Remove(item); return new(entry, item, null); }
            return new(entry, null, ItemRules.CanAdd(entry.Item) ? null : "cannot_add");
        }).ToArray();
    }

    /// <summary>The owned items an import can dye, in the order it offers them: worn first, then the inventory, wardrobe and storage.</summary>
    public InventoryItem[] DyeCandidates()
    {
        static int Rank(InventoryItem i) => i.Source switch
        {
            ItemSource.Equipment => 0, ItemSource.Mercenary when i.OwnerSlot == "equipped" => 1, ItemSource.Inventory when i.Bag == InventoryBag => 2,
            ItemSource.Inventory when i.Bag == WardrobeBag => 3, ItemSource.Inventory when i.Bag == StorageBag => 4, _ => -1
        };
        // A worn item's row in a bag is the same item, so it counts once.
        return Items.Where(i => Rank(i) >= 0).OrderBy(Rank).DistinctBy(i => (i.Number, i.Key)).ToArray();
    }

    /// <summary>Dyes owned items and adds new dyed ones, as one edit. Targets come from <see cref="PlanDyes"/>.</summary>
    public void ApplyDyes(IReadOnlyList<DyeTarget> targets)
    {
        if (targets.Count == 0) return;
        if (!CanDye) throw new InvalidOperationException("Dye one item in the game first; this save has no dye records yet.");
        if (targets.Any(t => t.Problem is not null || DyeProblem(t.Entry.Item, t.Entry.Parts) is not null || t.Item is { } i && i.Key != t.Entry.Item || t.Item is null && !ItemRules.CanAdd(t.Entry.Item)))
            throw new InvalidOperationException("This dye cannot be applied.");
        ValidateSelection(targets.Select(t => t.Item).OfType<InventoryItem>(), requireStackEditable: false);
        if (targets.Where(t => t.Item is not null).GroupBy(t => (t.Item!.Number, t.Item.Key)).Any(g => g.Count() > 1)) throw new InvalidOperationException("Two dyes for one item.");
        var created = targets.Where(t => t.Item is null).ToArray();
        Room(InventoryBag, created.Length);
        var regions = new List<(int Start, int End, Func<int, byte[]> Encode)>();
        var blocks = new HashSet<string>();
        var expected = new Dictionary<(ulong, uint), (DyePart[] Parts, int Copies)>();
        // Each block is read once, however many items are dyed.
        var owned = OwnedItems(blob, Layout);
        foreach (var target in targets.Where(t => t.Item is not null))
        {
            var rows = target.Entry.Parts.Select(DyeRow).ToArray();
            int copies = 0;
            foreach (var (block, items) in owned)
                foreach (var record in items[(target.Item!.Number, target.Item.Key)])
                {
                    regions.AddRange(ListRegions(record, record.ChildFields.Named(DyeList), rows));
                    blocks.Add(block); copies++;
                }
            if (copies == 0) throw new InvalidDataException("Selected item reference was not decoded.");
            expected[(target.Item!.Number, target.Item.Key)] = (target.Entry.Parts, copies);
        }
        var added = created.Select(t => (InventoryBag, t.Entry.Parts.Length == 0 ? PlainRow(t.Entry.Item, 1) : With(PlainRow(t.Entry.Item, 1), (DyeList, t.Entry.Parts.Select(DyeRow).ToArray())))).ToArray();
        AddRows(added, [], regions, blocks, (raw, layout, numbers) =>
        {
            for (int i = 0; i < created.Length; i++) expected[(numbers[i], created[i].Entry.Item)] = (created[i].Entry.Parts, 1);
            var written = OwnedItems(raw, layout);
            foreach (var (item, (parts, copies)) in expected)
            {
                var records = written.SelectMany(b => b.Items[item]).ToArray();
                if (records.Length != copies || records.Any(r => !Parts(raw, r).SequenceEqual(parts))) throw new InvalidDataException("Dye verification failed.");
            }
        });
    }
    private static (string Block, ILookup<(ulong, uint), SaveField> Items)[] OwnedItems(byte[] raw, ParcLayout layout) =>
        OwnedBlocks.Where(b => layout.Blocks.Any(l => l.Name == b)).Select(b => (b, SaveRecords.ItemReferences(raw, layout, b))).ToArray();

    // Replaces an object list of a record, present or not: its presence bit, and 00, u32 count, 13 zero bytes and the rows, or
    // the single 01 an absent list takes.
    private (int Start, int End, Func<int, byte[]> Encode)[] ListRegions(SaveField record, SaveField list, IReadOnlyList<RecordLists.Nested> rows)
    {
        int at = record.StartOffset + 2 + list.FieldIndex / 8;
        if (list.MetaKind != 6 || System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(record.StartOffset)) != record.ChildMaskByteCount ||
            !list.Present && (list.EndOffset != list.StartOffset + 1 || blob[list.StartOffset] != 1)) throw new InvalidDataException("Unsupported record list.");
        int bit = 1 << (list.FieldIndex % 8);
        byte mask = (byte)(rows.Count > 0 ? blob[at] | bit : blob[at] & ~bit);
        byte[] Encode(int start)
        {
            if (rows.Count == 0) return [1];
            byte[] header = new byte[18]; BitConverter.TryWriteBytes(header.AsSpan(1), rows.Count);
            return [.. header, .. RowsBytes(rows, start + header.Length)];
        }
        return [(at, at + 1, _ => [mask]), (list.StartOffset, list.EndOffset, Encode)];
    }
}
