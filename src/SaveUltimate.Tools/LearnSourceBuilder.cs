using System.Buffers.Binary;
using System.Text.Json.Nodes;
using SaveUltimate.Core.Catalogs;

namespace SaveUltimate.Tools;

// Which kinds of client events teach each knowledge, for edits of knowledge no player was seen learning:
//   item       an item's own knowledge (+107 of the fixed item block), learned with the item
//   read/zoom  an item's inspect entry with the read (0xD01D) or zoom (0xC10C) action
//   region     RegionInfo's knowledge (+14) of a region whose type (+4) is not 7
//   node       the same for type 7 regions (town districts, landmarks, caves)
//   character  a knowledge listing characters (people, creatures, mounts)
//   meditation a member of a KnowledgeGroupInfo group with the meditation flag
// The fixed priority over these lives with the chooser (LearnPathCatalog).
public static class LearnSourceBuilder
{
    private const int ItemKnowledge = 107, RegionType = 4, RegionKnowledge = 14;
    private const byte NodeRegion = 7;

    public static JsonObject Build(ClientSource source)
    {
        var groups = KnowledgeCatalogBuilder.Groups(source);
        var knowledge = KnowledgeCatalogBuilder.Records(source, groups.Keys.ToHashSet());
        var sources = new SortedDictionary<uint, SortedSet<string>>();
        void Add(uint key, string kind, string where)
        {
            if (!knowledge.ContainsKey(key)) throw new InvalidDataException($"{where} teaches {key}, which is not in KnowledgeInfo.");
            (sources.TryGetValue(key, out var kinds) ? kinds : sources[key] = new(StringComparer.Ordinal)).Add(kind);
        }
        foreach (var (key, record) in ClientTables.Records(source.Tables("iteminfo")))
        {
            uint own = BinaryPrimitives.ReadUInt32LittleEndian(record.Bytes.Span[(record.Prefix + ItemKnowledge)..]);
            if (own != 0) Add(own, "item", $"Item {key}");
            foreach (var (k, action) in CollectionCatalogBuilder.Inspected(record))
                Add(k, action switch
                {
                    CollectionCatalogBuilder.ReadAction => "read", CollectionCatalogBuilder.ZoomAction => "zoom",
                    _ => throw new InvalidDataException($"Item {key} has an unknown inspect action {action:X}.")
                }, $"Item {key}");
        }
        foreach (var (key, (_, reader)) in ClientTables.Records16(source.Tables("regioninfo")))
        {
            var head = reader.Take(RegionKnowledge + 4);
            uint k = BinaryPrimitives.ReadUInt32LittleEndian(head[RegionKnowledge..]);
            if (k != 0) Add(k, head[RegionType] == NodeRegion ? "node" : "region", $"Region {key}");
        }
        // shortcut: the 58 records the character profiles also skip (none in the journal) are left without this source.
        foreach (var k in knowledge.Values)
        {
            uint[] listed;
            try { listed = KnowledgeCatalogBuilder.Characters(k.Record); } catch (InvalidDataException) { continue; }
            if (listed.Length > 0) Add(k.Key, "character", $"Knowledge {k.Key}");
        }
        foreach (var group in groups.Values.Where(g => g.Meditation))
            foreach (uint k in group.Keys) Add(k, "meditation", $"Knowledge group {group.Key}");
        return new JsonObject
        {
            ["source"] = "Client events that teach each knowledge: item (iteminfo +107), read/zoom (inspect entries, action 0xD01D/0xC10C), region/node (regioninfo knowledge +14, node when the region type +4 is 7), character (knowledgeinfo character list), meditation (knowledgegroupinfo meditation flag).",
            ["knowledge"] = new JsonObject(sources.Select(p => KeyValuePair.Create<string, JsonNode?>(p.Key.ToString(),
                new JsonArray(p.Value.Select(v => (JsonNode)v).ToArray())))),
            ["sourceSha256"] = source.Digests()
        };
    }
}
