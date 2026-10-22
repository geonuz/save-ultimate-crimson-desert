using System.IO;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SaveUltimate.App;

internal sealed record KnowledgeText(string Name, string InternalName, string Description)
{
    private static Lazy<Dictionary<uint, KnowledgeText>> texts = new(LoadTexts);
    internal static void Reset() => texts = new(LoadTexts);
    internal static KnowledgeText Get(uint key) => texts.Value[key];
    private static Dictionary<uint, KnowledgeText> LoadTexts()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "resources");
        using var names = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "data", "knowledge_keys_all.json")));
        using var details = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "data", "knowledge_details.json")));
        var descriptions = details.RootElement.GetProperty("items");
        var result = names.RootElement.EnumerateArray().ToDictionary(e => e.GetProperty("key").GetUInt32(), e =>
        {
            string key = e.GetProperty("key").GetUInt32().ToString();
            string description = descriptions.TryGetProperty(key, out var d) && d.TryGetProperty(Tr.Language == "ko" ? "descriptionKo" : "description", out var desc) ? desc.GetString() ?? "" : "";
            description = Tr.Name("knowledge_descriptions", key, description);
            description = Regex.Replace(description, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            return new KnowledgeText(Tr.Name("knowledge", key, e.GetProperty("display_name").GetString()!),
                e.GetProperty("name").GetString()!, WebUtility.HtmlDecode(Regex.Replace(description, "<[^>]*>", "")));
        });
        foreach (uint key in result.Keys.ToArray())
            result[key] = result[key] with { Description = GameText.Display(result[key].Description) };
        return result;
    }
}

