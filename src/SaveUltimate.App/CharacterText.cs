using System.IO;
using System.Text.Json;

namespace SaveUltimate.App;

internal sealed record CharacterText(string Name, string Title, string Area, string Service)
{
    private static Lazy<Dictionary<uint, CharacterText>> texts = new(Load);
    internal static void Reset() => texts = new(Load);
    internal static CharacterText Get(uint key) => texts.Value.GetValueOrDefault(key) ?? new(Tr.Format("mercenary.unknown", ("character_key", key)), "", "", "");
    private static Dictionary<uint, CharacterText> Load()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "resources");
        using var names = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "data", "character_names.json")));
        using var catalog = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "data", "npc_catalog.json")));
        return names.RootElement.EnumerateObject().ToDictionary(p => uint.Parse(p.Name), p =>
        {
            string fallback = p.Value.GetProperty("name").GetString() ?? p.Value.GetProperty("internalName").GetString() ?? p.Name;
            string name = Tr.Name("characters", p.Name, fallback);
            JsonElement npc = default;
            if (catalog.RootElement.GetProperty("characters").TryGetProperty(p.Name, out var key))
                catalog.RootElement.GetProperty("npcs").TryGetProperty(key.ToString(), out npc);
            string Field(string field) => npc.ValueKind == JsonValueKind.Object && (npc.TryGetProperty(Tr.Language, out var locale) || npc.TryGetProperty("en", out locale)) && locale.TryGetProperty(field, out var v) ? v.GetString() ?? "" : "";
            return new CharacterText(name, Field("title"), Field("areaOrAffiliation"), Field("service"));
        });
    }
}
