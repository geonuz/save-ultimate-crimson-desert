using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;

namespace SaveUltimate.Core.Catalogs;

// Port of the Python knowledge_catalog, refresh_knowledge_fishing and extract_knowledge_details builders: the visible
// journal hierarchy, knowledge names and descriptions and the fishing list, including DLC tables. Compact (2.04)
// offsets were checked against every record of the 2.03 tables.
public static class KnowledgeCatalogBuilder
{
    internal sealed record Knowledge(uint Key, string Internal, ClientRecord Record, bool Visible, byte Default, uint Skill, int Levels, int LevelStart);
    internal sealed record Group(uint Key, string Internal, string Label, uint Parent, uint[] Children, bool Visible, uint[] Keys);
    private const uint NameMember = 0x490, LevelNameMember = 0x49D, DescriptionMember = 0x491;
    private static readonly uint[] FishGroups = [1310, 1311, 1312, 1320, 1321, 1322, 1340, 1330];

    private static uint U32(ReadOnlySpan<byte> span, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(span[offset..]);

    internal static Dictionary<uint, Group> Groups(ClientSource source)
    {
        var groups = new Dictionary<uint, Group>();
        foreach (var (key, record) in ClientTables.Records(source.Tables("knowledgegroupinfo")))
        {
            var r = new ClientReader(record.Bytes, record.Prefix);
            string Label() { string label = r.Reference(9, 0x92, key); r.Reference(9, 0x94, key, true); r.Reference(9, 0x93, key, true); return label; }
            byte[] head; uint parent; string label; uint[] keys, children;  // head: meditationLearnable, showUI, showUIAlert, owner
            if (record.Compact)
            {
                head = r.Take(4).ToArray(); r.Take(8); parent = r.U32(); r.Take(8);
                label = Label(); keys = r.Array32(); children = r.Array32();
            }
            else
            {
                label = Label(); r.Take(12); keys = r.Array32(); children = r.Array32(); parent = r.U32();
                var tail = r.Take(4); head = [tail[3], tail[1], tail[2], tail[0]];
            }
            if (head[3] > 3 || head[..3].Any(f => f > 1) || r.Remaining != 0) throw new InvalidDataException("Unsupported knowledge group layout.");
            groups[key] = new(key, record.Name, label, parent, children, head[1] == 1, keys);
        }
        return groups;
    }

    internal static Dictionary<uint, Knowledge> Records(ClientSource source, IReadOnlySet<uint> groups)
    {
        var result = new Dictionary<uint, Knowledge>();
        foreach (var (key, record) in ClientTables.Records(source.Tables("knowledgeinfo")))
        {
            byte[] flags; byte initial; uint skill; int levels, start;  // flags: showUI, showUIAlert, legendaryAnimal, useAlertDesc
            if (!record.Compact)
            {
                var r = new ClientReader(record.Bytes, record.Prefix);
                r.Take(4); initial = r.U8(); r.Take(8); skill = r.U32();
                r.Array(4); r.Array(4); r.Array(2); r.Array(4);
                flags = r.Take(4).ToArray(); r.Take(8);
                for (int i = r.Count(); i > 0; i--) { if (r.U8() != 0) throw new InvalidDataException("Unsupported knowledge source kind."); r.Take(8); }
                r.Array(4); levels = r.Count(255); start = r.Position;
            }
            else
            {
                var span = record.Bytes.Span[record.Prefix..];
                // The fourth byte is no longer a flag in 2.04 (eight records hold 130-200).
                flags = span[..3].ToArray(); initial = span[4]; skill = U32(span, 30);
                (levels, start) = CompactLevels(record, groups);
            }
            if (initial > 1 || flags.Any(f => f > 1) || levels < 1 || new ClientReader(record.Bytes, start).U32() != 1)
                throw new InvalidDataException("Unsupported knowledge level list.");
            result[key] = new(key, record.Name, record, flags[0] == 1, initial, skill, levels, start);
        }
        return result;
    }

    // shortcut: 2.04 moved variable-length fields of unknown shape ahead of the group list, so the level list is located
    // as the only [count][known group keys][level count][1] run instead of walked. It is unique in every 2.04 record and
    // matches every 2.03 count; walk the fields once their shapes are decoded.
    private static (int Levels, int Start) CompactLevels(ClientRecord record, IReadOnlySet<uint> groups)
    {
        var span = record.Bytes.Span; (int, int)? found = null;
        for (int q = record.Prefix + 42; q + 16 <= span.Length; q++)
        {
            uint count = U32(span, q);
            if (count is 0 or > 20 || q + 12 + count * 4 > span.Length) continue;
            int end = q + 4 + (int)count * 4; bool known = true;
            for (int i = q + 4; known && i < end; i += 4) known = groups.Contains(U32(span, i));
            uint levels = U32(span, end);
            if (!known || levels is 0 or > 255 || U32(span, end + 4) != 1) continue;
            if (found is not null) throw new InvalidDataException("Ambiguous knowledge level list.");
            found = ((int)levels, end + 4);
        }
        return found ?? throw new InvalidDataException("Missing knowledge level list.");
    }

    // 2.04 knowledge lists its characters in the fifth array after the 42-byte fixed block (checked on every reviewed fish).
    private static uint[] CompactCharacters(ClientRecord record)
    {
        var r = new ClientReader(record.Bytes, record.Prefix + 42);
        for (int i = 0; i < 4; i++) r.Array(4);
        return r.Array32();
    }

    // shortcut: the 2.04 CharacterInfo fields before the fish size range are not decoded, so the range is located as the
    // only positive float pair between 16 zero bytes on each side. That finds exactly the reviewed range in all 44 sized
    // 2.03 fish and nothing in the 11 unsized ones; walk the fields once they are decoded.
    private static (int Offset, float Minimum, float Maximum)? SizeRange(ClientRecord record)
    {
        var span = record.Bytes.Span; (int, float, float)? found = null;
        for (int i = record.Prefix + 16; i + 24 <= span.Length; i++)
        {
            if (span.Slice(i - 16, 16).ContainsAnyExcept((byte)0) || span.Slice(i + 8, 16).ContainsAnyExcept((byte)0)) continue;
            float minimum = BitConverter.ToSingle(span[i..]), maximum = BitConverter.ToSingle(span[(i + 4)..]);
            if (!(minimum > 1e-3f && minimum <= maximum && maximum < 1000)) continue;
            if (found is not null) return null;
            found = (i, minimum, maximum);
        }
        return found;
    }

    // Typed localization references ([9][member][owner][length][decimal owner << 32 | member]) in record order.
    internal static List<(uint Member, string Key)> References(ClientRecord record)
    {
        var span = record.Bytes.Span; var result = new List<(uint, string)>();
        for (int i = record.Prefix; i + 13 <= span.Length; i++)
        {
            if (span[i] != 9) continue;
            uint member = U32(span, i + 1), owner = U32(span, i + 5), length = U32(span, i + 9);
            if (length is 0 or > 32 || i + 13 + length > span.Length) continue;
            string key = Encoding.ASCII.GetString(span.Slice(i + 13, (int)length));
            if (key == (((ulong)owner << 32) | member).ToString()) { result.Add((member, key)); i += 12 + (int)length; }
        }
        return result;
    }

    private static JsonArray Json(IEnumerable<uint> values) => new(values.Select(v => (JsonNode)v).ToArray());

    // Rebuilds the knowledge resources from the client on top of the previous ones (load returns a resource by its
    // relative path): rows of keys the client no longer has and fishing evidence are carried over, and only existing
    // name-pack sections are updated.
    public static Dictionary<string, JsonNode> Build(ClientSource source, Func<string, JsonNode> load)
    {
        var groups = Groups(source);
        var knowledge = Records(source, groups.Keys.ToHashSet());
        var texts = ClientMetadata.Languages.ToDictionary(p => p.Key, p => source.Strings(p.Value.Name, "knowledge"));
        var en = texts["en"]; var ko = texts["ko"];
        foreach (var group in groups.Values)
        {
            if (group.Keys.Any(k => !knowledge.ContainsKey(k))) throw new InvalidDataException("Unresolved knowledge group member.");
            if (group.Children.Any(c => !groups.TryGetValue(c, out var child) || child.Parent != group.Key)) throw new InvalidDataException("Invalid knowledge hierarchy.");
        }

        var included = new JsonArray(); var paths = new Dictionary<uint, uint[]>();
        SortedSet<uint> Visit(Group group, uint[] ancestors)
        {
            if (ancestors.Contains(group.Key)) throw new InvalidDataException("Cyclic knowledge hierarchy.");
            if (!group.Visible) return [];
            string name = en.GetValueOrDefault(group.Label, ""), nameKo = ko.GetValueOrDefault(group.Label, "");
            if (name.Length == 0 || nameKo.Length == 0) throw new InvalidDataException("Missing knowledge group translation.");
            uint[] path = [.. ancestors, group.Key], keys = group.Keys.Where(k => knowledge[k].Visible).ToArray();
            foreach (uint key in keys) if (!paths.TryAdd(key, path)) throw new InvalidDataException("Ambiguous visible knowledge group membership.");
            var row = new JsonObject
            {
                ["key"] = group.Key, ["internalName"] = group.Internal, ["name"] = name, ["nameKo"] = nameKo, ["parent"] = group.Parent,
                ["children"] = Json(group.Children), ["visible"] = true, ["keys"] = Json(keys)
            };
            included.Add(row);
            var members = new SortedSet<uint>(keys);
            foreach (uint child in group.Children) members.UnionWith(Visit(groups[child], path));
            row["members"] = Json(members);
            return members;
        }
        var roots = groups.Values.Where(g => g.Visible && g.Parent == 0).ToArray();
        foreach (var root in roots) Visit(root, []);
        var digests = source.Digests();
        var journal = new JsonObject
        {
            ["roots"] = Json(roots.Select(g => g.Key)), ["groups"] = included,
            ["itemPaths"] = new JsonObject(paths.Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key.ToString(), Json(p.Value)))),
            ["levelCounts"] = new JsonObject(knowledge.Values.Select(k => KeyValuePair.Create<string, JsonNode?>(k.Key.ToString(), k.Levels))),
            ["clientKnowledgeCount"] = knowledge.Count, ["sourceSha256"] = digests.DeepClone()
        };

        var names = load("data/knowledge_keys_all.json").AsArray().Select(n => n!.AsObject()).ToDictionary(n => n["key"]!.GetValue<uint>(), n => (JsonObject)n.DeepClone());
        var packs = new[] { "ko", "ja", "ru" }.ToDictionary(c => c, c => load($"locale/names_{c}.json").AsObject());
        void Pack(string section, string key, Func<string, string> text)
        {
            foreach (var (code, pack) in packs)
                if (pack[section] is JsonObject entries && text(code) is { Length: > 0 } value) entries[key] = value;
        }
        var details = new JsonObject();
        foreach (var k in knowledge.Values)
        {
            var references = References(k.Record); string key = k.Key.ToString();
            var label = references.FirstOrDefault(r => r.Member is NameMember or LevelNameMember && en.ContainsKey(r.Key)).Key;
            if (label is not null)
            {
                var row = names.TryGetValue(k.Key, out var old) ? old : names[k.Key] = new JsonObject();
                row["key"] = k.Key; row["name"] = k.Internal; row["display_name"] = en[label];
                Pack("knowledge", key, c => texts[c].GetValueOrDefault(label, ""));
            }
            var descriptions = references.Where(r => r.Member == DescriptionMember).Select(r => r.Key).ToArray();
            string Description(string code) => string.Join("\n\n", descriptions.Select(d => texts[code].GetValueOrDefault(d, "")).Where(t => t.Length > 0).Distinct());
            details[key] = new JsonObject { ["description"] = Description("en"), ["descriptionKo"] = Description("ko") };
            Pack("knowledge_descriptions", key, Description);
        }
        foreach (var group in groups.Values) Pack("knowledge_groups", group.Key.ToString(), c => texts[c].GetValueOrDefault(group.Label, ""));

        var fishing = load("data/fishing_catalog.json").AsObject();
        var previousFish = fishing["fish"]!.AsArray().ToDictionary(f => f!["key"]!.GetValue<uint>(), f => f!);
        // Reviewed fish keep their evidence; fish new to the client are checked from their CharacterInfo size range.
        var characters = ClientTables.Records(source.Tables("characterinfo"));
        JsonObject NewFish(uint key, uint group)
        {
            var evidence = new JsonArray();
            foreach (uint character in knowledge[key].Record.Compact ? CompactCharacters(knowledge[key].Record) : [])
                if (characters.TryGetValue(character, out var record) && SizeRange(record) is var (offset, minimum, maximum))
                    evidence.Add(new JsonObject
                    {
                        ["characterKey"] = character, ["internalName"] = record.Name, ["sizePairOffset"] = offset, ["minimum"] = minimum, ["maximum"] = maximum,
                        ["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(record.Bytes.Span))
                    });
            bool supported = evidence.Count > 0;
            return new JsonObject
            {
                ["key"] = key, ["name"] = names.GetValueOrDefault(key)?["display_name"]?.DeepClone() ?? knowledge[key].Internal, ["groupKey"] = group, ["groupName"] = en[groups[group].Label],
                ["recordSupported"] = supported, ["supportReason"] = supported ? "client_size_parameters" : "no_client_size_parameters", ["evidence"] = evidence
            };
        }
        var fish = FishGroups.SelectMany(g => groups[g].Keys.Select(k => previousFish.TryGetValue(k, out var old) ? old.DeepClone() : NewFish(k, g))).ToArray();
        fishing["fish"] = new JsonArray(fish); fishing["sourceSha256"] = source.Digests();

        var outputs = new Dictionary<string, JsonNode>
        {
            ["data/knowledge_ui.json"] = journal,
            ["data/knowledge_keys_all.json"] = new JsonArray(names.Values.ToArray<JsonNode>()),
            ["data/knowledge_details.json"] = new JsonObject { ["sourceSha256"] = digests.DeepClone(), ["items"] = details },
            ["data/fishing_catalog.json"] = fishing
        };
        foreach (var (code, pack) in packs) outputs[$"locale/names_{code}.json"] = pack;
        return outputs;
    }
}
