using System.Text.Json.Nodes;
using CDSE.Core.Archives;
using CDSE.Core.Catalogs;

namespace CDSE.Tools;

/// <summary>
/// Builds a game-name pack (locale/names_xx.json) for a language the editor adds, from the packs it already ships: every entry
/// of the Japanese and Russian packs is found in the game's string tables by the string id both texts share, and that id's text in
/// the new language is taken. A pair the client uses under several ids takes the id of the section's own table first, then the
/// text most of them hold. A text joined from several strings ("\n\n") is carried piece by piece. What cannot be found is left
/// out, and the editor shows English for it.
/// </summary>
internal static class LocaleNamesBuilder
{
    private static readonly Dictionary<string, string[]> Tables = new()
    {
        ["items"] = ["item"], ["item_descriptions"] = ["item"], ["item_groups"] = ["itemgroup", "localstringinfo"],
        ["characters"] = ["character", "npcname"], ["npc_titles"] = ["character", "npc"], ["npc_services"] = ["character", "npc"], ["npc_areas"] = ["character", "npc", "region"],
        ["knowledge"] = ["knowledge"], ["knowledge_descriptions"] = ["knowledge"], ["knowledge_groups"] = ["knowledge"], ["skill_descriptions"] = ["knowledge"],
        ["quests"] = ["quest"], ["missions"] = ["quest"], ["quest_groups"] = ["quest"], ["stores"] = ["npc", "character"],
    };

    public static (JsonObject Pack, Dictionary<string, (int Total, int Found)> Coverage) Build(GameFiles game, string resources, string code, string languageName)
    {
        if (!ClientMetadata.Languages.TryGetValue(code, out var target)) throw new ArgumentException($"Unknown language {code}.");
        Dictionary<string, string> Load(string language)
        {
            var (group, folder) = ClientMetadata.Languages[language];
            var result = new Dictionary<string, string>();
            foreach (string table in game.Archive(group).Index.Entries.Keys.Where(k => k.EndsWith(".paloc", StringComparison.Ordinal)).Select(k => Path.GetFileNameWithoutExtension(k)!))
                foreach (var (_, layer) in game.ReadLayers(group, $"gamedata/stringtable/binary__/{folder}/{table}.paloc"))
                    foreach (var (key, text) in ClientTables.Strings(layer)) result[table + "/" + key] = text;
            return result;
        }
        var ja = Load("ja"); var ru = Load("ru"); var texts = Load(code);
        var pairs = new Dictionary<(string, string), List<string>>(); var single = new Dictionary<string, List<string>>();
        foreach (var (id, j) in ja)
        {
            if (ru.TryGetValue(id, out var r)) { if (!pairs.TryGetValue((j, r), out var ids)) pairs[(j, r)] = ids = []; ids.Add(id); }
            if (!single.TryGetValue(j, out var same)) single[j] = same = []; same.Add(id);
        }
        string? Pick(IEnumerable<string> ids, string[] tables, bool agreeing)
        {
            var found = ids.Select(i => (Table: i[..i.IndexOf('/')], Text: texts.GetValueOrDefault(i, ""))).Where(t => t.Text.Length > 0).ToArray();
            if (found.Length == 0) return null;
            var own = found.Where(f => tables.Contains(f.Table)).ToArray();
            var options = (own.Length > 0 ? own : found).GroupBy(f => f.Text).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToArray();
            return agreeing && options.Length > 1 ? null : options[0].Key;
        }
        // The pair first; a Japanese text alone only where every id holding it says the same in the new language.
        string? One(string j, string r, string[] tables)
            => pairs.TryGetValue((j, r), out var ids) ? Pick(ids, tables, false) : single.TryGetValue(j, out var same) ? Pick(same, tables, true) : null;

        var japanese = JsonNode.Parse(File.ReadAllBytes(Path.Combine(resources, "locale/names_ja.json")))!.AsObject();
        var russian = JsonNode.Parse(File.ReadAllBytes(Path.Combine(resources, "locale/names_ru.json")))!.AsObject();
        var pack = new JsonObject { ["_language_name"] = languageName, ["_generated_by"] = "Official game string references (CDSE.Tools locale-names)" };
        var coverage = new Dictionary<string, (int, int)>();
        foreach (var (section, node) in japanese)
        {
            if (node is not JsonObject entries) continue;
            if (section.StartsWith('_')) { pack[section] = node.DeepClone(); continue; }
            var other = russian[section] as JsonObject; string[] tables = Tables.GetValueOrDefault(section, []);
            var result = new JsonObject(); int found = 0;
            foreach (var (key, value) in entries)
            {
                string j = value!.GetValue<string>(), r = other?[key]?.GetValue<string>() ?? "";
                string? text = One(j, r, tables);
                if (text is null && j.Split("\n\n") is { Length: > 1 } parts)
                {
                    var rest = r.Split("\n\n");
                    var pieces = parts.Select((p, i) => One(p, rest.Length == parts.Length ? rest[i] : "", tables)).ToArray();
                    if (pieces.All(p => p is not null)) text = string.Join("\n\n", pieces);
                }
                if (text is { Length: > 0 }) { result[key] = text; found++; }
            }
            pack[section] = result; coverage[section] = (entries.Count, found);
        }
        return (pack, coverage);
    }
}
