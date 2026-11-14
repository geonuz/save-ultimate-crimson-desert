using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using SaveUltimate.Core.Catalogs;

namespace SaveUltimate.Tools;

// Port of the Python map_exploration scene catalog and map_actions builders, including the DLC scene file.
public static partial class MapCatalogBuilder
{
    private static readonly uint[] Travel = [1000000, 1000001, 1000004, 1000008, 1000103, 1000114];
    private static readonly uint[] PlaceGroups = [114, 115, 116, 662, 663, 664, 665, 661];
    private static readonly (string Group, uint[] Keys)[] IconGroups =
    [
        ("crafting", [1004140, 1000136, 1001504, 1004758, 1003362, 1003363, 1003357, 1005475, 1005476]),
        ("collectibles", [1004786, 1000079, 1004790]), ("abyss", [1001031, 1001032])
    ];
    private const uint AbyssNexus = 1001031, AbyssCresset = 1001032, MemoryFragments = 1004786, Bells = 1004790;
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
                // 2.05.00 layout: the attachments and prefab path come first and the type key moved behind the transform.
                r.Take(checked(r.Count() * 20));  // attachments
                r.SkipText();  // prefab path (filled for housing)
                string name = r.Text();
                r.SkipText();  // socket
                string uuid = Convert.ToHexStringLower(r.Take(16));
                r.Take(16);
                var transform = r.Take(92);
                float[] position = [BitConverter.ToSingle(transform), BitConverter.ToSingle(transform[4..]), BitConverter.ToSingle(transform[8..])];
                if (r.U32() != key) throw new InvalidDataException("Scene identity mismatch.");
                r.Take(29);
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
            // Icons start with [u32 count][18-byte entries] (one entry only on Actor_* markers, which have no knowledge); the
            // related knowledge sits 35 bytes after the entries (36 before 2.05.00, which dropped one byte in front of it).
            uint related = U32(record, U32(record, 0) switch { 0 => 39, 1 => 57, _ => throw new InvalidDataException("Unsupported map icon layout.") });
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
        return new() { ["data/map_catalog.json"] = catalog, ["data/map_actions.json"] = Actions(catalog, load, source) };
    }

    // Bulk map actions over node, journal-group and scene-icon knowledge, as in map_actions.py.
    private static JsonArray Actions(JsonObject catalog, Func<string, JsonNode> load, ClientSource source)
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
                    var sceneNames = scenes.ToDictionary(s => s.Id, s => s.Name);
                    if (key == AbyssCresset) Cressets(action!, placed, load, source, sceneNames);
                    if (key == Bells) BellNames(action!, placed, source, sceneNames);
                }
                action!["label_ref"] = Label("knowledge", key);
            }
        return actions;
    }

    // Abyss Cressets need fog pivots captured from a save and the challenge quests that place them.
    private static void Cressets(JsonObject action, string[] placed, Func<string, JsonNode> load, ClientSource source, Dictionary<string, string> sceneNames)
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
        action["scene_roots"] = CressetRoots(placed, roots, quests, source, sceneNames);
    }

    // Each cresset challenge (its root mission or the root's "_0", "_1" sub-missions) references a condition that names
    // the cresset's level scene, "LevelName(<scene name>)". That pairs every scene with exactly one challenge.
    private static JsonObject CressetRoots(string[] placed, List<uint> roots, JsonNode quests, ClientSource source, Dictionary<string, string> sceneNames)
    {
        var byName = placed.ToDictionary(id => sceneNames[id], id => id);
        var sceneOf = new Dictionary<uint, string>();
        foreach (var (key, body) in RawRecords(source.Tables("conditioninfo")))
            foreach (Match m in LevelName().Matches(Encoding.Latin1.GetString(body.Span)))
                if (byName.TryGetValue(m.Groups[1].Value, out string? id) && sceneOf.GetValueOrDefault(key, id) == id) sceneOf[key] = id;
                else if (byName.ContainsKey(m.Groups[1].Value)) throw new InvalidDataException("A condition names two Abyss Cresset scenes.");
        var missions = ClientTables.Records(source.Tables("missioninfo"));
        var names = quests.Root["missions"]!;
        var result = new JsonObject();
        foreach (uint root in roots)
        {
            string name = names[root.ToString()]!["internalName"]!.GetValue<string>();
            var found = new HashSet<string>();
            foreach (var record in missions.Values.Where(r => r.Name == name || r.Name.StartsWith(name + "_", StringComparison.Ordinal)))
            {
                var bytes = record.Bytes.Span;
                for (int i = 0; i + 4 <= bytes.Length; i++)
                    if (sceneOf.TryGetValue(BinaryPrimitives.ReadUInt32LittleEndian(bytes[i..]), out string? id)) found.Add(id);
            }
            if (found.Count != 1 || result.ContainsKey(found.Single())) throw new InvalidDataException("An Abyss Cresset challenge does not name exactly one cresset.");
            result[found.Single()] = root;
        }
        if (result.Count != placed.Length) throw new InvalidDataException("Abyss Cresset challenges do not cover every cresset.");
        return new JsonObject(placed.Select(id => KeyValuePair.Create<string, JsonNode?>(id, result[id]!.GetValue<uint>())));
    }

    // Each region's bell scene is named after the mission that rings it (Mission_PororinVillage_Bell_All_<region>), whose
    // localized title names the bell. One scene name extends its mission's ("..._ScholastoneInstitute" for "..._Scholastone"),
    // so the longest mission name the scene name starts with is the match; every bell must get its own mission.
    private static void BellNames(JsonObject action, string[] placed, ClientSource source, Dictionary<string, string> sceneNames)
    {
        var missions = ClientTables.Records(source.Tables("missioninfo")).Values.GroupBy(r => r.Name).ToDictionary(g => g.Key, g => g.Last());
        var strings = ClientMetadata.Languages.ToDictionary(l => l.Key, l => source.Strings(l.Value.Name, "quest"));
        var result = new JsonObject();
        var used = new HashSet<string>();
        foreach (string id in placed)
        {
            string scene = sceneNames[id];
            var record = missions.Values.Where(r => scene.StartsWith(r.Name, StringComparison.Ordinal) && r.Name.Contains("_Bell_", StringComparison.Ordinal))
                .MaxBy(r => r.Name.Length) ?? throw new InvalidDataException("A bell scene has no mission of its name.");
            if (!used.Add(record.Name)) throw new InvalidDataException("Two bell scenes match one mission.");
            byte[] body = record.Bytes.ToArray();
            var labels = ChallengeCatalogBuilder.MissionLabel().Matches(Encoding.Latin1.GetString(body)).Select(m =>
            {
                try { return ChallengeCatalogBuilder.StringRef(new ClientReader(body, m.Index)); } catch (InvalidDataException) { return ""; }
            }).Where(l => strings["en"].ContainsKey(l)).Distinct().ToArray();
            if (labels.Length != 1) throw new InvalidDataException("A bell mission has no single title.");
            result[id] = new JsonObject(strings.Select(s => KeyValuePair.Create<string, JsonNode?>(s.Key, s.Value.GetValueOrDefault(labels[0], strings["en"][labels[0]]))));
        }
        action["scene_names"] = result;
    }

    // conditioninfo holds records the strict table reader rejects; only each record's bytes are needed here.
    private static IEnumerable<(uint Key, ReadOnlyMemory<byte> Body)> RawRecords(IEnumerable<(string Tag, byte[] Header, byte[] Body)> layers)
    {
        foreach (var (_, header, body) in layers)
        {
            bool wide = header.Length >= 4 && (header.Length - 4) % 8 == 0 && BinaryPrimitives.ReadUInt32LittleEndian(header) == (header.Length - 4) / 8;
            int start = wide ? 4 : 2, count = wide ? (int)BinaryPrimitives.ReadUInt32LittleEndian(header) : BinaryPrimitives.ReadUInt16LittleEndian(header);
            if (start + count * 8L > header.Length) throw new InvalidDataException("Invalid table header.");
            for (int i = 0; i < count; i++)
            {
                uint key = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(start + i * 8)), offset = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(start + i * 8 + 4));
                uint end = i + 1 < count ? BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(start + (i + 1) * 8 + 4)) : (uint)body.Length;
                if (offset > end || end > body.Length) throw new InvalidDataException("Invalid table offsets.");
                yield return (key, body.AsMemory((int)offset, (int)(end - offset)));
            }
        }
    }

    [GeneratedRegex(@"LevelName\(([^)]*)\)")]
    private static partial Regex LevelName();
}
