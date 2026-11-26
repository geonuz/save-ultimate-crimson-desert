using System.Text.Json;
using CDSE.Core.Saves;

namespace CDSE.Core.Catalogs;

// Per-item limits from the shipped catalog or the last game sync, swapped as one so a reader never sees a mix.
public static class ItemRules
{
    private sealed record Catalog(Rule[] Items);
    internal sealed record Rule(uint ItemKey, int SocketLimit, ulong MaxStack, uint GearTag = 0, uint[]? GearTags = null, bool Addable = false);
    private sealed record Limits(Dictionary<uint, int> Sockets, Dictionary<uint, ulong> Stacks, Dictionary<uint, int> Enchants,
        Dictionary<uint, uint> GearTags, Dictionary<uint, uint[]> EquipTags, HashSet<uint> Addable);
    private static readonly Lazy<HashSet<uint>> CollectionBox = new(() => CollectionCatalog.Shared.Items.Select(i => i.Key).ToHashSet());
    private static Limits current = Shipped();

    public static int Sockets(uint key) => current.Sockets.GetValueOrDefault(key);
    /// <summary>How many of an item the game shows in one slot: 1 for items that never stack, 0 for an unknown item.</summary>
    public static ulong Stack(uint key) => current.Stacks.GetValueOrDefault(key);
    /// <summary>Whether the game lets this equipment hold this Abyss Gear. Without known tags nothing is ruled out.</summary>
    public static bool Fits(uint equipment, uint gear) => !current.EquipTags.TryGetValue(equipment, out var tags) ||
        !current.GearTags.TryGetValue(gear, out uint tag) || tags.Contains(tag);
    /// <summary>Whether the item can go in the inventory as a new row. Collection box items have their own tab.</summary>
    public static bool CanAdd(uint key) => current.Addable.Contains(key) && !CollectionBox.Value.Contains(key) && !(DlcContent.Hidden && DlcContent.Shared.Items.Contains(key));
    // -1 for an item without enchant levels.
    internal static int Enchant(uint key) => current.Enchants.GetValueOrDefault(key, -1);

    internal static Rule[] ReadItems(Stream stream)
    {
        var items = JsonSerializer.Deserialize<Catalog>(stream, Embedded.Options)?.Items ?? throw new InvalidDataException("Missing item limits.");
        if (items.Any(i => i.ItemKey == 0 || i.SocketLimit is < 0 or > 5)) throw new InvalidDataException("Invalid socket limits.");
        return items;
    }

    private static Limits Read(Stream items, Stream enchantLimits)
    {
        var rules = ReadItems(items);
        var enchants = JsonSerializer.Deserialize<Dictionary<uint, int>>(enchantLimits) ?? throw new InvalidDataException("Invalid enchant limits.");
        if (enchants.Any(p => p.Key == 0 || p.Value is < 0 or > 10)) throw new InvalidDataException("Invalid enchant limits.");
        return new(rules.ToDictionary(i => i.ItemKey, i => i.SocketLimit), rules.ToDictionary(i => i.ItemKey, i => i.MaxStack), enchants,
            rules.Where(i => i.GearTag != 0).ToDictionary(i => i.ItemKey, i => i.GearTag),
            rules.Where(i => i.GearTags is not null).ToDictionary(i => i.ItemKey, i => i.GearTags!),
            rules.Where(i => i.Addable).Select(i => i.ItemKey).ToHashSet());
    }

    private static Limits Shipped()
    {
        using var items = Embedded.Open("ItemCatalog.json");
        using var enchants = Embedded.Open("MaxEnchant.json");
        return Read(items, enchants);
    }

    public static void Reset() => current = Shipped();
    public static void LoadCache(string root)
    {
        using var items = File.OpenRead(Path.Combine(root, "data/item_names.json"));
        using var enchants = File.OpenRead(Path.Combine(root, "data/max_enchant_map.json"));
        current = Read(items, enchants);
    }
}
