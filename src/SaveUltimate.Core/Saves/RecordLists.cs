using System.Buffers.Binary;

namespace SaveUltimate.Core.Saves;

internal sealed record ScalarValue(string Type, int Size, ulong Value, uint[]? Elements = null)
{
    internal static ScalarValue Remove { get; } = new("", 0, 0);
}
internal sealed record ListEdit(SaveField Field, string Type, string Key, Dictionary<uint, Dictionary<string, ScalarValue>> Changes, bool CloneLast = false,
    Func<byte[], ParcType, SaveField, Dictionary<string, ReadOnlyMemory<byte>>>? Salvage = null);

internal static class RecordLists
{
    internal static IEnumerable<(int Start, int End, Func<int, byte[]> Encode)> Regions(byte[] raw, ParcLayout layout, IEnumerable<ListEdit> edits)
    {
        foreach (var edit in edits)
        {
            var field = edit.Field;
            if (!field.Present) throw new InvalidDataException("Cannot edit an absent record list.");
            int index = layout.TypeIndex(edit.Type);
            var type = layout.Types[index];
            // Rewriting nothing must give back the same bytes, or this writer misunderstands the list. Salvaged rows change by design.
            if (edit.Salvage is null && !Encode(raw, edit with { Changes = [] }, type, index, field.StartOffset)
                .AsSpan().SequenceEqual(raw.AsSpan(field.StartOffset, field.EndOffset - field.StartOffset)))
                throw new InvalidDataException("Record list does not round-trip.");
            yield return (field.StartOffset, field.EndOffset, start => Encode(raw, edit, type, index, start));
        }
    }

    // start receives where the replacement will land, after the ones before it.
    internal static byte[] RewriteRegions(byte[] raw, Func<int, byte[]> bufferForEdit, IEnumerable<(int Start, int End, Func<int, byte[]> Encode)> regions)
    {
        var replacements = new List<(int Start, int End, byte[] Value)>();
        int delta = 0, previous = 0;
        foreach (var (start, end, encode) in regions.OrderBy(r => r.Start))
        {
            if (start < previous || end < start || end > raw.Length) throw new InvalidDataException("Invalid replacement boundaries.");
            byte[] value = encode(checked(start + delta));
            replacements.Add((start, end, value));
            delta = checked(delta + value.Length - (end - start)); previous = end;
        }
        return replacements.Count == 0 ? raw : Relocate(raw, replacements, bufferForEdit);
    }

    internal static byte[] Rewrite(byte[] raw, ParcLayout layout, Func<int, byte[]> bufferForEdit, params ListEdit[] edits)
        => RewriteRegions(raw, bufferForEdit, Regions(raw, layout, edits));

    // Field bytes by the game's layout, or null when the row does not follow it. An absent array still takes one byte, 1, in its
    // place; a present one is 0, u32 count and the elements.
    internal static Dictionary<string, ReadOnlyMemory<byte>>? Fields(byte[] raw, ParcType type, SaveField record)
    {
        var values = new Dictionary<string, ReadOnlyMemory<byte>>();
        int cursor = record.ChildPayloadOffset + 4, end = record.EndOffset - 4;
        for (int i = 0; i < type.Fields.Count; i++)
        {
            var f = type.Fields[i];
            bool present = (raw[record.StartOffset + 2 + i / 8] & (1 << (i % 8))) != 0;
            int size = f.Size;
            if (f.Kind == 3)
            {
                if (cursor >= end || raw[cursor] != (present ? 0 : 1)) return null;
                if (!present) { cursor++; continue; }
                if (cursor + 5 > end) return null;
                long total = 5L + BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(cursor + 1)) * (long)f.Size;
                if (total > end - cursor) return null;
                size = (int)total;
            }
            else if (!present) continue;
            else if (f.Kind is not 0 and not 2 || size <= 0 || cursor + size > end) return null;
            values.Add(f.Name, raw.AsMemory(cursor, size));
            cursor += size;
        }
        return cursor == end ? values : null;
    }

    internal static byte[] ArrayBytes(uint[] elements)
    {
        byte[] bytes = new byte[5 + elements.Length * 4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), elements.Length);
        for (int i = 0; i < elements.Length; i++) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(5 + i * 4), elements[i]);
        return bytes;
    }

    private static uint Identity(byte[] raw, SaveField field)
    {
        if (!field.Present && field.TypeName != "SkillPointOwnerType") throw new InvalidDataException("Missing record identity.");
        return field.Present ? checked((uint)field.Scalar(raw, field.TypeName, field.TypeName == "SkillPointOwnerType" ? 1 : 4)) : 0;
    }

    private static byte[] Integer(ScalarValue value)
    {
        byte[] bytes = new byte[value.Size is 1 or 2 or 4 or 8 ? value.Size : throw new InvalidDataException("Unsupported record scalar.")];
        switch (value.Size)
        {
            case 1: bytes[0] = checked((byte)value.Value); break;
            case 2: BinaryPrimitives.WriteUInt16LittleEndian(bytes, checked((ushort)value.Value)); break;
            case 4: BinaryPrimitives.WriteUInt32LittleEndian(bytes, checked((uint)value.Value)); break;
            case 8: BinaryPrimitives.WriteUInt64LittleEndian(bytes, value.Value); break;
        }
        return bytes;
    }

    private static byte[] Encode(byte[] raw, ListEdit edit, ParcType type, int typeIndex, int start)
    {
        var field = edit.Field;
        // Indexed lists hold an id per row and a next id, which rows rebuilt from their fields would lose.
        if (field.MetaKind != 6) throw new InvalidDataException("Unsupported record list.");
        var records = field.Elements(type.Name).ToDictionary(r => Identity(raw, r.ChildFields.Named(edit.Key)), r => (SaveField?)r);
        foreach (uint key in edit.Changes.Keys.Except(records.Keys).Order())
            records.Add(key, edit.CloneLast ? field.ListElements.LastOrDefault() ?? throw new InvalidDataException("Missing insertion template.") : null);
        return ListBytes(start, type, typeIndex, records.Select(pair =>
        {
            var (key, record) = pair;
            var values = record is null ? [] : Fields(raw, type, record) ?? edit.Salvage?.Invoke(raw, type, record) ?? throw new InvalidDataException("Unsupported inline record fields.");
            if (edit.Changes.TryGetValue(key, out var changes))
                foreach (var (name, value) in changes)
                {
                    var f = type.Fields.Single(f => f.Name == name);
                    if (value == ScalarValue.Remove) { values.Remove(name); continue; }
                    if (f.TypeName != value.Type || f.Size != value.Size || (value.Elements is null ? f.Kind is not 0 and not 2 : f.Kind != 3 || f.Size != 4))
                        throw new InvalidDataException("Record scalar schema changed.");
                    values[name] = value.Elements is null ? Integer(value) : ArrayBytes(value.Elements);
                }
            return values;
        }));
    }

    // The layout table in ParcReader, written: a row holds its fields in schema order, and a mask bit says which are there.
    private static void WriteRow(MemoryStream output, int start, ParcType type, int typeIndex, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> values)
    {
        int maskSize = type.MaskSize;
        byte[] mask = new byte[maskSize];
        using var payload = new MemoryStream();
        payload.Write(new byte[4]);
        for (int i = 0; i < type.Fields.Count; i++)
            if (values.TryGetValue(type.Fields[i].Name, out var value))
            {
                mask[i / 8] |= (byte)(1 << (i % 8));
                payload.Write(value.Span);
            }
            else if (type.Fields[i].Kind == 3) payload.WriteByte(1);
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        uint address = checked((uint)(start + output.Length + 17 + maskSize));
        writer.Write(checked((ushort)maskSize)); writer.Write(mask); writer.Write(checked((ushort)typeIndex));
        writer.Write((byte)0); writer.Write(ulong.MaxValue); writer.Write(address);
        writer.Flush();
        payload.WriteTo(output);
        writer.Write(checked((uint)payload.Length));
    }

    // An object nested in a record (kind 4 field): its type, which the schema field does not name, and its fields.
    internal sealed record Nested(string Type, IReadOnlyDictionary<string, object> Values);

    // A whole object by the layout table in ParcReader, nested objects included. Values name the present fields: fixed fields
    // hold their bytes, object fields a Nested. Absent arrays and lists still take their 01 marker. start is where it lands.
    internal static byte[] ObjectBytes(ParcLayout layout, Nested value, int start)
    {
        using var output = new MemoryStream();
        WriteObject(output, start, layout, value);
        return output.ToArray();
    }

    private static void WriteObject(MemoryStream output, int start, ParcLayout layout, Nested value)
    {
        int typeIndex = layout.TypeIndex(value.Type);
        var type = layout.Types[typeIndex];
        if (value.Values.Keys.Any(name => !type.Fields.Any(f => f.Name == name))) throw new InvalidDataException($"{value.Type} has no such field.");
        byte[] mask = new byte[type.MaskSize];
        foreach (var f in type.Fields) if (value.Values.ContainsKey(f.Name)) mask[f.Index / 8] |= (byte)(1 << (f.Index % 8));
        using var writer = new BinaryWriter(output, System.Text.Encoding.UTF8, leaveOpen: true);
        writer.Write(checked((ushort)mask.Length)); writer.Write(mask); writer.Write(checked((ushort)typeIndex));
        writer.Write((byte)0); writer.Write(ulong.MaxValue);
        long payload = output.Position + 4;
        writer.Write(checked((uint)(start + payload)));
        writer.Write(0u);
        writer.Flush();
        foreach (var f in type.Fields)
        {
            if (!value.Values.TryGetValue(f.Name, out var field))
            {
                if (f.Kind is 3 or 6 or 7) output.WriteByte(1);
                continue;
            }
            switch (f.Kind, field)
            {
                case (0 or 2, byte[] bytes) when bytes.Length == f.Size && f.Size > 0: output.Write(bytes); break;
                case (4, Nested nested): WriteObject(output, start, layout, nested); break;
                default: throw new InvalidDataException($"Unsupported value for {value.Type}.{f.Name}.");
            }
        }
        writer.Write(checked((uint)(output.Position - payload)));
    }

    internal static byte[] RowBytes(int start, ParcType type, int typeIndex, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> values)
    {
        using var output = new MemoryStream();
        WriteRow(output, start, type, typeIndex, values);
        return output.ToArray();
    }

    internal static byte[] ListBytes(int start, ParcType type, int typeIndex, IEnumerable<Dictionary<string, ReadOnlyMemory<byte>>> rows)
    {
        using var output = new MemoryStream();
        output.Write(new byte[18]);
        int count = 0;
        foreach (var row in rows) { WriteRow(output, start, type, typeIndex, row); count++; }
        byte[] bytes = output.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(1), count);
        return bytes;
    }

    private static int UpperBound(int[] sorted, int value)
    {
        int low = 0, high = sorted.Length;
        while (low < high)
        {
            int middle = (low + high) / 2;
            if (sorted[middle] <= value) low = middle + 1; else high = middle;
        }
        return low;
    }

    // Replaces byte ranges, sorted by position, then points every record address, record size, block bound and the stream size
    // at where their targets moved. What lies inside a replacement is the replacement's own business.
    internal static byte[] Relocate(byte[] raw, IReadOnlyList<(int Start, int End, byte[] Value)> regions, Func<int, byte[]> bufferForEdit)
    {
        var layout = ParcLayout.Parse(raw);
        var (pointers, sizes, _) = ParcReader.Validate(raw, layout);
        int table = checked(layout.SchemaEnd + 12), cursor = 0;
        int[] starts = new int[regions.Count], ends = new int[regions.Count];
        long[] shifts = new long[regions.Count + 1];
        for (int i = 0; i < regions.Count; i++)
        {
            var (start, end, value) = regions[i];
            if (start < cursor || end < start || end > raw.Length || start < table + 20L * layout.Blocks.Count || (i > 0 && start == starts[i - 1]))
                throw new InvalidDataException("Overlapping or invalid record replacements.");
            (starts[i], ends[i], cursor) = (start, end, end);
            shifts[i + 1] = shifts[i] + value.Length - (end - start);
        }
        long length = raw.Length + shifts[^1];
        if (length <= 0 || length > Array.MaxLength) throw new InvalidDataException("Invalid relocated stream size.");
        byte[] result = bufferForEdit((int)length);
        // An edit that builds on its own candidate may be handed that same buffer back.
        if (ReferenceEquals(result, raw)) result = new byte[length];
        cursor = 0;
        int written = 0;
        for (int i = 0; i < regions.Count; i++)
        {
            var (start, end, value) = regions[i];
            raw.AsSpan(cursor, start - cursor).CopyTo(result.AsSpan(written));
            written += start - cursor;
            value.CopyTo(result, written);
            written += value.Length;
            cursor = end;
        }
        raw.AsSpan(cursor).CopyTo(result.AsSpan(written));

        bool Inside(int position)
        {
            int region = UpperBound(starts, position);
            return region != 0 && position < ends[region - 1];
        }
        int Moved(int position)
        {
            long moved = position + shifts[UpperBound(ends, position)];
            if (moved < 0 || moved > length) throw new InvalidDataException("Invalid relocated address.");
            return (int)moved;
        }
        void Write(int position, int value)
        {
            if (position > length - 4) throw new InvalidDataException("Relocation outside the stream.");
            BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(position), value);
        }
        for (int i = 0; i < pointers.Length; i++)
        {
            int pointer = pointers[i], payload = pointer + 4, size = sizes[i];
            if ((!Inside(pointer) || !Inside(size)) && Inside(payload)) throw new InvalidDataException("A record reaches into a replaced range.");
            if (!Inside(pointer)) Write(Moved(pointer), Moved(payload));
            if (!Inside(size)) Write(Moved(size), Moved(size) - Moved(payload));
        }
        for (int i = 0; i < layout.Blocks.Count; i++)
        {
            var block = layout.Blocks[i];
            int start = Moved(block.Offset), end = Moved(block.Offset + block.Size), entry = table + 20 * i;
            if (end < start) throw new InvalidDataException("Invalid relocated block size.");
            Write(entry + 12, start);
            Write(entry + 16, end - start);
        }
        Write(layout.SchemaEnd + 8, (int)length);
        return result;
    }
}
