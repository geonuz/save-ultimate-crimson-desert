using CDSE.Core.Saves;

namespace CDSE.Core.Catalogs;

/// <summary>
/// What gear the game's tables allow where (GearCatalogBuilder in Tools). Dye holds one letter per dye part 0 to 11 of an item:
/// - for none, c cloth, l leather, m metal, n no material choice, x differs between the models the item has. Mounts holds a
/// companion's tribe and the slot set it wears gear in; Slots holds the equipment types each slot of a set takes; Wearables holds
/// an item's equipment type and the tribes it fits (none listed: any). Addable holds the mounts an addition offers. Parts holds the
/// ship, dragon and A.T.A.G. gear that can be made, with the charges it has; Comes what a mount has on from the start; Services the
/// quest stages that run a stable, yard or harbor. Names are by language.
/// </summary>
public sealed record GearCatalog(Dictionary<uint, string> Dye, Dictionary<uint, Dictionary<string, string>> ColorGroups, Dictionary<uint, Dictionary<string, string>> EquipTypes,
    Dictionary<string, uint[]> Addable, Dictionary<uint, uint[]> Mounts, Dictionary<uint, uint[][]> Slots, Dictionary<uint, uint[]> Wearables,
    Dictionary<uint, uint> Parts, Dictionary<uint, uint[]> Comes, HashSet<uint> Services)
{
    public static GearCatalog Shared { get; } = Embedded.Load<GearCatalog>("Gear.json");
    /// <summary>The dye materials are numbered 1 to 10, one per color group, in the regions' order of PartPrefabDyeTexturePalleteInfo.</summary>
    public const int Textures = 10, DyeParts = 12;

    public bool CanDye(uint item) => Dye.ContainsKey(item);
    /// <summary>The dye parts an item has.</summary>
    public int[] PartsOf(uint item) => Dye.TryGetValue(item, out var parts) ? Enumerable.Range(0, parts.Length).Where(i => parts[i] != '-').ToArray() : [];
    public char MaterialOf(uint item, int part) => Dye.TryGetValue(item, out var parts) && part < parts.Length ? parts[part] : '-';

    /// <summary>The equipment types each gear slot of a companion takes; none for one that wears no gear.</summary>
    public uint[][] SlotsOf(uint character) => Mounts.TryGetValue(character, out var mount) && Slots.TryGetValue(mount[1], out var slots) ? slots : [];
    /// <summary>Whether a companion can wear an item in a slot: the slot takes its type and the item has a model for the tribe.</summary>
    public bool CanWear(uint character, int slot, uint item) => SlotsOf(character) is var slots && slot >= 0 && slot < slots.Length &&
        Wearables.TryGetValue(item, out var wearable) && slots[slot].Contains(wearable[0]) &&
        (wearable.Length == 1 || wearable.AsSpan(1).Contains(Mounts[character][0]));
    /// <summary>Whether the editor makes the item as gear: what an inventory can be given, and mount parts.</summary>
    public bool CanMake(uint item) => ItemRules.CanAdd(item) || Parts.ContainsKey(item) && !(DlcContent.Hidden && DlcContent.Shared.Items.Contains(item));
    public uint[] WearablesFor(uint character, int slot) => Wearables.Keys.Where(k => CanWear(character, slot, k)).Order().ToArray();
}
