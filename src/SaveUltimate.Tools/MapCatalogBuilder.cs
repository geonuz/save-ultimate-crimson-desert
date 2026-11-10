using System.Buffers.Binary;
using System.Text.Json.Nodes;

using SaveUltimate.Core.Catalogs;

namespace SaveUltimate.Tools;

// Port of the Python map_exploration scene catalog and map_actions builders, including the DLC scene file.
public static class MapCatalogBuilder
{
    private static readonly uint[] Travel = [1000000, 1000001, 1000004, 1000008, 1000103, 1000114];
    private static readonly uint[] PlaceGroups = [114, 115, 116, 662, 663, 664, 665, 661];
    private static readonly (string Group, uint[] Keys)[] IconGroups =
    [
        ("crafting", [1004140, 1000136, 1001504, 1004758, 1003362, 1003363, 1003357, 1005475, 1005476]),
        ("collectibles", [1004786, 1000079, 1004790]), ("abyss", [1001031, 1001032])
    ];
    private const uint AbyssNexus = 1001031, AbyssCresset = 1001032, MemoryFragments = 1004786;
    internal sealed record Scene(string Id, uint Key, string Internal, string Name, string[] Parents, float[] Position, string Category);

    private static uint U32(ClientRecord record, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(record.Bytes.Span[(record.Prefix + offset)..]);

    internal static IEnumerable<Scene> Scenes(byte[] data, Dictionary<uint, ClientRecord> types)
    {
        var r = new ClientReader(data); var scenes = new List<Scene>();
        for (int group = r.Count(); group > 0; group--)
        {
            uint key = r.U32(); int count = r.Count();
            if (!types.TryGetValue(key, out var type)) throw new InvalidDataException("Unsupported scene group.");
            for (int i = 0; i < count; i++)
            {
                if (r.U32() != key) throw new InvalidDataException("Scene identity mismatch.");
                r.SkipText(); r.Take(12);  // prefab path (filled for 2.04 housing) and three references
                string name = r.Text();
                string uuid = Convert.ToHexStringLower(r.Take(44)[8..24]);
                r.SkipText();  // attachment socket
                var transform = r.Take(93);
                float[] position = [BitConverter.ToSingle(transform), BitConverter.ToSingle(transform[4..]), BitConverter.ToSingle(transform[8..])];
                r.Take(4);
                r.Take(checked(r.Count() * 20));
                string[] parents = Enumerable.Range(0, r.Count()).Select(_ => r.Text()).ToArray();
                if (!position.All(float.IsFinite)) throw new InvalidDataException("Invalid scene position.");
                string category = Travel.Contains(key) ? "travel"
                    : type.Name.StartsWith("Mine") || type.Name.StartsWith("Vein_Minerals") || type.Name.StartsWith("SulfurStone") || type.Name == "Mercury" ? "ore"
                    : type.Name.Contains("campfire", StringComparison.OrdinalIgnoreCase) ? "camp" : "other";
                scenes.Add(new($"{key}:{uuid}", key, type.Name, name, parents, position, category));
            }
        }
        if (r.Remaining != 0) throw new InvalidDataException("Unparsed scene data.");
        return scenes;
    }

    public static Dictionary<string, JsonNode> Build(ClientSource source, Func<string, JsonNode> load)
    {
        var types = ClientTables.Records(source.Tables("levelgimmicksceneobjectinfo"));
        var knowledge = ClientTables.Records(source.Tables("knowledgeinfo"));
        var nodes = ClientTables.Records(source.Tables("factionnode"));
        var icons = ClientTables.Records(source.Tables("uimaptextureinfo"), emptyNames: true);
        var languages = new[] { ("en", "eng"), ("ko", "kor") }.ToDictionary(l => l.Item1, l => (Knowledge: source.Strings(l.Item2, "knowledge"), Ui: source.Strings(l.Item2, "ui")));
        // Typed references self-identify their owner and member; used only for captions, never for save fields.
        string KnowledgeName(uint key, string language)
        {
            var references = KnowledgeCatalogBuilder.References(knowledge[key]);
            foreach (uint member in new uint[] { 0x49D, 0x490 })
                foreach (var (m, label) in references)
                    if (m == member && languages[language].Knowledge.GetValueOrDefault(label, "") is { Length: > 0 } text) return text;
            return "";
        }

        var iconInfo = new Dictionary<uint, JsonObject>();
        foreach (var (key, record) in icons)
        {
            // Icons may start with one 18-byte entry (only Actor_* markers, which have no knowledge).
            uint related = U32(record, U32(record, 0) switch { 0 => 40, 1 => 58, _ => throw new InvalidDataException("Unsupported map icon layout.") });
            if (related != 0 && !knowledge.ContainsKey(related)) throw new InvalidDataException("Unresolved map icon knowledge.");
            int caption = ClientTables.Anchor(record, 25, 0x190, key);
            string label = caption < 0 ? "" : (((ulong)key << 32) | 0x190).ToString();
            iconInfo[key] = new JsonObject
            {
                ["knowledge"] = related,
                ["names"] = new JsonObject(languages.Select(l => KeyValuePair.Create<string, JsonNode?>(l.Key,
                    (related != 0 ? KnowledgeName(related, l.Key) : "") is { Length: > 0 } name ? name : l.Value.Ui.GetValueOrDefault(label, ""))))
            };
        }
        var typeInfo = new JsonObject();
        foreach (var (key, record) in types)
        {
            uint icon = U32(record, 16);
            if (icon != 0 && !iconInfo.ContainsKey(icon)) throw new InvalidDataException("Unresolved scene map icon.");
            typeInfo[key.ToString()] = iconInfo.TryGetValue(icon, out var info) ? info.DeepClone() : new JsonObject { ["knowledge"] = 0u, ["names"] = new JsonObject() };
        }
        // Explicit KnowledgeInfo -> FactionNode references, not name matching.
        var nodeKnowledge = new Dictionary<uint, List<uint>>();
        foreach (var (key, record) in knowledge)
        {
            uint node = U32(record, 22);
            if (node == 0) continue;
            if (!nodes.ContainsKey(node)) throw new InvalidDataException("Unresolved knowledge node reference.");
            (nodeKnowledge.TryGetValue(node, out var keys) ? keys : nodeKnowledge[node] = []).Add(key);
        }
        // DLC scene files add placements and replace base ones with the same identity.
        var scenes = new Dictionary<string, Scene>();
        foreach (var (_, data) in source.Misc("levelgimmicksceneobjectinfo_misc"))
        {
            var layer = Scenes(data, types).ToList();
            if (layer.Select(s => s.Id).Distinct().Count() != layer.Count) throw new InvalidDataException("Duplicate client scene identity.");
            foreach (var scene in layer) scenes[scene.Id] = scene;
        }
        static JsonArray Strings(IEnumerable<string> values) => new(values.Select(v => (JsonNode)v).ToArray());
        var catalog = new JsonObject
        {
            ["sourceSha256"] = source.Digests(),
            ["scenes"] = new JsonArray(scenes.Values.Select(s => (JsonNode)new JsonObject
            {
                ["id"] = s.Id, ["key"] = s.Key, ["internal"] = s.Internal, ["name"] = s.Name, ["parents"] = Strings(s.Parents),
                ["position"] = new JsonArray(s.Position.Select(p => (JsonNode)(double)p).ToArray()), ["category"] = s.Category
            }).ToArray()),
            ["types"] = typeInfo,
            ["nodes"] = new JsonArray(nodeKnowledge.Select(p => (JsonNode)new JsonObject
            {
                ["key"] = p.Key, ["internal"] = nodes[p.Key].Name, ["knowledge"] = new JsonArray(p.Value.Select(k => (JsonNode)k).ToArray()),
                ["names"] = new JsonObject(languages.Keys.Select(l => KeyValuePair.Create<string, JsonNode?>(l,
                    string.Join(" / ", p.Value.Select(k => KnowledgeName(k, l)).Where(n => n.Length > 0).Distinct()))))
            }).ToArray())
        };
        return new() { ["data/map_catalog.json"] = catalog, ["data/map_actions.json"] = Actions(catalog, load) };
    }

    // Bulk map actions over node, journal-group and scene-icon knowledge, as in map_actions.py.
    private static JsonArray Actions(JsonObject catalog, Func<string, JsonNode> load)
    {
        var journal = load("data/knowledge_ui.json");
        var visible = journal["itemPaths"]!.AsObject().Select(p => uint.Parse(p.Key)).ToHashSet();
        var known = load("data/knowledge_keys_all.json").AsArray();
        var valid = known.Select(r => r!["key"]!.GetValue<uint>()).ToHashSet();
        var named = known.GroupBy(r => r!["name"]!.GetValue<string>()).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single()!["key"]!.GetValue<uint>());
        var groups = journal["groups"]!.AsArray().Select(g => g!.AsObject()).ToArray();
        uint[] Members(uint group) => groups.Single(g => g["key"]!.GetValue<uint>() == group)["members"]!.AsArray().Select(m => m!.GetValue<uint>()).ToArray();
        var scenes = catalog["scenes"]!.AsArray().Select(s => (Id: s!["id"]!.GetValue<string>(), Key: s["key"]!.GetValue<uint>(), Name: s["name"]!.GetValue<string>(), Category: s["category"]!.GetValue<string>())).ToArray();
        var types = catalog["types"]!.AsObject().Select(p => (Key: uint.Parse(p.Key), Knowledge: p.Value!["knowledge"]!.GetValue<uint>(), Names: p.Value["names"]!)).ToArray();
        var actions = new JsonArray();
        JsonObject? Add(string group, JsonNode names, IEnumerable<uint> keys, string mode, IEnumerable<string>? scenes = null)
        {
            uint[] sorted = keys.Distinct().Order().ToArray();
            if (!sorted.All(valid.Contains)) throw new InvalidDataException("Map action contains unknown knowledge keys.");
            if (sorted.Length == 0) return null;
            var action = new JsonObject
            {
                ["group"] = group, ["names"] = names.DeepClone(), ["keys"] = Json(sorted), ["mode"] = mode,
                ["scenes"] = new JsonArray((scenes ?? []).Select(s => (JsonNode)s).ToArray()), ["journal_keys"] = Json(sorted.Where(visible.Contains))
            };
            actions.Add(action); return action;
        }
        static JsonArray Json(IEnumerable<uint> values) => new(values.Select(v => (JsonNode)v).ToArray());
        static JsonArray Label(string kind, uint key) => new(kind, key);

        Add("places", new JsonObject { ["en"] = "Nodes", ["ko"] = "거점" }, catalog["nodes"]!.AsArray().SelectMany(n => n!["knowledge"]!.AsArray().Select(k => k!.GetValue<uint>())), "locations")!["label_key"] = "map.node";
        foreach (var group in groups.Where(g => PlaceGroups.Contains(g["key"]!.GetValue<uint>())))
            if (Add("places", new JsonObject { ["en"] = group["name"]!.DeepClone(), ["ko"] = group["nameKo"]!.DeepClone() }, Members(group["key"]!.GetValue<uint>()), "locations") is { } action)
                action["label_ref"] = Label("knowledge_groups", group["key"]!.GetValue<uint>());
        // Knowledge definitions and scene identities are counted separately.
        uint[] ores = types.Where(t => t.Knowledge != 0 && scenes.Any(s => s.Category == "ore" && s.Key == t.Key)).Select(t => t.Knowledge).ToArray();
        foreach (var (group, keys) in IconGroups.Append(("resources", ores)))
            foreach (uint key in keys.Distinct().Order())
            {
                var info = types.First(t => t.Knowledge == key);
                var owners = types.Where(t => t.Knowledge == key).Select(t => t.Key).ToHashSet();
                // Island/quest variants of the Abyss Nexus have no shared-icon reference in the type table.
                if (key == AbyssNexus) owners.UnionWith([1000008, 1000114]);
                string[] placed = scenes.Where(s => owners.Contains(s.Key)).Select(s => s.Id).ToArray();
                JsonObject? action;
                // The user-confirmed complete save holds the Abyss Nexus travel records. A field nexus scene teaches the knowledge
                // named after it (all 213 2.03 field scenes and both DLC nexuses visited in the 1-2 save), which adds the DLC's 27.
                if (key == AbyssNexus) action = Add(group, info.Names, load("data/abyss_nexus_knowledge.json")["keys"]!.AsArray().Select(k => k!.GetValue<uint>())
                    .Concat(scenes.Where(s => owners.Contains(s.Key)).Select(s => named.GetValueOrDefault("Knowledge_" + s.Name)).Where(k => k != 0)), "travel", placed);
                else if (key == MemoryFragments)
                {
                    action = Add(group, info.Names, Members(77).Append(key), "knowledge");
                    action!["count_keys"] = Json(Members(77));
                }
                else
                {
                    action = Add(group, info.Names, [key], "icons", placed);
                    if (key == AbyssCresset) Cressets(action!, placed, load);
                }
                action!["label_ref"] = Label("knowledge", key);
            }
        return actions;
    }

    // Abyss Cressets need fog pivots captured from a save and the challenge quests that place them.
    private static void Cressets(JsonObject action, string[] placed, Func<string, JsonNode> load)
    {
        var records = load("data/abyss_cresset_records.json");
        var pivots = records["pivots"]!.AsObject().DeepClone().AsObject();
        // Pivots differ per playthrough, so recorded values serve for the DLC's 29 as well.
        foreach (var (id, pivot) in records["dlcPivots"]!.AsObject()) pivots[id] = pivot!.DeepClone();
        if (!pivots.All(p => placed.Contains(p.Key)) || pivots.Any(p => Convert.FromHexString(p.Value!.GetValue<string>()).Length != 12))
            throw new InvalidDataException("Abyss Cresset reference does not match the current placement catalog.");
        placed = placed.Where(pivots.ContainsKey).ToArray();
        action["scenes"] = new JsonArray(placed.Select(s => (JsonNode)s).ToArray());
        action["reference_pivots"] = pivots;
        var quests = load("data/challenge_catalog.json")["quests"]!;
        var roots = new List<uint>();
        foreach (var (quest, name, count) in new[] { (1000066u, "Challenge_Standstone_Adventure", 60), (1000860u, "Challenge_Standstone_Puzzle", 37),
            (1000039u, "Challenge_Maze", 4), (1000830u, "Challenge_Standstone_Adventure_II", 20), (1000887u, "Challenge_Standstone_Puzzle_II", 9) })
        {
            var definition = quests[quest.ToString()]!;
            var missions = definition["missions"]!.AsArray().Select(m => m!.GetValue<uint>()).ToArray();
            if (definition["internalName"]!.GetValue<string>() != name || missions.Length != count) throw new InvalidDataException("Abyss Cresset challenge definitions changed.");
            roots.AddRange(missions);
        }
        if (roots.Distinct().Count() != placed.Length) throw new InvalidDataException("Abyss Cresset challenge count does not match placements.");
        action["challenge_roots"] = new JsonArray(roots.Select(r => (JsonNode)r).ToArray());
    }
}
