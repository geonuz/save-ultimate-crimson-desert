using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SaveUltimate.Core.Catalogs;

// Port of the Python quest_catalog builder: journal groups, quests and challenge missions from the installed client,
// including DLC tables.
public static partial class ChallengeCatalogBuilder
{
    // Tables mark an empty hashed key with this value.
    private const uint EmptyKey = 0xEAC5E173;
    private sealed record Group(uint Key, string Internal, int Type, string Label, uint[] Quests);
    private sealed record Quest(uint Key, string Internal, string Label, uint Group, int Category, int Type, uint Faction, string Kind);
    private sealed record Mission(uint Key, string Internal, uint Quest, uint[] Children, uint[] Stages, string Label, uint[] Tags);

    // A mission's trigger groups name their tag in a u32-counted array of stringinfo keys. On 1,291 of 1,294 challenge missions completed
    // in saves this is exactly what _usedTagList holds besides Start, Complete and the empty string; two of the rest gained patch_2_04_06 after those saves.
    private static uint[] DefinedTags(ClientRecord record, HashSet<uint> strings)
    {
        const uint Start = 0x872571C6, Complete = 0x599D2AA7;
        var bytes = record.Bytes.Span; var tags = new List<uint>();
        for (int i = record.Prefix; i + 8 <= bytes.Length; i++)
        {
            uint count = BitConverter.ToUInt32(bytes[i..(i + 4)]);
            if (count is < 1 or > 8 || i + 4 + count * 4 > bytes.Length) continue;
            var keys = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(bytes.Slice(i + 4, (int)count * 4)).ToArray();
            if (keys.All(strings.Contains)) tags.AddRange(keys.Where(k => k is not Start and not Complete && !tags.Contains(k)).Distinct());
        }
        return tags.ToArray();
    }

    private static string StringRef(ClientReader r)
    {
        byte category = r.U8(); uint member = r.U32(), owner = r.U32(); string value = r.Text();
        if (category == 0 && member == 0 && owner == 0 && value.Length == 0) return "";
        if (category != 16 || value != (((ulong)owner << 32) | member).ToString()) throw new InvalidDataException("Invalid quest string reference.");
        return value;
    }

    [GeneratedRegex(@"\x10\x01\x01\x00\x00[\s\S]{4}[\x01-\x20]\x00\x00\x00")]
    private static partial Regex MissionLabel();

    public static JsonObject Build(ClientSource source, JsonObject? previous = null)
    {
        var en = source.Strings("eng", "quest"); var ko = source.Strings("kor", "quest");

        var groups = new Dictionary<uint, Group>();
        foreach (var (key, (name, r)) in ClientTables.Records16(source.Tables("questgroupinfo")))
        {
            r.Take(5); int type = r.U8(); r.Take(18);
            string label = StringRef(r); StringRef(r);
            groups[key] = new(key, name, type, label, r.Array32());
        }
        var quests = new Dictionary<uint, Quest>();
        foreach (var (key, record) in ClientTables.Records(source.Tables("questinfo")))
        {
            var span = record.Bytes.Span; int type = span[record.Prefix + 4], category = span[record.Prefix + 5];
            uint group = BitConverter.ToUInt16(span.Slice(record.Prefix + 10, 2)), faction = BitConverter.ToUInt32(span.Slice(record.Prefix + 16, 4));
            var r = new ClientReader(record.Bytes, record.Prefix + 73); string label = StringRef(r); StringRef(r);
            if (group != 0 && !groups.ContainsKey(group)) throw new InvalidDataException("Unresolved quest group.");
            string kind = groups.GetValueOrDefault(group)?.Type == 3 ? "challenge" : category == 13 ? "main" : category == 12 ? "regional" : "other";
            quests[key] = new(key, record.Name, label, group, category, type, faction, kind);
        }
        var strings = new HashSet<uint>();
        foreach (var (_, header, _) in source.Tables("stringinfo"))
        {
            var index = new ClientReader(header); int count = index.U16();
            for (int i = 0; i < count; i++) { strings.Add(index.U32()); index.U32(); }
        }
        var missions = new Dictionary<uint, Mission>();
        foreach (var (key, record) in ClientTables.Records(source.Tables("missioninfo")))
        {
            var r = new ClientReader(record.Bytes, record.Prefix + 32);
            uint owner = r.U32(); r.Take(8);
            uint[] children = r.Array32();
            // Stage entries are 25 bytes led by the stage key. Stages only gate the reviewed objective layout below.
            uint[] stages = Enumerable.Range(0, r.Count()).Select(_ => { uint s = r.U32(); r.Take(21); return s; }).ToArray()
                .Where(s => s != 0 && s != EmptyKey).ToArray();
            byte[] body = record.Bytes.ToArray();
            var labels = new HashSet<string>();
            foreach (Match m in MissionLabel().Matches(Encoding.Latin1.GetString(body)))
            {
                var reader = new ClientReader(body, m.Index); string label;
                try { label = StringRef(reader); } catch (InvalidDataException) { continue; }
                if (en.ContainsKey(label) || ko.ContainsKey(label)) labels.Add(label);
            }
            if (labels.Count > 1) throw new InvalidDataException("Ambiguous mission name.");
            missions[key] = new(key, record.Name, owner, children, stages, labels.FirstOrDefault() ?? "", DefinedTags(record, strings));
        }

        var parents = new Dictionary<uint, uint>();
        foreach (var mission in missions.Values)
            foreach (uint child in mission.Children)
                if (!missions.TryGetValue(child, out var c) || c.Quest != mission.Quest || !parents.TryAdd(child, mission.Key))
                    throw new InvalidDataException("Invalid mission ownership.");
        foreach (var group in groups.Values)
            if (group.Quests.Any(q => !quests.TryGetValue(q, out var quest) || quest.Group != group.Key)) throw new InvalidDataException("Invalid quest group membership.");
        var challengeQuests = quests.Values.Where(q => q.Kind == "challenge").Select(q => q.Key).ToHashSet();
        var challengeMissions = missions.Values.Where(m => challengeQuests.Contains(m.Quest)).ToDictionary(m => m.Key);
        var roots = challengeQuests.ToDictionary(q => q, q => challengeMissions.Values.Where(m => m.Quest == q && !parents.ContainsKey(m.Key)).Select(m => m.Key).ToArray());
        var visited = new HashSet<uint>();
        void Visit(uint key, HashSet<uint> ancestors)
        {
            if (ancestors.Contains(key)) throw new InvalidDataException("Cyclic mission hierarchy.");
            visited.Add(key);
            foreach (uint child in challengeMissions[key].Children) Visit(child, [.. ancestors, key]);
        }
        foreach (uint root in roots.Values.SelectMany(r => r)) Visit(root, []);
        if (!visited.SetEquals(challengeMissions.Keys)) throw new InvalidDataException("Unreachable challenge mission.");

        string Ko(string label) => ko.GetValueOrDefault(label, "");
        string En(string label) => en.GetValueOrDefault(label, "");
        var previousMissions = previous?["missions"]?.AsObject();
        var objectives = new Dictionary<uint, JsonObject>();
        foreach (uint rootKey in roots.Values.SelectMany(r => r))
        {
            var root = missions[rootKey]; var children = root.Children.Select(k => missions[k]).ToArray();
            uint[] acquire = children.Where(m => Ko(m.Label) == "봉인된 어비스 아티팩트 획득하기").Select(m => m.Key).ToArray();
            uint[] use = children.Where(m => Ko(m.Label) == "봉인된 어비스 아티팩트 사용하기").Select(m => m.Key).ToArray();
            if (acquire.Length != 1 || use.Length != 1 || root.Stages.Length != 0) continue;
            uint[] goals = children.Select(m => m.Key).Except(acquire.Concat(use)).ToArray();
            if (goals.Length == 0 || children.Any(m => m.Children.Length != 0 || m.Stages.Length != 0)) continue;
            objectives[rootKey] = new JsonObject { ["prerequisite"] = acquire[0], ["artifact"] = use[0], ["goals"] = new JsonArray(goals.Select(g => (JsonNode)g).ToArray()) };
            foreach (uint goal in goals)
            {
                // shortcut: objective blocks are not decoded, so the reviewed value in the previous catalog is reused.
                if (missions[goal].Label.Length == 0 || previousMissions?[goal.ToString()] is not JsonObject old ||
                    old["quest"]?.GetValue<uint>() != missions[goal].Quest || old["objectiveEdit"] is not JsonObject kept) continue;
                var info = (JsonObject)kept.DeepClone();
                if (info["parent"]?.GetValue<uint>() != rootKey) continue;
                if (Ko(missions[goal].Label).Contains("지식") && info["knowledgeGroups"]!.AsArray().Count == 0) continue;
                objectives[goal] = info;
            }
        }

        JsonObject MissionJson(Mission m)
        {
            var row = new JsonObject
            {
                ["key"] = m.Key, ["internalName"] = m.Internal, ["quest"] = m.Quest,
                ["children"] = new JsonArray(m.Children.Select(c => (JsonNode)c).ToArray()),
                ["stages"] = new JsonArray(m.Stages.Select(c => (JsonNode)c).ToArray()),
                ["name"] = En(m.Label), ["nameKo"] = Ko(m.Label)
            };
            if (objectives.TryGetValue(m.Key, out var objective)) row["objectiveEdit"] = objective.DeepClone();
            if (m.Tags.Length > 0) row["tags"] = new JsonArray(m.Tags.Select(t => (JsonNode)t).ToArray());
            return row;
        }
        JsonObject QuestJson(Quest q)
        {
            var row = new JsonObject
            {
                ["key"] = q.Key, ["internalName"] = q.Internal, ["name"] = En(q.Label), ["nameKo"] = Ko(q.Label),
                ["group"] = q.Group, ["category"] = q.Category, ["type"] = q.Type, ["faction"] = q.Faction, ["kind"] = q.Kind
            };
            if (roots.TryGetValue(q.Key, out var missionRoots)) row["missions"] = new JsonArray(missionRoots.Select(k => (JsonNode)k).ToArray());
            return row;
        }
        return new JsonObject
        {
            ["groups"] = new JsonArray(groups.Values.Where(g => g.Type == 3).Select(g => (JsonNode)new JsonObject
            {
                ["key"] = g.Key, ["internalName"] = g.Internal, ["type"] = g.Type, ["name"] = En(g.Label), ["nameKo"] = Ko(g.Label),
                ["quests"] = new JsonArray(g.Quests.Select(q => (JsonNode)q).ToArray())
            }).ToArray()),
            ["quests"] = new JsonObject(quests.Values.Select(q => KeyValuePair.Create<string, JsonNode?>(q.Key.ToString(), QuestJson(q)))),
            ["missions"] = new JsonObject(challengeMissions.Values.Select(m => KeyValuePair.Create<string, JsonNode?>(m.Key.ToString(), MissionJson(m)))),
            ["missionOwners"] = new JsonObject(missions.Values.Select(m => KeyValuePair.Create<string, JsonNode?>(m.Key.ToString(), m.Quest))),
            ["sourceSha256"] = source.Digests()
        };
    }
}
