using System.Text.Json.Nodes;

namespace SaveUltimate.Core.Catalogs;

// Port of the Python character name, companion type and NPC catalog builders, including DLC tables. CharacterInfo is
// only partly decoded: owned localization references are located by their self-identifying bytes, and the 2.04
// MercenaryInfo and NpcInfo links sit at fixed offsets checked against every 2.03 companion and NPC link.
public static class CharacterCatalogBuilder
{
    private const uint NameMember = 0x30, CompanionMember = 0x32;
    private static readonly (string Field, uint Member, string Section)[] NpcLabels =
        [("title", 226, "npc_titles"), ("service", 228, "npc_services"), ("areaOrAffiliation", 229, "npc_areas")];

    public static Dictionary<string, JsonNode> Build(ClientSource source, Func<string, JsonNode> load)
    {
        var characters = ClientTables.Records(source.Tables("characterinfo"));
        var npcs = ClientTables.Records(source.Tables("npcinfo"));
        var types = ClientTables.Records16(source.Tables("mercenaryinfo"), 1);
        var names = ClientMetadata.Languages.ToDictionary(p => p.Key, p => source.Strings(p.Value.Name, "character"));
        var labels = ClientMetadata.Languages.ToDictionary(p => p.Key, p => source.Strings(p.Value.Name, "npc"));

        var nameRows = new JsonObject(); var companions = new JsonObject(); var links = new JsonObject();
        foreach (var (key, record) in characters)
        {
            if (ClientTables.Anchor(record, 3, NameMember, key) < 0) throw new InvalidDataException("Unsupported character-name reference.");
            string label = (((ulong)key << 32) | NameMember).ToString();
            nameRows[key.ToString()] = new JsonObject { ["name"] = names["en"].GetValueOrDefault(label, ""), ["internalName"] = record.Name, ["stringKey"] = label };
            var span = record.Bytes.Span; uint npc; int type = -1, companion = ClientTables.Anchor(record, 3, CompanionMember, key);
            if (!record.Compact)
            {
                var r = new ClientReader(record.Bytes, record.Prefix);
                foreach (uint member in new[] { NameMember, NameMember + 1 }) r.Reference(3, member, key, true);
                r.Take(8); r.SkipText(); r.Take(6); npc = r.U32();
                if (companion > record.Prefix) type = span[companion - 1];
            }
            else
            {
                npc = BitConverter.ToUInt32(span[(record.Prefix + 210)..]);
                if (companion >= 0) type = span[record.Prefix + 214];
            }
            if (npc != 0 && !npcs.ContainsKey(npc)) throw new InvalidDataException("Unresolved character NPC link.");
            if (npc != 0) links[key.ToString()] = npc;
            if (type >= 0 && types.ContainsKey((uint)type)) companions[key.ToString()] = type;
        }

        string Label(string code, uint key, uint member) => ClientTables.Anchor(npcs[key], 14, member, key) >= 0 ? labels[code].GetValueOrDefault((((ulong)key << 32) | member).ToString(), "") : "";
        JsonObject Fields(string code, uint key) => new(NpcLabels.Select(l => KeyValuePair.Create<string, JsonNode?>(l.Field, Label(code, key, l.Member))));
        var npcRows = new JsonObject();
        foreach (uint key in npcs.Keys) npcRows[key.ToString()] = new JsonObject { ["ko"] = Fields("ko", key), ["en"] = Fields("en", key) };

        var digests = source.Digests();
        var outputs = new Dictionary<string, JsonNode>
        {
            ["data/character_names.json"] = nameRows,
            ["data/companion_types.json"] = new JsonObject
            {
                ["sourceSha256"] = digests.DeepClone(), ["characters"] = companions,
                ["types"] = new JsonObject(types.Select(t => KeyValuePair.Create<string, JsonNode?>(t.Key.ToString(), t.Value.Name))),
                ["method"] = "Owned CharacterInfo localization reference 0x32; MercenaryInfo key before it (2.03) or at +214 (2.04). Unresolved keys omitted."
            },
            ["data/npc_catalog.json"] = new JsonObject { ["sourceSha256"] = digests.DeepClone(), ["characters"] = links, ["npcs"] = npcRows }
        };
        foreach (string code in new[] { "ko", "ja", "ru" })
        {
            var pack = load($"locale/names_{code}.json").AsObject();
            pack["characters"] = new JsonObject(characters.Keys.Select(k => (k, Text: names[code].GetValueOrDefault((((ulong)k << 32) | NameMember).ToString(), "")))
                .Where(p => p.Text.Length > 0).Select(p => KeyValuePair.Create<string, JsonNode?>(p.k.ToString(), p.Text)));
            // Rebuilt rather than updated: 2.04 renumbers NpcInfo keys.
            foreach (var (_, member, section) in NpcLabels.Where(l => pack[l.Section] is not null))
                pack[section] = new JsonObject(npcs.Keys.Select(k => (k, Text: Label(code, k, member)))
                    .Where(p => p.Text.Length > 0).Select(p => KeyValuePair.Create<string, JsonNode?>(p.k.ToString(), p.Text)));
            outputs[$"locale/names_{code}.json"] = pack;
        }
        return outputs;
    }
}
