using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using CDSE.Core.Binary;

namespace CDSE.Core.Catalogs;

// Since 2.06 the index names the records and each record opens with [key][row][0]. Only this layout is read; an older install
// reports an error.
internal sealed record ClientRecord(string Name, ReadOnlyMemory<byte> Bytes, int Prefix);
internal sealed record ItemFields(ulong MaxStack, string NameRef, string Description, string Extra, uint[] Icons, uint Category, int End);

// ItemInfo has no reflection schema: every read stays within its indexed record.
internal sealed class ClientReader(ReadOnlyMemory<byte> data, int position = 0)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public int Position { get; private set; } = position;
    public int Remaining => data.Length - Position;
    public ReadOnlySpan<byte> Take(int size)
    {
        if (size < 0 || size > Remaining) throw new InvalidDataException("Truncated client record.");
        var value = data.Span.Slice(Position, size); Position += size; return value;
    }
    public byte U8() => Take(1)[0];
    public ushort U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
    public uint U32() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4));
    public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
    public int Count(int maximum = 100000)
    {
        uint count = U32();
        return count <= maximum ? (int)count : throw new InvalidDataException("Invalid client array length.");
    }
    public string Text() => Utf8.GetString(Take(Count(128 * 1024 * 1024)));
    public void SkipText() => Utf8.GetCharCount(Take(Count(128 * 1024 * 1024)));
    public void Array(int width) => Take(checked(Count() * width));
    public uint[] Array32() { var values = new uint[Count()]; for (int i = 0; i < values.Length; i++) values[i] = U32(); return values; }
    public string Reference(byte category, uint member, uint owner, bool optional = false)
    {
        var actual = (U8(), U32(), U32()); string value = Text();
        if (optional && actual == (0, 0u, 0u) && value.Length == 0) return "";
        if (actual != (category, member, owner) || value != (((ulong)owner << 32) | member).ToString())
            throw new InvalidDataException("Invalid localized client reference.");
        return value;
    }
}

internal static class ClientTables
{
    // The record's only [category][member][owner][length][decimal owner << 32 | member] reference, or -1.
    public static int Anchor(ClientRecord record, byte category, uint member, uint owner)
    {
        string label = (((ulong)owner << 32) | member).ToString(); var anchor = new byte[13 + label.Length];
        anchor[0] = category; BinaryPrimitives.WriteUInt32LittleEndian(anchor.AsSpan(1), member); BinaryPrimitives.WriteUInt32LittleEndian(anchor.AsSpan(5), owner);
        BinaryPrimitives.WriteInt32LittleEndian(anchor.AsSpan(9), label.Length); Encoding.ASCII.GetBytes(label, anchor.AsSpan(13));
        var span = record.Bytes.Span; int first = span.IndexOf(anchor);
        return first >= 0 && span[(first + 1)..].IndexOf(anchor) < 0 ? first : -1;
    }
    // Since 2.06 the header names the records: [count][key][u32 length][name][u32 offset][u32 size] per record, the records
    // following each other through the whole body. Most tables count with a uint16; QuestInfo, StageInfo and a few more with a
    // uint32. Each record then opens with [key][u32 row][u8], the row numbering the table's records.
    // ConditionInfo names its records by their whole condition: over 1 KB, one of them broken over lines.
    private static (uint Key, string Name, int Offset, int Size)[] Index(byte[] header, int body, int width)
    {
        foreach (bool wide in new[] { false, true })
        {
            try
            {
                var index = new ClientReader(header); int count = wide ? index.Count(1000000) : index.U16();
                var entries = new (uint, string, int, int)[count]; long next = 0;
                for (int i = 0; i < count; i++)
                {
                    uint key = width == 4 ? index.U32() : width == 2 ? index.U16() : index.U8();
                    string name = Utf8Strict.GetString(index.Take(index.Count(64 * 1024)));
                    uint offset = index.U32(), size = index.U32();
                    if (offset != next || size < width + 5 || name.Any(c => char.IsControl(c) && !char.IsWhiteSpace(c))) throw new InvalidDataException("Invalid static-info index.");
                    entries[i] = (key, name, (int)offset, (int)size); next = offset + size;
                }
                if (count > 0 && index.Remaining == 0 && next == body) return entries;
            }
            catch (Exception error) when (error is InvalidDataException or DecoderFallbackException) { }
        }
        throw new InvalidDataException("Invalid static-info index.");
    }
    private static readonly UTF8Encoding Utf8Strict = new(false, true);
    // UiMapTextureInfo and StringInfo have unnamed records, hence emptyNames.
    public static Dictionary<uint, ClientRecord> Records(byte[] header, byte[] body, bool emptyNames = false)
    {
        var records = new Dictionary<uint, ClientRecord>();
        foreach (var (key, name, offset, size) in Index(header, body.Length, 4))
        {
            var bytes = body.AsMemory(offset, size); var r = new ClientReader(bytes);
            if (key == 0 || name.Length == 0 && !emptyNames) throw new InvalidDataException("Empty client name.");
            if (r.U32() != key) throw new InvalidDataException("Static-info identity mismatch.");
            r.U32();
            if (r.U8() != 0 || !records.TryAdd(key, new(name, bytes, r.Position))) throw new InvalidDataException("Invalid or duplicate client record.");
        }
        return records;
    }
    public static Dictionary<uint, ClientRecord> Records(IEnumerable<(string Tag, byte[] Header, byte[] Body)> layers, bool emptyNames = false)
    {
        // DLC tables add records and replace base records with the same key.
        var merged = new Dictionary<uint, ClientRecord>();
        foreach (var (_, header, body) in layers)
            foreach (var (key, record) in Records(header, body, emptyNames)) merged[key] = record;
        return merged;
    }
    public static Dictionary<string, string> Strings(byte[] data, IReadOnlySet<string>? needed = null)
    {
        if (data.AsSpan().StartsWith("paloc"u8))
        {
            if (data.Length < 512) throw new InvalidDataException("Truncated PALOC.");
            uint packed = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(9)), expanded = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(13));
            if (packed != data.Length - 512 || expanded is 0 or > 128 * 1024 * 1024) throw new InvalidDataException("Invalid PALOC size.");
            data = Compression.Decode(data.AsSpan(512), 2, (int)expanded);
        }
        if (data.Length < 4) throw new InvalidDataException("Truncated PALOC strings.");
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(data.Length - 4));
        var r = new ClientReader(data.AsMemory(0, data.Length - 4)); var result = new Dictionary<string, string>();
        HashSet<string>? seen = needed is null ? null : [];
        if (count > data.Length / 16) throw new InvalidDataException("Invalid PALOC count.");
        for (uint i = 0; i < count; i++)
        {
            r.Take(8); string key = r.Text();
            if (key.Length == 0 || (seen is null ? result.ContainsKey(key) : !seen.Add(key))) throw new InvalidDataException("Empty or duplicate PALOC key.");
            if (needed is null || needed.Contains(key)) result.Add(key, r.Text());
            else r.SkipText();
        }
        if (r.Remaining != 0) throw new InvalidDataException("Trailing PALOC data.");
        return result;
    }
    // 2.05.01 added one byte to the fixed item block somewhere between +10 and +101: the stack limit stayed at +2, the
    // category moved from +101 to +102 and the name reference from +152 to +153.
    public const int ItemBlock = 153, ItemCategory = 102, ItemKnowledge = 108;
    // More of the fixed block: the equipment type (u32, 0 for others), the tag an Abyss Gear fits by (u32) and how many charges
    // the item holds (u32, 1 for most). The pickup bag (u16) is counted from the record's end.
    public const int ItemEquipType = 72, ItemGearTag = 116, ItemCharges = 141, ItemPickupBagFromEnd = 24;
    public static uint ItemU32(ClientRecord record, int offset) => new ClientReader(record.Bytes, record.Prefix + offset).U32();
    public static ushort PickupBag(ClientRecord record) => new ClientReader(record.Bytes, record.Bytes.Length - ItemPickupBagFromEnd).U16();
    // The gear tags each equipment type takes: EquipTypeInfo holds them 51 bytes in, right before the type's name.
    public static Dictionary<uint, uint[]> EquipTags(Dictionary<uint, ClientRecord> types) => types.ToDictionary(p => p.Key, p =>
    {
        var r = new ClientReader(p.Value.Bytes, p.Value.Prefix);
        r.Take(51);
        uint[] tags = r.Array32();
        r.Reference(0x2E, 0x2E0, p.Key);
        return r.Remaining == 0 ? tags : throw new InvalidDataException("Unsupported EquipTypeInfo layout.");
    });
    // An item with its own use cooldown holds it three times in a row after a set flag; the default is one second.
    public static bool OwnCooldown(ClientRecord record, int start)
    {
        var span = record.Bytes.Span;
        for (int i = start; i + 25 <= span.Length; i++)
        {
            if (span[i] != 1) continue;
            ulong time = BinaryPrimitives.ReadUInt64LittleEndian(span[(i + 1)..]);
            if (time is >= 100 and < 100_000_000 and not 1000 && time == BinaryPrimitives.ReadUInt64LittleEndian(span[(i + 9)..]) &&
                time == BinaryPrimitives.ReadUInt64LittleEndian(span[(i + 17)..])) return true;
        }
        return false;
    }
    public static ItemFields Item(uint key, ClientRecord record)
    {
        // A fixed block (stack limit at +2, category at ItemCategory) precedes the references, then the group label, three
        // optional arrays and the icons. The 12 bytes after the label are [4][sound text][sound name text][two hashed keys];
        // the texts are filled only on ship cannons.
        var r = new ClientReader(record.Bytes, record.Prefix);
        r.Take(2); ulong stack = r.U64();
        uint category = new ClientReader(record.Bytes, record.Prefix + ItemCategory).U16();
        r = new ClientReader(record.Bytes, record.Prefix + ItemBlock);
        string name = r.Reference(7, 0x70, key), description = r.Reference(7, 0x71, key);
        string extra = r.U8() switch { 0 => "", 1 => r.Reference(7, 0x72, key), _ => throw new InvalidDataException("Invalid item text flag.") };
        r.SkipText(); r.Take(4); r.SkipText(); r.SkipText(); r.Take(8);
        foreach (int width in new[] { 9, 4, 4 }) Optional(r, width);
        var icons = new uint[r.Count()];
        for (int i = 0; i < icons.Length; i++) { icons[i] = r.U32(); r.Take(5); r.Array(4); r.Take(1); }
        return new(stack, name, description, extra, icons, category, r.Position);
    }
    private static void Optional(ClientReader r, int width)
    {
        byte present = r.U8();
        if (present == 1) r.Array(width);
        else if (present != 0) throw new InvalidDataException("Invalid optional client array.");
    }
    public static int SocketLimit(ClientRecord record, int start, Dictionary<uint, ClientRecord> items)
    {
        // shortcut: only equipment-shaped tails are decoded; any other tail reports 0. A search of every other tail for a
        // socket-rule block found none with sockets (and the search finds exactly the walked block in all 891 socketed
        // items). Decode the remaining tail variants if a client update breaks that check.
        try
        {
            var r = new ClientReader(record.Bytes, start);
            Optional(r, 8); r.Take(10); Optional(r, 4); r.Take(2);
            int enchant = r.U16(), gems = r.Count(5);
            for (int i = 0; i < gems; i++) if (!items.ContainsKey(r.U32())) throw new InvalidDataException("Unresolved socket gem.");
            int materials = r.Count(5);
            for (int i = 0; i < materials; i++) if (!items.ContainsKey(r.U32()) || r.U64() == 0) throw new InvalidDataException("Invalid socket material.");
            byte sub = r.U8();
            if (sub is 0 or 3 or 9) r.Take(4); else if (sub != 18) throw new InvalidDataException("Unsupported sub-item tag.");
            byte valid = r.U8(), enabled = r.U8();
            r.Take(1);
            int levels = r.Count(11);
            if (levels > 0 && r.U16() != 0 || enabled > 1 || valid != gems || gems > materials || enchant > 10 || enabled == 0 && (gems > 0 || materials > 0))
                throw new InvalidDataException("Invalid socket rules.");
            return enabled == 1 ? materials : 0;
        }
        catch (InvalidDataException) { return 0; }
    }
    public static JsonArray? EnchantLevels(ClientRecord record, int start, Dictionary<uint, ClientRecord> statuses, Dictionary<uint, ClientRecord> items)
    {
        JsonArray? match = null; int matches = 0;
        for (int count = 2; count <= 11; count++)
        {
            byte[] marker = new byte[6]; BinaryPrimitives.WriteInt32LittleEndian(marker, count);
            int position = start;
            while (position <= record.Bytes.Length - marker.Length)
            {
                int found = record.Bytes.Span[position..].IndexOf(marker);
                if (found < 0) break;
                int offset = position + found; position = offset + 1;
                try
                {
                    var r = new ClientReader(record.Bytes, offset + 4); var levels = new JsonArray(); bool hasStats = false;
                    for (int level = 0; level < count; level++)
                    {
                        if (r.U16() != level) throw new InvalidDataException("Nonconsecutive enchant level.");
                        var stats = new JsonArray();
                        foreach (string kind in new[] { "maximum", "regen", "stat", "level" })
                            for (int i = r.Count(32); i > 0; i--)
                            {
                                uint key = r.U32(); long value = kind == "level" ? (sbyte)r.U8() : BinaryPrimitives.ReadInt64LittleEndian(r.Take(8));
                                if (!statuses.ContainsKey(key) || value is < -1000000000000 or > 1000000000000) throw new InvalidDataException("Unresolved stat.");
                                stats.Add(new JsonObject { ["key"] = key, ["value"] = value, ["kind"] = kind });
                            }
                        int prices = r.Count(8);
                        if (prices == 0) throw new InvalidDataException("Missing enchant price.");
                        for (int i = 0; i < prices; i++)
                        {
                            uint key = r.U32(); ulong price = r.U64(); r.Take(4); uint other = r.U32();
                            if (!items.ContainsKey(key) || !items.ContainsKey(other) || price > 1000000000000) throw new InvalidDataException("Unresolved currency.");
                        }
                        r.Take(r.Count(32) * 8); r.Take(r.Count(32) * 4);
                        r.Take(4);
                        hasStats |= stats.Count > 0; levels.Add(new JsonObject { ["level"] = level, ["stats"] = stats });
                    }
                    if (hasStats) { match = levels; matches++; }
                }
                catch (InvalidDataException) { }
            }
        }
        return matches == 1 ? match : null;
    }
    // Small tables keyed by a uint16, or by a byte when width is 1 (MercenaryInfo).
    public static Dictionary<uint, (string Name, ClientReader Reader)> Records16(byte[] header, byte[] body, int width = 2)
    {
        var result = new Dictionary<uint, (string, ClientReader)>();
        foreach (var (key, name, offset, size) in Index(header, body.Length, width))
        {
            var r = new ClientReader(body.AsMemory(offset, size));
            if ((width == 1 ? r.U8() : r.U16()) != key) throw new InvalidDataException("Classification identity mismatch.");
            r.U32();
            if (r.U8() > 1 || !result.TryAdd(key, (name, r))) throw new InvalidDataException("Invalid classification record.");
        }
        return result;
    }
    public static Dictionary<uint, (string Name, ClientReader Reader)> Records16(IEnumerable<(string Tag, byte[] Header, byte[] Body)> layers, int width = 2)
    {
        var merged = new Dictionary<uint, (string Name, ClientReader Reader)>();
        foreach (var (_, header, body) in layers)
            foreach (var (key, record) in Records16(header, body, width)) merged[key] = record;
        return merged;
    }
}
