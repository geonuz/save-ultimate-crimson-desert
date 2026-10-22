using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace SaveUltimate.Core.Saves;

internal sealed class SaveField
{
    public string Name { get; set; } = "";
    public string TypeName { get; set; } = "";
    public int MetaKind { get; set; }
    public int MetaSize { get; set; }
    public int FieldIndex { get; set; }
    public int ChildMaskByteCount { get; set; }
    public bool Present { get; set; }
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public string ChildTypeName { get; set; } = "";
    public int ChildPayloadOffset { get; set; }
    public int ChildSizeU32 { get; set; }
    public int ListCount { get; set; }
    public int ListHeaderSize { get; set; }
    public string? Note { get; set; }
    public SaveField[] ChildFields { get; set; } = [];
    public SaveField[] ListElements { get; set; } = [];
    public int[][] ChildUndecodedRanges { get; set; } = [];

    internal SaveField Relocate(int delta)
    {
        if (delta == 0) return this;
        var copy = (SaveField)MemberwiseClone();
        copy.StartOffset = StartOffset == 0 ? 0 : checked(StartOffset + delta);
        copy.EndOffset = EndOffset == 0 ? 0 : checked(EndOffset + delta);
        copy.ChildPayloadOffset = ChildPayloadOffset == 0 ? 0 : checked(ChildPayloadOffset + delta);
        copy.ChildFields = ChildFields.Select(f => f.Relocate(delta)).ToArray();
        copy.ListElements = ListElements.Select(f => f.Relocate(delta)).ToArray();
        copy.ChildUndecodedRanges = ChildUndecodedRanges.Select(r => r.Select(p => checked(p + delta)).ToArray()).ToArray();
        return copy;
    }

    public ulong Scalar(byte[] raw, string type, int size)
    {
        if (!Present || TypeName != type || MetaKind is not 0 and not 2 || MetaSize != size ||
            StartOffset <= 0 || EndOffset - StartOffset != size || EndOffset > raw.Length)
            throw new InvalidDataException($"Unsupported scalar layout: {Name}");
        var span = raw.AsSpan(StartOffset, size);
        return size switch
        {
            1 => span[0], 2 => BinaryPrimitives.ReadUInt16LittleEndian(span),
            4 => BinaryPrimitives.ReadUInt32LittleEndian(span), 8 => BinaryPrimitives.ReadUInt64LittleEndian(span),
            _ => throw new InvalidDataException("Unsupported integer size.")
        };
    }

    public SaveField[] Elements(string type)
    {
        if (!Present) return [];
        if (ListElements.Length != ListCount || ListElements.Any(f => f.ChildTypeName != type || f.ChildFields.Length == 0))
            throw new InvalidDataException($"Incomplete or unsupported {Name} records.");
        return ListElements;
    }

    public string Text(byte[] raw)
    {
        if (!Present) return "";
        if (StartOffset < 0 || EndOffset > raw.Length || EndOffset - StartOffset < 4 ||
            BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(StartOffset, 4)) != EndOffset - StartOffset - 4)
            throw new InvalidDataException($"Invalid text boundary: {Name}");
        return new UTF8Encoding(false, true).GetString(raw, StartOffset + 4, EndOffset - StartOffset - 4);
    }
}

internal sealed class SaveObject
{
    public string ClassName { get; set; } = "";
    public SaveField[] Fields { get; set; } = [];
    public int[][] UndecodedRanges { get; set; } = [];
}

internal static class SaveRecords
{
    public static SaveField Named(this IEnumerable<SaveField> fields, string name)
    {
        SaveField? match = null;
        foreach (var field in fields)
        {
            if (field.Name != name) continue;
            if (match is not null) throw new InvalidDataException($"Missing or ambiguous field: {name}");
            match = field;
        }
        return match ?? throw new InvalidDataException($"Missing or ambiguous field: {name}");
    }

    internal static void ValidateItem(byte[] raw, SaveField record)
    {
        if (record.ChildTypeName != "ItemSaveData" || record.ChildFields.Length == 0) throw new InvalidDataException("Incomplete item record.");
        var fields = record.ChildFields;
        int end = checked(record.ChildPayloadOffset + record.ChildSizeU32);
        if (record.ChildUndecodedRanges.Length != 0 || end < record.ChildPayloadOffset || end > raw.Length - 4 || end + 4 != record.EndOffset ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(raw.AsSpan(end)) != record.ChildSizeU32)
            throw new InvalidDataException("Invalid item record boundary.");
        foreach (var field in fields.Where(f => f.Present))
            if (field.StartOffset < record.ChildPayloadOffset + 4 || field.StartOffset >= field.EndOffset || field.EndOffset > end)
                throw new InvalidDataException("Incomplete item field.");
        int tail = fields.Where(f => f.Present).Max(f => f.EndOffset);
        for (int i = tail; i < end; i++)
            if (raw[i] != 1) throw new InvalidDataException("Unrecognized item tail.");
    }

    public static SaveObject Read(byte[] raw, ParcLayout layout, string name)
        => ReadObjects(raw, layout, name).Single();

    internal static SaveObject ReadLists(byte[] raw, ParcLayout layout, string name, params string[] lists)
        => ReadLists(raw, layout, name, false, lists);

    internal static SaveObject ReadLists(byte[] raw, ParcLayout layout, string name, bool shallow, params string[] lists)
    {
        var block = layout.Blocks.Single(b => b.Name == name);
        var fields = layout.Types[block.TypeIndex].Fields;
        if (lists.Length == 0 || lists.Distinct().Count() != lists.Length ||
            lists.Any(n => fields.Count(f => f.Name == n && f.Kind is 6 or 7) != 1))
            throw new InvalidDataException("Missing or unsupported selected list.");
        int maskSize = BinaryPrimitives.ReadUInt16LittleEndian(raw.AsSpan(block.Offset, 2));
        if (block.Offset + 2 + maskSize > block.Offset + block.Size)
            throw new InvalidDataException("Invalid record block mask.");
        // Like Python, absent lists need the full decoder to locate their marker bytes.
        if (fields.Select((f, i) => (f, i)).Any(p => lists.Contains(p.f.Name) &&
            (p.i / 8 >= maskSize || (raw[block.Offset + 2 + p.i / 8] & (1 << (p.i % 8))) == 0)))
            return Read(raw, layout, name);
        int code = parc_field_spans(raw, checked((uint)raw.Length), (uint)block.TypeIndex, out var output, out uint length);
        uint[] spans;
        try
        {
            if (output == IntPtr.Zero) throw new InvalidDataException("Missing field spans.");
            if (code != 0) throw new InvalidDataException(Marshal.PtrToStringUTF8(output, checked((int)length)));
            if (length % 12 != 0) throw new InvalidDataException("Invalid field spans.");
            var values = new int[checked((int)length / 4)];
            Marshal.Copy(output, values, 0, values.Length);
            var selected = new List<uint>();
            var seen = new HashSet<string>();
            for (int i = 0; i < values.Length; i += 3)
            {
                int index = values[i], start = values[i + 1], end = values[i + 2];
                if (index < 0 || index >= fields.Count || start < block.Offset || end <= start || end > block.Offset + block.Size)
                    throw new InvalidDataException("Selected list outside its block.");
                if (!lists.Contains(fields[index].Name)) continue;
                if (fields[index].Kind is not 6 and not 7 || !seen.Add(fields[index].Name))
                    throw new InvalidDataException("Unsupported selected list.");
                selected.Add((uint)index); selected.Add((uint)start); selected.Add((uint)end);
            }
            if (seen.Count != lists.Length || lists.Length == 0) throw new InvalidDataException("Missing selected list.");
            spans = selected.Chunk(3).OrderBy(s => s[1]).SelectMany(s => s).ToArray();
        }
        finally { if (output != IntPtr.Zero) parc_index_free(output); }
        return ReadObjects(raw, layout, name, spans: spans, shallow: shallow).Single();
    }

    internal static unsafe SaveObject[] ReadObjects(byte[] raw, ParcLayout layout, string name, InventoryItem? references = null, uint[]? spans = null, bool shallow = false)
    {
        var blocks = layout.Blocks.Where(b => b.Name == name).ToArray();
        if (blocks.Length == 0 || blocks.Any(b => b.TypeIndex != blocks[0].TypeIndex)) throw new InvalidDataException($"Missing or ambiguous {name} type.");
        IntPtr output;
        uint length;
        int code = spans is not null
            ? parc_read_lists(raw, checked((uint)raw.Length), (uint)blocks[0].TypeIndex, spans, (uint)spans.Length, shallow ? 1u : 0u, out output, out length)
            : references is null
            ? parc_read_block(raw, checked((uint)raw.Length), checked((uint)blocks[0].TypeIndex), out output, out length)
            : parc_read_item_references(raw, checked((uint)raw.Length), checked((uint)blocks[0].TypeIndex), references.Number, references.Key, out output, out length);
        if (output == IntPtr.Zero) throw new InvalidDataException($"Record reader failed ({code}).");
        try
        {
            // Read directly from the native buffer; release it after constructing the records.
            var bytes = new ReadOnlySpan<byte>((void*)output, checked((int)length));
            if (code != 0) throw new InvalidDataException(Encoding.UTF8.GetString(bytes));
            var objects = RecordReader.Read(bytes);
            if (objects.Length != blocks.Length || objects.Any(o => o.ClassName != name))
                throw new InvalidDataException($"Incomplete {name} decode.");
            // Absent trailing lists retain 0x01 markers in existing game saves.
            foreach (var range in objects.SelectMany(o => o.UndecodedRanges))
            {
                if (range.Length != 2 || range[0] > range[1] || !blocks.Any(b => range[0] >= b.Offset && range[1] <= b.Offset + b.Size))
                    throw new InvalidDataException("Invalid undecoded range.");
                for (int i = range[0]; i < range[1]; i++)
                    if (raw[i] != 1) throw new InvalidDataException($"Unrecognized {name} bytes.");
            }
            return objects;
        }
        finally { parc_reader_free(output); }
    }

    [DllImport("parc_reader.dll", EntryPoint = "parc_read_lists_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int parc_read_lists(byte[] raw, uint size, uint type, uint[] spans, uint count, uint shallow, out IntPtr output, out uint length);
    [DllImport("parc_index.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern int parc_field_spans(byte[] raw, uint size, uint type, out IntPtr output, out uint length);
    [DllImport("parc_index.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void parc_index_free(IntPtr output);
    [DllImport("parc_reader.dll", EntryPoint = "parc_read_item_references_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int parc_read_item_references(byte[] raw, uint size, uint type, ulong number, uint key, out IntPtr output, out uint length);
    [DllImport("parc_reader.dll", EntryPoint = "parc_read_block_v2", CallingConvention = CallingConvention.Cdecl)]
    private static extern int parc_read_block(byte[] raw, uint size, uint type, out IntPtr output, out uint length);
    [DllImport("parc_reader.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void parc_reader_free(IntPtr output);
}
