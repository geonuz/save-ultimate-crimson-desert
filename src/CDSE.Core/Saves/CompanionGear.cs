using CDSE.Core.Catalogs;

namespace CDSE.Core.Saves;

/// <summary>One gear slot of a pet or mount: what it takes and what is worn there. A required slot is never emptied, only swapped.</summary>
public sealed record GearSlot(int Number, uint[] Types, InventoryItem? Item, bool Required = false);

// Pets and mounts: adding and letting go of them, and what they wear.
public sealed partial class SaveDocument
{
    /// <summary>The groups an addition or removal works on. The story gives the dedicated mounts (Blackstar, A.T.A.G.).</summary>
    public static string[] AddableGroups { get; } = ["pet", "horse", "special", "wagon", "ship"];
    // The ship the ocean story gives first.
    private const uint StoryShip = 1004537;

    /// <summary>The kinds of a group an addition can create: each one not in the roster, DLC ones only with the DLC.</summary>
    public static uint[] CompanionChoices(string group, IEnumerable<Companion> roster)
    {
        if (group == "pet") return PetChoices(roster);
        var present = roster.Select(c => c.Key).ToHashSet();
        return GearCatalog.Shared.Addable.GetValueOrDefault(group, []).Where(k => k != StoryShip && !present.Contains(k) && !(DlcContent.Hidden && DlcContent.Shared.Characters.Contains(k))).ToArray();
    }
    // The story opens wagons and ships, so a save has to have one first.
    public bool CanAddTo(string group) => group == "pet" ? CanAddPets : AddableGroups.Contains(group) && CanAddWorkers && MercenaryList(blob, Layout).Present &&
        (group is not ("wagon" or "ship") || ReadCompanions().Any(c => c.Group == group));

    /// <summary>Adds a pet or mount. A mount starts as one never called out: the record of a recruit and, for a ship, the gear it
    /// comes with, which is what saves hold for such mounts; a pet also takes a place to stand (see AddPet).</summary>
    public Companion[] AddCompanion(uint key)
    {
        string group = CompanionTypes.Shared.Group(key);
        if (group == "pet") return AddPet(key);
        var before = ReadCompanions();
        if (!CompanionChoices(group, before).Contains(key)) throw new InvalidOperationException("This character cannot be added or is already in the roster.");
        if (!CanAddTo(group)) throw new InvalidOperationException("This save's format has no room for a new mount yet. Get one in the game first.");
        var gear = GearCatalog.Shared.Comes.GetValueOrDefault(key, []).Select(k => (Slot: Enumerable.Range(0, GearCatalog.Shared.SlotsOf(key).Length).Single(s => GearCatalog.Shared.CanWear(key, s, k)), Key: k)).OrderBy(g => g.Slot).ToArray();
        ulong[] numbers = NewNumbers(1 + gear.Length);
        var record = WorkerRecord(key, numbers[0], FieldTime())!;
        if (gear.Length > 0) record = With(record, ("_equipItemList", gear.Select((g, i) => NewGear(g.Key, numbers[i + 1], g.Slot)).ToArray()));
        return AppendCompanion(before, record, "Mount verification failed.");
    }
    private Companion[] AppendCompanion(Companion[] before, RecordLists.Nested record, string failure) =>
        CommitCompanions(RecordLists.RewriteRegions(blob, EditBuffer, RosterRegions([record], null)),
            [.. before, new Companion(BitConverter.ToUInt64((byte[])record.Values["_mercenaryNo"]), BitConverter.ToUInt32((byte[])record.Values["_characterKey"]), null)], failure);

    // The roster with rows after its last one, or without one. A list header holds the marker byte, then the count.
    private List<(int Start, int End, Func<int, byte[]> Encode)> RosterRegions(RecordLists.Nested[] add, Companion? remove)
    {
        var list = MercenaryList(blob, Layout);
        byte[] count = BitConverter.GetBytes(list.ListElements.Length + add.Length - (remove is null ? 0 : 1));
        List<(int Start, int End, Func<int, byte[]> Encode)> regions = [(list.StartOffset + 1, list.StartOffset + 5, _ => count)];
        if (remove is not null && CompanionRow(remove) is var row) regions.Add((row.StartOffset, row.EndOffset, _ => []));
        if (add.Length > 0) regions.Add((list.EndOffset, list.EndOffset, start => RowsBytes(add, start)));
        return regions;
    }

    private SaveField CompanionRow(Companion companion) => MercenaryList(blob, Layout).Elements("MercenarySaveData")
        .Single(r => r.ChildFields.Named("_mercenaryNo").Scalar(blob, "MercenaryNo", 8) == companion.Number);

    /// <summary>The numbers of the companions quests name. Reading the quests takes a while, so a page asks once for its rows.
    /// Stables, yards and harbors are quest stages too, but they only name what was last brought there: most saves have them
    /// without a name, and the game leaves the number of sold livestock in them.</summary>
    public HashSet<ulong> QuestLinked() => QuestLinks(blob, Layout).Where(l => !GearCatalog.Shared.Services.Contains(l.Stage)).Select(l => l.Number).ToHashSet();

    /// <summary>Why a companion cannot be removed ("kind", "main", "quest", "cargo"), or null when it can.</summary>
    public string? ReleaseBlock(Companion companion, IReadOnlySet<ulong>? questLinked = null)
    {
        if (!AddableGroups.Contains(companion.Group)) return "kind";
        var fields = CompanionRow(companion).ChildFields;
        bool Flag(string name) => fields.Named(name) is { Present: true } f && f.Scalar(blob, "bool", 1) != 0;
        return Flag("_isMainMercenary") ? "main" : companion.Key == StoryShip || (questLinked ?? QuestLinked()).Contains(companion.Number) ? "quest"
            : fields.Named("_inventoryItemList").Present || fields.Named("_sealedDropResultSubSaveItemList").Present ? "cargo" : null;
    }

    /// <summary>Removes a pet or mount that nothing else names, with what it wears.</summary>
    public Companion[] RemoveCompanion(Companion companion)
    {
        var before = ReadCompanions();
        if (!before.Contains(companion)) throw new InvalidOperationException("The companion selection changed. Load the save again.");
        if (ReleaseBlock(companion) is not null) throw new InvalidOperationException("This companion cannot be removed.");
        return CommitGear(RosterRegions([], companion), CompanionRow(companion).ChildFields.Named("_equipItemList").Elements("ItemSaveData"), before.Where(c => c.Number != companion.Number).ToArray());
    }

    // Slots no save has empty on a mount that wears anything (37 saves, 2.04 to 2.05.01): a horse's saddle, dragon armor, and what
    // a ship or the A.T.A.G. comes with.
    private static bool Required(uint character, int slot) => (GearCatalog.Shared.Mounts[character][1], slot) is (201, 2) or (203, 0) ||
        GearCatalog.Shared.Comes.GetValueOrDefault(character, []).Any(k => GearCatalog.Shared.CanWear(character, slot, k));

    /// <summary>The gear slots of a companion, by slot number. Empty for one that wears no gear.</summary>
    public GearSlot[] GearOf(Companion companion)
    {
        var worn = Items.Where(i => i.Source == ItemSource.Mercenary && i.OwnerNumber == companion.Number && i.OwnerSlot == "equipped").ToLookup(i => (int)i.Slot);
        return GearCatalog.Shared.SlotsOf(companion.Key).Select((types, i) => new GearSlot(i, types, worn[i].FirstOrDefault(), Required(companion.Key, i))).ToArray();
    }

    /// <summary>The kinds a slot can take: what the editor can make, and what the player has one of.</summary>
    public uint[] GearChoices(Companion companion, int slot)
    {
        if (GearOf(companion).ElementAtOrDefault(slot) is not { } gear) return [];
        var held = Items.Where(i => i.Source == ItemSource.Inventory && i.Bag == InventoryBag).Select(i => i.Key).ToHashSet();
        return GearCatalog.Shared.WearablesFor(companion.Key, slot).Where(k => k != gear.Item?.Key && (held.Contains(k) || GearCatalog.Shared.CanMake(k))).ToArray();
    }

    /// <summary>
    /// Puts a new item in a companion's gear slot, or empties it (key 0). What was worn there is gone.
    /// </summary>
    public Companion[] SetGear(Companion companion, int slot, uint key)
    {
        var before = ReadCompanions();
        if (!before.Contains(companion)) throw new InvalidOperationException("The companion selection changed. Load the save again.");
        if (key != 0 && !GearChoices(companion, slot).Contains(key)) throw new InvalidOperationException("This companion cannot wear that item.");
        if (key == 0 && GearOf(companion).ElementAtOrDefault(slot) is not { Required: false }) throw new InvalidOperationException("That gear slot cannot be left empty.");
        var row = CompanionRow(companion);
        var list = row.ChildFields.Named("_equipItemList");
        ulong SlotOf(SaveField item) => item.ChildFields.Named("_slotNo") is { Present: true } f ? f.Scalar(blob, "TItemSlotNo", 2) : 0;
        var present = list.Elements("ItemSaveData");
        var old = present.Where(r => SlotOf(r) == (ulong)slot).ToArray();
        if (old.Length > 1 || old.Length == 0 && key == 0) throw new InvalidOperationException("That gear slot is empty or holds two items.");
        ulong number = key == 0 ? 0 : NewNumbers(1)[0];
        // The game keeps worn gear in slot order.
        var rows = present.Except(old).Select(r => (Slot: SlotOf(r), Row: Decode(r))).ToList();
        if (key != 0) rows.Add(((ulong)slot, NewGear(key, number, slot)));
        var regions = ListRegions(row, list, rows.OrderBy(r => r.Slot).Select(r => r.Row).ToArray());
        return CommitGear(regions, old, before, number);
    }
    private RecordLists.Nested NewGear(uint key, ulong number, int slot)
    {
        ulong charges = GearCatalog.Shared.Parts.GetValueOrDefault(key, 1u);
        return With(PlainRow(key, 1), ("_itemNo", Bytes(number, 8)), ("_slotNo", Bytes((ulong)slot, 2)), ("_timeWhenPushItem", Bytes((ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds(), 8)),
            ("_maxChargeUseableCount", Bytes(charges, 4)), ("_chargedUseableCount", Bytes(charges, 8)));
    }

    // A row that moves keeps everything; reading must miss nothing, so writing it back in place has to give the same bytes.
    private RecordLists.Nested Movable(SaveField record)
    {
        var value = Decode(record);
        if (!RecordLists.ObjectBytes(Layout, value, record.StartOffset).AsSpan().SequenceEqual(blob.AsSpan(record.StartOffset, record.EndOffset - record.StartOffset)))
            throw new InvalidDataException("Unsupported item record.");
        return value;
    }

    // Commits a companion's regions without the gear that goes (off). Every other item is there once as before, and a new one if made.
    private Companion[] CommitGear(IEnumerable<(int Start, int End, Func<int, byte[]> Encode)> regions, SaveField[] off, Companion[] roster, ulong made = 0)
    {
        var gone = off.Select(i => i.ChildFields.Named("_itemNo").Scalar(blob, "ItemNo", 8)).ToHashSet();
        static (ulong, uint, ulong)[] Held(IEnumerable<InventoryItem> items, IReadOnlySet<ulong> except) => items.Where(i => i.Source is ItemSource.Inventory or ItemSource.Mercenary && !except.Contains(i.Number))
            .Select(i => (i.Number, i.Key, i.Quantity)).Order().ToArray();
        var held = Held(Items, gone);
        byte[] candidate = RecordLists.RewriteRegions(blob, EditBuffer, regions);
        Companion[] after = [];
        Transact(candidate, ["MercenaryClanSaveData"], (layout, items) =>
        {
            after = CompanionRecords(candidate, layout).Select(r => r.Value).ToArray();
            if (!after.SequenceEqual(roster) || !Held(items, made == 0 ? new HashSet<ulong>() : [made]).SequenceEqual(held) || made != 0 && items.Count(i => i.Number == made) != 1 ||
                items.Where(i => i.Source == ItemSource.Mercenary && i.OwnerSlot == "equipped" && GearCatalog.Shared.Mounts.ContainsKey(i.OwnerCharacter)).GroupBy(i => (i.OwnerNumber, i.Slot)).Any(g => g.Count() > 1))
                throw new InvalidDataException("Gear verification failed.");
        });
        return after;
    }
}
