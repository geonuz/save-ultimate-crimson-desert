using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using SaveUltimate.Core.Catalogs;

namespace SaveUltimate.Tools;

// The collection box (inventory 18, "Housing_Collecting") lists every item it can hold; the DLC record replaces the base one.
// Each item carries the knowledge the game teaches with it: its own knowledge at +107 of the fixed block (journey items,
// learned through the story, recorded as None) and the knowledge each "InspectSocket_N" entry teaches when the item is
// inspected, recorded as Read or Zoom by the entry's inspect action.
public static class CollectionCatalogBuilder
{
    private const uint CollectionBox = 18;
    private const uint Read = 10, Zoom = 12, None = 25;
    // The u16 4 bytes after an entry's knowledge (measured; the byte after it is 1 on read entries, 0, 4, 8... on zoom
    // entries): 0xD01D on notices, letters and books read flat, 0xC10C on treasure maps, the illusion picture book and the
    // Goldleaf guildhouse clues, which the game records as Zoom. Against five unedited saves it agrees on 1,424 of 1,439
    // entries; the rest are knowledge another item or event also teaches (the biology research journal is Zoom in one save).
    internal const ushort ReadAction = 0xD01D, ZoomAction = 0xC10C;

    public static JsonObject Build(ClientSource source)
    {
        var layers = source.Tables("inventory");
        uint[] baseList = List(ClientTables.Records16(layers.Where(l => l.Tag.Length == 0))[CollectionBox].Reader);
        uint[] list = List(ClientTables.Records16(layers)[CollectionBox].Reader);
        var items = ClientTables.Records(source.Tables("iteminfo"));
        var knowledge = ClientTables.Records(source.Tables("knowledgeinfo")).Keys.ToHashSet();
        var rows = new JsonArray();
        foreach (uint key in list)
        {
            var record = items[key];
            var learned = new JsonArray();
            uint own = BinaryPrimitives.ReadUInt32LittleEndian(record.Bytes.Span[(record.Prefix + 107)..]);
            if (own != 0) learned.Add(Entry(Known(own), None));
            foreach (var entries in Inspected(record).GroupBy(e => e.Knowledge))
            {
                uint[] paths = entries.Select(e => e.Action switch
                {
                    ReadAction => Read, ZoomAction => Zoom,
                    _ => throw new InvalidDataException($"Collection item {key} has an unknown inspect action {e.Action:X}.")
                }).Distinct().ToArray();
                if (paths.Length != 1) throw new InvalidDataException($"Collection item {key} teaches {entries.Key} by reading and by zooming.");
                learned.Add(Entry(Known(entries.Key), paths[0]));
            }
            rows.Add(new JsonObject { ["key"] = key, ["base"] = baseList.Contains(key), ["knowledge"] = learned });
        }
        return new JsonObject
        {
            ["source"] = "inventory 18 (Housing_Collecting, DLC layer wins) and iteminfo: knowledge at +107 of the fixed block (None) and 9 bytes after each InspectSocket_N name (Read for the u16 inspect action 0xD01D 4 bytes later, Zoom for 0xC10C).",
            ["items"] = rows, ["sourceSha256"] = source.Digests()
        };
        uint Known(uint k) => knowledge.Contains(k) ? k : throw new InvalidDataException($"Collection item knowledge {k} is not in KnowledgeInfo.");
        static JsonObject Entry(uint k, uint from) => new() { ["key"] = k, ["from"] = from };
    }
    // The record ends with the item list: u32 count, then [u32 item key][u32 1][u32 0] per item.
    private static uint[] List(ClientReader record)
    {
        byte[] span = record.Take(record.Remaining).ToArray();
        for (int p = 0; p + 4 <= span.Length; p++)
        {
            int count = BinaryPrimitives.ReadInt32LittleEndian(span[p..]);
            if (count <= 0 || p + 4 + count * 12L != span.Length) continue;
            var keys = Enumerable.Range(0, count).Select(i => p + 4 + i * 12).ToArray();
            if (keys.All(o => BinaryPrimitives.ReadUInt32LittleEndian(span[(o + 4)..]) == 1 && BinaryPrimitives.ReadUInt32LittleEndian(span[(o + 8)..]) == 0))
                return keys.Select(o => BinaryPrimitives.ReadUInt32LittleEndian(span[o..])).ToArray();
        }
        throw new InvalidDataException("Missing collection item list.");
    }

    // shortcut: the inspect entries sit past fields not decoded yet, so they are found by their "InspectSocket_" name; walk the
    // record once those fields are decoded. Each entry is [u32 length][name][u32][5 bytes][u32 knowledge][u16 inspect action].
    internal static List<(uint Knowledge, ushort Action)> Inspected(ClientRecord record)
    {
        var span = record.Bytes.Span;
        byte[] marker = Encoding.ASCII.GetBytes("InspectSocket_");
        var found = new List<(uint, ushort)>();
        for (int p = record.Prefix + 4; p + marker.Length <= span.Length; p++)
        {
            if (!span.Slice(p, marker.Length).SequenceEqual(marker)) continue;
            int end = p + BinaryPrimitives.ReadInt32LittleEndian(span[(p - 4)..]);
            uint k = BinaryPrimitives.ReadUInt32LittleEndian(span[(end + 9)..]);
            if (k != 0) found.Add((k, BinaryPrimitives.ReadUInt16LittleEndian(span[(end + 13)..])));
        }
        return found;
    }
}
