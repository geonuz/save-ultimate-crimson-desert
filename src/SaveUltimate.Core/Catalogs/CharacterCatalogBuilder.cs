using System.Text.Json.Nodes;

namespace SaveUltimate.Core.Catalogs;

// Port of the Python character name, companion type and NPC catalog builders, including DLC tables. CharacterInfo is
// only partly decoded: owned localization references are located by their self-identifying bytes, and the
// MercenaryInfo and NpcInfo links sit at fixed offsets.
public static class CharacterCatalogBuilder
{
    private const uint NameMember = 0x30, CompanionMember = 0x32, TitleMember = 226;
    private const int NpcStoreOffset = 14;
    private const uint CampCostumeStore = 6700, CampTailor = 1000351;
    private static readonly (string Field, uint Member, string Section)[] NpcLabels =
        [("title", TitleMember, "npc_titles"), ("service", 228, "npc_services"), ("areaOrAffiliation", 229, "npc_areas")];

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
            var span = record.Bytes.Span;
            uint npc = BitConverter.ToUInt32(span[(record.Prefix + 210)..]);
            int type = ClientTables.Anchor(record, 3, CompanionMember, key) >= 0 ? span[record.Prefix + 214] : -1;
            if (npc != 0 && !npcs.ContainsKey(npc)) throw new InvalidDataException("Unresolved character NPC link.");
            if (npc != 0) links[key.ToString()] = npc;
            if (type >= 0 && types.ContainsKey((uint)type)) companions[key.ToString()] = type;
        }

        string Label(string code, uint key, uint member) => ClientTables.Anchor(npcs[key], 14, member, key) >= 0 ? labels[code].GetValueOrDefault((((ulong)key << 32) | member).ToString(), "") : "";
        JsonObject Fields(string code, uint key) => new(NpcLabels.Select(l => KeyValuePair.Create<string, JsonNode?>(l.Field, Label(code, key, l.Member))));
        var npcRows = new JsonObject();
        foreach (uint key in npcs.Keys) npcRows[key.ToString()] = new JsonObject { ["ko"] = Fields("ko", key), ["en"] = Fields("en", key) };

        // StoreInfo holds no display name: a store is called by the title of the NPC who runs it.
        var stores = ClientTables.Records16(source.Tables("storeinfo"));
        var sellers = npcs.Keys.Order().GroupBy(k => (uint)BitConverter.ToUInt16(npcs[k].Bytes.Span[(npcs[k].Prefix + NpcStoreOffset)..])).Where(g => g.Key != 0).ToDictionary(g => g.Key, g => g.ToArray());
        if (sellers.Keys.Any(k => !stores.ContainsKey(k))) throw new InvalidDataException("Unresolved NPC store link.");
        // Barbers in every region share the camp's costume store, which is the camp tailor's.
        sellers[CampCostumeStore] = [CampTailor];
        // shortcut: NPCs sharing a store with different titles take the most common, then the lowest NPC key, so a store shared across regions (3500, 3802, 5502) shows one region's name.
        string StoreName(string code, uint[] owners) => owners.Select(n => Label(code, n, TitleMember)).Where(t => t.Length > 0).GroupBy(t => t).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key ?? "";
        // Stores no NPC runs keep their label.
        var storeNames = load("data/store_names.json").AsObject();
        foreach (var (store, owners) in sellers.OrderBy(p => p.Key))
            if (StoreName("en", owners) is { Length: > 0 } name) storeNames[store.ToString()] = name;

        var digests = source.Digests();
        var outputs = new Dictionary<string, JsonNode>
        {
            ["data/character_names.json"] = nameRows,
            ["data/companion_types.json"] = new JsonObject
            {
                ["sourceSha256"] = digests.DeepClone(), ["characters"] = companions,
                ["types"] = new JsonObject(types.Select(t => KeyValuePair.Create<string, JsonNode?>(t.Key.ToString(), t.Value.Name))),
                ["method"] = "Owned CharacterInfo localization reference 0x32; MercenaryInfo key at +214. Unresolved keys omitted."
            },
            ["data/npc_catalog.json"] = new JsonObject { ["sourceSha256"] = digests.DeepClone(), ["characters"] = links, ["npcs"] = npcRows },
            ["data/store_names.json"] = storeNames
        };
        foreach (string code in new[] { "ko", "ja", "ru" })
        {
            var pack = load($"locale/names_{code}.json").AsObject();
            pack["characters"] = new JsonObject(characters.Keys.Select(k => (k, Text: names[code].GetValueOrDefault((((ulong)k << 32) | NameMember).ToString(), "")))
                .Where(p => p.Text.Length > 0).Select(p => KeyValuePair.Create<string, JsonNode?>(p.k.ToString(), p.Text)));
            pack["stores"] = new JsonObject(sellers.OrderBy(p => p.Key).Select(p => (p.Key, Text: StoreName(code, p.Value)))
                .Where(p => p.Text.Length > 0).Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key.ToString(), p.Text)));
            // Rebuilt rather than updated: 2.04 renumbers NpcInfo keys.
            foreach (var (_, member, section) in NpcLabels.Where(l => pack[l.Section] is not null))
                pack[section] = new JsonObject(npcs.Keys.Select(k => (k, Text: Label(code, k, member)))
                    .Where(p => p.Text.Length > 0).Select(p => KeyValuePair.Create<string, JsonNode?>(p.k.ToString(), p.Text)));
            outputs[$"locale/names_{code}.json"] = pack;
        }
        return outputs;
    }
}
