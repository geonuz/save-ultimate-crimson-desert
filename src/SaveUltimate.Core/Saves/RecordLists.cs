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
            int index = layout.Types.Select((t, i) => (t, i)).Single(p => p.t.Name == edit.Type).i;
            var type = layout.Types[index];
            if (!Encode(raw, edit with { Changes = [] }, type, index, edit.Field.StartOffset)
                .AsSpan().SequenceEqual(raw.AsSpan(edit.Field.StartOffset, edit.Field.EndOffset - edit.Field.StartOffset)))
                throw new InvalidDataException("Record list does not round-trip.");
            yield return (edit.Field.StartOffset, edit.Field.EndOffset, start => Encode(raw, edit, type, index, start));
        }
    }

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
    {
        var replacements = new List<(int Start, int End, byte[] Value)>();
        int delta = 0, end = 0;
        var patches = new List<(SaveField Field, ScalarValue Value)>();
        bool existing = true;
        foreach (var edit in edits.OrderBy(e => e.Field.StartOffset))
        {
            var field = edit.Field;
            if (!field.Present || field.StartOffset < end || field.EndOffset > raw.Length)
                throw new InvalidDataException("Unsupported overlapping record lists.");
            end = field.EndOffset;
            int typeIndex = layout.Types.Select((t, i) => (t, i)).Single(p => p.t.Name == edit.Type).i;
            byte[] original = Encode(raw, edit with { Changes = [] }, layout.Types[typeIndex], typeIndex, field.StartOffset);
            if (!original.AsSpan().SequenceEqual(raw.AsSpan(field.StartOffset, field.EndOffset - field.StartOffset)))
                throw new InvalidDataException("Record list does not round-trip.");
            byte[] encoded = Encode(raw, edit, layout.Types[typeIndex], typeIndex, field.StartOffset + delta);
            replacements.Add((field.StartOffset, field.EndOffset, encoded));
            delta += encoded.Length - (field.EndOffset - field.StartOffset);
            var records = field.ListElements.ToDictionary(r => Identity(raw, r.ChildFields.Named(edit.Key)), r => r);
            foreach (var (key, values) in edit.Changes)
                foreach (var (name, value) in values)
                {
                    if (!records.TryGetValue(key, out var record)) { existing = false; continue; }
                    var target = record.ChildFields.Named(name);
                    if (value == ScalarValue.Remove) { if (target.Present) existing = false; continue; }
                    if (!target.Present || value.Elements is not null) { existing = false; continue; }
                    target.Scalar(raw, value.Type, value.Size);
                    patches.Add((target, value));
                }
        }
        if (!existing) return Relocate(raw, replacements, bufferForEdit);
        byte[] result = bufferForEdit(raw.Length);
        raw.CopyTo(result, 0);
        foreach (var (field, value) in patches) Integer(value).CopyTo(result, field.StartOffset);
        return result;
    }

    // A list that does not round-trip because of its rows, rewritten through edit.Salvage.
    internal static byte[] Rewritten(byte[] raw, ParcLayout layout, ListEdit edit, int start)
    {
        int index = layout.Types.Select((t, i) => (t, i)).Single(p => p.t.Name == edit.Type).i;
        return Encode(raw, edit, layout.Types[index], index, start);
    }

    // Field bytes by the game's layout, or null when the row does not follow it. An absent array still takes one byte, 1, in its
    // place; a present one is 0, u32 count and the elements. The native reader leaves those markers to the following fields.
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
        if (field.ListHeaderSize is not 18 and not 19 || !string.IsNullOrEmpty(field.Note))
            throw new InvalidDataException("Unsupported record list header.");
        byte[] header = raw.AsSpan(field.StartOffset, field.ListHeaderSize).ToArray();
        int countStart = header.Length == 18 ? 1 : 2, countSize = header.Length == 18 ? 3 : 4;
        uint count = 0;
        for (int i = 0; i < countSize; i++) count |= (uint)header[countStart + i] << (8 * i);
        if (header[0] != (header.Length == 18 ? 0 : 1) || header.Length == 19 && header[1] != 0 ||
            header.Skip(countStart + countSize).Any(b => b != 0) || count != field.ListCount)
            throw new InvalidDataException("Unsupported record list encoding.");
        var records = field.Elements(type.Name).ToDictionary(r => Identity(raw, r.ChildFields.Named(edit.Key)), r => (SaveField?)r);
        foreach (uint key in edit.Changes.Keys.Except(records.Keys).Order())
            records.Add(key, edit.CloneLast ? field.ListElements.LastOrDefault() ?? throw new InvalidDataException("Missing insertion template.") : null);
        if (records.Count >= 0xffffff) throw new InvalidDataException("Too many records.");
        for (int i = 0; i < countSize; i++) header[countStart + i] = (byte)(records.Count >> (8 * i));
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output);
        writer.Write(header);
        foreach (var (key, record) in records)
        {
            var values = new Dictionary<string, ReadOnlyMemory<byte>>();
            int maskSize = (type.Fields.Count + 7) / 8;
            if (record is not null)
            {
                int offset = record.StartOffset;
                if (record.ChildUndecodedRanges.Length != 0 || record.ChildMaskByteCount != maskSize ||
                    record.ChildPayloadOffset != offset + 17 + maskSize || record.EndOffset > raw.Length ||
                    BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(offset)) != maskSize ||
                    BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(offset + 2 + maskSize)) != typeIndex ||
                    raw[offset + 4 + maskSize] != 0 ||
                    raw.AsSpan(offset + 5 + maskSize, 8).ContainsAnyExcept((byte)255) ||
                    BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(offset + 13 + maskSize)) != record.ChildPayloadOffset ||
                    BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(record.ChildPayloadOffset)) != 0 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(record.EndOffset - 4)) != record.EndOffset - 4 - record.ChildPayloadOffset)
                    throw new InvalidDataException("Unsupported inline record header.");
                values = Fields(raw, type, record) ?? edit.Salvage?.Invoke(raw, type, record) ?? throw new InvalidDataException("Unsupported inline record fields.");
            }
            if (edit.Changes.TryGetValue(key, out var changes))
                foreach (var (name, value) in changes)
                {
                    var f = type.Fields.Single(f => f.Name == name);
                    if (value == ScalarValue.Remove) { values.Remove(name); continue; }
                    if (f.TypeName != value.Type || f.Size != value.Size || (value.Elements is null ? f.Kind is not 0 and not 2 : f.Kind != 3 || f.Size != 4))
                        throw new InvalidDataException("Record scalar schema changed.");
                    values[name] = value.Elements is null ? Integer(value) : ArrayBytes(value.Elements);
                }
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
            uint position = checked((uint)(start + output.Length + 17 + maskSize));
            writer.Write(checked((ushort)maskSize)); writer.Write(mask); writer.Write(checked((ushort)typeIndex));
            writer.Write((byte)0); writer.Write(ulong.MaxValue); writer.Write(position);
            payload.WriteTo(output); writer.Write(checked((uint)payload.Length));
        }
        return output.ToArray();
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
