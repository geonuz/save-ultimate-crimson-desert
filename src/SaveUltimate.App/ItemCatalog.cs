using System.Globalization;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using SaveUltimate.Core.Saves;

namespace SaveUltimate.App;

public sealed class ItemCatalog
{
    public static ItemCatalog Shared { get; private set; } = new();
    internal static void Reset() => Shared = new();
    private readonly Dictionary<uint, JsonElement> items;
    private readonly Dictionary<string, JsonElement> details;
    private readonly Dictionary<string, string> stores;
    private readonly Dictionary<string, JsonElement> characterInfo;
    public ItemSearchWindow.Choice[] Choices { get; }
    public GemChoice[] Gems { get; }
    public string[] GemCategories { get; }
    private static JsonElement Read(string relative) => JsonSerializer.Deserialize<JsonElement>(File.ReadAllBytes(CatalogFiles.Find(relative)));
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) ? property.GetString() ?? "" : "";
    private static Dictionary<string, string> Strings(JsonElement value) => value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");

    private ItemCatalog()
    {
        items = Read("data/item_names.json").GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("itemKey").GetUInt32());
        details = Read("data/item_details.json").GetProperty("items").EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        stores = Strings(Read("data/store_names.json"));
        characterInfo = Read("data/character_names.json").EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        var templates = Read("data/template_keys.json").EnumerateArray().Select(v => v.GetUInt32()).ToHashSet();
        Choices = items.Select(p => new ItemSearchWindow.Choice(p.Key, Name(p.Key), Category(p.Key), Text(p.Value, "internalName"), templates.Contains(p.Key))).OrderBy(c => c.Key).ToArray();
        Gems = new[] { new GemChoice(0, Tr.Text("Clear")) }.Concat(Read("data/socket_gems.json").EnumerateArray().Select(g =>
        {
            uint key = g.GetProperty("key").GetUInt32();
            string category = Text(g, "category");
            return new GemChoice(key, Name(key), Tr.Text(category), Text(g, "name"), category);
        })).ToArray();
        GemCategories = new[] { Tr.Text("map.all") }.Concat(Gems.Skip(1).Select(g => g.Category).Distinct()).ToArray();
    }

    public string InternalName(uint key) => items.TryGetValue(key, out var row) ? Text(row, "internalName") : "";
    public string Name(uint key) => Tr.Name("items", key.ToString(), items.TryGetValue(key, out var row) ? Text(row, "name") : $"Item {key}");
    public string KnowledgeName(uint key) => Tr.Name("knowledge", key.ToString(), SkillCatalog.Shared.Names.TryGetValue(key, out var names) ? names[Tr.Language == "ko" ? 1 : 0] : key.ToString());
    public string Category(uint key)
    {
        if (!items.TryGetValue(key, out var row) || !row.TryGetProperty("gameClassification", out var group)) return Tr.Text("Unclassified");
        string name = Tr.Language == "ko" ? Text(group, "nameKo") : Text(group, "name");
        if (group.TryGetProperty("groupKey", out var groupKey)) name = Tr.Name("item_groups", groupKey.ToString(), name);
        if (name.Length == 0) name = Text(group, "name");
        return name.Length == 0 ? Tr.Text("Unclassified") : name;
    }
    public string CategoryHint(uint key)
    {
        if (!items.TryGetValue(key, out var row) || !row.TryGetProperty("gameClassification", out var group)) return Tr.Text("No verified game classification");
        string category = Category(key), detail = Text(group, Tr.Language == "ko" ? "detailKo" : "detail");
        if (detail.Length == 0) detail = Text(group, "detail");
        if (group.TryGetProperty("detailKey", out var detailKey)) detail = Tr.Name("item_groups", detailKey.ToString(), detail);
        return category + (detail.Length > 0 && detail != category ? "\n" + detail : "") + $"\nCategoryInfo: {group.GetProperty("categoryKey")} ({string.Join("/", group.GetProperty("indices").EnumerateArray())})\nItemGroupInfo: {group.GetProperty("groupKey")} / {group.GetProperty("detailKey")}";
    }
    public ulong MaxStack(uint key) => items.TryGetValue(key, out var row) && row.TryGetProperty("maxStack", out var value) && value.TryGetUInt64(out ulong count) ? count : 0;
    public string Store(ushort key) => stores.GetValueOrDefault(key.ToString(), $"Store_{key}").Replace("Store_", "").Replace('_', ' ');
    public string Owner(InventoryItem item) => item.Source == ItemSource.Equipment ? Tr.Text("skills.character.0")
        : item.OwnerNumber == 0 ? "" : item.OwnerName.Length > 0 ? item.OwnerName
        : Tr.Name("characters", item.OwnerCharacter.ToString(), characterInfo.TryGetValue(item.OwnerCharacter.ToString(), out var row) ? Text(row, "name") : $"? ({item.OwnerCharacter})");
    public string Description(uint key)
    {
        if (!details.TryGetValue(key.ToString(), out var row)) return Tr.Text("No description available");
        string text = Tr.Name("item_descriptions", key.ToString(), Text(row, Tr.Language == "ko" ? "descriptionKo" : "description"));
        if (text.Length == 0) text = Text(row, "description");
        text = WebUtility.HtmlDecode(Regex.Replace(Regex.Replace(text, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase), "<[^>]*>", ""));
        return text.Length == 0 ? Tr.Text("No description available") : text;
    }
    public string Stats(InventoryItem item)
    {
        if (!details.TryGetValue(item.Key.ToString(), out var row)) return item.EnchantLevel is ushort e ? Tr.Format("port.enchant", ("level", e), ("max", "?")) : "";
        var lines = new List<string>();
        if (item.EnchantLevel is ushort enchant) lines.Add(Tr.Format("port.enchant", ("level", enchant), ("max", row.TryGetProperty("maxEnchant", out var max) ? max.ToString() : "?")));
        if (row.TryGetProperty("levels", out var levels))
            foreach (var level in levels.EnumerateArray().Where(l => l.GetProperty("level").GetInt32() == (item.EnchantLevel ?? 0)))
                foreach (var stat in level.GetProperty("stats").EnumerateArray())
                {
                    string label = stat.GetProperty("key").GetInt32() switch { 1000000 => Tr.Text("Health"), 1000002 => Tr.Text("Attack"), 1000003 => Tr.Text("Defense"), 1000007 => Tr.Text("Crit Rate"), 1000009 => Tr.Text("Damage Reduction"), 1000010 => Tr.Text("Attack Speed"), 1000011 => Tr.Text("Move Speed"), _ => "" };
                    double value = stat.GetProperty("value").GetDouble();
                    if (label.Length > 0 && value != 0) lines.Add($"{label}: {(Text(stat, "kind") == "level" ? "Lv." + value.ToString(CultureInfo.InvariantCulture) : (value / 1000).ToString("G", CultureInfo.InvariantCulture))}");
                }
        return string.Join("\n", lines);
    }
}

