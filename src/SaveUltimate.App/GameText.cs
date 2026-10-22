using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SaveUltimate.App;

internal static class GameText
{
    internal static void Reset() => names = CreateNames();
    private static Lazy<Dictionary<string, string>> names = CreateNames();
    private static Lazy<Dictionary<string, string>> CreateNames() => new(() =>
    {
        using var knowledge = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "resources/data/knowledge_keys_all.json")));
        var result = new Dictionary<string, string>();
        foreach (var row in knowledge.RootElement.EnumerateArray())
        {
            string key = row.GetProperty("key").GetUInt32().ToString();
            result["knowledge:" + row.GetProperty("name").GetString()] = Tr.Name("knowledge", key, row.GetProperty("display_name").GetString()!);
        }
        foreach (var item in ItemCatalog.Shared.Choices.Where(c => c.InternalName.Length > 0))
            result["item:" + item.InternalName] = item.Name;
        return result;
    });
    internal static string Display(string text) => Regex.Replace(text, @"\{StaticInfo:(Knowledge|Item):([^#{}]+)#([^{}]*)\}",
        m => names.Value.GetValueOrDefault(m.Groups[1].Value.ToLowerInvariant() + ":" + m.Groups[2].Value, m.Groups[3].Value), RegexOptions.IgnoreCase);
}
