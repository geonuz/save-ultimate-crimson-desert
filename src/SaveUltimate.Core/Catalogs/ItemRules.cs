using System.Text.Json;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.Core.Catalogs;

public static class ItemRules
{
    private sealed record SocketCatalog(SocketRule[] Items);
    private sealed record SocketRule(uint ItemKey, int SocketLimit);
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static Dictionary<uint, int> ReadSocketLimits(Stream stream)
    {
        var items = JsonSerializer.Deserialize<SocketCatalog>(stream, Options)?.Items
            ?? throw new InvalidDataException("Missing item limits.");
        var limits = items.ToDictionary(i => i.ItemKey, i => i.SocketLimit);
        if (limits.Any(p => p.Key == 0 || p.Value is < 0 or > 5))
            throw new InvalidDataException("Invalid socket limits.");
        return limits;
    }

    public static void Reset() { SocketState.SetLimits(null); EnchantLimits.SetLimits(null); }
    public static void LoadCache(string root)
    {
        using var items = File.OpenRead(Path.Combine(root, "data/item_names.json"));
        var sockets = ReadSocketLimits(items);
        var enchants = JsonSerializer.Deserialize<Dictionary<uint, int>>(File.ReadAllBytes(Path.Combine(root, "data/max_enchant_map.json")))
            ?? throw new InvalidDataException("Invalid enchant limits.");
        if (enchants.Any(p => p.Key == 0 || p.Value is < 0 or > 10))
            throw new InvalidDataException("Invalid synchronized item limits.");
        SocketState.SetLimits(sockets); EnchantLimits.SetLimits(enchants);
    }
}
