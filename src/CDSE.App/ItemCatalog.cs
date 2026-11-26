using System.Globalization;
using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using CDSE.Core.Saves;

namespace CDSE.App;

public sealed class ItemCatalog
{
    // Built on first use: startup resets it several times (language, cache, DLC) before anything reads it.
    private static ItemCatalog? shared;
    public static ItemCatalog Shared => shared ??= new();
    public static void Reset() => shared = null;
    private readonly Dictionary<uint, JsonElement> items;
    private readonly Dictionary<string, JsonElement> details;
    private readonly Dictionary<string, string> stores;
    private readonly Dictionary<string, JsonElement> characterInfo;
    public ItemSearchWindow.Choice[] Choices { get; }
    public GemChoice[] Gems { get; }
    private static JsonElement Read(string relative) => JsonSerializer.Deserialize<JsonElement>(File.ReadAllBytes(CatalogFiles.Find(relative)));
    private static string Text(JsonElement value, string name) => value.TryGetProperty(name, out var property) ? property.GetString() ?? "" : "";
    private static Dictionary<string, string> Strings(JsonElement value) => value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");

    private ItemCatalog()
    {
        items = Read("data/item_names.json").GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("itemKey").GetUInt32());
        details = Read("data/item_details.json").GetProperty("items").EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        stores = Strings(Read("data/store_names.json"));
        characterInfo = Read("data/character_names.json").EnumerateObject().ToDictionary(p => p.Name, p => p.Value);
        static bool Shown(uint key) => !DlcContent.Hidden || !DlcContent.Shared.Items.Contains(key);
        Choices = items.Where(p => Shown(p.Key)).Select(p => new ItemSearchWindow.Choice(p.Key, Name(p.Key), Category(p.Key), Text(p.Value, "internalName"))).OrderBy(c => c.Key).ToArray();
        gemRows = Read("data/socket_gems.json").EnumerateArray().ToDictionary(g => g.GetProperty("key").GetUInt32(), g => g.Clone());
        var categories = gemRows.ToDictionary(p => p.Key, p => Text(p.Value, "category"));
        // ItemGroupInfo 18317 is the game's "Abyss Gear" group, so gears from game updates list themselves.
        // shortcut: a gear missing from socket_gems.json counts as Special Effects with no effect text; rerun catalog-gems after a game update.
        var gears = categories.Keys.Concat(items.Keys.Where(k => InGroup(k, 18317)).Except(categories.Keys).Order()).Where(k => items.ContainsKey(k) && Shown(k));
        Gems = new[] { new GemChoice(0, Tr.Text("Clear")) }.Concat(gears.Select(key =>
        {
            string category = categories.GetValueOrDefault(key, "Special Effects");
            return new GemChoice(key, Name(key), Tr.Text(category), Text(items[key], "name"), category);
        })).ToArray();
    }

    private readonly Dictionary<uint, JsonElement> gemRows;
    /// <summary>
    /// What an Abyss Gear does in one line: its category and amount ("Attack +2", "Move Speed +4%"), its level for the elemental
    /// resistances, the game's own tooltip line for a skill gear that has one ("+2 Pet Trust Gain"), or else the effect its knowledge
    /// entry names. Empty for a gear the catalog does not know.
    /// </summary>
    public string GemEffect(uint key)
    {
        if (!gemRows.TryGetValue(key, out var row)) return "";
        string category = Tr.Text(Text(row, "category"));
        if (row.TryGetProperty("amount", out var amount))
        {
            double value = amount.GetDouble();
            return $"{category} {(value >= 0 ? "+" : "")}{value.ToString("0.###", CultureInfo.CurrentCulture)}{(row.TryGetProperty("percent", out var percent) && percent.GetBoolean() ? "%" : "")}";
        }
        if (row.TryGetProperty("level", out var level)) return $"{category} Lv.{level.GetInt32()}";
        if (row.TryGetProperty("effect", out var line))
            return line.TryGetProperty(Tr.Language, out var local) ? local.GetString() ?? "" : Text(line, "en");
        string[] lines = GemUse(row).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // The skill gears explain their effect on a line marked with a reference mark (name : what it does), whose name is mostly
        // the gear's own, so only what it does is kept; the others explain it in their last sentence.
        if (lines.FirstOrDefault(l => l.StartsWith('\u203B')) is { } marked)
            return Regex.Match(marked, @"^\u203B[^:\uFF1A]*[:\uFF1A]\s*(.+)$") is { Success: true } said ? said.Groups[1].Value : marked.TrimStart('\u203B', ' ');
        return lines.LastOrDefault() ?? "";
    }
    /// <summary>The effect line, then what the game's knowledge entry says about using the gear (where it fits, what it does).</summary>
    public string GemDetail(uint key)
    {
        if (!gemRows.TryGetValue(key, out var row)) return "";
        string use = GemUse(row), effect = GemEffect(key);
        return use.Length == 0 ? effect : row.TryGetProperty("amount", out _) || row.TryGetProperty("level", out _) || row.TryGetProperty("effect", out _) ? effect + "\n\n" + use : use;
    }
    // The knowledge text past its crafting paragraph: "[How to Use] Equippable on: ..." and the effect.
    private static string GemUse(JsonElement row)
    {
        if (!row.TryGetProperty("knowledge", out var knowledge) || KnowledgeText.Find(knowledge.GetUInt32()) is not { } text) return "";
        string[] paragraphs = text.Description.Replace("\r", "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("\n", paragraphs.Length > 2 ? paragraphs.Skip(1) : paragraphs);
    }

    private Dictionary<string, uint>? named;
    /// <summary>The item with an English name, or 0. Names the fish of the journal, whose items have the cut-out pictures.</summary>
    public uint KeyNamed(string english) => (named ??= items.GroupBy(p => Text(p.Value, "name")).ToDictionary(g => g.Key, g => g.Min(p => p.Key))).GetValueOrDefault(english);
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
    private bool InGroup(uint key, uint itemGroup) => items.TryGetValue(key, out var row) && row.TryGetProperty("gameClassification", out var group) &&
        group.TryGetProperty("groupKey", out var groupKey) && groupKey.GetUInt32() == itemGroup;
    // ItemGroupInfo 18312 is the game's "Currency" group.
    public bool IsCurrency(uint key) => InGroup(key, 18312);
    public string CategoryHint(uint key)
    {
        if (!items.TryGetValue(key, out var row) || !row.TryGetProperty("gameClassification", out var group)) return Tr.Text("No verified game classification");
        string category = Category(key), detail = Text(group, Tr.Language == "ko" ? "detailKo" : "detail");
        if (detail.Length == 0) detail = Text(group, "detail");
        if (group.TryGetProperty("detailKey", out var detailKey)) detail = Tr.Name("item_groups", detailKey.ToString(), detail);
        // The game's table keys stay out of the tooltip: it names the category and its detail only.
        return category + (detail.Length > 0 && detail != category ? "\n" + detail : "");
    }
    public ulong MaxStack(uint key) => items.TryGetValue(key, out var row) && row.TryGetProperty("maxStack", out var value) && value.TryGetUInt64(out ulong count) ? count : 0;
    public string Store(ushort key) => Tr.Name("stores", key.ToString(), stores.GetValueOrDefault(key.ToString(), key.ToString()));
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

