using System.IO;
using System.Text.Json;

namespace SaveUltimate.App;

internal sealed record CharacterText(string Name, string Title, string Area, string Service)
{
    private static Lazy<Dictionary<uint, CharacterText>> texts = new(Load);
    internal static void Reset() => texts = new(Load);
    internal static CharacterText Get(uint key) => texts.Value.GetValueOrDefault(key) ?? new(Tr.Format("mercenary.unknown", ("character_key", key)), "", "", "");
    // A companion's or NPC's profile is the journal description and picture of the knowledge entry that lists it.
    internal static (string Description, System.Windows.Media.ImageSource? Picture) Profile(uint key) =>
        SaveUltimate.Core.Saves.KnowledgeCatalog.Shared.Characters.TryGetValue(key, out uint knowledge)
            ? (KnowledgeText.Find(knowledge)?.Description ?? "", Picture(key, 280)) : ("", null);
    internal static System.Windows.Media.ImageSource? Picture(uint key, uint size) =>
        SaveUltimate.Core.Saves.KnowledgeCatalog.Shared.Characters.TryGetValue(key, out uint knowledge)
            ? ItemImages.GetFile(Path.Combine(AppContext.BaseDirectory, "game_data", "images", "knowledge", $"{knowledge}.png"), size) : null;
    private static Dictionary<uint, CharacterText> Load()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "resources");
        using var names = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "data", "character_names.json")));
        using var catalog = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "data", "npc_catalog.json")));
        return names.RootElement.EnumerateObject().ToDictionary(p => uint.Parse(p.Name), p =>
        {
            string fallback = p.Value.GetProperty("name").GetString() ?? p.Value.GetProperty("internalName").GetString() ?? p.Name;
            string name = Tr.Name("characters", p.Name, fallback);
            JsonElement npc = default; string npcKey = "";
            if (catalog.RootElement.GetProperty("characters").TryGetProperty(p.Name, out var key))
            { npcKey = key.ToString(); catalog.RootElement.GetProperty("npcs").TryGetProperty(npcKey, out npc); }
            // npc_catalog holds Korean and English; Japanese and Russian come from the synced name packs, keyed by NPC.
            string Field(string field, string section)
            {
                string Pick(string language) => npc.ValueKind == JsonValueKind.Object && npc.TryGetProperty(language, out var locale) && locale.TryGetProperty(field, out var v) ? v.GetString() ?? "" : "";
                string english = Pick("en");
                return Tr.Language switch { "ko" => Pick("ko") is { Length: > 0 } ko ? ko : english, "en" => english, _ => Tr.Name(section, npcKey, english) };
            }
            return new CharacterText(name, Field("title", "npc_titles"), Field("areaOrAffiliation", "npc_areas"), Field("service", "npc_services"));
        });
    }
}
