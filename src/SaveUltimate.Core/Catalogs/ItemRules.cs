using System.Text.Json;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Core.Catalogs;

// Per-item limits from the shipped catalog or the last game sync, swapped as one so a reader never sees a mix.
public static class ItemRules
{
    private sealed record Catalog(Rule[] Items);
    private sealed record Rule(uint ItemKey, int SocketLimit, ulong MaxStack);
    private sealed record Limits(Dictionary<uint, int> Sockets, Dictionary<uint, ulong> Stacks, Dictionary<uint, int> Enchants);
    private static Limits current = Shipped();

    public static int Sockets(uint key) => current.Sockets.GetValueOrDefault(key);
    internal static ulong Stack(uint key) => current.Stacks.GetValueOrDefault(key);
    // -1 for an item without enchant levels.
    internal static int Enchant(uint key) => current.Enchants.GetValueOrDefault(key, -1);

    internal static (Dictionary<uint, int> Sockets, Dictionary<uint, ulong> Stacks) ReadItems(Stream stream)
    {
        var items = JsonSerializer.Deserialize<Catalog>(stream, Embedded.Options)?.Items ?? throw new InvalidDataException("Missing item limits.");
        var sockets = items.ToDictionary(i => i.ItemKey, i => i.SocketLimit);
        if (sockets.Any(p => p.Key == 0 || p.Value is < 0 or > 5)) throw new InvalidDataException("Invalid socket limits.");
        return (sockets, items.ToDictionary(i => i.ItemKey, i => i.MaxStack));
    }

    private static Limits Read(Stream items, Stream enchantLimits)
    {
        var (sockets, stacks) = ReadItems(items);
        var enchants = JsonSerializer.Deserialize<Dictionary<uint, int>>(enchantLimits) ?? throw new InvalidDataException("Invalid enchant limits.");
        if (enchants.Any(p => p.Key == 0 || p.Value is < 0 or > 10)) throw new InvalidDataException("Invalid enchant limits.");
        return new(sockets, stacks, enchants);
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
