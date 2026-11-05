using System.Buffers.Binary;
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
    {
        var fields = layout.Types[Blocks(layout, name)[0].TypeIndex].Fields;
        if (lists.Length == 0 || lists.Distinct().Count() != lists.Length ||
            lists.Any(n => fields.Count(f => f.Name == n && f.Kind is 6 or 7) != 1))
            throw new InvalidDataException("Missing or unsupported selected list.");
        return new() { ClassName = name, Fields = Read(raw, layout, name).Fields.Where(f => lists.Contains(f.Name)).ToArray() };
    }

    private static ParcBlock[] Blocks(ParcLayout layout, string name)
    {
        var blocks = layout.Blocks.Where(b => b.Name == name).ToArray();
        if (blocks.Length == 0 || blocks.Any(b => b.TypeIndex != blocks[0].TypeIndex)) throw new InvalidDataException($"Missing or ambiguous {name} type.");
        return blocks;
    }

    internal static SaveObject[] ReadObjects(byte[] raw, ParcLayout layout, string name)
        => Array.ConvertAll(Blocks(layout, name), b => ParcReader.Read(raw, layout, b));

    // Every record of the item, wherever the block keeps one.
    internal static List<SaveField> ItemReferences(byte[] raw, ParcLayout layout, string name, InventoryItem item)
    {
        var found = new List<SaveField>();
        void Visit(SaveField field)
        {
            if (field.ChildTypeName == "ItemSaveData" && field.ChildFields.Named("_saveVersion").Present &&
                field.ChildFields.Named("_itemNo") is { Present: true } number && number.Scalar(raw, "ItemNo", 8) == item.Number &&
                field.ChildFields.Named("_itemKey") is { Present: true } key && key.Scalar(raw, "ItemKey", 4) == item.Key)
                found.Add(field);
            foreach (var child in field.ChildFields) Visit(child);
            foreach (var element in field.ListElements) Visit(element);
        }
        foreach (var block in ReadObjects(raw, layout, name))
            foreach (var field in block.Fields) Visit(field);
        return found;
    }
}
