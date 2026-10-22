using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using SaveUltimate.Core.Binary;

namespace SaveUltimate.Core.Catalogs;

internal sealed record ClientRecord(string Name, ReadOnlyMemory<byte> Bytes, int Prefix);

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
    public static Dictionary<uint, ClientRecord> Records(byte[] header, byte[] body)
    {
        var index = new ClientReader(header); int count = index.U16();
        if (count == 0 || index.Remaining != count * 8) throw new InvalidDataException("Invalid static-info index.");
        var entries = Enumerable.Range(0, count).Select(_ => (Key: index.U32(), Offset: index.U32())).ToArray();
        if (entries[0].Offset != 0) throw new InvalidDataException("Invalid first record offset.");
        var records = new Dictionary<uint, ClientRecord>(count);
        for (int i = 0; i < count; i++)
        {
            var (key, offset) = entries[i]; uint end = i + 1 < count ? entries[i + 1].Offset : (uint)body.Length;
            if (key == 0 || end <= offset || end > body.Length) throw new InvalidDataException("Invalid static-info boundary.");
            var bytes = body.AsMemory((int)offset, (int)(end - offset)); var r = new ClientReader(bytes);
            if (r.U32() != key) throw new InvalidDataException("Static-info identity mismatch.");
            int length = r.Count(512);
            if (length == 0) throw new InvalidDataException("Empty client name.");
            string name = new UTF8Encoding(false, true).GetString(r.Take(length));
            if (r.U8() != 0 || name.Any(char.IsControl) || !records.TryAdd(key, new(name, bytes, r.Position)))
                throw new InvalidDataException("Invalid or duplicate client record.");
        }
        return records;
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
    public static (string Description, string Extra, uint[] Icons, int End) ItemHeader(uint key, ClientRecord record)
    {
        var r = new ClientReader(record.Bytes, record.Prefix);
        r.Take(8); r.Reference(7, 0x70, key); r.Take(8);
        for (int i = r.Count(); i > 0; i--) { r.Take(4); r.Array(1); }
        r.Array(4); r.Take(4); r.Array(4); r.Array(4);
        var icons = new uint[r.Count()];
        for (int i = 0; i < icons.Length; i++) { icons[i] = r.U32(); r.Take(5); r.Array(4); r.Take(1); }
        r.Take(14);
        return (r.Reference(7, 0x71, key), r.Reference(7, 0x72, key, true), icons, r.Position);
    }
    public static int SocketLimit(ClientRecord record, int start, Dictionary<uint, ClientRecord> items)
    {
        var r = new ClientReader(record.Bytes, start);
        r.Take(15); r.Take(r.Count(10000) * 8); r.Take(8); r.Text(); r.Text(); r.Take(4);
        for (int i = r.Count(10000); i > 0; i--) r.Text();
        r.Take(6);
        for (int a = 0; a < 5; a++)
            for (int i = r.Count(10000); i > 0; i--)
            {
                byte tag = r.U8(); r.Take(12);
                if (tag == 2) r.Text();
                else if (tag is 0 or 1 or 3 or 4) r.Take(tag == 4 ? 2 : 4);
                else throw new InvalidDataException("Unsupported sealed item tag.");
            }
        r.Array(4); r.Take(6); r.Array(4); r.Array(4); r.Array(2); r.Take(1); r.Array(4); r.Take(8);
        r.Take(r.Count(10000) * 8); r.Take(4);
        int enchant = r.U16(), gems = r.Count(5);
        for (int i = 0; i < gems; i++) if (!items.ContainsKey(r.U32())) throw new InvalidDataException("Unresolved socket gem.");
        int materials = r.Count(5);
        for (int i = 0; i < materials; i++) if (!items.ContainsKey(r.U32()) || r.U64() == 0) throw new InvalidDataException("Invalid socket material.");
        byte sub = r.U8();
        if (sub is 0 or 3 or 9) r.Take(4); else if (sub != 18) throw new InvalidDataException("Unsupported sub-item tag.");
        byte valid = r.U8(), enabled = r.U8(); int levels = r.Count(11);
        if (levels > 0 && r.U16() != 0 || enabled > 1 || valid != gems || gems > materials || enchant > 10 || enabled == 0 && (gems > 0 || materials > 0))
            throw new InvalidDataException("Invalid socket rules.");
        return enabled == 1 ? materials : 0;
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
                        hasStats |= stats.Count > 0; levels.Add(new JsonObject { ["level"] = level, ["stats"] = stats });
                    }
                    if (hasStats) { match = levels; matches++; }
                }
                catch (InvalidDataException) { }
            }
        }
        return matches == 1 ? match : null;
    }
    public static Dictionary<uint, (string Name, ClientReader Reader)> Records16(byte[] header, byte[] body)
    {
        var h = new ClientReader(header); int count = h.U16();
        if (count == 0 || h.Remaining != count * 6) throw new InvalidDataException("Invalid classification index.");
        var entries = Enumerable.Range(0, count).Select(_ => (Key: (uint)h.U16(), Offset: h.U32())).ToArray();
        if (entries[0].Offset != 0) throw new InvalidDataException("Invalid classification first offset.");
        var result = new Dictionary<uint, (string, ClientReader)>();
        for (int i = 0; i < count; i++)
        {
            var (key, offset) = entries[i]; uint end = i + 1 < count ? entries[i + 1].Offset : (uint)body.Length;
            if (offset >= end || end > body.Length) throw new InvalidDataException("Invalid classification boundary.");
            var r = new ClientReader(body.AsMemory((int)offset, (int)(end - offset)));
            if (r.U16() != key) throw new InvalidDataException("Classification identity mismatch.");
            string name = r.Text();
            if (r.U8() > 1 || !result.TryAdd(key, (name, r))) throw new InvalidDataException("Invalid classification record.");
        }
        return result;
    }
}
