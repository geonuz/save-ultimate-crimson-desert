using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using SaveUltimate.Core.Binary;

namespace SaveUltimate.Core.Catalogs;

// Records open with [name][key][0]. Only the 2.04.06+ client layout is read; an older install reports an error.
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
    // UiMapTextureInfo has unnamed records, hence emptyNames.
    public static Dictionary<uint, ClientRecord> Records(byte[] header, byte[] body, bool emptyNames = false)
    {
        var index = new ClientReader(header);
        // QuestInfo counts its records with a uint32; the other tables use a uint16.
        int count = header.Length >= 4 && (header.Length - 4) % 8 == 0 && BinaryPrimitives.ReadUInt32LittleEndian(header) == (header.Length - 4) / 8
            ? index.Count(65535) : index.U16();
        if (count == 0 || index.Remaining != count * 8) throw new InvalidDataException("Invalid static-info index.");
        var entries = Enumerable.Range(0, count).Select(_ => (Key: index.U32(), Offset: index.U32())).ToArray();
        if (entries[0].Offset != 0) throw new InvalidDataException("Invalid first record offset.");
        var records = new Dictionary<uint, ClientRecord>(count);
        for (int i = 0; i < count; i++)
        {
            var (key, offset) = entries[i]; uint end = i + 1 < count ? entries[i + 1].Offset : (uint)body.Length;
            if (key == 0 || end <= offset || end > body.Length) throw new InvalidDataException("Invalid static-info boundary.");
            var bytes = body.AsMemory((int)offset, (int)(end - offset)); var r = new ClientReader(bytes);
            int length = r.Count(512);
            if (length == 0 && !emptyNames) throw new InvalidDataException("Empty client name.");
            string name;
            try { name = new UTF8Encoding(false, true).GetString(r.Take(length)); }
            catch (DecoderFallbackException) { throw new InvalidDataException("Invalid client name."); }
            if (r.U32() != key) throw new InvalidDataException("Static-info identity mismatch.");
            if (r.U8() != 0 || name.Any(char.IsControl) || !records.TryAdd(key, new(name, bytes, r.Position)))
                throw new InvalidDataException("Invalid or duplicate client record.");
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
    public static ItemFields Item(uint key, ClientRecord record)
    {
        // A fixed 152-byte block (stack limit at +2, category at +101) precedes the references, then the group label, three
        // optional arrays and the icons. The 12 bytes after the label are [4][sound text][sound name text][two hashed keys];
        // the texts are filled only on ship cannons.
        var r = new ClientReader(record.Bytes, record.Prefix);
        r.Take(2); ulong stack = r.U64();
        uint category = new ClientReader(record.Bytes, record.Prefix + 101).U16();
        r = new ClientReader(record.Bytes, record.Prefix + 152);
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
        var h = new ClientReader(header); int count = h.U16();
        if (count == 0 || h.Remaining != count * (width + 4)) throw new InvalidDataException("Invalid classification index.");
        uint Key(ClientReader r) => width == 1 ? r.U8() : r.U16();
        var entries = Enumerable.Range(0, count).Select(_ => (Key: Key(h), Offset: h.U32())).ToArray();
        if (entries[0].Offset != 0) throw new InvalidDataException("Invalid classification first offset.");
        var result = new Dictionary<uint, (string, ClientReader)>();
        for (int i = 0; i < count; i++)
        {
            var (key, offset) = entries[i]; uint end = i + 1 < count ? entries[i + 1].Offset : (uint)body.Length;
            if (offset >= end || end > body.Length) throw new InvalidDataException("Invalid classification boundary.");
            var r = new ClientReader(body.AsMemory((int)offset, (int)(end - offset)));
            string name;
            try { name = r.Text(); }
            catch (DecoderFallbackException) { throw new InvalidDataException("Invalid classification name."); }
            if (Key(r) != key) throw new InvalidDataException("Classification identity mismatch.");
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
