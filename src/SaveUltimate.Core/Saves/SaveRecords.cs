using System.Buffers.Binary;
using System.Text;

namespace SaveUltimate.Core.Saves;

internal sealed class SaveField
{
    private static readonly ParcField NoDefinition = new("", "", 0, 0, 0, 0);
    // List rows have no definition. The schema's values are not copied here: one block can hold hundreds of thousands of these.
    public ParcField Definition { get; init; } = NoDefinition;
    public string Name => Definition.Name;
    public string TypeName => Definition.TypeName;
    public int MetaKind => Definition.Kind;
    public int MetaSize => Definition.Size;
    public int FieldIndex => Definition.Index;
    public bool Present { get; set; }
    public int StartOffset { get; set; }
    public int EndOffset { get; set; }
    public ParcType? ChildType { get; set; }
    public string ChildTypeName => ChildType?.Name ?? "";
    public int ChildMaskByteCount => ChildType?.MaskSize ?? 0;
    public int ChildPayloadOffset { get; set; }
    public SaveField[] ChildFields { get; set; } = [];
    public SaveField[] ListElements { get; set; } = [];

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
        if (ListElements.Any(f => f.ChildTypeName != type || f.ChildFields.Length == 0))
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

    public static SaveObject Read(byte[] raw, ParcLayout layout, string name)
        => ReadObjects(raw, layout, name).Single();

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
